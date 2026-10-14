using System.Globalization;
using System.Text;
using System.Web;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// A JSON value read without System.Text.Json. That assembly is outside the sandbox.
/// </summary>
internal sealed class JsonNode {
    public enum Kind {
        Undefined,
        Null,
        True,
        False,
        Number,
        String,
        Array,
        Object
    }

    public Kind ValueKind { get; private init; }
    public string Raw { get; private init; } = "";
    public List<JsonNode> Items { get; } = new();
    public List<string> Keys { get; } = new();
    public Dictionary<string, JsonNode> Fields { get; } = new(StringComparer.Ordinal);

    public static JsonNode Undefined { get; } = new() { ValueKind = Kind.Undefined };

    public int GetArrayLength() => Items.Count;

    public JsonNode this[int index] => Items[index];

    public bool TryGetProperty(string name, out JsonNode value) {
        return Fields.TryGetValue(name, out value!);
    }

    public string? GetString() => ValueKind == Kind.String ? Raw : null;

    public string GetRawText() => Raw;

    public double GetDouble() {
        return double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    public bool TryGetInt32(out int number) {
        return int.TryParse(Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
    }

    public static bool TryParse(string text, out JsonNode node) {
        node = Undefined;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var reader = new Reader(text);
        if (!reader.TryValue(out var parsed) || parsed == null)
            return false;

        reader.Skip();
        if (reader.More)
            return false;

        node = parsed;
        return true;
    }

    private sealed class Reader {
        private readonly string _text;
        private int _index;

        public Reader(string text) {
            _text = text;
        }

        public bool More {
            get {
                Skip();
                return _index < _text.Length;
            }
        }

        public bool TryValue(out JsonNode? node) {
            node = null;
            Skip();
            if (_index >= _text.Length)
                return false;

            var mark = _text[_index];
            if (mark == '{')
                return TryObject(out node);
            if (mark == '[')
                return TryArray(out node);
            if (mark == '"')
                return TryString(out node);
            if (mark == 't' || mark == 'f')
                return TryBool(out node);
            if (mark == 'n')
                return TryNull(out node);
            if (mark is '-' or >= '0' and <= '9')
                return TryNumber(out node);
            return false;
        }

        private bool TryObject(out JsonNode? node) {
            node = null;
            _index++;
            var made = new JsonNode { ValueKind = Kind.Object };
            Skip();
            if (Take('}')) {
                node = made;
                return true;
            }

            while (true) {
                Skip();
                if (!TryString(out var key) || key == null)
                    return false;
                Skip();
                if (!Take(':') || !TryValue(out var value) || value == null)
                    return false;
                made.Keys.Add(key.Raw);
                made.Fields[key.Raw] = value;
                Skip();
                if (Take('}')) {
                    node = made;
                    return true;
                }
                if (!Take(','))
                    return false;
            }
        }

        private bool TryArray(out JsonNode? node) {
            node = null;
            _index++;
            var made = new JsonNode { ValueKind = Kind.Array };
            Skip();
            if (Take(']')) {
                node = made;
                return true;
            }

            while (true) {
                if (!TryValue(out var value) || value == null)
                    return false;
                made.Items.Add(value);
                Skip();
                if (Take(']')) {
                    node = made;
                    return true;
                }
                if (!Take(','))
                    return false;
            }
        }

        private bool TryString(out JsonNode? node) {
            node = null;
            if (_index >= _text.Length || _text[_index] != '"')
                return false;

            var built = new StringBuilder();
            for (var i = _index + 1; i < _text.Length; i++) {
                var c = _text[i];
                if (c == '\\' && i + 1 < _text.Length) {
                    var escaped = _text[++i];
                    if (escaped == 'u' && i + 4 < _text.Length && int.TryParse(_text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) {
                        built.Append((char)code);
                        i += 4;
                        continue;
                    }

                    built.Append(escaped switch {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'b' => '\b',
                        'f' => '\f',
                        _ => escaped
                    });
                    continue;
                }

                if (c == '"') {
                    _index = i + 1;
                    node = new JsonNode { ValueKind = Kind.String, Raw = built.ToString() };
                    return true;
                }

                built.Append(c);
            }

            return false;
        }

        private bool TryBool(out JsonNode? node) {
            node = null;
            if (Match("true")) {
                node = new JsonNode { ValueKind = Kind.True, Raw = "true" };
                return true;
            }

            if (Match("false")) {
                node = new JsonNode { ValueKind = Kind.False, Raw = "false" };
                return true;
            }

            return false;
        }

        private bool TryNull(out JsonNode? node) {
            node = null;
            if (!Match("null"))
                return false;

            node = new JsonNode { ValueKind = Kind.Null, Raw = "null" };
            return true;
        }

        private bool TryNumber(out JsonNode? node) {
            node = null;
            var start = _index;
            if (_text[_index] == '-')
                _index++;
            if (_index >= _text.Length || !char.IsDigit(_text[_index]))
                return false;

            while (_index < _text.Length && char.IsDigit(_text[_index]))
                _index++;
            if (_index < _text.Length && _text[_index] == '.') {
                _index++;
                while (_index < _text.Length && char.IsDigit(_text[_index]))
                    _index++;
            }

            if (_index < _text.Length && _text[_index] is 'e' or 'E') {
                _index++;
                if (_index < _text.Length && _text[_index] is '+' or '-')
                    _index++;
                while (_index < _text.Length && char.IsDigit(_text[_index]))
                    _index++;
            }

            node = new JsonNode { ValueKind = Kind.Number, Raw = _text[start.._index] };
            return true;
        }

        private bool Match(string word) {
            if (_index + word.Length > _text.Length || !_text.AsSpan(_index, word.Length).SequenceEqual(word))
                return false;

            _index += word.Length;
            return true;
        }

        private bool Take(char c) {
            if (_index >= _text.Length || _text[_index] != c)
                return false;

            _index++;
            return true;
        }

        public void Skip() {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                _index++;
        }
    }
}

/// <summary>
/// One tgui output() packet: {"type": "...", "payload": ...}, url-encoded on the wire.
/// </summary>
internal readonly struct TguiPacket {
    public string Type { get; }
    public JsonNode Payload { get; }
    public bool HasElement => Payload.ValueKind is not JsonNode.Kind.Undefined and not JsonNode.Kind.Null;

    private TguiPacket(string type, JsonNode payload) {
        Type = type;
        Payload = payload;
    }

    public static bool TryParse(string raw, out TguiPacket packet) {
        packet = default;
        if (string.IsNullOrWhiteSpace(raw) || !JsonNode.TryParse(HttpUtility.UrlDecode(raw), out var outer))
            return false;
        if (!outer.TryGetProperty("type", out var typeNode) || typeNode.GetString() is not { Length: > 0 } type)
            return false;

        if (!outer.TryGetProperty("payload", out var payload)) {
            packet = new TguiPacket(type, JsonNode.Undefined);
            return true;
        }

        if (payload.ValueKind == JsonNode.Kind.String && payload.GetString() is { Length: > 0 } text && JsonNode.TryParse(text, out var inner))
            payload = inner;

        packet = new TguiPacket(type, payload);
        return true;
    }
}
