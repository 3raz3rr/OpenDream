using OpenDreamClient.Interface;
using OpenDreamShared.Dream;
using OpenDreamShared.Network.Messages;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace OpenDreamClient.Rendering;

/// <summary>
/// Disables RobustToolbox's transform lerping and replaces it with our own gliding
/// </summary>
public sealed partial class AtomGlideSystem : EntitySystem {
    private sealed class Glide(EntityUid uid, TransformComponent transform, DMISpriteComponent sprite) {
        public readonly EntityUid Uid = uid;
        public readonly TransformComponent Transform = transform;
        public readonly DMISpriteComponent Sprite = sprite;
        public Vector2 StartPos;
        public Vector2 EndPos;
        public TimeSpan StartTime;
        public float Duration;
        public bool Predicted;
    }

    [Dependency] private TransformSystem _transformSystem = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private IDreamInterfaceManager _interfaceManager = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    private EntityQuery<DMISpriteComponent> _spriteQuery;

    // Newest tile the server has committed for the local mob, and its step id.
    // A snapshot of an earlier tile must not start a slide back to it.
    private Vector2 _localStepEnd;
    private bool _hasLocalStep;
    private int _latestStep;

    private readonly List<Glide> _currentGlides = new();
    private readonly Dictionary<EntityUid, TimeSpan> _lastMoveAt = new();

    /// <summary>
    /// Ignore MoveEvent when this is true.
    /// Prevents an infinite loop when setting the position within the event handler.
    /// </summary>
    private bool _ignoreMoveEvent;

    public override void Initialize() {
        UpdatesBefore.Add(typeof(SharedTransformSystem));

        _spriteQuery = _entityManager.GetEntityQuery<DMISpriteComponent>();
        _xformQuery = _entityManager.GetEntityQuery<TransformComponent>();

        _transformSystem.OnGlobalMoveEvent += OnTransformMove;
        _input.FirstChanceOnKeyEvent += OnMoveKey;
    }

    public override void Shutdown() {
        _input.FirstChanceOnKeyEvent -= OnMoveKey;
        _currentGlides.Clear();
        _lastMoveAt.Clear();
    }

    public override void FrameUpdate(float frameTime) {
        // As of writing, Reset() does nothing but clear the transform system's _lerpingTransforms list
        // We update before SharedTransformSystem so this serves to disable RT's lerping, which fights our gliding
        // TODO: This kinda fights RT. Would be nice to modify RT to make it play nicer.
        _transformSystem.Reset();

        _ignoreMoveEvent = false;
        var now = _gameTiming.RealTime;

        for (int i = 0; i < _currentGlides.Count; i++) {
            var glide = _currentGlides[i];

            if (_entityManager.Deleted(glide.Uid) || glide.Sprite.Icon.Appearance == null) {
                _lastMoveAt.Remove(glide.Uid);
                _currentGlides.RemoveSwap(i--);
                continue;
            }

            var elapsed = (float)(now - glide.StartTime).TotalSeconds;
            var t = glide.Duration <= 0.0001f ? 1f : Math.Clamp(elapsed / glide.Duration, 0f, 1f);
            var newPos = t >= 1f ? glide.EndPos : Vector2.Lerp(glide.StartPos, glide.EndPos, t);
            if (t >= 1f)
                _currentGlides.RemoveSwap(i--);

            _ignoreMoveEvent = true;
            _transformSystem.SetLocalPositionNoLerp(glide.Uid, newPos, glide.Transform);
            _ignoreMoveEvent = false;
        }
    }

