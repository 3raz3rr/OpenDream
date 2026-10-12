using System.Globalization;
using System.Web;
using OpenDreamClient.Interface.Controls.UI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.IoC;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// A screen described by the game. The client only lays the nodes out and reports clicks.
/// </summary>
internal sealed class DocumentView : Control {
    private readonly Action<string> _sendTopic;
    private readonly Action<string>? _runCommand;
    private readonly Dictionary<string, Built> _byId = new();
    private readonly List<LogSlot> _logs = new();
    private string? _pendingText;
    private List<string>? _pendingLines;

    public string? Title { get; private set; }
    public string? CloseHref { get; private set; }
    public float RequestedWidth { get; private set; }
    public float RequestedHeight { get; private set; }

    public DocumentView(Action<string> sendTopic, Action<string>? runCommand = null) {
        _sendTopic = sendTopic;
        _runCommand = runCommand;
        HorizontalExpand = true;
        VerticalExpand = true;
    }

    public void ShowDocument(string text) {
        var pendingText = _pendingText;
        var pendingLines = _pendingLines;
        RemoveAllChildren();
        _byId.Clear();
        _logs.Clear();
        _pendingText = null;
        _pendingLines = null;
        Title = null;
        CloseHref = null;
        RequestedWidth = 0;
        RequestedHeight = 0;

        var roots = NativeDoc.Parse(text);
        if (roots.Count == 1 && roots[0].Type == "screen")
            AddChild(Build(roots[0], verticalParent: true));
        else if (roots.Count > 0) {
            var wrap = new DocNode { Type = "vbox" };
            wrap.Children.AddRange(roots);
            AddChild(Build(wrap, verticalParent: true));
        }

        if (pendingLines != null)
            AppendDecoded(pendingLines, html: true);
        if (pendingText != null)
            SetLogText(pendingText);
    }

    public void AppendLines(string payload) {
        var lines = DecodeLines(payload);
        if (_logs.Count == 0) {
            _pendingLines ??= new List<string>();
            _pendingLines.AddRange(lines);
            return;
        }

        AppendDecoded(lines, _logs[0].Html);
    }

    public void ClearLog() {
        _pendingLines = null;
        _pendingText = null;
        foreach (var log in _logs)
            log.View.Clear();
    }

    public void SetLogText(string payload) {
        var text = HttpUtility.UrlDecode(payload) ?? "";
        if (_logs.Count == 0) {
            _pendingText = payload;
            return;
        }

        var log = _logs[0];
        log.View.Clear();
        foreach (var line in text.Replace("\r", "").Split('\n')) {
            var msg = new FormattedMessage();
            if (log.Html)
                ChatMarkup.Append(msg, line.Length == 0 ? " " : line);
            else {
                msg.PushColor(new Color(0.9f, 0.91f, 0.93f));
                msg.AddText(line.Length == 0 ? " " : line);
                msg.Pop();
            }

            log.View.AddMessage(msg);
        }
    }

    public void Patch(string text) {
        foreach (var line in text.Split('\n')) {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            var attrs = NativeDoc.Attributes(trimmed);
            if (!attrs.TryGetValue("id", out var id) || !_byId.TryGetValue(id, out var built))
                continue;

            built.Apply(attrs);
        }
    }

    private Control Build(DocNode node, bool verticalParent) {
        var view = Create(node);
        ApplyBox(view, node, verticalParent);
        if (node.Attr.TryGetValue("id", out var id) && id.Length > 0)
            _byId[id] = new Built(view, node);

        return view;
    }

