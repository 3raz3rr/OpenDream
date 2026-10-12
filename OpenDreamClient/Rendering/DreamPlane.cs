using OpenDreamShared.Dream;
using Robust.Client.Graphics;
using Robust.Shared.Utility;

namespace OpenDreamClient.Rendering;

internal sealed class DreamPlane(IRenderTexture mainRenderTarget) : IDisposable {
    public IRenderTexture RenderTarget => _temporaryRenderTarget ?? mainRenderTarget;
    public RendererMetaData? Master;
    public bool Enabled = true;

    public readonly List<RendererMetaData> Sprites = new();

    private IRenderTexture? _temporaryRenderTarget;

    public void Clear() {
        Master = null;
        Sprites.Clear();
        _temporaryRenderTarget = null;
    }

    public void Dispose() {
        mainRenderTarget.Dispose();
        Clear();
    }

    /// <summary>
    /// Sets this plane's main render target<br/>
    /// Persists through calls to <see cref="Clear()"/>
    /// </summary>
    public void SetMainRenderTarget(IRenderTexture renderTarget) {
        mainRenderTarget.Dispose();
        mainRenderTarget = renderTarget;
        _cacheValid = false;
    }

    /// <summary>
    /// Sets this plane's render target until the next <see cref="Clear()"/>
    /// </summary>
    public void SetTemporaryRenderTarget(IRenderTexture renderTarget) {
        DebugTools.Assert(_temporaryRenderTarget == null, "Temporary render target has already been set");
        _temporaryRenderTarget = renderTarget;
    }

    private int _contentSignature;
    private bool _cacheValid;

    /// <summary>
    /// Draw the plane, or keep the texture from the previous frame when nothing on it changed.
    /// Planes that sample a render source always redraw: their input is another plane from this frame.
    /// </summary>
    public void DrawCached(DreamViewOverlay overlay, DrawingHandleWorld handle, Box2 worldAABB) {
        if (SamplesRenderSource() || _temporaryRenderTarget != null || HasFrameAnimatedSprite()) {
            _cacheValid = false;
            Draw(overlay, handle, worldAABB);
            return;
        }

        var signature = ContentSignature(overlay, worldAABB);
        if (_cacheValid && signature == _contentSignature)
            return;

        Draw(overlay, handle, worldAABB);
        _contentSignature = signature;
        _cacheValid = true;
    }

