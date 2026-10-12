using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// The station chat stylesheet, reduced to the colors the log can draw.
/// Span classes such as notice and danger become real text color.
/// </summary>
internal static class ChatMarkup {
    private static readonly Color Ink = new(0.86f, 0.89f, 0.92f);

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase) {
        ["notice"] = Color.FromHex("#8AA4F5"),
        ["boldnotice"] = Color.FromHex("#8AA4F5"),
        ["smallnotice"] = Color.FromHex("#8AA4F5"),
        ["tinynotice"] = Color.FromHex("#8AA4F5"),
        ["info"] = Color.FromHex("#9BB6FF"),
        ["admin"] = Color.FromHex("#6A8FD4"),
        ["danger"] = Color.FromHex("#C4453C"),
        ["bolddanger"] = Color.FromHex("#C4453C"),
        ["userdanger"] = Color.FromHex("#E05048"),
        ["warning"] = Color.FromHex("#C4453C"),
        ["boldwarning"] = Color.FromHex("#C4453C"),
        ["announce"] = Color.FromHex("#C4453C"),
        ["boldannounce"] = Color.FromHex("#C4453C"),
        ["deadsay"] = Color.FromHex("#E4B3FF"),
        ["radio"] = Color.FromHex("#2EBF55"),
        ["ooc"] = Color.FromHex("#CC9900"),
        ["emote"] = Color.FromHex("#C8C8C8"),
        ["green"] = Color.FromHex("#3DCC5C"),
        ["nicegreen"] = Color.FromHex("#3DCC5C"),
        ["rose"] = Color.FromHex("#FF6B8A"),
        ["link"] = Color.FromHex("#8EC5FF"),
    };

    public static void Append(FormattedMessage message, string html) {
        message.PushColor(Ink);
        var pops = new Stack<bool>();
        var index = 0;
        while (index < html.Length) {
            var mark = html.IndexOfAny(new[] { '<', '&' }, index);
            if (mark < 0) {
                message.AddText(html[index..]);
                break;
            }

            if (mark > index)
                message.AddText(html[index..mark]);

            if (html[mark] == '&') {
                var end = html.IndexOf(';', mark);
                if (end < 0 || end - mark > 16) {
                    message.AddText("&");
                    index = mark + 1;
                    continue;
                }

                message.AddText(Entity(html[(mark + 1)..end]));
                index = end + 1;
                continue;
            }

            var close = html.IndexOf('>', mark);
            if (close < 0)
                break;

            ApplyTag(message, html[(mark + 1)..close], pops);
            index = close + 1;
        }

        while (pops.Count > 0) {
            if (pops.Pop())
                message.Pop();
        }

        message.Pop();
    }

    private static void ApplyTag(FormattedMessage message, string tag, Stack<bool> pops) {
        var body = tag.Trim();
        if (body.Length == 0)
            return;

        if (body.StartsWith('/')) {
            if (pops.Count > 0 && pops.Pop())
                message.Pop();
            return;
        }

        var nameEnd = 0;
        while (nameEnd < body.Length && !char.IsWhiteSpace(body[nameEnd]) && body[nameEnd] != '/')
            nameEnd++;
        var name = body[..nameEnd].ToLowerInvariant();
        if (name is "br" or "hr") {
            message.PushNewline();
            return;
        }

        if (name is "img" or "!--")
            return;

        var color = ClassColor(body) ?? (name == "a" ? Colors["link"] : (Color?)null);
        if (color == null) {
            pops.Push(false);
            return;
        }

        message.PushColor(color.Value);
        pops.Push(true);
    }

    private static Color? ClassColor(string tag) {
        var at = tag.IndexOf("class", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;

        var quote = tag.IndexOfAny(new[] { '\'', '"' }, at);
        if (quote < 0)
            return null;

        var end = tag.IndexOf(tag[quote], quote + 1);
        if (end < 0)
            return null;

        foreach (var token in tag[(quote + 1)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
            if (Colors.TryGetValue(token, out var color))
                return color;
        }

        return null;
    }

    private static string Entity(string name) {
        return name switch {
            "nbsp" => "\u00A0",
            "lt" => "<",
            "gt" => ">",
            "amp" => "&",
            "quot" => "\"",
            "apos" => "'",
            _ when name.StartsWith('#') && int.TryParse(name[1..], out var code) => ((char)code).ToString(),
            _ => "&" + name + ";"
        };
    }
}
