using System.IO;
using System.Net;
using System.Text;
using System.Web;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamClient.Resources;
using OpenDreamShared.Network.Messages;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.WebView;
using Robust.Shared.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Timing;
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
    private readonly Queue<string> _urlQueue = new();
    private bool _urlPump;

    public ControlBrowser(ControlDescriptor controlDescriptor, ControlWindow window)
        : base(controlDescriptor, window) {
        IoCManager.InjectDependencies(this);
    }

    protected override Control CreateUIElement() {
        // CEF times out if a WebView is created before its OS window exists, and the
        // skin has several browsers that are never shown. Create the view on first use.
        _panel = new PanelContainer();

        if (ControlDescriptor.IsVisible.Value)
            OnShowEvent();
        else
            OnHideEvent();

        return _panel;
    }

    private WebViewControl EnsureWebView() {
        if (_webView != null)
            return _webView;

        var webView = new WebViewControl {
            AlwaysActive = true,
            HorizontalExpand = true,
            VerticalExpand = true,
            MinSize = new Vector2(Math.Max(ControlDescriptor.Size.X, 64), Math.Max(ControlDescriptor.Size.Y, 64))
        };
        webView.AddResourceRequestHandler(RequestHandler);
        webView.AddBeforeBrowseHandler(BeforeBrowseHandler);
        _panel.AddChild(webView);
        _webView = webView;
        return webView;
    }

    protected override void UpdateElementDescriptor() {
        base.UpdateElementDescriptor();

        _panel.PanelOverride = new StyleBoxFlat(Color.White); // Always white background
    }

    public override void Output(string value, string? jsFunction) {
        if (jsFunction == null) return;

        // Prepare the argument to be used in JS
        //output is formatted by list2params sometimes, which means raw strings are url encoded, but the message contains & chars which are not encoded that are the params
        //so we split on &, url decode the parts, and then join them back together with , as the separator for the JS params
        var parts = value.Split('&');
        for (var i = 0; i < parts.Length; i++) {
            parts[i] = "\""+HttpUtility.JavaScriptStringEncode(HttpUtility.UrlDecode(parts[i]))+"\""; //wrap in quotes and encode for JS
        }

        // Insert the values directly into JS and execute it (what could go wrong??)
        EnsureWebView().ExecuteJavaScript($"{jsFunction}({string.Join(",", parts)})");
    }

    public void SetFileSource(ResPath? filepath) {
        if (filepath == null) {
            _urlQueue.Clear();
            if (_webView != null)
                _webView.Url = "about:blank";
            return;
        }

        // hostname must be the localhost IP for TGUI to work properly.
        // Asset cache browses this control many times in one tick; CEF drops frames if they all start at once.
        _urlQueue.Enqueue("http://127.0.0.1/" + filepath);
        PumpUrls();
    }

    private void PumpUrls() {
        if (_urlPump || _urlQueue.Count == 0)
            return;

        var url = _urlQueue.Dequeue();
        _urlPump = true;
        EnsureWebView().Url = url;
        Timer.Spawn(TimeSpan.FromMilliseconds(150), () => {
            _urlPump = false;
            PumpUrls();
        });
    }

    private void BeforeBrowseHandler(IBeforeBrowseContext context) {
        // An exception in here will freeze up / crash CEF, so catch any
        try {
            if (_webView == null || string.IsNullOrEmpty(_webView.Url))
                return;

            Uri oldUri = new Uri(_webView.Url);
            Uri newUri = new Uri(context.Url);

            if (newUri.Scheme == "byond" || (newUri.AbsolutePath == oldUri.AbsolutePath && newUri.Query != string.Empty)) {
                context.DoCancel();

                switch (newUri.Host) {
                    case "winset":
                        HandleEmbeddedWinset(newUri.Query);
                        return;
                    case "winget":
                        HandleEmbeddedWinget(newUri.Query);
                        return;
                    default: {
                        var msg = new MsgTopic { Query = newUri.Query };
                        _netManager.ClientSendMessage(msg);
                        break;
                    }
                }
            }
        } catch (Exception e) {
            _sawmill.Error($"Exception in BeforeBrowseHandler: {e}");
        }
    }

    private void RequestHandler(IRequestHandlerContext context) {
        // An exception in here will crash RT (and not log it because it's uncaught)
        try {
            Uri newUri = new Uri(context.Url);

            if (newUri is {Scheme: "http", Host: "127.0.0.1"}) {
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
