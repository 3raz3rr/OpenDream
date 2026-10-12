using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using OpenDreamClient.Interface.Native;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamShared.Interface.DMF;
using OpenDreamClient.Resources;
using OpenDreamShared.Network.Messages;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.WebView;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Controls;

internal sealed partial class ControlBrowser : InterfaceControl {
    private static readonly Dictionary<string, string> FileExtensionMimeTypes = new() {
        { "css", "text/css" },
        { "html", "text/html" },
        { "htm", "text/html" },
        { "png", "image/png" },
        { "svg", "image/svg+xml" },
        { "jpeg", "image/jpeg" },
        { "jpg", "image/jpeg" },
        { "js", "application/javascript" },
        { "json", "application/json" },
        { "ttf", "font/ttf" },
        { "txt", "text/plain" }
    };

    [Dependency] private IResourceManager _resourceManager = default!;
    [Dependency] private IClientNetManager _netManager = default!;
    [Dependency] private IDreamResourceManager _dreamResource = default!;

    private readonly ISawmill _sawmill = Logger.GetSawmill("opendream.browser");

    private PanelContainer _panel = default!;
    private WebViewControl? _webView;
    private string? _pendingUrl;
    private readonly List<string> _pendingScripts = new();
    private readonly ConcurrentQueue<string> _pendingTopics = new();
    private bool _pageReady;
    private bool _sawLoading;
    private int _settleFrames;
    private string? _navigatedUrl;
    private NativePanel? _screen;

    /// <summary>
    /// Skin browsers the client draws itself. These never start a WebView.
    /// </summary>
    public bool IsNativeScreen => NativeInterfaceHost.IsSlot(Id.Value, Window?.Id.Value);

    public ControlBrowser(ControlDescriptor controlDescriptor, ControlWindow window)
        : base(controlDescriptor, window) {
        IoCManager.InjectDependencies(this);
    }

    protected override Control CreateUIElement() {
        // CEF times out if a WebView is created before its OS window exists, and the
        // skin has several browsers that are never shown. Create the view on first use.
        _panel = new PanelContainer();

        if (InterfaceManager is DreamInterfaceManager manager && manager.NativeHost != null) {
            _screen = manager.NativeHost.Mount(Id.Value, Window?.Id.Value);
            if (_screen != null) {
                _screen.Root.HorizontalExpand = true;
                _screen.Root.VerticalExpand = true;
                _panel.AddChild(_screen.Root);
            }
        }

        if (ControlDescriptor.IsVisible.Value)
            OnShowEvent();
        else
            OnHideEvent();

        return _panel;
    }

    private WebViewControl EnsureWebView() {
        if (IsNativeScreen)
            throw new InvalidOperationException($"Screen {Id.Value} is drawn by the client.");

        if (_webView != null)
            return _webView;

        var webView = new WebViewControl {
            HorizontalExpand = true,
            VerticalExpand = true
        };
        webView.AddResourceRequestHandler(RequestHandler);
        webView.AddBeforeBrowseHandler(BeforeBrowseHandler);
        _panel.AddChild(webView);
        _webView = webView;
        return webView;
    }

    protected override void UpdateElementDescriptor() {
        base.UpdateElementDescriptor();

        // A forced white panel is what showed up as the blank chat. Use the skin color, or stay clear.
        _panel.PanelOverride = ControlDescriptor.BackgroundColor.Value != Color.Transparent
            ? new StyleBoxFlat(ControlDescriptor.BackgroundColor.Value)
            : null;

        if (ControlDescriptor.IsVisible.Value && !IsNativeScreen)
            ActivatePending();

        _screen?.SetVisible(ControlDescriptor.IsVisible.Value);
    }

    public override void SetProperty(string property, string value, bool manualWinset = false) {
        switch (property) {
            case "focus":
                if (value is "true" or "1") {
                    ActivatePending();
                    _webView?.GrabKeyboardFocus();
                }

                return;
        }

        base.SetProperty(property, value, manualWinset);
    }

