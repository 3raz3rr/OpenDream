using System.Web;
using OpenDreamClient.Interface.Controls.UI;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// The station chat, drawn by the client. Pages filter the same messages the browser used to sort.
/// Speech and emotes go back as commands and topics.
/// </summary>
internal sealed class ChatPanel : NativePanel {
    private const int MaxMessages = 2500;
    private static readonly Color Accent = Color.FromHex("#5EEAD4");

    private readonly Action<string> _runCommand;
    private readonly Action<string> _sendTopic;
    private readonly FlowStrip _tabs = new();
    private readonly FlowStrip _filters = new();
    private readonly FlowStrip _emotes = new();
    private readonly OutputControl _log;
    private readonly List<Page> _pages = new();
    private readonly List<ChatLine> _messages = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<string> _emoteKeys = new();
    private readonly Dictionary<string, Emote> _emoteByKey = new(StringComparer.Ordinal);

    private int _current;
    private bool _filtersOpen;
    private bool _emotesOpen;

    public ChatPanel(string id, Action<string> runCommand, Action<string> sendTopic) : base(id, new Control()) {
        _runCommand = runCommand;
        _sendTopic = sendTopic;
        _pages.Add(Page.MainPage());
        _pages.Add(Page.Filtered("IC", "localchat", "subtle"));
        _pages.Add(Page.Filtered("Radio", "radio", "entertainment"));
        _pages.Add(Page.Filtered("OOC", "ooc", "looc"));
        _pages.Add(Page.Filtered("Dead", "deadchat"));

        var root = (Control)Root;
        root.HorizontalExpand = true;
        root.VerticalExpand = true;

        _log = new OutputControl {
            HorizontalExpand = true,
            VerticalExpand = true,
            StyleBoxOverride = new StyleBoxFlat(Color.FromHex("#202020"))
        };
        var input = new LineEdit {
            HorizontalExpand = true,
            MinSize = new Vector2(0, 22),
            PlaceHolder = "Say, /me, /ooc",
            Margin = new Thickness(4, 2, 4, 4)
        };
        input.AddStyleClass("NativeField");
        input.OnTextEntered += args => Submit(input, args.Text);

        _filters.Margin = new Thickness(4, 0, 4, 2);
        _emotes.Margin = new Thickness(4, 0, 4, 2);
        root.AddChild(new BoxContainer {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            Children = { _tabs, _filters, _emotes, _log, input }
        });
        RebuildTabs();
        RebuildFilters();
        RebuildEmotes();
    }

    public override void ApplyState(string value, string? channel) {
        if (channel != "update" || !TguiPacket.TryParse(value, out var packet))
            return;

        if (packet.Type == "chat/reset") {
            _seen.Clear();
            _messages.Clear();
            foreach (var page in _pages)
                page.Unread = 0;
            _log.Clear();
            RebuildTabs();
            return;
        }

        if (packet.Type == "emotes/setList") {
            ReadEmotes(packet.Payload);
            return;
        }

        if (packet.Type != "chat/message" || !packet.HasElement)
            return;

        if (packet.Payload.ValueKind == JsonNode.Kind.Array) {
            foreach (var entry in packet.Payload.Items)
                Append(entry);
            return;
        }

        Append(packet.Payload);
    }

    private void Append(JsonNode entry) {
        if (entry.ValueKind != JsonNode.Kind.Object)
            return;

        if (entry.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt32(out var number) && !_seen.Add(number))
            return;

        var content = entry;
        if (entry.TryGetProperty("content", out var inner) && inner.ValueKind == JsonNode.Kind.Object)
            content = inner;

        var html = Text(content, "html") ?? Text(content, "text");
        if (string.IsNullOrEmpty(html))
            return;

        var type = Text(content, "type");
        if (string.IsNullOrEmpty(type))
            type = ChatKinds.Classify(html);

        _messages.Add(new ChatLine(html, type));
        if (_messages.Count > MaxMessages) {
            _messages.RemoveAt(0);
        }

        var current = _pages[_current];
        if (current.Accepts(type))
            Paint(html);

        var unread = false;
        for (var i = 0; i < _pages.Count; i++) {
            if (i == _current || !_pages[i].Accepts(type))
                continue;
            _pages[i].Unread++;
            unread = true;
        }

        if (unread)
            Retitle();
    }

