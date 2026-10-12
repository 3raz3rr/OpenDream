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
    private readonly BoxContainer _messageContainer;

    public OutputControl() {
        _panelContainer = new PanelContainer {
            HorizontalExpand = true,
            VerticalExpand = true,
            Children = {
                new ScrollContainer {
                    HorizontalExpand = true,
                    VerticalExpand = true,
                    HScrollEnabled = false,
                    Children = {
                        (_messageContainer = new BoxContainer {
                            Orientation = BoxContainer.LayoutOrientation.Vertical,
                            HorizontalExpand = true
                        })
                    }
                }
            }
        };

        AddChild(_panelContainer);
        OnResized += WrapMessages;
    }

    public void AddMessage(FormattedMessage message) {
        var label = new RichTextLabel {
            HorizontalExpand = true
        };

        label.SetMessage(message);
        label.Margin = new Thickness(8, 1, 8, 1);
        _messageContainer.AddChild(label);
        WrapMessages();
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