    private Control Create(DocNode node) {
        switch (node.Type) {
            case "screen": {
                Title = node.Attr.GetValueOrDefault("title");
                CloseHref = node.Attr.GetValueOrDefault("close");
                if (float.TryParse(node.Attr.GetValueOrDefault("w"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                    RequestedWidth = width;
                if (float.TryParse(node.Attr.GetValueOrDefault("h"), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
                    RequestedHeight = height;

                var panel = new PanelContainer {
                    HorizontalExpand = true,
                    VerticalExpand = true,
                    PanelOverride = new StyleBoxFlat(NativeDoc.ColorOf(node.Attr.GetValueOrDefault("bg"), Color.FromHex("#060608")))
                };
                foreach (var child in node.Children) {
                    var built = Build(child, verticalParent: true);
                    built.HorizontalExpand = true;
                    built.VerticalExpand = true;
                    panel.AddChild(built);
                }
                return panel;
            }
            case "vbox":
            case "hbox": {
                var vertical = node.Type == "vbox";
                var box = new BoxContainer {
                    Orientation = vertical
                        ? BoxContainer.LayoutOrientation.Vertical
                        : BoxContainer.LayoutOrientation.Horizontal,
                    HorizontalExpand = true
                };
                foreach (var child in node.Children)
                    box.AddChild(Build(child, vertical));
                return box;
            }
            case "scroll": {
                var scroll = new ScrollContainer { HScrollEnabled = false, VerticalExpand = true, HorizontalExpand = true };
                var inner = node.Children.Count == 1
                    ? Build(node.Children[0], verticalParent: true)
                    : Build(new DocNode { Type = "vbox", Children = node.Children }, verticalParent: true);
                scroll.AddChild(inner);
                return scroll;
            }
            case "section": {
                var box = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(0, 8, 0, 8) };
                box.AddChild(MakeLabel(node.Attr.GetValueOrDefault("text") ?? "", node, 18, Color.FromHex("#ffffffd0")));
                box.AddChild(new PanelContainer {
                    MinSize = new Vector2(0, 1),
                    Margin = new Thickness(0, 4, 0, 8),
                    PanelOverride = new StyleBoxFlat(new Color(1f, 1f, 1f, 0.12f))
                });
                foreach (var child in node.Children)
                    box.AddChild(Build(child, verticalParent: true));
                return box;
            }
            case "label":
            case "note":
                return MakeLabel(
                    node.Attr.GetValueOrDefault("text") ?? "",
                    node,
                    node.Type == "note" ? 13 : 16,
                    node.Type == "note" ? new Color(1f, 0.75f, 0.4f, 0.8f) : Color.White);
            case "link":
                return MakeLink(node);
            case "button":
                return MakeButton(node);
            case "input":
                return MakeInput(node);
            case "rule": {
                var width = NativeDoc.FloatOf(node, "w", 180);
                return new PanelContainer {
                    MinSize = new Vector2(width, 1),
                    PanelOverride = new StyleBoxFlat(NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), new Color(1f, 1f, 1f, 0.1f)))
                };
            }
            case "bar":
                if (node.Attr.ContainsKey("value") || node.Attr.ContainsKey("max"))
                    return new Meter(node);
                return new PanelContainer {
                    MinSize = new Vector2(NativeDoc.FloatOf(node, "w", 2), 0),
                    VerticalExpand = true,
                    PanelOverride = new StyleBoxFlat(NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), new Color(1f, 1f, 1f, 0.15f)))
                };
            case "spacer":
                return new Control { MouseFilter = Control.MouseFilterMode.Ignore };
            case "stars": {
                var stars = new ParallaxView {
                    HorizontalExpand = true,
                    VerticalExpand = true,
                    MouseFilter = Control.MouseFilterMode.Ignore
                };
                stars.Apply(node);
                return stars;
            }
            case "log":
                return MakeLog(node);
            case "command":
                return MakeCommand(node);
            default:
                return MakeLabel(node.Type, node, 13, new Color(1f, 0.75f, 0.4f, 0.8f));
        }
    }

