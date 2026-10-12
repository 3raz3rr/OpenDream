using OpenDreamClient.Interface;
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

        TickPrediction(now, frameTime);
    }

    /// <summary>
    /// Disables RT lerping and sets up the entity's glide
    /// </summary>
    private void OnTransformMove(ref MoveEvent e) {
        if (_ignoreMoveEvent)
            return;
        if (e.ParentChanged) {
            if (IsLocal(e.Sender)) {
                _confirmed = e.NewPosition.Position;
                _visual = _confirmed;
                _hasConfirmed = true;
                _ownVisual = false;
                _steering = false;
                _blocked = false;
                var riding = FindGlide(e.Sender);
                if (riding != null)
                    _currentGlides.Remove(riding);
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
        var walkedFrom = _hasConfirmed && IsLocal(e.Sender) ? _confirmed : glidingTo;
        NoteServerMove(e.Sender, glidingTo, now);

        Vector2? shovedFrom = null;
        if (IsLocal(e.Sender) && _ownVisual) {
            if ((glidingTo - _visual).Length() > 2f) {
                _ownVisual = false;
                _steering = false;
                _blocked = false;
                _visual = glidingTo;
                if (glide != null)
                    _currentGlides.Remove(glide);
                _ignoreMoveEvent = false;
                return;
            }

            var walk = OnWalk(walkedFrom, glidingTo);
            var behind = _moveDir != Vector2.Zero && Vector2.Dot(glidingTo - _visual, _moveDir) <= 0.05f;
            if (walk || behind) {
                if (_blocked && (glidingTo - walkedFrom).LengthSquared() > 0.25f)
                    _blocked = false;
                if (glide != null)
                    _currentGlides.Remove(glide);
                _transformSystem.SetLocalPositionNoLerp(e.Sender, _visual, e.Component);
                _ignoreMoveEvent = false;
                return;
            }

            shovedFrom = _visual;
            _ownVisual = false;
            _steering = false;
            _blocked = false;
            if (glide != null) {
                _currentGlides.Remove(glide);
                glide = null;
            }
        }

        // Where the sprite is, not the last authoritative tile. Restarting from that tile
        // yanks the mob backward at the start of every step.
        Vector2 startingFrom;
        var sameBurst = false;
        if (shovedFrom != null) {
            startingFrom = shovedFrom.Value;
        } else if (glide != null) {
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
            if (IsLocal(e.Sender)) {
                _ownVisual = false;
                _steering = false;
                _blocked = false;
                _visual = glidingTo;
            }
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
                duration = remaining + (glidingTo - glide.EndPos).Length() / speed;
            else
                duration = speed > 0f ? distance / speed : 0f;
        } else {
            duration = speed > 0f ? distance / speed : 0f;
        }

        // A step that finishes before the next one sits, then jerks. Stretch across that
        // gap when it is still a walk. A real pause is longer than this and stays a pause.
        // The local mob keeps the step period instead: stretching it makes every tile late.
        if (!IsLocal(e.Sender) && !sameBurst && _lastMoveAt.TryGetValue(e.Sender, out var previous)) {
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
