using OpenDreamClient.Interface;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Key = Robust.Client.Input.Keyboard.Key;

namespace OpenDreamClient.Rendering;

public sealed partial class AtomGlideSystem {
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;

    private EntityQuery<TransformComponent> _xformQuery;
    private readonly HashSet<Key> _heldMoveKeys = new();
    private readonly Dictionary<Key, int> _moveKeyDirs = new();
    private bool _hasMoveKeyList;
    private int _clientDir = 1;
    private Vector2 _moveDir;
    private Vector2 _confirmed;
    private Vector2 _visual;
    private Vector2 _steerDir;
    private bool _hasConfirmed;
    private bool _ownVisual;
    private bool _steering;
    private bool _blocked;
    // True until the server accepts a step of this key press. The first step
    // may pass the last confirmed tile: the key arrives after that step started.
    private bool _opening;
    private TimeSpan _releaseAt;
    private float _cardinalPeriod;
    private bool _hasStepSample;
    private TimeSpan _lastStepAt;
    private Vector2 _lastStepDelta;

    private void OnMoveKey(KeyEventArgs args, KeyEventType type) {
        if (!IsMoveKey(args.Key))
            return;

        if (type == KeyEventType.Up)
            _heldMoveKeys.Remove(args.Key);
        else if (type == KeyEventType.Down)
            _heldMoveKeys.Add(args.Key);

        ApplyHeldDirection(HeldDirection());
    }

    /// <summary>
    /// key=dir lines from the station, plus cam= for the camera dir keyLoop turns by.
    /// An empty key list leaves the WASD fallback in place.
    /// </summary>
    public void SetMoveKeys(string text) {
        var next = new Dictionary<Key, int>();
        var cam = _clientDir;
        var sawCam = false;
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = line[..eq];
            if (!int.TryParse(line[(eq + 1)..], out var bits))
                continue;
            if (name == "cam") {
                cam = bits;
                sawCam = true;
                continue;
            }

            var key = ParsedKeybind.KeyNameToKey(name.ToUpperInvariant());
            if (key == Key.Unknown)
                continue;
            next[key] = bits;
        }

        if (sawCam) {
            _hasMoveKeyList = true;
            _moveKeyDirs.Clear();
            foreach (var pair in next)
                _moveKeyDirs[pair.Key] = pair.Value;
        }

