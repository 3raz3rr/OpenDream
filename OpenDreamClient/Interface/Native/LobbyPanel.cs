using System.Globalization;
using OpenDreamClient.Resources;
using OpenDreamClient.Resources.ResourceTypes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// A text link. The box stays clear, and the words brighten under the cursor.
/// The vertical margin never changes, so a hover does not shove the next row.
/// </summary>
internal sealed class LobbyLink : ContainerButton {
    private readonly Font _font;
    private readonly BoxContainer _row;
    private readonly List<(Label Label, Color Idle)> _parts = new();
    private static readonly Color Hot = new(1f, 1f, 1f, 0.95f);

    public LobbyLink(Font font) {
        _font = font;
        StyleBoxOverride = new StyleBoxFlat(Color.Transparent);
        _row = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Horizontal };
        AddChild(_row);
    }

    public void SetParts(params (string Text, Color Color)[] parts) {
        _row.RemoveAllChildren();
        _parts.Clear();
        foreach (var (text, color) in parts) {
            var label = new Label {
                Text = text,
                FontOverride = _font,
                FontColorOverride = color
            };
            label.Margin = new Thickness(0, 1, 0, 1);
            _parts.Add((label, color));
            _row.AddChild(label);
        }
    }

    protected override void DrawModeChanged() {
        base.DrawModeChanged();
        var hot = DrawMode is DrawModeEnum.Hover or DrawModeEnum.Pressed;
        var shift = hot ? 12 : 0;
        for (var i = 0; i < _parts.Count; i++) {
            var (label, idle) = _parts[i];
            label.FontColorOverride = hot ? Hot : idle;
            label.Margin = new Thickness(i == 0 ? shift : 0, 1, 0, 1);
        }
    }
}

/// <summary>
/// Layers the game names. Each one is a file from the build, drawn larger than the
/// screen and shifted by the cursor. Nothing here is a picture owned by the client.
/// </summary>
internal sealed class ParallaxView : Control {
    private sealed class Layer {
        public readonly float Opacity;
        public readonly float Shift;
        public Texture? Texture;

        public Layer(float opacity, float shift) {
            Opacity = opacity;
            Shift = shift;
        }
    }

    private readonly IUserInterfaceManager _ui = IoCManager.Resolve<IUserInterfaceManager>();
    private readonly IDreamResourceManager _resources = IoCManager.Resolve<IDreamResourceManager>();
    private readonly List<Layer> _layers = new();
    private Color? _wash;
    private float _washSolid = 0.15f;
    private float _washMid = 0.35f;
    private float _washEnd = 0.6f;
    private float _x;
    private float _y;

    public void Apply(DocNode node) {
        _layers.Clear();
        if (node.Attr.TryGetValue("wash", out var wash))
            _wash = NativeDoc.ColorOf(wash, new Color(0.024f, 0.024f, 0.031f));
        if (node.Attr.TryGetValue("fade", out var fade)) {
            var parts = fade.Split(',');
            if (parts.Length >= 3
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var solid)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mid)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)) {
                _washSolid = solid;
                _washMid = mid;
                _washEnd = end;
            }
        }

        foreach (var child in node.Children) {
            if (child.Type != "layer")
                continue;
            var path = child.Attr.GetValueOrDefault("src");
            if (string.IsNullOrEmpty(path))
                continue;
            var layer = new Layer(
                NativeDoc.FloatOf(child, "opacity", 1f),
                NativeDoc.FloatOf(child, "shift", 0f));
            _layers.Add(layer);
            _resources.LookupResourceAsync(path, id => {
                _resources.LoadResourceAsync<DMIResource>(id, resource => layer.Texture = resource.Texture);
            }, () => { });
        }
    }

    protected override void Draw(DrawingHandleScreen handle) {
        var size = PixelSize;
        if (size.X < 2 || size.Y < 2)
            return;

        var width = (float)size.X;
        var height = (float)size.Y;
        var mouse = _ui.MousePositionScaled.Position - GlobalPosition;
        var targetX = Math.Clamp(mouse.X / width - 0.5f, -0.5f, 0.5f) * 2f;
        var targetY = Math.Clamp(mouse.Y / height - 0.5f, -0.5f, 0.5f) * 2f;
        _x += (targetX - _x) * 0.08f;
        _y += (targetY - _y) * 0.08f;

        foreach (var layer in _layers)
            DrawLayer(handle, layer, width, height);

        if (_wash != null)
            DrawWash(handle, width, height, _wash.Value);
    }

    private void DrawLayer(DrawingHandleScreen handle, Layer layer, float width, float height) {
        var texture = layer.Texture;
        if (texture == null || texture.Size.X < 1 || texture.Size.Y < 1)
            return;

        var left = -100f + _x * layer.Shift;
        var top = -100f + _y * layer.Shift;
        var destW = width + 200f;
        var destH = height + 200f;
        var scale = Math.Max(destW / texture.Size.X, destH / texture.Size.Y);
        var drawW = texture.Size.X * scale;
        var drawH = texture.Size.Y * scale;
        var x = left + (destW - drawW) / 2f;
        var y = top + (destH - drawH) / 2f;
        handle.DrawTextureRect(
            texture,
            new UIBox2(x, y, x + drawW, y + drawH),
            new Color(1f, 1f, 1f, layer.Opacity));
    }

    private void DrawWash(DrawingHandleScreen handle, float width, float height, Color color) {
        if (_washEnd <= 0.001f)
            return;

        const int bands = 24;
        for (var i = 0; i < bands; i++) {
            var t0 = _washEnd * i / bands;
            var t1 = _washEnd * (i + 1) / bands;
            var alpha = WashAlpha((t0 + t1) * 0.5f);
            if (alpha <= 0.001f)
                continue;
            handle.DrawRect(
                UIBox2.FromDimensions(new Vector2(width * t0, 0), new Vector2(width * (t1 - t0), height)),
                new Color(color.R, color.G, color.B, alpha));
        }
    }

    private float WashAlpha(float t) {
        if (t <= _washSolid)
            return 1f;
        if (t <= _washMid) {
            var span = Math.Max(_washMid - _washSolid, 0.001f);
            return 1f - 0.2f * ((t - _washSolid) / span);
        }

        var tail = Math.Max(_washEnd - _washMid, 0.001f);
        return 0.8f * (1f - Math.Clamp((t - _washMid) / tail, 0f, 1f));
    }
}