    private void Paint(string html) {
        var message = new FormattedMessage();
        ChatMarkup.Append(message, html);
        _log.AddMessage(message);
    }

    private void ShowPage() {
        _log.Clear();
        var page = _pages[_current];
        foreach (var line in _messages) {
            if (page.Accepts(line.Type))
                Paint(line.Html);
        }
    }

    private void Select(int index) {
        if (index < 0 || index >= _pages.Count || index == _current)
            return;
        _current = index;
        _pages[index].Unread = 0;
        if (_pages[index].Main)
            _filtersOpen = false;
        RebuildTabs();
        RebuildFilters();
        ShowPage();
    }

    private void AddPage() {
        var page = Page.Filtered("New tab");
        _pages.Add(page);
        _current = _pages.Count - 1;
        _filtersOpen = true;
        RebuildTabs();
        RebuildFilters();
        ShowPage();
    }

    private void ClosePage() {
        if (_pages[_current].Main || _pages.Count < 2)
            return;
        _pages.RemoveAt(_current);
        _current = 0;
        _filtersOpen = false;
        RebuildTabs();
        RebuildFilters();
        ShowPage();
    }

    private void Retitle() {
        var index = 0;
        foreach (var child in _tabs.Children) {
            if (index >= _pages.Count)
                break;
            if (child is Button button) {
                var page = _pages[index];
                var badge = index == _current || page.Unread <= 0 ? "" : " " + Math.Min(page.Unread, 99);
                button.Text = page.Name + badge;
            }
            index++;
        }
    }

    private void RebuildTabs() {
        _tabs.RemoveAllChildren();
        for (var i = 0; i < _pages.Count; i++) {
            var index = i;
            var page = _pages[i];
            var badge = i == _current || page.Unread <= 0 ? "" : " " + Math.Min(page.Unread, 99);
            _tabs.AddChild(Chip(page.Name + badge, i == _current, () => Select(index)));
        }

        _tabs.AddChild(Chip("+", false, AddPage));
        if (!_pages[_current].Main)
            _tabs.AddChild(Chip("filter", _filtersOpen, ToggleFilters));
        _tabs.AddChild(Chip("*", _emotesOpen, ToggleEmotes));
    }

    private void ToggleFilters() {
        if (_pages[_current].Main)
            return;
        _filtersOpen = !_filtersOpen;
        RebuildTabs();
        RebuildFilters();
    }

    private void ToggleEmotes() {
        _emotesOpen = !_emotesOpen;
        RebuildTabs();
        RebuildEmotes();
    }

    private void RebuildFilters() {
        _filters.RemoveAllChildren();
        var page = _pages[_current];
        _filters.Visible = _filtersOpen && !page.Main;
        if (!_filters.Visible)
            return;

        var name = new LineEdit {
            Text = page.Name,
            MinSize = new Vector2(96, 24),
            Margin = new Thickness(2, 1, 6, 1)
        };
        name.AddStyleClass("NativeField");
        name.OnTextChanged += args => {
            var text = args.Text.Trim();
            if (text.Length == 0)
                return;
            page.Name = text;
            Retitle();
        };
        _filters.AddChild(name);
        _filters.AddChild(Chip("close", false, ClosePage));

        foreach (var kind in ChatKinds.All) {
            if (kind.Important)
                continue;
            var id = kind.Id;
            var on = page.Accepted.TryGetValue(id, out var enabled) && enabled;
            _filters.AddChild(Chip(kind.Name, on, () => {
                page.Accepted[id] = !on;
                RebuildFilters();
                ShowPage();
            }));
        }
    }

    private void ReadEmotes(JsonNode payload) {
        _emoteKeys.Clear();
        _emoteByKey.Clear();
        if (payload.ValueKind != JsonNode.Kind.Object) {
            RebuildEmotes();
            return;
        }

        foreach (var key in payload.Keys) {
            if (!payload.TryGetProperty(key, out var entry))
                continue;
            _emoteKeys.Add(key);
            _emoteByKey[key] = new Emote(Text(entry, "name") ?? key, Text(entry, "color"));
        }

        RebuildEmotes();
    }