    /// <summary>
    /// Disables RT lerping and sets up the entity's glide
    /// </summary>
    private void OnTransformMove(ref MoveEvent e) {
        if (_ignoreMoveEvent)
            return;
        if (e.ParentChanged) {
            if (IsLocal(e.Sender)) {
                NoteLocalMob(e.Sender);
                _hasLocalStep = false;
            }
            return;
        }
        if (!_spriteQuery.TryGetComponent(e.Sender, out var sprite) || sprite.Icon?.Appearance is null)
            return;

        _ignoreMoveEvent = true;

        // Look for any in-progress glides on this transform
        Glide? glide = null;
        foreach (var potentiallyThisTransform in _currentGlides) {
            if (potentiallyThisTransform.Transform != e.Component)
                continue;

            glide = potentiallyThisTransform;
            break;
        }

        var now = _gameTiming.RealTime;
        var tick = _gameTiming.TickRate > 0 ? 1f / _gameTiming.TickRate : 0.05f;
        var glidingTo = e.NewPosition.Position;

        if (IsLocal(e.Sender))
            NoteLocalMob(e.Sender);

        // The step message is already on screen. The replicated transform is an older
        // snapshot of that walk and must not pull the sprite back to a tile it left.
        if (IsLocal(e.Sender) && _hasLocalStep) {
            var restore = glide != null ? VisualPosition(glide, now) : _localStepEnd;
            _transformSystem.SetLocalPositionNoLerp(e.Sender, restore, e.Component);
            _ignoreMoveEvent = false;
            return;
        }

        // Where the sprite already is. A glide on the way continues from that pixel.
        Vector2 startingFrom;
        var sameBurst = false;
        if (glide != null) {
            var since = (float)(now - glide.StartTime).TotalSeconds;
            sameBurst = since < tick * 0.5f;
            startingFrom = sameBurst ? glide.StartPos : VisualPosition(glide, now);
        } else {
            startingFrom = e.OldPosition.Position;
        }

        var distance = (glidingTo - startingFrom).Length();

        // Moving a greater distance than 2 tiles. Don't glide.
        // TODO: Support step_size values (I think that's what decides whether or not to glide?)
        if (distance > 2f) {
            if (glide != null)
                _currentGlides.Remove(glide);

            _lastMoveAt.Remove(e.Sender);
            _ignoreMoveEvent = false;
            return;
        }

        var speed = CalculateMovementSpeed(_interfaceManager.IconSize, sprite.Icon.Appearance.GlideSize);
        float duration;
        if (glide != null && !sameBurst) {
            var elapsed = (float)(now - glide.StartTime).TotalSeconds;
            var remaining = Math.Max(0f, glide.Duration - elapsed);
            var forward = Vector2.Dot(glidingTo - glide.EndPos, glide.EndPos - glide.StartPos);
            if (forward > 0.0001f && speed > 0f)
                duration = remaining + TileDistance(glide.EndPos, glidingTo) / speed;
            else
                duration = speed > 0f ? TileTime(startingFrom, glidingTo, speed) : 0f;
        } else {
            duration = speed > 0f ? TileTime(startingFrom, glidingTo, speed) : 0f;
        }

        // A step that finishes before the next one sits, then jerks. Stretch across that
        // gap when it is still a walk. A real pause is longer than this and stays a pause.
        if (!sameBurst && _lastMoveAt.TryGetValue(e.Sender, out var previous)) {
            var gap = (float)(now - previous).TotalSeconds;
            if (gap > duration && gap <= duration * 1.6f && gap < duration + 0.12f)
                duration = gap;
        }

        _lastMoveAt[e.Sender] = now;

        if (glide == null) {
            glide = new(e.Sender, e.Component, sprite);
            _currentGlides.Add(glide);
        }

        _transformSystem.SetLocalPositionNoLerp(e.Sender, startingFrom, e.Component);

        glide.StartPos = startingFrom;
        glide.EndPos = glidingTo;
        glide.StartTime = now;
        glide.Duration = Math.Max(duration, 0.001f);
        _ignoreMoveEvent = false;
    }

