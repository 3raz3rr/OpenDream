using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Utility;

namespace OpenDreamClient.Interface.Controls.UI;

/// <summary>
/// Similar functionality to RobustToolbox's OutputPanel
/// Works with inline controls in the rich texts
/// </summary>
public sealed class OutputControl : Control {
    public StyleBox? StyleBoxOverride {
        get => _panelContainer.PanelOverride;
        set => _panelContainer.PanelOverride = value;
    }

    private readonly PanelContainer _panelContainer;
    private readonly ScrollContainer _scroll;
    private readonly BoxContainer _messageContainer;

    public OutputControl() {
        _scroll = new ScrollContainer {
            HorizontalExpand = true,
            VerticalExpand = true,
            HScrollEnabled = false,
            Children = {
                (_messageContainer = new BoxContainer {
                    Orientation = BoxContainer.LayoutOrientation.Vertical,
                    HorizontalExpand = true
                })
            }
        };
        _panelContainer = new PanelContainer {
            HorizontalExpand = true,
            VerticalExpand = true,
            Children = { _scroll }
        };

        AddChild(_panelContainer);
        OnResized += WrapMessages;
    }

    public void AddMessage(FormattedMessage message) {
        var stick = _messageContainer.ChildCount == 0 || _scroll.VScroll >= _scroll.VScrollTarget - 12;
        var label = new RichTextLabel {
            HorizontalExpand = true
        };

        label.SetMessage(message);
        label.Margin = new Thickness(6, 0, 6, 0);
        _messageContainer.AddChild(label);
        WrapMessages();
        if (stick)
            _scroll.VScrollTarget = float.MaxValue;
    }

    public void Clear() {
        _messageContainer.RemoveAllChildren();
    }

    public void Trim(int maxMessages) {
        while (_messageContainer.ChildCount > maxMessages)
            _messageContainer.RemoveChild(_messageContainer.GetChild(0));
    }

    private void WrapMessages() {
        var width = Math.Max(Width, 1);
        _messageContainer.SetWidth = width;
        foreach (var child in _messageContainer.Children) {
            child.SetWidth = width;
            child.InvalidateMeasure();
        }
    }
}
