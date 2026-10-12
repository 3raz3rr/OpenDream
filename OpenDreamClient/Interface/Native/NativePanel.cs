using Robust.Client.UserInterface;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// A screen the client draws itself. The server sends state and receives actions.
/// </summary>
internal abstract class NativePanel {
    public string Id { get; }

    public Control Root { get; }

    protected NativePanel(string id, Control root) {
        Id = id;
        Root = root;
    }

    /// <summary>
    /// Apply one update. <paramref name="channel"/> is the name after the colon in output().
    /// </summary>
    public abstract void ApplyState(string value, string? channel);

    public void SetVisible(bool visible) {
        Root.Visible = visible;
    }
}
