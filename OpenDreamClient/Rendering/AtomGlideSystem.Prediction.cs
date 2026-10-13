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
    private Vector2 _steerDir;
    private Vector2 _visualHold;
    private bool _hasConfirmed;
    private bool _steering;
    private bool _blocked;
    private bool _stepConfirmed = true;
    private bool _holding;
    private bool _waitingAtEnd;
    private TimeSpan _releaseAt;
    private TimeSpan _waitFrom;
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
        if (dir != _moveDir && dir != Vector2.Zero)
            _blocked = false;

        _moveDir = dir;
    }

    private void TickPrediction(TimeSpan now, float frameTime) {
        if (!TryLocal(out var uid, out var xform, out var sprite))
            return;

        var held = _moveDir != Vector2.Zero && !_blocked && _ui.KeyboardFocused == null;
        var glide = FindGlide(uid);
        if (!held) {
            if (_steering) {
                _steering = false;
                _releaseAt = now;
            }

            // The step has not been accepted yet. Stop where the key came up
            // and give the server one tick to agree before giving the pixels back.
            if (glide is { Predicted: true } && !_stepConfirmed) {
                _visualHold = VisualPosition(glide, now);
                _currentGlides.Remove(glide);
                Place(uid, xform, _visualHold);
                _holding = true;
                glide = null;
            }

            if (_holding) {
                if (_stepConfirmed) {
                    _holding = false;
                    if ((_visualHold - _confirmed).LengthSquared() > 0.01f)
                        BeginGlide(uid, xform, sprite, _visualHold, _confirmed, false, now);
                    return;
                }

                if ((now - _releaseAt).TotalSeconds < 0.08f) {
                    Place(uid, xform, _visualHold);
                    return;
                }

                _holding = false;
                if ((_visualHold - _confirmed).LengthSquared() > 0.01f)
                    BeginGlide(uid, xform, sprite, _visualHold, _confirmed, false, now);
            }

            return;
        }

        if (!_steering) {
            _steering = true;
            _stepConfirmed = true;
            _waitingAtEnd = false;
            if (!_hasConfirmed) {
                _confirmed = TileOf(xform.LocalPosition);
                _hasConfirmed = true;
            }
        }

        if (glide is { Predicted: true } && !SameDir(glide.EndPos - glide.StartPos, _moveDir)) {
            var visual = VisualPosition(glide, now);
            _currentGlides.Remove(glide);
            Place(uid, xform, visual);
            glide = null;
            _stepConfirmed = true;
            _waitingAtEnd = false;
        }

        if (glide != null)
            return;

        if (!_stepConfirmed) {
            if (!_waitingAtEnd) {
                _waitingAtEnd = true;
                _waitFrom = now;
            }

            // Still the tile the glide already finished. A late accept continues;
            // a wall does not, and only then do we step back.
            if ((now - _waitFrom).TotalSeconds < 0.15f)
                return;

            var here = xform.LocalPosition;
            if ((here - _confirmed).LengthSquared() > 0.01f)
                BeginGlide(uid, xform, sprite, here, _confirmed, false, now);
            _blocked = true;
            _stepConfirmed = true;
            _waitingAtEnd = false;
            return;
        }

        var destination = _confirmed + _moveDir;
        BeginGlide(uid, xform, sprite, xform.LocalPosition, destination, true, now);
        _steerDir = _moveDir;
        _stepConfirmed = false;
        _waitingAtEnd = false;
    }

    /// <summary>
    /// The local mob already started this step when the key went down.
    /// A server position on that same step must not replay it from the tile behind.
    /// </summary>
    private bool HandleLocalMove(EntityUid uid, TransformComponent xform, DMISpriteComponent sprite, Glide? glide, Vector2 to, TimeSpan now) {
        var predicting = _steering || _holding || glide is { Predicted: true };
        if (!predicting)
            return false;

        var visual = glide != null ? VisualPosition(glide, now) : _holding ? _visualHold : xform.LocalPosition;
        if ((to - visual).Length() > 2f) {
            if (glide != null)
                _currentGlides.Remove(glide);
            _holding = false;
            _steering = false;
            _waitingAtEnd = false;
            _stepConfirmed = true;
            _hasConfirmed = false;
            return false;
        }

        var dir = _moveDir != Vector2.Zero ? _moveDir : _steerDir;
        if (dir == Vector2.Zero)
            return false;

        if (!OnWalk(_confirmed, to)) {
            if (Vector2.Dot(to - visual, dir) <= 0.05f) {
                Place(uid, xform, visual);
                return true;
            }

            if (glide != null)
                _currentGlides.Remove(glide);
            _holding = false;
            _steering = false;
            _stepConfirmed = true;
            _waitingAtEnd = false;
            _confirmed = to;
            _hasConfirmed = true;
            BeginGlide(uid, xform, sprite, visual, to, false, now);
            Place(uid, xform, visual);
            return true;
        }

        var end = _confirmed + dir;
        var reachedStep = (to - end).LengthSquared() < 0.16f || glide != null && (to - glide.EndPos).LengthSquared() < 0.16f;
        if (reachedStep) {
            var previous = _confirmed;
            _confirmed = to;
            _hasConfirmed = true;
            _stepConfirmed = true;
            _waitingAtEnd = false;
            RememberCardinal(previous, to, now);
        } else if (_moveDir != Vector2.Zero && glide != null && SameDir(dir, to - glide.EndPos) && (to - glide.EndPos).LengthSquared() > 0.25f) {
            var previous = _confirmed;
            _confirmed = to;
            _hasConfirmed = true;
            _stepConfirmed = true;
            _waitingAtEnd = false;
            RememberCardinal(previous, to, now);
            var speed = Math.Max(WalkSpeed(sprite, dir), 0.01f);
            glide.StartPos = visual;
            glide.EndPos = to;
            glide.StartTime = now;
            glide.Duration = Math.Max((to - visual).Length() / speed, 0.04f);
            Place(uid, xform, visual);
            return true;
        }

        if (glide is { Predicted: true }) {
            Place(uid, xform, visual);
            return true;
        }

        if (_holding) {
            _holding = false;
            _confirmed = to;
            _hasConfirmed = true;
            _stepConfirmed = true;
            if ((visual - to).LengthSquared() > 0.01f)
                BeginGlide(uid, xform, sprite, visual, to, false, now);
            else
                Place(uid, xform, to);
            return true;
        }

        Place(uid, xform, visual);
        return true;
    }

    private void BeginGlide(EntityUid uid, TransformComponent xform, DMISpriteComponent sprite, Vector2 from, Vector2 to, bool predicted, TimeSpan now) {
        var distance = (to - from).Length();
        if (distance < 0.001f) {
            Place(uid, xform, to);
            return;
        }

        var speed = Math.Max(WalkSpeed(sprite, to - from), 0.01f);
        _currentGlides.Add(new Glide(uid, xform, sprite) {
            StartPos = from,
            EndPos = to,
            StartTime = now,
            Duration = Math.Max(distance / speed, 0.04f),
            Predicted = predicted
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