    public override void Output(string value, string? jsFunction) {
        if (jsFunction == null)
            return;

        if (_screen != null) {
            _screen.ApplyState(value, jsFunction);
            return;
        }

        if (IsNativeScreen)
            return;

        // Prepare the argument to be used in JS
        //output is formatted by list2params sometimes, which means raw strings are url encoded, but the message contains & chars which are not encoded that are the params
        //so we split on &, url decode the parts, and then join them back together with , as the separator for the JS params
        var parts = value.Split('&');
        for (var i = 0; i < parts.Length; i++) {
            parts[i] = "\""+HttpUtility.JavaScriptStringEncode(HttpUtility.UrlDecode(parts[i]))+"\""; //wrap in quotes and encode for JS
        }

        var script = $"{jsFunction}({string.Join(",", parts)})";
        // Hidden browsers must not start CEF. A visible page still has to finish loading
        // before window.update exists, or the payload is thrown away.
        if (!HostIsReady() || !_pageReady) {
            _pendingScripts.Add(script);
            return;
        }

        RunScript(script);
    }

    /// <summary>
    /// Called every client frame. Delivers output() once the browse page has loaded.
    /// </summary>
    public void PumpScripts() {
        while (_pendingTopics.TryDequeue(out var topic))
            DispatchByondUrl(topic);

        if (!UpdatePageReady() || _pendingScripts.Count == 0)
            return;

        var queued = _pendingScripts.ToArray();
        _pendingScripts.Clear();
        foreach (var script in queued)
            RunScript(script);
    }

    /// <summary>
    /// The window that owns this browser just became visible. Load the page and deliver queued output().
    /// </summary>
    public void ActivatePending() {
        if (!HostIsReady())
            return;

        if (_pendingUrl != null)
            Navigate(_pendingUrl);
    }

    private void RunScript(string script) {
        if (IsNativeScreen)
            return;
        try {
            EnsureWebView().ExecuteJavaScript(script);
        } catch (Exception e) {
            _pendingScripts.Add(script);
            _sawmill.Warning($"Browser script deferred ({Id.Value}): {e.Message}");
        }
    }

    public void SetFileSource(ResPath? filepath) {
        if (filepath == null) {
            _pendingUrl = null;
            _navigatedUrl = null;
            _pageReady = false;
            _sawLoading = false;
            _settleFrames = 0;
            if (_webView != null)
                _webView.Url = "about:blank";
            return;
        }

        if (IsNativeScreen)
            return;

        // hostname must be the localhost IP for TGUI to work properly.
        // Hidden controls (the asset cache especially) get a browse() per file. Starting CEF
        // for each one stalls the client; the bytes are already on disk from browse_rsc.
        _pendingUrl = "http://127.0.0.1/" + filepath;
        if (!HostIsReady())
            return;

        Navigate(_pendingUrl);
    }

    /// <summary>
    /// A browser control can be marked visible while its popup window is still closed.
    /// Starting CEF then times out, because there is no OS window yet.
    /// </summary>
    private bool HostIsReady() {
        if (IsNativeScreen)
            return false;

        if (!ControlDescriptor.IsVisible.Value)
            return false;

        if (Window != null && !Window.WindowDescriptor.IsPane.Value && !Window.WindowDescriptor.IsVisible.Value)
            return false;

        return true;
    }

    private void Navigate(string url) {
        if (IsNativeScreen)
            return;
        var webView = EnsureWebView();
        var samePage = string.Equals(_navigatedUrl, url, StringComparison.OrdinalIgnoreCase);
        if (samePage && (_pageReady || _sawLoading || _settleFrames > 0 || webView.IsLoading))
            return;

        _navigatedUrl = url;
        _pageReady = false;
        _sawLoading = false;
        _settleFrames = 0;
        webView.Url = url;
    }

    private bool UpdatePageReady() {
        if (_webView == null || _pendingUrl == null) {
            _pageReady = false;
            return false;
        }

        if (_pageReady)
            return true;

        if (_webView.IsLoading) {
            _sawLoading = true;
            return false;
        }

        if (!_sawLoading) {
            // LoadUrl is async. A cached page may never report IsLoading, so settle for a few frames.
            _settleFrames++;
            if (_settleFrames < 3)
                return false;
        }

        if (!PageUrlMatches(_webView.Url, _pendingUrl))
            return false;

        _pageReady = true;
        return true;
    }

