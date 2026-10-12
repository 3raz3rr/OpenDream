using System.ComponentModel;
using Robust.Client.UserInterface.Controls;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// The native interface framework. Every screen is a document the game sends.
/// </summary>
internal sealed class NativeInterfaceHost {
    private readonly Dictionary<string, NativePanel> _panels = new();
    private readonly Dictionary<string, NativePopup> _popups = new();
    private readonly Action<string> _runCommand;
    private readonly Action<string> _sendTopic;

    public NativeInterfaceHost(Action<string> runCommand, Action<string> sendTopic) {
        _runCommand = runCommand;
        _sendTopic = sendTopic;
    }

    public static bool IsSlot(string? elementId, string? windowId) {
        return elementId is "title_browser" or "browseroutput" or "statbrowser" or "preferences_browser"
            || (elementId == "browser" && windowId == "tgui_say");
    }

    /// <summary>
    /// The screen for a skin browser, or null when the slot stays empty (the say popup).
    /// </summary>
    public NativePanel? Mount(string elementId, string? windowId) {
        if (!IsSlot(elementId, windowId) || elementId == "browser")
            return null;

        return Panel(elementId);
    }

    public bool TryApply(string elementId, string value, string? channel) {
        // Movement keys are not a screen. Swallow the id so it never opens a browser.
        if (elementId == "movekeys")
            return true;

        if (channel is "ui" or "patch" or "line" or "clear" or "text") {
            ApplyDocument(elementId, value, channel);
            return true;
        }

        if (elementId is not ("title_browser" or "browseroutput" or "statbrowser" or "preferences_browser"))
            return false;

        Panel(elementId).ApplyState(value, channel);
        return true;
    }

    private static bool IsDocumentSlot(string elementId) {
        return elementId is "title_browser" or "preferences_browser" or "browseroutput" or "statbrowser";
    }

    private void ApplyDocument(string elementId, string value, string channel) {
        NativePopup? popup = null;
        DocumentView? document;
        if (IsDocumentSlot(elementId))
            document = ((DocumentPanel)Panel(elementId)).Document;
        else if (channel == "ui") {
            popup = Popup(elementId);
            document = popup.Document;
        } else if (_popups.TryGetValue(elementId, out popup))
            document = popup.Document;
        else
            return;

        switch (channel) {
            case "ui":
                document.ShowDocument(value);
                break;
            case "patch":
                document.Patch(value);
                break;
            case "line":
                document.AppendLines(value);
                break;
            case "clear":
                document.ClearLog();
                break;
            case "text":
                document.SetLogText(value);
                break;
        }

        if (channel != "ui" || popup == null)
            return;

        if (!string.IsNullOrEmpty(popup.Document.Title))
            popup.Title = popup.Document.Title;
        if (popup.Document.RequestedWidth > 0)
            popup.SetWidth = popup.Document.RequestedWidth;
        if (popup.Document.RequestedHeight > 0)
            popup.SetHeight = popup.Document.RequestedHeight;
        popup.Show();
    }

    private NativePopup Popup(string elementId) {
        if (_popups.TryGetValue(elementId, out var existing))
            return existing;

        var created = new NativePopup(_sendTopic, _runCommand);
        created.Closing += args => {
            if (args.Cancel)
                return;
            var href = created.Document.CloseHref;
            if (!string.IsNullOrEmpty(href))
                _sendTopic(href);
        };
        _popups[elementId] = created;
        return created;
    }

    private NativePanel Panel(string elementId) {
        if (_panels.TryGetValue(elementId, out var existing))
            return existing;

        NativePanel created = elementId switch {
            "title_browser" or "browseroutput" or "statbrowser" or "preferences_browser"
                => new DocumentPanel(elementId, _sendTopic, _runCommand),
            _ => throw new InvalidOperationException(elementId)
        };
        _panels[elementId] = created;
        return created;
    }
}
