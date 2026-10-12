using System.Text;
using System.Web;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// Reads the small strings the server sends. The sandbox does not allow a JSON parser.
/// </summary>
internal static class NativePayload {
    public static Dictionary<string, string> ReadParams(string value) {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
            return result;

        foreach (var part in value.Split('&')) {
            if (part.Length == 0)
                continue;

            var eq = part.IndexOf('=');
            if (eq < 0) {
                result[HttpUtility.UrlDecode(part)] = "";
                continue;
            }

            result[HttpUtility.UrlDecode(part[..eq])] = HttpUtility.UrlDecode(part[(eq + 1)..]);
        }

        return result;
    }

    public static string DecodeMessage(string value) {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{'))
            return trimmed;

        return HttpUtility.UrlDecode(value);
    }

    public static bool TryReadJsonString(string json, string key, out string? value) {
        value = null;
        var keyAt = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (keyAt < 0)
            return false;

        return TryReadJsonStringAt(json, keyAt, out value);
    }

    public static bool TryReadJsonStringAt(string json, int keyAt, out string? value) {
        value = null;
        var colon = json.IndexOf(':', keyAt);
        if (colon < 0)
            return false;

        var quote = colon + 1;
        while (quote < json.Length && char.IsWhiteSpace(json[quote]))
            quote++;
        if (quote >= json.Length || json[quote] != '"')
            return false;

        return TryReadString(json, quote, out value, out _);
    }

    public static void AppendStatValues(string json, string key, StringBuilder text) {
        var keyAt = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (keyAt < 0)
            return;

        var colon = json.IndexOf(':', keyAt + key.Length + 2);
        if (colon < 0)
            return;

        var depth = 0;
        var end = json.Length;
        for (var i = colon + 1; i < json.Length; i++) {
            if (json[i] == '{' || json[i] == '[')
                depth++;
            else if (json[i] == '}' || json[i] == ']') {
                depth--;
                if (depth == 0) {
                    end = i + 1;
                    break;
                }
            }
        }

        var region = json[colon..end];
        var index = 0;
        while (index < region.Length) {
            var quote = region.IndexOf('"', index);
            if (quote < 0)
                return;

            if (!TryReadString(region, quote, out var statValue, out var next) || statValue == null) {
                index = quote + 1;
                continue;
            }

            index = next;
            var after = index;
            while (after < region.Length && char.IsWhiteSpace(region[after]))
                after++;
            if (after < region.Length && region[after] == ':')
                continue;
            if (statValue.Length == 0 || statValue == "same_line")
                continue;

            text.Append(statValue);
            text.Append('\n');
        }
    }

    private static bool TryReadString(string json, int quote, out string? value, out int next) {
        value = null;
        next = quote + 1;
        var built = new StringBuilder();
        for (var i = quote + 1; i < json.Length; i++) {
            var c = json[i];
            if (c == '\\' && i + 1 < json.Length) {
                var escaped = json[++i];
                built.Append(escaped switch {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' when i + 4 < json.Length && int.TryParse(json.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code)
                        => (char)code,
                    _ => escaped
                });
                if (escaped == 'u')
                    i += 4;
                continue;
            }

            if (c == '"') {
                value = built.ToString();
                next = i + 1;
                return true;
            }

            built.Append(c);
        }

        return false;
    }
}