    private void RebuildEmotes() {
        _emotes.RemoveAllChildren();
        _emotes.Visible = _emotesOpen;
        if (!_emotesOpen)
            return;

        foreach (var key in _emoteKeys) {
            if (!_emoteByKey.TryGetValue(key, out var emote))
                continue;
            var id = key;
            _emotes.AddChild(Chip(emote.Name, false, () => Send("emotes/execute", "{\"key\":\"" + Escape(id) + "\"}"), emote.Color));
        }

        _emotes.AddChild(Chip("+", false, () => Send("emotes/create", null)));
    }

    private void Send(string type, string? payload) {
        var topic = "?tgui=1&window_id=browseroutput&type=" + HttpUtility.UrlEncode(type);
        if (payload != null)
            topic += "&payload=" + HttpUtility.UrlEncode(payload);
        _sendTopic(topic);
    }

    private static string Escape(string text) {
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string? Text(JsonNode obj, string name) {
        return obj.TryGetProperty(name, out var value) && value.ValueKind == JsonNode.Kind.String
            ? value.GetString()
            : null;
    }

    private void Submit(LineEdit input, string text) {
        input.Clear();
        text = text.Trim();
        if (text.Length == 0)
            return;

        string command;
        if (text.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
            command = "me " + text[4..].Trim();
        else if (text.StartsWith("/ooc ", StringComparison.OrdinalIgnoreCase))
            command = "ooc " + text[5..].Trim();
        else if (text.Equals("/me", StringComparison.OrdinalIgnoreCase) || text.Equals("/ooc", StringComparison.OrdinalIgnoreCase))
            return;
        else
            command = "say " + text;

        _runCommand(command);
    }

    private static Button Chip(string text, bool on, Action click, string? color = null) {
        var button = new Button {
            Text = text,
            MinSize = new Vector2(0, 18),
            Margin = new Thickness(1, 1, 1, 0)
        };
        button.Label.FontOverride = NativeFonts.Ui(11);
        var ink = ColorOf(color) ?? (on ? Color.White : new Color(1f, 1f, 1f, 0.62f));
        button.Label.FontColorOverride = ink;
        button.StyleBoxOverride = new StyleBoxFlat(on ? new Color(0.37f, 0.92f, 0.83f, 0.16f) : Color.Transparent) {
            BorderColor = on ? Accent : Color.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            ContentMarginLeftOverride = 7,
            ContentMarginRightOverride = 7,
            ContentMarginTopOverride = 2,
            ContentMarginBottomOverride = 1
        };
        button.OnPressed += _ => click();
        return button;
    }

    private static Color? ColorOf(string? text) {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var hex = text[0] == '#' ? text[1..] : text;
        if (hex.Length is not (3 or 6 or 8))
            return null;
        foreach (var c in hex) {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return null;
        }
        return Color.FromHex("#" + hex);
    }

    private readonly record struct ChatLine(string Html, string Type);

    private readonly record struct Emote(string Name, string? Color);

    private sealed class Page {
        public bool Main;
        public string Name = "";
        public int Unread;
        public Dictionary<string, bool> Accepted { get; } = new(StringComparer.Ordinal);

        public bool Accepts(string type) {
            if (Main || type.StartsWith("internal", StringComparison.Ordinal))
                return true;
            if (ChatKinds.IsImportant(type))
                return true;
            return Accepted.TryGetValue(type, out var on) && on;
        }

        public static Page MainPage() {
            var page = new Page { Main = true, Name = "Main" };
            foreach (var kind in ChatKinds.All)
                page.Accepted[kind.Id] = true;
            return page;
        }

        public static Page Filtered(string name, params string[] types) {
            var page = new Page { Name = name };
            foreach (var kind in ChatKinds.All)
                page.Accepted[kind.Id] = false;
            foreach (var type in types)
                page.Accepted[type] = true;
            return page;
        }
    }
}

/// <summary>
/// The chat type list from the station chat. A message uses its type, or the first matching span class.
/// </summary>
internal static class ChatKinds {
    internal readonly record struct Kind(string Id, string Name, string? Selector, bool Important);

    internal static readonly Kind[] All = {
        new("system", "System", ".boldannounce", true),
        new("localchat", "Local", ".say, .emote", false),
        new("radio", "Radio", ".alert, .minorannounce, .syndradio, .centcomradio, .aiprivradio, .comradio, .secradio, .gangradio, .engradio, .medradio, .sciradio, .suppradio, .servradio, .radio, .deptradio, .binarysay, .resonate, .abductor, .alien, .changeling", false),
        new("entertainment", "Entertainment", ".enteradio, .newscaster", false),
        new("info", "Info", ".notice:not(.pm), .adminnotice, .info, .sinister, .cult, .infoplain, .announce, .hear, .smallnotice, .holoparasite, .boldnotice", false),
        new("warning", "Warnings", ".warning:not(.pm), .critical, .userdanger, .italics, .alertsyndie, .warningplain", false),
        new("deadchat", "Deadchat", ".deadsay, .ghostalert", false),
        new("ooc", "OOC", ".ooc, .adminooc, .adminobserverooc, .oocplain", false),
        new("looc", "LOOC", ".looc", false),
        new("subtle", "Subtle", ".subtle, .subtler", false),
        new("mentor", "Mentor", ".mentor, .mentornotice", false),
        new("adminpm", "Admin PMs", ".pm, .adminhelp", false),
        new("combat", "Combat", ".danger", false),
        new("unknown", "Unsorted", null, false),
        new("adminchat", "Admin", ".admin_channel, .adminsay", false),
        new("modchat", "Mod", ".mod_channel", false),
        new("prayer", "Prayers", null, false),
        new("adminlog", "Admin Log", ".log_message", false),
        new("attacklog", "Attack Log", null, false),
        new("debug", "Debug", null, false),
        new("rlooc", "Remote LOOC", ".rlooc", false)
    };

    public static bool IsImportant(string type) {
        foreach (var kind in All) {
            if (kind.Id == type)
                return kind.Important;
        }
        return false;
    }

    public static string Classify(string html) {
        foreach (var kind in All) {
            if (kind.Selector != null && Hits(html, kind.Selector))
                return kind.Id;
        }
        return "unknown";
    }

    private static bool Hits(string html, string selector) {
        var start = 0;
        while (start < selector.Length) {
            var comma = selector.IndexOf(',', start);
            var part = (comma < 0 ? selector[start..] : selector[start..comma]).Trim();
            if (Part(html, part))
                return true;
            if (comma < 0)
                break;
            start = comma + 1;
        }
        return false;
    }

    private static bool Part(string html, string part) {
        if (part.Length < 2 || part[0] != '.')
            return false;

        string want;
        string? exclude = null;
        var not = part.IndexOf(":not(.", StringComparison.Ordinal);
        if (not > 0) {
            want = part[1..not];
            var close = part.IndexOf(')', not + 6);
            if (close < 0)
                return false;
            exclude = part[(not + 6)..close];
        } else {
            want = part[1..];
        }

        var scan = 0;
        while (scan < html.Length) {
            var at = html.IndexOf("class", scan, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;
            var eq = html.IndexOf('=', at);
            var quote = html.IndexOfAny(new[] { '\'', '"' }, at);
            if (eq < 0 || quote < 0 || quote > at + 12 || eq > quote) {
                scan = at + 5;
                continue;
            }

            var end = html.IndexOf(html[quote], quote + 1);
            if (end < 0)
                return false;
            var classes = html[(quote + 1)..end];
            if (Token(classes, want) && (exclude == null || !Token(classes, exclude)))
                return true;
            scan = end + 1;
        }

        return false;
    }

    private static bool Token(string classes, string token) {
        var index = 0;
        while (index < classes.Length) {
            while (index < classes.Length && classes[index] == ' ')
                index++;
            var start = index;
            while (index < classes.Length && classes[index] != ' ')
                index++;
            if (index > start && classes.AsSpan(start, index - start).Equals(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
