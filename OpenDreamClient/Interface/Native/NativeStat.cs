using System.Linq;
using System.Web;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// The station stat panel, drawn by the client from the same messages the page used to receive.
/// A tab click and a verb click are the only things sent back.
/// </summary>
internal sealed class StatPanel : NativePanel {
    private static readonly Color Accent = Color.FromHex("#5EEAD4");
    private static readonly Color Ink = Color.White;
    private static readonly Color LabelInk = Color.FromHex("#8F9396");
    private static readonly Color Good = Color.FromHex("#5BA626");
    private static readonly Color Average = Color.FromHex("#F2760D");
    private static readonly Color Bad = Color.FromHex("#DC2323");
    private static readonly Color Panel = Color.FromHex("#262626");

    private readonly Action<string> _sendTopic;
    private readonly Action<string> _runCommand;
    private readonly FlowStrip _tabs = new();
    private readonly FlowStrip _subtabs = new();
    private readonly BoxContainer _body = new() {
        Orientation = BoxContainer.LayoutOrientation.Vertical,
        HorizontalExpand = true
    };

    private readonly List<string> _tabsNames = new() { "Status" };
    private readonly List<string> _favorites = new();
    private readonly List<(string Category, string Name)> _verbs = new();
    private readonly Dictionary<string, Color> _tabColors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private readonly HashSet<string> _structured = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _perRow = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private readonly Dictionary<string, List<StatLine>> _pages = new();
    private string _selected = "Status";
    private bool _admin;
    private bool _mentor;
    private string? _shuttleMode;
    private string? _shuttleTimer;
    private double? _shuttleRemaining;
    private double? _shuttleElapsed;
    private readonly ShuttleBar _shuttle = new();

    public StatPanel(string id, Action<string> sendTopic, Action<string> runCommand) : base(id, new Control()) {
        _sendTopic = sendTopic;
        _runCommand = runCommand;
        var root = (Control)Root;
        root.HorizontalExpand = true;
        root.VerticalExpand = true;

        var scroll = new ScrollContainer {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
            Children = { _body }
        };
        root.AddChild(new PanelContainer {
            HorizontalExpand = true,
            VerticalExpand = true,
            PanelOverride = new StyleBoxFlat(Panel),
            Children = {
                new BoxContainer {
                    Orientation = BoxContainer.LayoutOrientation.Vertical,
                    HorizontalExpand = true,
                    VerticalExpand = true,
                    Children = { _tabs, _subtabs, scroll }
                }
            }
        });
        _body.Margin = new Thickness(8, 6, 8, 8);
        Rebuild();
    }

    public override void ApplyState(string value, string? channel) {
        if (channel != "update" || !TguiPacket.TryParse(value, out var packet))
            return;

        switch (packet.Type) {
                case "init_verbs":
                    ReadVerbs(packet.Payload);
                    break;
                case "update_stat":
                    _pages["Status"] = ReadStatus(packet.Payload);
                    break;
                case "update_mc":
                    _pages["MC"] = ReadLines(packet.Payload, "mc_data");
                    break;
                case "update_tickets":
                    _pages["Tickets"] = ReadLines(packet.Payload);
                    break;
                case "update_sdql2":
                    _pages["SDQL2"] = ReadLines(packet.Payload);
                    _tabsNames.Add("SDQL2");
                    break;
                case "update_spells":
                    ReadSpells(packet.Payload);
                    break;
                case "update_mentor_tickets":
                    _pages["Mentor"] = ReadLines(packet.Payload);
                    break;
                case "update_favorites":
                    ReadFavorites(packet.Payload);
                    break;
                case "add_admin_tabs":
                    _admin = true;
                    break;
                case "remove_admin_tabs":
                    _admin = false;
                    break;
                case "remove_mentor_tickets":
                    _mentor = false;
                    break;
                default:
                    if (packet.Type.Contains("mentor", StringComparison.OrdinalIgnoreCase))
                        _mentor = true;
                    break;
        }

        Rebuild();
    }