    private static bool PageUrlMatches(string actual, string expected) {
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            return true;

        // CEF can add a slash or a query. Compare the path without UriKind, which the sandbox rejects.
        var actualPath = PathAfterHost(actual);
        var expectedPath = PathAfterHost(expected);
        return actualPath != null && string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string? PathAfterHost(string url) {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return null;

        var start = url.IndexOf('/', scheme + 3);
        if (start < 0)
            return "/";

        var query = url.IndexOf('?', start);
        var hash = url.IndexOf('#', start);
        var end = query < 0 ? hash : hash < 0 ? query : Math.Min(query, hash);
        return end < 0 ? url[start..] : url[start..end];
    }

    private void BeforeBrowseHandler(IBeforeBrowseContext context) {
        // An exception in here will freeze up / crash CEF, so catch any
        try {
            if (_webView == null || string.IsNullOrEmpty(_webView.Url))
                return;

            // TGUI topics are byond://?tgui=1&... which is not a valid URI (no host).
            if (context.Url.StartsWith("byond://", StringComparison.OrdinalIgnoreCase)) {
                context.DoCancel();
                DispatchByondUrl(context.Url);
                return;
            }

            Uri oldUri = new Uri(_webView.Url);
            Uri newUri = new Uri(context.Url);

            if (newUri.AbsolutePath == oldUri.AbsolutePath && newUri.Query != string.Empty) {
                context.DoCancel();
                DispatchByondUrl("byond://" + newUri.Query);
            }
        } catch (Exception e) {
            _sawmill.Error($"Exception in BeforeBrowseHandler: {e}");
        }
    }

    private void DispatchByondUrl(string url) {
        var rest = url["byond://".Length..];
        string host;
        string query;
        if (rest.StartsWith('?') || rest.Length == 0) {
            host = string.Empty;
            query = rest;
        } else {
            var split = rest.IndexOf('?');
            if (split < 0) {
                host = rest;
                query = string.Empty;
            } else {
                host = rest[..split];
                query = rest[split..];
            }
        }

        switch (host) {
            case "winset":
                HandleEmbeddedWinset(query);
                return;
            case "winget":
                HandleEmbeddedWinget(query);
                return;
            default:
                _netManager.ClientSendMessage(new MsgTopic { Query = query });
                return;
        }
    }

    private void RequestHandler(IRequestHandlerContext context) {
        // An exception in here will crash RT (and not log it because it's uncaught)
        try {
            Uri newUri = new Uri(context.Url);

            if (newUri is {Scheme: "http", Host: "127.0.0.1"}) {
                // TGUI posts byond:// through this instead of location.href. A real
                // navigation never gets a render frame and the lobby stalls on the timeout.
                if (newUri.AbsolutePath == "/__opendream_topic") {
                    var payload = HttpUtility.UrlDecode(newUri.Query.Length > 0 ? newUri.Query[1..] : string.Empty);
                    if (payload.Length > 0)
                        _pendingTopics.Enqueue(payload);
                    context.DoRespondStream(Stream.Null, "text/plain", HttpStatusCode.NoContent);
                    return;
                }

                Stream stream;
                HttpStatusCode status;
                var resource = new ResPath(newUri.AbsolutePath);
                var path = HttpUtility.UrlDecode(resource.CanonPath); // files are saved in decoded form

                if (!_dreamResource.EnsureCacheFile(path)) {
                    stream = Stream.Null;
                    status = HttpStatusCode.NotFound;
                } else {
                    try {
                        stream = _resourceManager.UserData.OpenRead(
                            _dreamResource.GetCacheFilePath(path));
                        status = HttpStatusCode.OK;
                    } catch (FileNotFoundException) {
                        stream = Stream.Null;
                        status = HttpStatusCode.NotFound;
                    } catch (Exception e) {
                        _sawmill.Error($"Exception while loading file from {path}:\n{e}");
                        stream = Stream.Null;
                        status = HttpStatusCode.InternalServerError;
                    }
                }

                var mimeType = FileExtensionMimeTypes.GetValueOrDefault(resource.Extension, "application/octet-stream");
                context.DoRespondStream(stream, mimeType, status);
            }
        } catch (Exception e) {
            _sawmill.Error($"Exception in RequestHandler: {e}");
            context.DoCancel();
        }
    }

    /// <summary>
    /// Handles an embedded winset
    /// <code>byond://winset?command=.quit</code>
    /// </summary>
    /// <param name="query">The query portion of the embedded winset</param>
    private void HandleEmbeddedWinset(string query) {
        // Strip the question mark out before parsing
        // Also replace ';' with '&' because they're both usable here
        var queryParams = HttpUtility.ParseQueryString(query.Substring(1).Replace(';', '&'));

        // We need to extract the control element (if one was included)
        string? element = queryParams.Get("element");
        queryParams.Remove("element");

        // Join as winset syntax. QueryString uses '&', which would split values that contain '&'.
        var parts = new List<string>(queryParams.Count);
        foreach (var paramKey in queryParams.AllKeys) {
            if (paramKey == null)
                continue;

            var paramValue = queryParams[paramKey];
            if (paramValue == null)
                continue;

            parts.Add($"{paramKey}=\"{paramValue}\"");
        }

        InterfaceManager.WinSet(element, string.Join(';', parts));
    }

    /// <summary>
    /// Handles an embedded winget
    /// </summary>
    /// <param name="query">The query portion of the embedded winget</param>
    // Example: byond://winget?id=browseroutput&property=size&callback=JSFunction
    // (Not in the XML comment because '&' breaks that apparently)
    private void HandleEmbeddedWinget(string query) {
        // Strip the question mark out before parsing
        // Also replace ';' with '&' because they're both usable here
        var queryParams = HttpUtility.ParseQueryString(query.Substring(1).Replace(';', '&'));

        var elementId = queryParams.Get("id");
        var property = queryParams.Get("property");
        var callback = queryParams.Get("callback");
        if (elementId == null || property == null || callback == null) {
            _sawmill.Error($"Required arg 'id', 'property', or 'callback' not provided in embedded winget ({query})");
            return;
        }

        // property=* returns every known property as raw strings, not JSON.
        var element = InterfaceManager.FindElementWithId(elementId);
        bool forceJson = property != "*";
        string[] properties;
        if (property == "*") {
            if (element == null) {
                _sawmill.Error($"Could not winget element {elementId} because it does not exist");
                return;
            }

            properties = element.EnumerateProperties();
        } else {
            // Multiple properties can be queried in a single winget with "&property=size,view-size"
            properties = property.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        var jsonBuilder = new StringBuilder(); // Build the JSON object that the callback receives

        jsonBuilder.Append("{ ");
        foreach (var wingetting in properties) {
            string result;
            if (!forceJson && element != null && element.TryGetProperty(wingetting, out var prop)) {
                result = prop.AsRaw();
            } else if (forceJson) {
                result = InterfaceManager.WinGet(elementId, wingetting, forceJson: true);
            } else {
                continue;
            }

            if (jsonBuilder.Length > 2)
                jsonBuilder.Append(", ");

            jsonBuilder.Append('"');
            jsonBuilder.Append(HttpUtility.JavaScriptStringEncode(wingetting));
            jsonBuilder.Append("\": ");

            if (forceJson) {
                jsonBuilder.Append(result);
            } else {
                jsonBuilder.Append('"');
                jsonBuilder.Append(HttpUtility.JavaScriptStringEncode(result));
                jsonBuilder.Append('"');
            }
        }

        jsonBuilder.Append(" }");

        // Execute the callback
        var json = jsonBuilder.ToString();
        _webView?.ExecuteJavaScript($"{callback}({json})");
    }

    private void OnShowEvent() {
        ControlDescriptorBrowser controlDescriptor = (ControlDescriptorBrowser)ControlDescriptor;
        if (!string.IsNullOrWhiteSpace(controlDescriptor.OnShowCommand.Value)) {
            InterfaceManager.RunCommand(controlDescriptor.OnShowCommand.AsRaw());
        }
    }

    private void OnHideEvent() {
        ControlDescriptorBrowser controlDescriptor = (ControlDescriptorBrowser)ControlDescriptor;
        if (!string.IsNullOrWhiteSpace(controlDescriptor.OnHideCommand.Value)) {
            InterfaceManager.RunCommand(controlDescriptor.OnHideCommand.AsRaw());
        }
    }
}

public sealed class BrowseWinCommand : IConsoleCommand {
    public string Command => "browsewin";
    public string Description => "";
    public string Help => "";

    public void Execute(IConsoleShell shell, string argStr, string[] args) {
        if (args.Length != 1) {
            shell.WriteError("Incorrect amount of arguments! Must be a single one.");
            return;
        }

        var parameters = new BrowserWindowCreateParameters(1280, 720) {Url = args[0]};

        var cef = IoCManager.Resolve<IWebViewManager>();
        cef.CreateBrowserWindow(parameters);
    }
}
