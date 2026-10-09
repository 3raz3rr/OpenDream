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

        _transformSystem.OnGlobalMoveEvent += OnTransformMove;
    }

    public override void Shutdown() {
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
        if (_ignoreMoveEvent || e.ParentChanged)
            return;
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

        // OldPosition is where the sprite actually was. Skipping ahead to the previous
        // destination teleports the camera and every moving atom at the start of each step.
        var startingFrom = e.OldPosition.Position;
        var glidingTo = e.NewPosition.Position;
        var distance = (glidingTo - startingFrom).Length();

        // Moving a greater distance than 2 tiles. Don't glide.
        // TODO: Support step_size values (I think that's what decides whether or not to glide?)
        if (distance > 2f) {
            // Stop the current glide if there is one
            if (glide != null)
                _currentGlides.Remove(glide);

            _lastMoveAt.Remove(e.Sender);
            _ignoreMoveEvent = false;
            return;
        }

        var now = _gameTiming.RealTime;
        var speed = CalculateMovementSpeed(_interfaceManager.IconSize, sprite.Icon.Appearance.GlideSize);
        var duration = speed > 0f ? distance / speed : 0f;
        // A step that ends before the next position packet sits still, which reads as a low frame rate.
        // Stretch it to the recent gap, and leave a real pause (standing still) alone.
        if (_lastMoveAt.TryGetValue(e.Sender, out var previous)) {
            var gap = (float)(now - previous).TotalSeconds;
            if (gap > duration && gap < 0.3f)
                duration = gap;
        }

        _lastMoveAt[e.Sender] = now;

        if (glide == null) {
            glide = new(e.Sender, e.Component, sprite);
            _currentGlides.Add(glide);
        }

        // Move the transform to our starting point
        _transformSystem.SetLocalPositionNoLerp(e.Sender, startingFrom, e.Component);

        glide.StartPos = startingFrom;
        glide.EndPos = glidingTo;
        glide.StartTime = now;
        glide.Duration = Math.Max(duration, 0.001f);
        _ignoreMoveEvent = false;
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