    private void ReadVerbs(JsonNode payload) {
        if (payload.ValueKind != JsonNode.Kind.Object)
            return;

        _verbs.Clear();
        if (payload.TryGetProperty("verblist", out var verbs) && verbs.ValueKind == JsonNode.Kind.Array) {
            foreach (var entry in verbs.Items) {
                if (entry.ValueKind != JsonNode.Kind.Array || entry.GetArrayLength() < 2)
                    continue;
                var category = AsText(entry[0]);
                var name = AsText(entry[1]);
                if (category.Length == 0 || name.Length == 0)
                    continue;
                _verbs.Add((category, name));
            }
        }

        _tabsNames.Clear();
        _tabsNames.Add("Status");
        if (payload.TryGetProperty("panel_tabs", out var tabs) && tabs.ValueKind == JsonNode.Kind.Array) {
            foreach (var tab in tabs.Items) {
                var name = AsText(tab);
                if (name.Length > 0)
                    _tabsNames.Add(name);
            }
        }

        if (payload.TryGetProperty("selected_tab", out var selected)) {
            var name = AsText(selected);
            if (name.Length > 0)
                _selected = name;
        }

        _tabColors.Clear();
        _hidden.Clear();
        _structured.Clear();
        _perRow.Clear();
        _order.Clear();
        if (!payload.TryGetProperty("tab_prefs", out var prefs) || prefs.ValueKind != JsonNode.Kind.Object)
            return;

        if (prefs.TryGetProperty("colors", out var colors) && colors.ValueKind == JsonNode.Kind.Object) {
            foreach (var key in colors.Keys) {
                if (!colors.TryGetProperty(key, out var value))
                    continue;
                if (ParseColor(AsText(value)) is { } color)
                    _tabColors[key == "Favorite" ? "Favorites" : key] = color;
            }
        }

        ReadNames(prefs, "hidden", _hidden);
        ReadNames(prefs, "structured", _structured);
        if (prefs.TryGetProperty("order", out var order) && order.ValueKind == JsonNode.Kind.Array) {
            foreach (var item in order.Items) {
                var name = AsText(item);
                if (name == "Favorite")
                    name = "Favorites";
                if (name.Length > 0)
                    _order.Add(name);
            }
        }

        if (prefs.TryGetProperty("max_buttons_per_row", out var rows) && rows.ValueKind == JsonNode.Kind.Object) {
            foreach (var key in rows.Keys) {
                if (rows.TryGetProperty(key, out var value) && value.TryGetInt32(out var count) && count > 0)
                    _perRow[key] = count;
            }
        }
    }

    private static void ReadNames(JsonNode prefs, string property, ISet<string> into) {
        if (!prefs.TryGetProperty(property, out var list) || list.ValueKind != JsonNode.Kind.Array)
            return;
        foreach (var item in list.Items) {
            var name = AsText(item);
            if (name == "Favorite")
                name = "Favorites";
            if (name.Length > 0)
                into.Add(name);
        }
    }

    private void ReadSpells(JsonNode payload) {
        if (payload.ValueKind != JsonNode.Kind.Object)
            return;

        if (payload.TryGetProperty("spell_tabs", out var tabs) && tabs.ValueKind == JsonNode.Kind.Array) {
            foreach (var tab in tabs.Items) {
                var name = AsText(tab);
                if (name.Length > 0 && !_tabsNames.Contains(name))
                    _tabsNames.Add(name);
            }
        }

        if (!payload.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonNode.Kind.Array)
            return;

        var grouped = new Dictionary<string, List<StatLine>>();
        foreach (var action in actions.Items) {
            if (action.ValueKind != JsonNode.Kind.Array || action.GetArrayLength() < 4)
                continue;
            var tab = AsText(action[0]);
            var status = AsText(action[1]);
            var name = AsText(action[2]);
            var href = AsText(action[3]);
            if (tab.Length == 0)
                continue;
            if (!grouped.TryGetValue(tab, out var lines)) {
                lines = new List<StatLine>();
                grouped[tab] = lines;
            }
            lines.Add(new StatLine(name, status, href.Length == 0 ? null : "?src=" + href + ";statpanel_item_click=left", null));
        }

        foreach (var (tab, lines) in grouped)
            _pages[tab] = lines;
    }

    private void ReadFavorites(JsonNode payload) {
        _favorites.Clear();
        if (payload.ValueKind != JsonNode.Kind.Array)
            return;
        foreach (var item in payload.Items) {
            var name = AsText(item);
            if (name.Length > 0)
                _favorites.Add(name);
        }
    }

    private List<StatLine> ReadStatus(JsonNode payload) {
        var lines = new List<StatLine>();
        _shuttleMode = null;
        if (payload.ValueKind != JsonNode.Kind.Object)
            return lines;

        var raw = new List<StatLine>();
        raw.AddRange(ReadLines(payload, "global_data"));
        raw.AddRange(ReadLines(payload, "other_str"));
        var split = new List<StatLine>(raw.Count);
        foreach (var line in raw)
            split.Add(SplitColon(line));

        var fromInfo = payload.TryGetProperty("status_info", out var info) && info.ValueKind == JsonNode.Kind.Object;
        if (fromInfo)
            lines.AddRange(DashboardFromInfo(info));
        else
            lines.AddRange(DashboardFromLines(split, payload));

        if (_shuttleMode == null)
            TakeShuttleLine(split);

        var extras = new List<StatLine>();
        foreach (var line in split) {
            if (line.Gap || IsKnownStatus(line))
                continue;
            extras.Add(line);
        }

        if (lines.Count > 0 && extras.Count > 0)
            lines.Add(StatLine.Blank);
        lines.AddRange(extras);
        return lines;
    }