    private Control MakeLabel(string text, DocNode node, int fallbackSize, Color fallback) {
        var fontKind = node.Attr.GetValueOrDefault("font") ?? "ui";
        var size = NativeDoc.IntOf(node, "size", fallbackSize);
        var font = fontKind == "title" ? NativeFonts.Title(size) : NativeFonts.Ui(size);
        var color = NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), fallback);
        var track = NativeDoc.IntOf(node, "track", 0);
        if (track > 0) {
            return new TrackedLabel {
                Text = text,
                Font = font,
                Color = color,
                Tracking = track
            };
        }

        return new Label {
            Text = text,
            FontOverride = font,
            FontColorOverride = color
        };
    }

    private Control MakeLink(DocNode node) {
        var size = NativeDoc.IntOf(node, "size", 22);
        var link = new LobbyLink(NativeFonts.Ui(size));
        var parts = new List<LinkPart>();
        if (node.Children.Count == 0) {
            parts.Add(new LinkPart(null, node.Attr.GetValueOrDefault("text") ?? "", NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), new Color(1f, 1f, 1f, 0.45f))));
        } else {
            foreach (var child in node.Children) {
                if (child.Type != "part")
                    continue;
                parts.Add(new LinkPart(
                    child.Attr.GetValueOrDefault("id"),
                    child.Attr.GetValueOrDefault("text") ?? "",
                    NativeDoc.ColorOf(child.Attr.GetValueOrDefault("color"), new Color(1f, 1f, 1f, 0.45f))));
            }
        }

        void Refresh() {
            var tuples = new (string Text, Color Color)[parts.Count];
            for (var i = 0; i < parts.Count; i++)
                tuples[i] = (parts[i].Text, parts[i].Color);
            link.SetParts(tuples);
        }

        Refresh();
        var href = node.Attr.GetValueOrDefault("href");
        link.OnPressed += _ => Fire(href);
        foreach (var part in parts) {
            if (string.IsNullOrEmpty(part.Id))
                continue;
            _byId[part.Id] = new Built(link, node, attrs => {
                if (attrs.TryGetValue("text", out var text))
                    part.Text = text;
                if (attrs.TryGetValue("color", out var color))
                    part.Color = NativeDoc.ColorOf(color, part.Color);
                Refresh();
            });
        }

        return link;
    }

    private Control MakeButton(DocNode node) {
        var button = new Button {
            Text = node.Attr.GetValueOrDefault("text") ?? "",
            MinSize = new Vector2(0, 28),
            StyleBoxOverride = new StyleBoxFlat(Color.FromHex("#12141c"))
        };
        button.Label.FontOverride = NativeFonts.Ui(NativeDoc.IntOf(node, "size", 15));
        button.Label.FontColorOverride = NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), new Color(1f, 1f, 1f, 0.82f));
        var href = node.Attr.GetValueOrDefault("href");
        button.OnPressed += _ => Fire(href);
        return button;
    }

    private Control MakeLog(DocNode node) {
        var log = new OutputControl {
            HorizontalExpand = true,
            VerticalExpand = true,
            StyleBoxOverride = new StyleBoxFlat(Color.FromHex("#111318"))
        };
        _logs.Add(new LogSlot(log, node.Attr.GetValueOrDefault("html") == "1", NativeDoc.IntOf(node, "max", 300)));
        return log;
    }

    private Control MakeCommand(DocNode node) {
        var prefixes = new List<(string Text, string Verb)>();
        foreach (var child in node.Children) {
            if (child.Type != "prefix")
                continue;
            prefixes.Add((child.Attr.GetValueOrDefault("text") ?? "", child.Attr.GetValueOrDefault("verb") ?? "say"));
        }

        if (prefixes.Count == 0)
            prefixes.Add(("", "say"));

        prefixes.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));

        var input = new LineEdit {
            HorizontalExpand = true,
            MinSize = new Vector2(0, 28),
            PlaceHolder = node.Attr.GetValueOrDefault("hint")
        };
        input.OnTextEntered += args => {
            var text = args.Text.Replace('"', '\'');
            input.Clear();
            if (string.IsNullOrWhiteSpace(text) || _runCommand == null)
                return;

            foreach (var (prefix, verb) in prefixes) {
                if (prefix.Length == 0 || text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    var rest = prefix.Length == 0 ? text : text[prefix.Length..];
                    _runCommand(verb + " " + rest.TrimStart());
                    return;
                }
            }
        };
        return input;
    }

    private void AppendDecoded(List<string> lines, bool html) {
        if (_logs.Count == 0)
            return;

        var log = _logs[0];
        foreach (var line in lines) {
            if (line.Length == 0)
                continue;

            var msg = new FormattedMessage(2);
            if (html && log.Html)
                ChatMarkup.Append(msg, line);
            else {
                msg.PushColor(new Color(0.9f, 0.91f, 0.93f));
                msg.AddText(line);
                msg.Pop();
            }

            log.View.AddMessage(msg);
            log.View.Trim(log.MaxLines);
        }
    }

    private static List<string> DecodeLines(string payload) {
        var lines = new List<string>();
        foreach (var raw in payload.Split('\n')) {
            if (raw.Length == 0)
                continue;
            lines.Add(HttpUtility.UrlDecode(raw.TrimEnd('\r')) ?? "");
        }

        return lines;
    }

    private Control MakeInput(DocNode node) {
        var input = new LineEdit {
            Text = node.Attr.GetValueOrDefault("value") ?? "",
            MinSize = new Vector2(160, 28),
            HorizontalExpand = true
        };
        if (node.Attr.GetValueOrDefault("tone") == "dark")
            input.AddStyleClass("NativeField");
        var href = node.Attr.GetValueOrDefault("href") ?? "";
        input.OnTextEntered += args => {
            var safe = args.Text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var encoded = HttpUtility.UrlEncode(safe);
            if (href.Contains("{{input}}", StringComparison.Ordinal)) {
                Fire(href.Replace("{{input}}", encoded));
                return;
            }

            var glue = href.Contains('?') ? "&" : "?";
            Fire(href + glue + "value=" + encoded);
        };
        return input;
    }

    private void Fire(string? href) {
        if (string.IsNullOrEmpty(href))
            return;

        _sendTopic(href.StartsWith('?') ? href : "?" + href);
    }

    private static void ApplyBox(Control view, DocNode node, bool verticalParent) {
        if (node.Attr.GetValueOrDefault("grow") == "1") {
            if (verticalParent)
                view.VerticalExpand = true;
            else
                view.HorizontalExpand = true;
        }

        var width = NativeDoc.FloatOf(node, "w", 0);
        var height = NativeDoc.FloatOf(node, "h", 0);
        if (width > 0 || height > 0)
            view.MinSize = new Vector2(width, height);

        if (node.Attr.TryGetValue("pad", out var pad))
            view.Margin = NativeDoc.ThicknessOf(pad);
        else if (node.Attr.TryGetValue("margin", out var margin))
            view.Margin = NativeDoc.ThicknessOf(margin);

        if (node.Attr.GetValueOrDefault("show") is "0" or "")
            view.Visible = false;
    }

    private sealed class Built {
        private readonly Control _view;
        private readonly Action<Dictionary<string, string>>? _custom;

        public Built(Control view, DocNode node, Action<Dictionary<string, string>>? custom = null) {
            _view = view;
            _custom = custom;
            _ = node;
        }

        public void Apply(Dictionary<string, string> attrs) {
            if (attrs.TryGetValue("show", out var show))
                _view.Visible = show is not ("0" or "");

            if (attrs.TryGetValue("text", out var text)) {
                switch (_view) {
                    case TrackedLabel tracked:
                        tracked.Text = text;
                        break;
                    case Label label:
                        label.Text = text;
                        break;
                    case Button button:
                        button.Text = text;
                        break;
                    case LineEdit input:
                        input.Text = text;
                        break;
                }
            }

            if (attrs.TryGetValue("color", out var color)) {
                var parsed = NativeDoc.ColorOf(color, Color.White);
                switch (_view) {
                    case TrackedLabel tracked:
                        tracked.Color = parsed;
                        break;
                    case Label label:
                        label.FontColorOverride = parsed;
                        break;
                    case Button button:
                        button.Label.FontColorOverride = parsed;
                        break;
                }
            }

            if (attrs.TryGetValue("value", out var value) && _view is LineEdit field)
                field.Text = value;

            _custom?.Invoke(attrs);
        }
    }

    private sealed class Meter : Control {
        public Meter(DocNode node) {
            HorizontalExpand = true;
            MinSize = new Vector2(NativeDoc.FloatOf(node, "w", 140), NativeDoc.FloatOf(node, "h", 12));
            MouseFilter = MouseFilterMode.Ignore;
            Value = NativeDoc.FloatOf(node, "value", 0);
            Max = NativeDoc.FloatOf(node, "max", 1);
            if (Max <= 0)
                Max = 1;
            Fill = NativeDoc.ColorOf(node.Attr.GetValueOrDefault("color"), new Color(0.45f, 0.7f, 1f, 0.9f));
        }

        private float Value { get; }
        private float Max { get; }
        private Color Fill { get; }

        protected override void Draw(DrawingHandleScreen handle) {
            var size = PixelSize;
            if (size.X < 2 || size.Y < 2)
                return;

            handle.DrawRect(UIBox2.FromDimensions(Vector2.Zero, size), new Color(1f, 1f, 1f, 0.08f));
            var span = Math.Clamp(Value / Max, 0f, 1f);
            handle.DrawRect(UIBox2.FromDimensions(Vector2.Zero, new Vector2(size.X * span, size.Y)), Fill);
        }
    }

    private sealed class LinkPart {
        public string? Id;
        public string Text;
        public Color Color;

        public LinkPart(string? id, string text, Color color) {
            Id = id;
            Text = text;
            Color = color;
        }
    }
}