    public bool SamplesRenderSource() {
        if (Master?.HasRenderSource == true)
            return true;

        foreach (var sprite in Sprites) {
            if (sprite.HasRenderSource)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Top-most sprite whose icon rectangle contains the world point.
    /// Sprites are stored in draw order, so the walk is back to front.
    /// </summary>
    public RendererMetaData? Pick(Vector2 worldPoint, int iconSize) {
        if (Master?.MouseOpacity == MouseOpacity.Transparent || !Enabled)
            return null;

        for (var i = Sprites.Count - 1; i >= 0; i--) {
            var sprite = Sprites[i];
            if (sprite.MouseOpacity == MouseOpacity.Transparent || sprite.ShouldPassMouse || sprite.IsPlaneMaster)
                continue;

            var sizePx = sprite.MainIcon?.DMI?.IconSize ?? new Vector2i(iconSize, iconSize);
            var size = sizePx / (float)iconSize;
            var origin = sprite.Position + sprite.RenderPosOffset;
            if (sprite.MainIcon != null)
                origin += sprite.MainIcon.TextureRenderOffset / iconSize;

            if (new Box2(origin, origin + size).Contains(worldPoint))
                return sprite;
        }

        return null;
    }

    private int ContentSignature(DreamViewOverlay overlay, Box2 worldAABB) {
        var hash = new HashCode();
        hash.Add(worldAABB.BottomLeft.X);
        hash.Add(worldAABB.BottomLeft.Y);
        hash.Add(overlay.IconSize);
        hash.Add(Enabled);
        hash.Add(Sprites.Count);
        foreach (var sprite in Sprites) {
            hash.Add(sprite.Position.X);
            hash.Add(sprite.Position.Y);
            hash.Add(sprite.ClickUid);
            hash.Add(sprite.TieBreaker);
            hash.Add(sprite.Layer);
            hash.Add(sprite.MainIcon?.Appearance?.GetHashCode() ?? 0);
            hash.Add(sprite.AlphaToApply);
            hash.Add(sprite.ColorToApply);
        }

        return hash.ToHashCode();
    }

    private bool HasFrameAnimatedSprite() {
        foreach (var sprite in Sprites) {
            if (sprite.Flick != null || sprite.Particles != null || IconAnimates(sprite.MainIcon))
                return true;
        }

        return false;
    }

    private static bool IconAnimates(DreamIcon? icon) {
        if (icon?.DMI == null || icon.Appearance == null)
            return false;

        var state = icon.DMI.Description.GetStateOrDefault(icon.Appearance.IconState);
        var frames = state?.GetFrames(icon.Appearance.Direction);
        return frames is { Length: > 1 };
    }

    /// <summary>
    /// Clears this plane's render target, then draws all the plane's icons onto it
    /// </summary>
    public void Draw(DreamViewOverlay overlay, DrawingHandleWorld handle, Box2 worldAABB) {
        // Draw all icons
        handle.RenderInRenderTarget(mainRenderTarget, () => {
            if (!Enabled) // Inside the RenderInRenderTarget() to ensure it gets cleared
                return;

            foreach (var sprite in Sprites) {
                var positionOffset = -worldAABB.BottomLeft;

                if (sprite.HasRenderSource && overlay.RenderSourceLookup.TryGetValue(sprite.RenderSource!, out var renderSourceTexture)) {
                    sprite.TextureOverride = renderSourceTexture.Texture;
                    sprite.PremultipliedTexture = true;
                }

                overlay.DrawIcon(handle, mainRenderTarget.Size, sprite, positionOffset);
            }
        }, new Color());

        if (_temporaryRenderTarget != null) {
            // Draw again, but with the color applied
            handle.RenderInRenderTarget(_temporaryRenderTarget, () => {
                if (Master == null || !Enabled)
                    return;

                Master.TextureOverride = mainRenderTarget.Texture;
                var texture = Master.GetTexture(overlay, handle);
                Master.TextureOverride = null;
                if (texture == null)
                    return;

                handle.UseShader(overlay.GetBlendAndColorShader(Master, useOverlayMode: true));
                handle.SetTransform(DreamViewOverlay.CreateRenderTargetFlipMatrix(_temporaryRenderTarget.Size, Master.MainIcon?.TextureRenderOffset ?? Vector2.Zero));
                handle.DrawTextureRect(texture, new Box2(Vector2.Zero, texture.Size), Master.ColorToApply);
            }, new Color());
        }
    }

    /// <summary>
    /// Draws this plane's mouse map onto the current render target
    /// </summary>
    public void DrawMouseMap(DrawingHandleWorld handle, DreamViewOverlay overlay, Vector2i renderTargetSize, Box2 worldAABB) {
        if (Master?.MouseOpacity == MouseOpacity.Transparent || !Enabled)
            return;

        handle.UseShader(overlay.BlockColorInstance);
        foreach (var sprite in Sprites) {
            if (sprite.MouseOpacity == MouseOpacity.Transparent || sprite.ShouldPassMouse)
                continue;

            var texture = sprite.MainIcon?.LastRenderedTexture;
            if (texture == null)
                continue;

            // For mouse_opacity = 2, discard transparency but keep the size.
            var textureSize = texture.Size;
            if (sprite.MouseOpacity == MouseOpacity.Opaque) {
                texture = Texture.White;
            }

            var posOffset = -worldAABB.BottomLeft + sprite.RenderPosOffset;

            var pos = (sprite.Position + posOffset) * overlay.IconSize;
            if (sprite.MainIcon != null)
                pos += sprite.MainIcon.TextureRenderOffset;

            int hash = sprite.GetHashCode();
            var colorR = (byte)(hash & 0xFF);
            var colorG = (byte)((hash >> 8) & 0xFF);
            var colorB = (byte)((hash >> 16) & 0xFF);
            Color targetColor = new Color(colorR, colorG, colorB); //TODO - this could result in mis-clicks due to hash-collision since we ditch a whole byte.
            overlay.MouseMapLookup[targetColor] = sprite;
            handle.SetTransform(DreamViewOverlay.CalculateDrawingMatrix(sprite.TransformToApply, pos, textureSize, renderTargetSize));
            handle.DrawTextureRect(texture, new Box2(Vector2.Zero, textureSize), targetColor);
        }
    }
}