    private List<StatLine> DashboardFromInfo(JsonNode info) {
        var lines = new List<StatLine>();
        _shuttleMode = TextOf(info, "shuttle_mode");
        _shuttleTimer = TextOf(info, "shuttle_timer");
        _shuttleRemaining = Number(info, "shuttle_remaining");
        _shuttleElapsed = Number(info, "shuttle_elapsed");
        if (_shuttleMode != null)
            _shuttle.Apply(_shuttleMode, _shuttleTimer, _shuttleRemaining, _shuttleElapsed);

        AddInfo(lines, "Map", TextOf(info, "map"));
        AddInfo(lines, "Next Map", TextOf(info, "next_map"));
        AddInfo(lines, "Station Name", TextOf(info, "station_name"));
        var round = TextOf(info, "round_id");
        if (round != null && !round.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            AddInfo(lines, "Round ID", round);
        var active = TextOf(info, "active");
        var popcap = Number(info, "popcap");
        if (active != null && popcap is > 0)
            active += "/" + TrimNumber(popcap.Value);
        AddInfo(lines, "Connected", Join(
            TextOf(info, "connected"),
            Prefixed("Active", active),
            Prefixed("Observing", TextOf(info, "observing"))));
        AddInfo(lines, "OOC", Flag(info, "ooc"), FlagColor(info, "ooc"));
        lines.Add(StatLine.Blank);
        AddInfo(lines, "Storyteller", TextOf(info, "storyteller"));
        AddInfo(lines, "Station Time", Join(TextOf(info, "station_time"), TextOf(info, "station_date")));
        AddInfo(lines, "Round Time", TextOf(info, "round_time"));
        AddInfo(lines, "Server Time", TextOf(info, "server_time"));
        AddInfo(lines, "Time Dilation", Dilation(info), DilationColor(info));
        AddInfo(lines, "Ping", Ping(info), PingColor(info));
        var reboot = TextOf(info, "reboot");
        AddInfo(lines, "Reboot", reboot, reboot == "DELAYED" ? Bad : Average);
        if (info.TryGetProperty("map_actions", out var actions) && actions.ValueKind == JsonNode.Kind.Array) {
            var acts = new List<(string Text, string Href)>();
            foreach (var action in actions.Items) {
                var label = TextOf(action, "label");
                var href = TextOf(action, "href");
                if (label != null && href != null)
                    acts.Add((MapAction(label), href));
            }
            if (acts.Count > 0) {
                var map = lines.FindIndex(line => line.Text == "Map" && line.Value != null);
                if (map >= 0)
                    lines[map] = lines[map] with { Acts = acts };
                else
                    lines.Add(new StatLine("Map", null, null, acts));
            }
        }
        return lines;
    }

    private List<StatLine> DashboardFromLines(List<StatLine> split, JsonNode payload) {
        var lines = new List<StatLine>();
        AddFound(lines, split, "Map");
        AddFound(lines, split, "Next Map");
        AddFound(lines, split, "Station Name");
        var round = FindValue(split, "Round ID");
        if (round != null && !round.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            AddInfo(lines, "Round ID", round);
        var connected = FindValue(split, "Connected");
        if (connected != null)
            AddInfo(lines, "Connected", FormatConnected(connected));
        var ooc = FindValue(split, "OOC");
        if (ooc != null)
            AddInfo(lines, "OOC", ooc, ooc.Equals("Enabled", StringComparison.OrdinalIgnoreCase) ? Good : ooc.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? Bad : null);
        lines.Add(StatLine.Blank);
        AddInfo(lines, "Storyteller", FindValue(split, "Storyteller") ?? FindValue(split, "Chaos"));
        AddFound(lines, split, "Station Time");
        AddFound(lines, split, "Round Time");
        AddFound(lines, split, "Server Time");
        var dilation = FindValue(split, "Time Dilation");
        if (dilation != null)
            AddInfo(lines, "Time Dilation", FormatDilation(dilation), ToneFromPercent(dilation));
        var ping = FindValue(split, "Ping");
        if (ping == null && TextOf(payload, "ping_str") is { } pingLine) {
            var cut = pingLine.IndexOf(':');
            ping = cut > 0 ? pingLine[(cut + 1)..].Trim() : pingLine;
        }
        if (ping != null)
            AddInfo(lines, "Ping", FormatPing(ping), ToneFromPing(ping));
        return lines;
    }

    private static void AddInfo(List<StatLine> lines, string label, string? value, Color? color = null) {
        if (string.IsNullOrWhiteSpace(value))
            return;
        lines.Add(new StatLine(label, value, null, null, color));
    }

    private static void AddFound(List<StatLine> lines, List<StatLine> split, string label) {
        AddInfo(lines, label, FindValue(split, label));
    }

    private static string? FindValue(List<StatLine> split, string label) {
        foreach (var line in split) {
            if (line.Text.Equals(label, StringComparison.OrdinalIgnoreCase) && line.Value != null)
                return line.Value;
        }
        return null;
    }

    private static StatLine SplitColon(StatLine line) {
        if (line.Gap || line.Value != null)
            return line;
        var text = line.Text.Trim();
        var cut = text.IndexOf(':');
        if (cut <= 0)
            return line with { Text = text };
        var label = text[..cut].Trim();
        var value = text[(cut + 1)..].Trim();
        return label.Length == 0 ? line : line with { Text = label, Value = value.Length == 0 ? null : value };
    }

    private static bool IsKnownStatus(StatLine line) {
        if (line.Gap)
            return true;
        if (string.IsNullOrWhiteSpace(line.Text))
            return true;
        if (line.Text.Equals("Chaos", StringComparison.OrdinalIgnoreCase))
            return true;
        if (KnownStatus.Contains(line.Text))
            return true;
        return IsShuttleText(line.Value == null ? line.Text : line.Text + " " + line.Value);
    }

    private void TakeShuttleLine(List<StatLine> split) {
        foreach (var line in split) {
            var text = line.Value == null ? line.Text : line.Text + " " + line.Value;
            if (!IsShuttleText(text))
                continue;
            var space = text.IndexOf(' ');
            _shuttleMode = space < 0 ? text : text[..space];
            _shuttleTimer = space < 0 ? null : text[(space + 1)..].Trim();
            _shuttleRemaining = null;
            _shuttleElapsed = null;
            _shuttle.Apply(_shuttleMode, _shuttleTimer, null, null);
            return;
        }
    }

    private static bool IsShuttleText(string text) {
        var space = text.IndexOf(' ');
        var mode = space < 0 ? text : text[..space];
        return mode is "IGN" or "RCL" or "ETA" or "ETD" or "ESC" or "ERR" or "RCH" or "LDN" or "DIS";
    }

    private static string FormatConnected(string value) {
        var text = value.Replace("Active:", "Active").Replace("Observing:", "Observing");
        text = text.Replace("/ |", " |").Replace("/|", "|");
        if (text.EndsWith('/'))
            text = text[..^1];
        return text.Trim();
    }

    private static string FormatDilation(string value) {
        var avg = value.IndexOf("AVG:", StringComparison.OrdinalIgnoreCase);
        if (avg < 0)
            return value;
        var head = value[..avg].Trim();
        var rest = value[(avg + 4)..].Trim().Trim('(', ')');
        var parts = rest.Split(',');
        if (parts.Length < 3)
            return head;
        return head + "  (" + parts[0].Replace("%", "").Trim() + " / " + parts[1].Replace("%", "").Trim() + " / " + parts[2].Replace("%", "").Trim() + ")";
    }

    private static string FormatPing(string value) {
        return value.Replace("(Average:", "(avg").Replace("(average:", "(avg");
    }

    private static Color? ToneFromPercent(string value) {
        var end = 0;
        while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.' || value[end] == '-'))
            end++;
        if (end == 0 || !double.TryParse(value[..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return null;
        if (number < 5)
            return Good;
        return number < 15 ? Average : Bad;
    }

    private static Color? ToneFromPing(string value) {
        var end = 0;
        while (end < value.Length && (char.IsDigit(value[end]) || value[end] == '.'))
            end++;
        if (end == 0 || !double.TryParse(value[..end], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return null;
        if (number < 80)
            return Good;
        return number < 150 ? Average : Bad;
    }

    private static string TrimNumber(double value) {
        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly HashSet<string> KnownStatus = new(StringComparer.OrdinalIgnoreCase) {
        "Map", "Next Map", "Station Name", "Round ID", "Connected", "OOC", "Storyteller", "Chaos",
        "Station Time", "Round Time", "Server Time", "Time Dilation", "Ping", "Reboot", "Loading"
    };

    private static string? Join(params string?[] parts) {
        var text = string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return text.Length == 0 ? null : text;
    }

    private static string? Prefixed(string label, string? value) {
        return string.IsNullOrWhiteSpace(value) ? null : label + " " + value;
    }

    private static string? Flag(JsonNode info, string name) {
        if (!info.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch {
            JsonNode.Kind.True => "Enabled",
            JsonNode.Kind.False => "Disabled",
            JsonNode.Kind.Number => value.GetDouble() != 0 ? "Enabled" : "Disabled",
            _ => null
        };
    }

    private static string? Dilation(JsonNode info) {
        var current = TextOf(info, "dilation");
        if (current == null)
            return null;
        return current + "%  (" + (TextOf(info, "dilation_fast") ?? "-") + " / " + (TextOf(info, "dilation_avg") ?? "-") + " / " + (TextOf(info, "dilation_slow") ?? "-") + ")";
    }

    private static string? Ping(JsonNode info) {
        var ping = TextOf(info, "ping");
        if (ping == null)
            return null;
        var average = TextOf(info, "avg_ping");
        return average == null ? ping + "ms" : ping + "ms  (avg " + average + "ms)";
    }

    private static Color? PingColor(JsonNode info) {
        if (!info.TryGetProperty("ping", out var ping) || ping.ValueKind != JsonNode.Kind.Number)
            return null;
        var value = ping.GetDouble();
        if (value < 80)
            return Good;
        return value < 150 ? Average : Bad;
    }

    private static Color? DilationColor(JsonNode info) {
        if (!info.TryGetProperty("dilation", out var node) || node.ValueKind != JsonNode.Kind.Number)
            return null;
        var value = node.GetDouble();
        if (value < 5)
            return Good;
        return value < 15 ? Average : Bad;
    }

    private static Color? FlagColor(JsonNode info, string name) {
        var text = Flag(info, name);
        if (text == "Enabled")
            return Good;
        return text == "Disabled" ? Bad : null;
    }

    private static double? Number(JsonNode info, string name) {
        if (!info.TryGetProperty(name, out var node))
            return null;
        if (node.ValueKind == JsonNode.Kind.Number)
            return node.GetDouble();
        if (node.ValueKind == JsonNode.Kind.String && double.TryParse(node.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string MapAction(string label) {
        var text = label.Trim().Trim('|').Trim().Trim('(', ')').Trim();
        return text.Length == 0 ? "Link" : text;
    }

    private static List<StatLine> ReadLines(JsonNode payload, string? property = null) {
        var lines = new List<StatLine>();
        JsonNode source = payload;
        if (property != null) {
            if (payload.ValueKind != JsonNode.Kind.Object || !payload.TryGetProperty(property, out source))
                return lines;
        }

        if (source.ValueKind != JsonNode.Kind.Array)
            return lines;

        var index = 0;
        var count = source.GetArrayLength();
        while (index < count) {
            var entry = source[index];
            if (IsSameLine(entry)) {
                index++;
                continue;
            }

            var line = LineOf(entry);
            index++;
            List<(string Text, string Href)>? acts = null;
            while (index < count && IsSameLine(source[index])) {
                var act = source[index];
                if (act.GetArrayLength() >= 3) {
                    acts ??= new List<(string, string)>();
                    var href = AsText(act[2]);
                    if (href.Length > 0)
                        acts.Add((AsText(act[1]), href));
                }
                index++;
            }

            if (acts != null)
                line = line with { Acts = acts };
            if (line.Text.Length > 0 || line.Value != null || acts is { Count: > 0 })
                lines.Add(line);
            else
                lines.Add(StatLine.Blank);
        }

        return lines;
    }

    private static bool IsSameLine(JsonNode entry) {
        return entry.ValueKind == JsonNode.Kind.Array && entry.GetArrayLength() > 0 && AsText(entry[0]) == "same_line";
    }

    private static StatLine LineOf(JsonNode entry) {
        if (entry.ValueKind == JsonNode.Kind.String)
            return new StatLine(entry.GetString() ?? "", null, null, null);
        if (entry.ValueKind != JsonNode.Kind.Array || entry.GetArrayLength() == 0)
            return StatLine.Blank;

        var text = AsText(entry[0]);
        string? value = entry.GetArrayLength() >= 2 ? NullIfEmpty(AsText(entry[1])) : null;
        string? href = null;
        if (entry.GetArrayLength() >= 4 && AsText(entry[3]) is { Length: > 0 } fourth)
            href = fourth;
        else if (entry.GetArrayLength() >= 3 && AsText(entry[2]) is { Length: > 0 } third)
            href = third;
        if (href != null && !href.Contains('=') && !href.Contains('?'))
            href = "?src=" + href + ";statpanel_item_click=left";
        return new StatLine(text, value, href, null);
    }

    private void Rebuild() {
        var names = new List<string>(_tabsNames);
        if (!names.Contains("Favorites"))
            names.Insert(Math.Min(1, names.Count), "Favorites");
        if (_admin) {
            names.Add("MC");
            names.Add("Tickets");
        }
        if (_mentor)
            names.Add("Mentor");

        var distinct = new List<string>();
        foreach (var name in names) {
            if (!distinct.Contains(name))
                distinct.Add(name);
        }

        var selectedMain = MainOf(_selected);
        _tabs.RemoveAllChildren();
        foreach (var main in Ordered(Mains(distinct))) {
            if (_hidden.Contains(main))
                continue;
            var captured = main;
            var custom = _tabColors.TryGetValue(main, out var saved);
            var accent = custom ? saved : Accent;
            _tabs.AddChild(TabButton(main, main == selectedMain, accent, custom, () => Select(captured)));
        }

        _subtabs.RemoveAllChildren();
        _subtabs.Visible = false;

        _body.RemoveAllChildren();
        if (_selected == "Favorites") {
            if (_favorites.Count == 0) {
                _body.AddChild(Plain("No favorites yet.", LabelInk));
                return;
            }
            _body.AddChild(VerbGrid(_favorites, "Favorites"));
            return;
        }

        if (_selected == "Status" || _pages.ContainsKey(_selected)) {
            var lines = _pages.GetValueOrDefault(_selected) ?? new List<StatLine>();
            if (_selected == "Status")
                _body.AddChild(Title("Status"));
            if (_selected == "Status" && _shuttleMode != null)
                _body.AddChild(_shuttle);
            if (lines.Count == 0 && _shuttleMode == null)
                _body.AddChild(Plain("Waiting for " + _selected + "...", LabelInk));
            foreach (var line in lines)
                _body.AddChild(LineView(line));
            return;
        }

        var children = distinct.Where(name => MainOf(name) == selectedMain && name != selectedMain).ToList();
        var root = VerbNames(selectedMain);
        if (children.Count > 0) {
            if (root.Count > 0) {
                _body.AddChild(Title("General"));
                _body.AddChild(VerbGrid(root, selectedMain));
            }
            foreach (var child in children) {
                var list = VerbNames(child);
                if (list.Count == 0)
                    continue;
                _body.AddChild(Title(child[(selectedMain.Length + 1)..]));
                _body.AddChild(VerbGrid(list, child));
            }
            return;
        }

        if (root.Count == 0) {
            _body.AddChild(Plain("Nothing in this tab.", LabelInk));
            return;
        }

        _body.AddChild(VerbGrid(root, _selected));
    }

    private List<string> VerbNames(string category) {
        return _verbs.Where(verb => verb.Category == category).Select(verb => verb.Name).ToList();
    }

    private IEnumerable<string> Ordered(IEnumerable<string> mains) {
        var left = mains.ToList();
        foreach (var name in _order) {
            if (!left.Remove(name))
                continue;
            yield return name;
        }
        foreach (var name in left)
            yield return name;
    }

    private void Select(string tab) {
        _selected = tab;
        Rebuild();
        var payload = HttpUtility.UrlEncode("{\"tab\":\"" + tab.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
        _sendTopic("?tgui=1&window_id=statbrowser&type=Set-Tab&payload=" + payload);
    }

    private Control LineView(StatLine line) {
        if (line.Gap)
            return new Control { MinSize = new Vector2(0, 8) };

        var row = new BoxContainer {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Margin = new Thickness(0, 1, 0, 1)
        };
        if (line.Value == null && (line.Acts == null || line.Acts.Count == 0)) {
            row.AddChild(Plain(line.Text, Ink, expand: true));
            return row;
        }

        row.AddChild(Plain(line.Text, LabelInk, expand: false));
        if (line.Value != null)
            row.AddChild(line.Href == null
                ? Plain(line.Value, line.Color ?? Ink, expand: true, align: Label.AlignMode.Right)
                : ActionButton(line.Value, () => Open(line.Href), expand: true));
        else
            row.AddChild(new Control { HorizontalExpand = true });
        if (line.Acts != null) {
            foreach (var act in line.Acts) {
                var href = act.Href;
                row.AddChild(ActionButton(act.Text, () => Open(href), expand: false));
            }
        }
        return row;
    }

    private Control VerbGrid(List<string> verbs, string tab) {
        var columns = ColumnsOf(tab);
        var expand = columns > 0;
        Control host = expand
            ? new GridContainer { Columns = columns, HorizontalExpand = true }
            : new FlowStrip { HorizontalExpand = true };
        foreach (var name in verbs) {
            var command = name;
            var marked = _favorites.Contains(command);
            var row = new BoxContainer {
                Orientation = BoxContainer.LayoutOrientation.Horizontal,
                HorizontalExpand = expand
            };
            var star = new Button {
                Text = marked ? "★" : "☆",
                MinSize = new Vector2(16, 18)
            };
            star.Label.FontOverride = NativeFonts.Ui(10);
            star.Label.FontColorOverride = marked ? Good : LabelInk;
            star.StyleBoxOverride = new StyleBoxFlat(Color.Transparent);
            star.OnPressed += _ => ToggleFavorite(command);
            row.AddChild(star);
            row.AddChild(VerbButton(command, marked, expand, () => _runCommand(command)));
            host.AddChild(row);
        }
        return host;
    }

    private int ColumnsOf(string tab) {
        var main = MainOf(tab);
        if (_perRow.TryGetValue(tab, out var count) || _perRow.TryGetValue(main, out count))
            return count;
        if (_structured.Count == 0 || _structured.Contains(tab) || _structured.Contains(main))
            return 2;
        return 0;
    }

    private void ToggleFavorite(string name) {
        var remove = _favorites.Contains(name);
        if (remove)
            _favorites.Remove(name);
        else
            _favorites.Add(name);
        var type = remove ? "Remove-Favorite" : "Add-Favorite";
        var payload = HttpUtility.UrlEncode("{\"command\":\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
        _sendTopic("?tgui=1&window_id=statbrowser&type=" + type + "&payload=" + payload);
        Rebuild();
    }

    private void Open(string href) {
        _sendTopic(href.StartsWith('?') ? href : "?" + href);
    }

    private static Button TabButton(string text, bool on, Color accent, bool custom, Action click) {
        var line = custom || on;
        var button = new Button {
            Text = text,
            MinSize = new Vector2(0, 18),
            Margin = new Thickness(1, 0, 1, 0)
        };
        button.Label.FontOverride = NativeFonts.Ui(11);
        button.Label.FontColorOverride = Color.White;
        button.StyleBoxOverride = new StyleBoxFlat(on ? new Color(accent.R, accent.G, accent.B, 0.16f) : Color.Transparent) {
            BorderColor = line ? accent : Color.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 2),
            ContentMarginLeftOverride = 6,
            ContentMarginRightOverride = 6,
            ContentMarginTopOverride = 1,
            ContentMarginBottomOverride = 0
        };
        button.OnPressed += _ => click();
        return button;
    }

    private static Button VerbButton(string text, bool marked, bool expand, Action click) {
        var button = new Button {
            Text = text,
            HorizontalExpand = expand,
            MinSize = new Vector2(0, 18),
            Margin = new Thickness(1),
            TextAlign = Label.AlignMode.Left,
            ClipText = true
        };
        button.Label.FontOverride = NativeFonts.Ui(11);
        button.Label.FontColorOverride = Ink;
        button.StyleBoxOverride = new StyleBoxFlat(marked ? new Color(Good.R, Good.G, Good.B, 0.25f) : Color.Transparent) {
            ContentMarginLeftOverride = 4,
            ContentMarginRightOverride = 4,
            ContentMarginTopOverride = 1,
            ContentMarginBottomOverride = 1
        };
        button.OnPressed += _ => click();
        return button;
    }

    private static Button ActionButton(string text, Action click, bool expand) {
        var button = new Button {
            Text = text,
            HorizontalExpand = expand,
            MinSize = new Vector2(0, 20),
            TextAlign = expand ? Label.AlignMode.Left : Label.AlignMode.Right
        };
        button.Label.FontOverride = NativeFonts.Ui(11);
        button.Label.FontColorOverride = new Color(0.55f, 0.78f, 1f);
        button.StyleBoxOverride = new StyleBoxFlat(Color.Transparent);
        button.OnPressed += _ => click();
        return button;
    }

    private static Label Title(string text) {
        return new Label {
            Text = text,
            FontOverride = NativeFonts.Ui(12),
            FontColorOverride = Color.White,
            Margin = new Thickness(0, 2, 0, 6)
        };
    }

    private static Label Plain(string text, Color color, bool expand = false, Label.AlignMode align = Label.AlignMode.Left) {
        return new Label {
            Text = text,
            FontOverride = NativeFonts.Ui(11),
            FontColorOverride = color,
            HorizontalExpand = expand,
            Align = align
        };
    }

    private static Color? ParseColor(string text) {
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

    private static string AsText(JsonNode value) {
        return value.ValueKind switch {
            JsonNode.Kind.String => value.GetString() ?? "",
            JsonNode.Kind.Number => value.GetRawText(),
            JsonNode.Kind.True => "1",
            JsonNode.Kind.False => "0",
            _ => ""
        };
    }

    private static string? TextOf(JsonNode obj, string name) {
        if (obj.ValueKind != JsonNode.Kind.Object || !obj.TryGetProperty(name, out var value))
            return null;
        var text = AsText(value);
        return text.Length == 0 ? null : text;
    }

    private static string? NullIfEmpty(string text) {
        return text.Length == 0 ? null : text;
    }

    private static string MainOf(string tab) {
        var dot = tab.IndexOf('.');
        return dot < 0 ? tab : tab[..dot];
    }

    private static IEnumerable<string> Mains(List<string> tabs) {
        var seen = new HashSet<string>();
        foreach (var tab in tabs) {
            var main = MainOf(tab);
            if (seen.Add(main))
                yield return main;
        }
    }

    private static string? FirstChild(List<string> tabs, string main) {
        foreach (var tab in tabs) {
            if (MainOf(tab) == main && tab != main)
                return tab;
        }
        return null;
    }

    private readonly record struct StatLine(string Text, string? Value, string? Href, List<(string Text, string Href)>? Acts, Color? Color = null, bool Gap = false) {
        public static StatLine Blank { get; } = new("", null, null, null, null, true);
    }
}

/// <summary>
/// Wraps children onto new rows using the width it is actually given.
/// </summary>
internal sealed class FlowStrip : Container {
    public int Gap { get; set; } = 2;

    protected override Vector2 MeasureOverride(Vector2 available) {
        var width = float.IsFinite(available.X) && available.X > 1 ? available.X : 240;
        return Place(width, apply: false);
    }

    protected override Vector2 ArrangeOverride(Vector2 finalSize) {
        Place(finalSize.X, apply: true);
        return finalSize;
    }

    private Vector2 Place(float width, bool apply) {
        var x = 0f;
        var y = 0f;
        var rowHeight = 0f;
        foreach (var child in Children) {
            var size = child.DesiredSize;
            if (size.X < 1 || size.Y < 1) {
                child.Measure(new Vector2(float.PositiveInfinity, float.PositiveInfinity));
                size = child.DesiredSize;
            }
            if (x > 0 && x + size.X > width) {
                y += rowHeight + Gap;
                x = 0;
                rowHeight = 0;
            }

            if (apply)
                child.Arrange(new UIBox2(x, y, x + size.X, y + size.Y));
            x += size.X + Gap;
            rowHeight = Math.Max(rowHeight, size.Y);
        }

        return new Vector2(width, y + rowHeight);
    }
}

/// <summary>
/// The emergency shuttle leg: route, countdown, and a marker that moves between server updates.
/// </summary>
internal sealed class ShuttleBar : BoxContainer {
    private static readonly Color Good = Color.FromHex("#5BA626");
    private static readonly Color Average = Color.FromHex("#F2760D");
    private static readonly Color Bad = Color.FromHex("#DC2323");

    private readonly Label _route;
    private readonly Label _time;
    private readonly ShuttleRail _rail = new();
    private bool _live;
    private double _elapsed;
    private double _remaining;
    private double _passed;

    public ShuttleBar() {
        Orientation = BoxContainer.LayoutOrientation.Vertical;
        HorizontalExpand = true;
        Margin = new Thickness(0, 0, 0, 8);
        _route = new Label {
            FontOverride = NativeFonts.Ui(11),
            FontColorOverride = Color.White,
            HorizontalExpand = true
        };
        _time = new Label {
            FontOverride = NativeFonts.Ui(11),
            MinSize = new Vector2(48, 0)
        };
        AddChild(new BoxContainer {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            Children = { _route, _time }
        });
        AddChild(_rail);
    }

    public void Apply(string mode, string? timer, double? remaining, double? elapsed) {
        var tone = Tone(mode);
        var ends = Ends(mode);
        _route.Text = ends.From + " → " + ends.To + "   " + Phase(mode);
        _live = mode != "ERR" && mode != "DIS" && remaining != null && elapsed != null;
        _elapsed = elapsed ?? 0;
        _remaining = remaining ?? 0;
        _passed = 0;
        _time.FontColorOverride = tone;
        _time.Text = _live ? Clock(_remaining) : timer ?? "";
        _rail.Visible = _live;
        _rail.Tone = tone;
        var total = _elapsed + _remaining;
        _rail.Progress = _live && total > 0 ? (float)Math.Min(1, _elapsed / total) : 0;
    }

    protected override void FrameUpdate(FrameEventArgs args) {
        base.FrameUpdate(args);
        if (!_live)
            return;

        _passed += args.DeltaSeconds;
        var elapsed = Math.Max(0, _elapsed + _passed);
        var remaining = Math.Max(0, _remaining - _passed);
        var total = elapsed + remaining;
        _rail.Progress = total > 0 ? (float)Math.Min(1, elapsed / total) : 0;
        var clock = Clock(remaining);
        if (_time.Text != clock)
            _time.Text = clock;
    }

    private static string Clock(double seconds) {
        if (seconds > 3600)
            return "--:--";
        var total = Math.Max(0, (int)Math.Ceiling(seconds));
        return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
    }

    private static string Phase(string mode) {
        return mode switch {
            "IGN" => "Igniting",
            "RCL" => "Recalling",
            "ETA" => "Arriving",
            "ETD" => "Departing",
            "ESC" => "Escape",
            "ERR" => "Stranded",
            "RCH" => "Recharging",
            "LDN" => "Landing",
            "DIS" => "Disabled",
            _ => mode
        };
    }

    private static (string From, string To) Ends(string mode) {
        return mode switch {
            "ETA" or "LDN" => ("CentCom", "Station"),
            "RCL" or "ESC" => ("Station", "CentCom"),
            "ETD" or "IGN" => ("Station", "Launch"),
            "RCH" => ("Dock", "Ready"),
            _ => ("Start", "End")
        };
    }

    private static Color Tone(string mode) {
        if (mode is "ESC" or "ERR" or "DIS")
            return Bad;
        if (mode is "ETA" or "RCL" or "LDN" or "RCH")
            return Good;
        return Average;
    }
}

internal sealed class ShuttleRail : Control {
    private StyleBoxFlat _fill = new(Color.White);
    private readonly StyleBoxFlat _track = new(new Color(1f, 1f, 1f, 0.14f));

    public float Progress { get; set; }

    public Color Tone {
        set => _fill = new StyleBoxFlat(value);
    }

    public ShuttleRail() {
        HorizontalExpand = true;
        MinSize = new Vector2(0, 14);
    }

    protected override Vector2 MeasureOverride(Vector2 available) {
        return new Vector2(float.IsFinite(available.X) ? available.X : 10, 14);
    }

    protected override void Draw(DrawingHandleScreen handle) {
        var scale = Math.Max(1f, UIScale);
        var width = Math.Max(1, PixelSizeBox.Right);
        var top = (int)(5 * scale);
        var thick = Math.Max(2, (int)(3 * scale));
        var rail = new UIBox2i(0, top, width, top + thick);
        _track.Draw(handle, rail, UIScale);
        var wake = (int)(width * Math.Clamp(Progress, 0f, 1f));
        if (wake > 0)
            _fill.Draw(handle, new UIBox2i(0, top, wake, top + thick), UIScale);
        var ship = Math.Max(8, (int)(16 * scale));
        var left = Math.Clamp(wake - ship / 2, 0, Math.Max(0, width - ship));
        _fill.Draw(handle, new UIBox2i(left, top - (int)(2 * scale), left + ship, top + thick + (int)(2 * scale)), UIScale);
    }
}