internal sealed class DocumentPanel : NativePanel {
    public DocumentView Document { get; }

    public DocumentPanel(string id, Action<string> sendTopic, Action<string>? runCommand = null) : base(id, new DocumentView(sendTopic, runCommand)) {
        Document = (DocumentView)Root;
    }

    public override void ApplyState(string value, string? channel) {
        switch (channel) {
            case "ui":
                Document.ShowDocument(value);
                return;
            case "patch":
                Document.Patch(value);
                return;
            case "line":
                Document.AppendLines(value);
                return;
            case "clear":
                Document.ClearLog();
                return;
            case "text":
                Document.SetLogText(value);
                return;
        }
    }
}

internal sealed class LogSlot {
    public OutputControl View { get; }
    public bool Html { get; }
    public int MaxLines { get; }

    public LogSlot(OutputControl view, bool html, int maxLines) {
        View = view;
        Html = html;
        MaxLines = maxLines;
    }
}

/// <summary>
/// A game interface that is not one of the skin slots. The document says how big it is.
/// </summary>
internal sealed class NativePopup : OSWindow {
    public DocumentView Document { get; }

    public NativePopup(Action<string> sendTopic, Action<string>? runCommand = null) {
        Document = new DocumentView(sendTopic, runCommand) {
            HorizontalExpand = true,
            VerticalExpand = true
        };
        Children.Add(Document);
        Title = "OpenDream";
        SetWidth = 720;
        SetHeight = 640;
    }
}