    /// <summary>
    /// Server committed this tile in the same tick as the move. Start the slide
    /// now. A following transform packet for the same tile leaves the slide alone.
    /// </summary>
    public void HandleMobStep(MsgMobStep msg) {
        if (!TryLocal(out var uid, out var xform, out var sprite))
            return;

        NoteLocalMob(uid);
        if (msg.Sequence <= _latestStep)
            return;

        _latestStep = msg.Sequence;
        var destination = new Vector2(msg.X, msg.Y);
        var now = _gameTiming.RealTime;
        var glide = FindGlide(uid);
        var speed = CalculateMovementSpeed(_interfaceManager.IconSize, msg.GlideSize);
        if (glide != null && (glide.EndPos - destination).LengthSquared() < 0.04f) {
            _localStepEnd = destination;
            _hasLocalStep = true;
            return;
        }

        // The server splits one diagonal into two cardinal loc changes. The first
        // of those is not a place to slide to. A snapshot may already be on the
        // corner; this half-step must not pull the sprite onto the side tile.
        var age = glide != null ? (float)(now - glide.StartTime).TotalSeconds : 0f;
        if (glide != null && age < 0.05f && IsDiagonalStep(glide.EndPos - glide.StartPos) && IsHalfStep(glide.StartPos, glide.EndPos, destination)) {
            if (speed > 0f)
                glide.Duration = Math.Max(TileTime(glide.StartPos, glide.EndPos, speed), 0.001f);
            _localStepEnd = glide.EndPos;
            _hasLocalStep = true;
            return;
        }

        var from = glide != null ? VisualPosition(glide, now) : xform.LocalPosition;
        // Both halves landed before a frame. Keep the slide straight from where it began.
        if (glide != null && age < 0.05f && IsCardinalStep(glide.EndPos - glide.StartPos) && IsDiagonalStep(destination - glide.StartPos))
            from = glide.StartPos;

        var distance = (destination - from).Length();
        if (distance > 2f) {
            if (glide != null)
                _currentGlides.Remove(glide);
            _hasLocalStep = false;
            return;
        }

        if (distance < 0.001f) {
            _localStepEnd = destination;
            _hasLocalStep = true;
            return;
        }

        // A diagonal tile is longer in pixels, but the server spends one step on it.
        // Euclidean time falls behind and the next pair then throws the sprite.
        var duration = speed > 0f ? TileTime(from, destination, speed) : 0.05f;
        if (glide == null) {
            glide = new Glide(uid, xform, sprite);
            _currentGlides.Add(glide);
        }

        glide.StartPos = from;
        glide.EndPos = destination;
        glide.StartTime = now;
        glide.Duration = Math.Max(duration, 0.001f);
        _localStepEnd = destination;
        _hasLocalStep = true;
        _lastMoveAt[uid] = now;
    }

    /// <summary>
    /// Dir changed on this tick. Turn the sprite now; the appearance packet is the same turn later.
    /// </summary>
    public void HandleMobFacing(MsgMobFacing msg) {
        if (!TryLocal(out var uid, out _, out var sprite))
            return;

        NoteLocalMob(uid);
        sprite.Icon?.SetDirection((AtomDirection)msg.Dir);
    }

    private EntityUid _stepMob;

    /// <summary>
    /// Step ids belong to one mob. A new body starts again at one.
    /// </summary>
    private void NoteLocalMob(EntityUid uid) {
        if (uid == _stepMob)
            return;

        _stepMob = uid;
        _latestStep = 0;
        _hasLocalStep = false;
    }

    /// <summary>
    /// King-move distance. A diagonal step counts as one tile, matching move delay.
    /// </summary>
    private static float TileDistance(Vector2 from, Vector2 to) {
        return Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
    }

    private static float TileTime(Vector2 from, Vector2 to, float tilesPerSecond) {
        return TileDistance(from, to) / tilesPerSecond;
    }

    private static bool IsCardinalStep(Vector2 delta) {
        var ax = Math.Abs(delta.X);
        var ay = Math.Abs(delta.Y);
        return (ax < 0.2f && Math.Abs(ay - 1f) < 0.2f) || (ay < 0.2f && Math.Abs(ax - 1f) < 0.2f);
    }

    private static bool IsDiagonalStep(Vector2 delta) {
        return Math.Abs(Math.Abs(delta.X) - 1f) < 0.2f && Math.Abs(Math.Abs(delta.Y) - 1f) < 0.2f;
    }

    private static bool IsHalfStep(Vector2 start, Vector2 diagonalEnd, Vector2 tile) {
        var mid = tile - start;
        if (!IsCardinalStep(mid))
            return false;

        var full = diagonalEnd - start;
        if (Math.Abs(mid.X) > 0.2f && Math.Abs(mid.X - full.X) > 0.2f)
            return false;
        if (Math.Abs(mid.Y) > 0.2f && Math.Abs(mid.Y - full.Y) > 0.2f)
            return false;
        return true;
    }

    private static Vector2 VisualPosition(Glide glide, TimeSpan now) {
        var elapsed = (float)(now - glide.StartTime).TotalSeconds;
        var t = glide.Duration <= 0.0001f ? 1f : Math.Clamp(elapsed / glide.Duration, 0f, 1f);
        return t >= 1f ? glide.EndPos : Vector2.Lerp(glide.StartPos, glide.EndPos, t);
    }

    private float CalculateMovementSpeed(int iconSize, float glideSize) {
        if (iconSize < 1)
            iconSize = 1;

        // glide_size 0 is BYOND's automatic value: one eighth of the icon, which is 4px at the default 32.
        if (glideSize == 0)
            glideSize = Math.Max(iconSize / 8f, 1f);

        var tickRate = _gameTiming.TickRate;
        var ticksPerSecond = tickRate > 0 ? tickRate : 20;
        return glideSize / iconSize * ticksPerSecond;
    }
}