        _clientDir = cam;
        ApplyHeldDirection(HeldDirection());
    }

    /// <summary>
    /// A new direction may walk again. The wall block stays until the direction
    /// actually changes, so a position packet cannot make us hit the same wall.
    /// </summary>
    private void ApplyHeldDirection(Vector2 dir) {
        if (dir != _moveDir && dir != Vector2.Zero) {
            _blocked = false;
            _opening = true;
        }

        _moveDir = dir;
    }

    private void TickPrediction(TimeSpan now, float frameTime) {
        if (!TryLocal(out var uid, out var xform, out var sprite))
            return;

        var held = _moveDir != Vector2.Zero && !_blocked && _ui.KeyboardFocused == null;
        if (held) {
            if (!_steering)
                _opening = true;

            var glide = FindGlide(uid);
            if (glide != null) {
                _visual = VisualPosition(glide, now);
                _currentGlides.Remove(glide);
            } else if (!_ownVisual) {
                _visual = xform.LocalPosition;
            }

            if (!_hasConfirmed) {
                _confirmed = TileOf(_visual);
                _hasConfirmed = true;
            }

            _steering = true;
            _ownVisual = true;
            _steerDir = _moveDir;
            Steer(uid, xform, sprite, now, frameTime);
            return;
        }

        if (_steering) {
            _steering = false;
            _releaseAt = now;
        }

        if (FindGlide(uid) != null)
            return;
        if (_ownVisual)
            Settle(uid, xform, sprite, now);
    }

    /// <summary>
    /// Walk at a constant speed. Releasing the key stops this immediately.
    /// Until the server accepts a step, the sprite may enter the tile after the
    /// next one. Stopping on the first tile is the hitch at the start of a walk:
    /// that tile finishes before the key comes back as a move. A cap behind the
    /// sprite is not walked toward.
    /// </summary>
    private void Steer(EntityUid uid, TransformComponent xform, DMISpriteComponent sprite, TimeSpan now, float frameTime) {
        var cap = _confirmed + _moveDir * (_opening ? 2f : 1f);
        var delta = cap - _visual;
        if (Vector2.Dot(delta, _moveDir) <= 0.001f) {
            Place(uid, xform, _visual);
            return;
        }

        var distance = delta.Length();
        var step = Math.Min(WalkSpeed(sprite, _moveDir) * Math.Max(frameTime, 0f), distance);
        _visual += Vector2.Normalize(delta) * step;
        Place(uid, xform, _visual);
    }

    /// <summary>
    /// The key is up. Stay where the walk stopped. Catch the server if it is
    /// already on a further tile, otherwise give back only the distance it never took.
    /// </summary>
    private void Settle(EntityUid uid, TransformComponent xform, DMISpriteComponent sprite, TimeSpan now) {
        if ((_visual - _confirmed).LengthSquared() <= 0.0004f) {
            _visual = _confirmed;
            Place(uid, xform, _visual);
            _ownVisual = false;
            return;
        }

        var ahead = _steerDir == Vector2.Zero
            ? (_visual - _confirmed).Length()
            : Vector2.Dot(_visual - _confirmed, Vector2.Normalize(_steerDir));
        if (ahead <= 0.02f) {
            BeginCorrection(uid, xform, sprite, _visual, _confirmed, now);
            _ownVisual = false;
            return;
        }

        if ((now - _releaseAt).TotalSeconds < Math.Max(CardinalDuration(sprite), 0.15f)) {
            Place(uid, xform, _visual);
            return;
        }

        BeginCorrection(uid, xform, sprite, _visual, _confirmed, now);
        _ownVisual = false;
    }

    private void BeginCorrection(EntityUid uid, TransformComponent xform, DMISpriteComponent sprite, Vector2 from, Vector2 to, TimeSpan now) {
        var distance = (to - from).Length();
        if (distance < 0.001f) {
            Place(uid, xform, to);
            return;
        }

        var speed = WalkSpeed(sprite, _steerDir == Vector2.Zero ? to - from : _steerDir);
        _currentGlides.Add(new Glide(uid, xform, sprite) {
            StartPos = from,
            EndPos = to,
            StartTime = now,
            Duration = Math.Max(distance / Math.Max(speed, 0.01f), 0.04f),
            Predicted = false
        });
    }

    private float WalkSpeed(DMISpriteComponent sprite, Vector2 dir) {
        var period = CardinalDuration(sprite);
        if (period < 0.02f)
            period = 0.1f;
        if (dir.LengthSquared() > 1.1f)
            return dir.Length() / (period * 1.5f);
        return 1f / period;
    }

    private void Place(EntityUid uid, TransformComponent xform, Vector2 position) {
        _ignoreMoveEvent = true;
        _transformSystem.SetLocalPositionNoLerp(uid, position, xform);
        _ignoreMoveEvent = false;
    }

    /// <summary>
    /// A server step along the held direction. The sprite stays where the walk
    /// put it: the packet does not start that step over.
    /// </summary>
    private bool OnWalk(Vector2 previous, Vector2 position) {
        var dir = _moveDir != Vector2.Zero ? _moveDir : _steerDir;
        if (dir == Vector2.Zero)
            return (position - previous).LengthSquared() < 0.04f;

        var cap = previous + dir;
        if (OnPredictedPath(previous, cap, position) || (position - cap).LengthSquared() < 0.16f)
            return true;
        if ((position - previous).LengthSquared() < 0.04f)
            return true;

        var beyond = position - cap;
        return SameDir(dir, beyond) && beyond.Length() <= dir.Length() + 0.35f;
    }

    /// <summary>
    /// One cardinal tile. glide_size until two accepted steps in one direction,
    /// then the interval those two steps actually took.
    /// </summary>
    private float CardinalDuration(DMISpriteComponent sprite) {
        if (_cardinalPeriod > 0.02f)
            return _cardinalPeriod;

        var speed = CalculateMovementSpeed(_interfaceManager.IconSize, sprite.Icon.Appearance?.GlideSize ?? 0f);
        return speed > 0f ? 1f / speed : 0.1f;
    }

    private static bool OnPredictedPath(Vector2 start, Vector2 end, Vector2 point) {
        var seg = end - start;
        var rel = point - start;
        return InsideAxis(rel.X, seg.X) && InsideAxis(rel.Y, seg.Y);
    }

    private static bool InsideAxis(float rel, float seg) {
        if (MathF.Abs(seg) < 0.2f)
            return MathF.Abs(rel) < 0.35f;

        var t = rel / seg;
        return t >= -0.15f && t <= 1.15f;
    }

    private static Vector2 TileOf(Vector2 pos) {
        return new Vector2(
            MathF.Round(pos.X, MidpointRounding.AwayFromZero),
            MathF.Round(pos.Y, MidpointRounding.AwayFromZero));
    }

    private static bool SameDir(Vector2 step, Vector2 dir) {
        if (step.LengthSquared() < 0.0001f || dir.LengthSquared() < 0.0001f)
            return false;
        return Vector2.Dot(Vector2.Normalize(step), Vector2.Normalize(dir)) > 0.9f;
    }

    private void NoteServerMove(EntityUid uid, Vector2 position, TimeSpan now) {
        if (!IsLocal(uid))
            return;

        var previous = _hasConfirmed ? _confirmed : position;
        _confirmed = position;
        _hasConfirmed = true;
        var stepDelta = position - previous;
        if (stepDelta.LengthSquared() > 0.25f && (_moveDir == Vector2.Zero || SameDir(stepDelta, _moveDir)))
            _opening = false;
        RememberCardinal(previous, position, now);
    }

    /// <summary>
    /// Two accepted cardinal steps in the held direction set the step period.
    /// A diagonal is two different halves, so it does not change that period.
    /// </summary>
    private void RememberCardinal(Vector2 previous, Vector2 position, TimeSpan now) {
        if (_moveDir.LengthSquared() < 0.5f || _moveDir.LengthSquared() > 1.1f) {
            _hasStepSample = false;
            return;
        }

        var delta = position - previous;
        var ax = MathF.Abs(delta.X);
        var ay = MathF.Abs(delta.Y);
        var cardinal = (ax < 0.35f || ay < 0.35f) && delta.LengthSquared() is > 0.5f and < 1.5f;
        if (!cardinal || !SameDir(delta, _moveDir)) {
            _hasStepSample = false;
            return;
        }

        if (_hasStepSample && SameDir(delta, _lastStepDelta)) {
            var interval = (float)(now - _lastStepAt).TotalSeconds;
            if (interval > 0.02f && interval < 0.8f)
                _cardinalPeriod = interval;
        }

        _lastStepDelta = delta;
        _lastStepAt = now;
        _hasStepSample = true;
    }

    private Glide? FindGlide(EntityUid uid) {
        foreach (var glide in _currentGlides) {
            if (glide.Uid == uid)
                return glide;
        }

        return null;
    }

    private bool TryLocal(out EntityUid uid, out TransformComponent xform, out DMISpriteComponent sprite) {
        uid = _player.LocalSession?.AttachedEntity ?? EntityUid.Invalid;
        xform = default!;
        sprite = default!;
        if (!uid.IsValid())
            return false;
        return _xformQuery.TryGetComponent(uid, out xform!) && _spriteQuery.TryGetComponent(uid, out sprite!);
    }

    private bool IsLocal(EntityUid uid) {
        var local = _player.LocalSession?.AttachedEntity;
        return local != null && local.Value == uid;
    }

    private Vector2 HeldDirection() {
        var bits = 0;
        if (!_hasMoveKeyList) {
            if (_heldMoveKeys.Contains(Key.W) || _heldMoveKeys.Contains(Key.Up))
                bits |= 1;
            if (_heldMoveKeys.Contains(Key.S) || _heldMoveKeys.Contains(Key.Down))
                bits |= 2;
            if (_heldMoveKeys.Contains(Key.D) || _heldMoveKeys.Contains(Key.Right))
                bits |= 4;
            if (_heldMoveKeys.Contains(Key.A) || _heldMoveKeys.Contains(Key.Left))
                bits |= 8;
        } else {
            foreach (var key in _heldMoveKeys) {
                if (_moveKeyDirs.TryGetValue(key, out var dir))
                    bits |= dir;
            }
        }

        if ((bits & 1) != 0 && (bits & 2) != 0)
            bits &= ~3;
        if ((bits & 4) != 0 && (bits & 8) != 0)
            bits &= ~12;

        if (_clientDir != 1)
            bits = TurnDir(bits, -Dir2Angle(_clientDir));
        return DirToVector(bits);
    }

    private bool IsMoveKey(Key key) {
        if (_hasMoveKeyList)
            return _moveKeyDirs.ContainsKey(key);
        return key is Key.W or Key.A or Key.S or Key.D or Key.Up or Key.Down or Key.Left or Key.Right;
    }

    /// <summary>dir2angle: north is 0 and the angle grows clockwise.</summary>
    private static int Dir2Angle(int dir) {
        return dir switch {
            1 => 0,
            5 => 45,
            4 => 90,
            6 => 135,
            2 => 180,
            10 => 225,
            8 => 270,
            9 => 315,
            _ => 0
        };
    }

    /// <summary>turn(): the same counterclockwise map the server uses.</summary>
    private static int TurnDir(int dir, int angle) {
        if (dir == 0 || angle == 0)
            return dir;

        float? dirAngle = dir switch {
            4 => 0,
            5 => 45,
            1 => 90,
            9 => 135,
            8 => 180,
            10 => 225,
            2 => 270,
            6 => 315,
            _ => null
        };
        if (dirAngle == null)
            return dir;

        dirAngle += MathF.Truncate(angle / 45f) * 45f;
        dirAngle %= 360f;
        if (dirAngle < 0)
            dirAngle += 360f;

        return dirAngle switch {
            45 => 5,
            90 => 1,
            135 => 9,
            180 => 8,
            225 => 10,
            270 => 2,
            315 => 6,
            _ => 4
        };
    }

    private static Vector2 DirToVector(int dir) {
        var x = 0;
        var y = 0;
        if ((dir & 1) != 0)
            y += 1;
        if ((dir & 2) != 0)
            y -= 1;
        if ((dir & 4) != 0)
            x += 1;
        if ((dir & 8) != 0)
            x -= 1;
        return new Vector2(x, y);
    }
}
