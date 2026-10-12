using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.IoC;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// Segoe UI and Bahnschrift, the faces the lobby stylesheet asks for.
/// The engine's system-font list loads them; this assembly never opens the font files.
/// </summary>
internal static class NativeFonts {
    public static Font Ui(int size) => Face(size, "Segoe UI", "Noto Sans");

    public static Font Title(int size) => Face(size, "Bahnschrift", "Century Gothic");

    private static Font Face(int size, string family, string alternate) {
        var fonts = IoCManager.Resolve<ISystemFontManager>();
        var face = Find(fonts, family) ?? Find(fonts, alternate);
        if (face != null)
            return face.Load(size);

        var cache = IoCManager.Resolve<IResourceCache>();
        return new VectorFont(cache.GetResource<FontResource>(new ResPath("/Fonts/NotoSans-Regular.ttf")), size);
    }

    private static ISystemFontFace? Find(ISystemFontManager fonts, string family) {
        ISystemFontFace? best = null;
        foreach (var face in fonts.SystemFontFaces) {
            if (!string.Equals(face.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                continue;
            if (face.Slant != FontSlant.Normal)
                continue;
            if (best == null || Distance(face.Weight) < Distance(best.Weight))
                best = face;
        }

        return best;
    }

    private static int Distance(FontWeight weight) => Math.Abs((int)weight - (int)FontWeight.Normal);
}

/// <summary>
/// A line of text with extra space between letters, matching the lobby tracking.
/// </summary>
internal sealed class TrackedLabel : Control {
    private string _text = "";

    public string Text {
        get => _text;
        set {
            _text = value;
            InvalidateMeasure();
        }
    }
    public int Tracking { get; set; }
    public Color Color { get; set; } = Color.White;
    public Font Font { get; set; } = default!;

    protected override Vector2 MeasureOverride(Vector2 availableSize) {
        return new Vector2(WidthOf(Text), Font.GetLineHeight(1));
    }

    protected override void Draw(DrawingHandleScreen handle) {
        var baseline = new Vector2(0, Font.GetAscent(1));
        foreach (var rune in Text.EnumerateRunes()) {
            var advance = Font.DrawChar(handle, rune, baseline, 1, Color);
            baseline.X += advance + Tracking;
        }
    }

    private float WidthOf(string text) {
        var width = 0f;
        var count = 0;
        foreach (var rune in text.EnumerateRunes()) {
            var metrics = Font.GetCharMetrics(rune, 1);
            width += metrics?.Advance ?? 0;
            count++;
        }

        if (count > 1)
            width += Tracking * (count - 1);
        return width;
    }
}
