using System;
using System.Collections.Generic;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// Station chat colors from the panel stylesheet. A span's class sets the ink, and bold or italic when that class uses them.
/// </summary>
internal static class ChatMarkup {
    private static readonly Color Ink = Color.FromHex("#B3CCE6");
    private static readonly Color Danger = Color.FromHex("#C51E1E");
    private static readonly Color Notice = Color.FromHex("#6685F5");
    private static readonly Color Green = Color.FromHex("#059223");

    private static readonly Dictionary<string, Color> Colors = new(StringComparer.OrdinalIgnoreCase) {
        ["ooc"] = Color.FromHex("#CCA300"),
        ["looc"] = Color.FromHex("#D8B555"),
        ["rlooc"] = Color.FromHex("#B09448"),
        ["adminobserverooc"] = Color.FromHex("#0099CC"),
        ["adminooc"] = Color.FromHex("#3D5BC3"),
        ["adminsay"] = Color.FromHex("#FF4500"),
        ["admin"] = Color.FromHex("#5975DA"),
        ["adminhelp"] = Color.FromHex("#FF0000"),
        ["deadsay"] = Color.FromHex("#E2C1FF"),
        ["binarysay"] = Color.FromHex("#1E90FF"),
        ["radio"] = Color.FromHex("#1ECC43"),
        ["sciradio"] = Color.FromHex("#C68CFA"),
        ["comradio"] = Color.FromHex("#FCDF03"),
        ["secradio"] = Color.FromHex("#DD3535"),
        ["medradio"] = Color.FromHex("#57B8F0"),
        ["engradio"] = Color.FromHex("#F37746"),
        ["suppradio"] = Color.FromHex("#B88646"),
        ["servradio"] = Color.FromHex("#6CA729"),
        ["enteradio"] = Color.FromHex("#79C5A8"),
        ["newscaster"] = Color.FromHex("#79C5A8"),
        ["syndradio"] = Color.FromHex("#8F4A4B"),
        ["gangradio"] = Color.FromHex("#AC2EA1"),
        ["centcomradio"] = Color.FromHex("#2681A5"),
        ["aiprivradio"] = Color.FromHex("#D65D95"),
        ["alert"] = Color.FromHex("#D82020"),
        ["danger"] = Danger,
        ["bolddanger"] = Danger,
        ["userdanger"] = Danger,
        ["warning"] = Danger,
        ["boldwarning"] = Danger,
        ["announce"] = Danger,
        ["boldannounce"] = Danger,
        ["minorannounce"] = Danger,
        ["alertsyndie"] = Color.FromHex("#FF0000"),
        ["minoralert"] = Color.FromHex("#A4BAD6"),
        ["priorityannounce"] = Color.FromHex("#A4BAD6"),
        ["prioritytitle"] = Notice,
        ["notice"] = Notice,
        ["boldnotice"] = Notice,
        ["smallnotice"] = Notice,
        ["tinynotice"] = Notice,
        ["adminnotice"] = Notice,
        ["hear"] = Notice,
        ["info"] = Color.FromHex("#9AB0FF"),
        ["green"] = Green,
        ["nicegreen"] = Green,
        ["boldnicegreen"] = Green,
        ["greenannounce"] = Green,
        ["rose"] = Color.FromHex("#FF5050"),
        ["red"] = Color.FromHex("#FF0000"),
        ["blue"] = Color.FromHex("#215CFF"),
        ["grey"] = Color.FromHex("#838383"),
        ["cult"] = Color.FromHex("#973E3B"),
        ["ghostalert"] = Color.FromHex("#6600FF"),
        ["alien"] = Color.FromHex("#855D85"),
        ["resonate"] = Color.FromHex("#298F85"),
        ["mentor"] = Color.FromHex("#8A2BE2"),
        ["subtle"] = Color.FromHex("#ABC6EC"),
        ["subtler"] = Color.FromHex("#E2C1FF"),
        ["link"] = Color.FromHex("#8EC5FF"),
        ["unconscious"] = Color.FromHex("#A4BAD6")
    };

    private static readonly HashSet<string> Bold = new(StringComparer.OrdinalIgnoreCase) {
        "bold", "name", "prefix", "ooc", "looc", "adminooc", "admin", "adminsay", "adminobserverooc",
        "yell", "userdanger", "bolddanger", "announce", "boldannounce", "minorannounce", "priorityannounce",
        "prioritytitle", "boldnotice", "adminhelp", "greenannounce", "boldnicegreen", "ghostalert", "medal"
    };

    private static readonly HashSet<string> Italic = new(StringComparer.OrdinalIgnoreCase) {
        "italic", "italics", "warning", "boldwarning", "hear", "suicide", "changeling", "ghostalert"
    };

    public static void Append(FormattedMessage message, string html) {
        message.PushColor(Ink);
        var pops = new Stack<int>();
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
            var count = pops.Pop();
            for (var i = 0; i < count; i++)
                message.Pop();
        }

        message.Pop();
    }

    private static void ApplyTag(FormattedMessage message, string tag, Stack<int> pops) {
        var body = tag.Trim();
        if (body.Length == 0)
            return;

        if (body.StartsWith('/')) {
            if (!pops.TryPop(out var count))
                return;
            for (var i = 0; i < count; i++)
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

        var tokens = Classes(body);
        Color? color = name == "a" ? Colors["link"] : null;
        var bold = name is "b" or "strong" or "em";
        var italic = name is "i";
        foreach (var token in tokens) {
            if (color == null && Colors.TryGetValue(token, out var found))
                color = found;
            if (Bold.Contains(token))
                bold = true;
            if (Italic.Contains(token))
                italic = true;
        }

        var pushed = 0;
        if (color != null) {
            message.PushColor(color.Value);
            pushed++;
        }
        if (bold) {
            message.PushTag(new MarkupNode("bold", null, null));
            pushed++;
        }
        if (italic) {
            message.PushTag(new MarkupNode("italic", null, null));
            pushed++;
        }
        pops.Push(pushed);
    }

    private static string[] Classes(string tag) {
        var at = tag.IndexOf("class", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return Array.Empty<string>();

        var quote = tag.IndexOfAny(new[] { '\'', '"' }, at);
        if (quote < 0)
            return Array.Empty<string>();

        var end = tag.IndexOf(tag[quote], quote + 1);
        if (end < 0)
            return Array.Empty<string>();

        return tag[(quote + 1)..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