internal sealed class DocNode {
    public string Type = "";
    public Dictionary<string, string> Attr = new();
    public List<DocNode> Children = new();
}

internal static class NativeDoc {
    public static List<DocNode> Parse(string text) {
        var roots = new List<DocNode>();
        var stack = new List<(int Indent, DocNode Node)>();
        foreach (var raw in text.Split('\n')) {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
                indent++;
            if (indent < raw.Length && raw[indent] == '#')
                continue;

            var depth = indent / 2;
            var body = raw.Trim();
            var node = ParseNode(body);
            while (stack.Count > 0 && stack[^1].Indent >= depth)
                stack.RemoveAt(stack.Count - 1);

            if (stack.Count == 0)
                roots.Add(node);
            else
                stack[^1].Node.Children.Add(node);

            stack.Add((depth, node));
        }

        return roots;
    }

    public static Dictionary<string, string> Attributes(string body) {
        var node = ParseNode("patch " + body);
        return node.Attr;
    }

    public static Color ColorOf(string? text, Color fallback) {
        if (string.IsNullOrEmpty(text))
            return fallback;

        return Color.TryFromHex(text, out var color) ? color : fallback;
    }

    public static int IntOf(DocNode node, string key, int fallback) {
        return int.TryParse(node.Attr.GetValueOrDefault(key), out var value) ? value : fallback;
    }

    public static float FloatOf(DocNode node, string key, float fallback) {
        return float.TryParse(node.Attr.GetValueOrDefault(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    public static Thickness ThicknessOf(string text) {
        var parts = text.Split(',');
        if (parts.Length == 1 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var all))
            return new Thickness(all);
        if (parts.Length == 4
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
            && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottom))
            return new Thickness(left, top, right, bottom);

        return new Thickness(0);
    }

    private static DocNode ParseNode(string body) {
        var tokens = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var node = new DocNode { Type = tokens[0] };
        for (var i = 1; i < tokens.Length; i++) {
            var eq = tokens[i].IndexOf('=');
            if (eq <= 0)
                continue;

            node.Attr[tokens[i][..eq]] = HttpUtility.UrlDecode(tokens[i][(eq + 1)..]);
        }

        return node;
    }
}
