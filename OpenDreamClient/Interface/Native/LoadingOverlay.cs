using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace OpenDreamClient.Interface.Native;

/// <summary>
/// Covers the window from the moment the interface exists until the lobby document
/// arrives. A reconnect that never gets a lobby drops the cover shortly after
/// the appearance sync finishes.
/// </summary>
internal sealed class LoadingOverlay : PanelContainer {
    private readonly SpinnerMark _mark;
    private bool _appearances;
    private float _sinceAppearances;
    private bool _dismissed;

    public LoadingOverlay() {
        PanelOverride = new StyleBoxFlat(new Color(0.09f, 0.09f, 0.09f, 0.94f));
        MouseFilter = MouseFilterMode.Stop;
        HorizontalExpand = true;
        VerticalExpand = true;

        var column = new BoxContainer {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            Align = BoxContainer.AlignMode.Center,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 12,
        };
        column.AddChild(new Control { VerticalExpand = true });
        _mark = new SpinnerMark { MinSize = new Vector2(36, 36) };
        _mark.HorizontalAlignment = HAlignment.Center;
        column.AddChild(_mark);
        column.AddChild(new Label {
            Text = "Loading",
            FontColorOverride = new Color(0.70f, 0.80f, 0.90f),
            Align = Label.AlignMode.Center,
            HorizontalAlignment = HAlignment.Center,
        });
        column.AddChild(new Control { VerticalExpand = true });
        AddChild(column);
    }

    public void Dismiss() {
        if (_dismissed)
            return;

        _dismissed = true;
        Orphan();
    }

    public void AppearancesFinished() {
        _appearances = true;
    }

    protected override void FrameUpdate(FrameEventArgs args) {
        base.FrameUpdate(args);
        if (_dismissed)
            return;

        if (_appearances) {
            _sinceAppearances += args.DeltaSeconds;
            if (_sinceAppearances > 4f)
                Dismiss();
        }
    }

    private sealed class SpinnerMark : Control {
        private float _angle;

        public SpinnerMark() {
            MouseFilter = MouseFilterMode.Ignore;
        }

        protected override void FrameUpdate(FrameEventArgs args) {
            base.FrameUpdate(args);
            _angle += args.DeltaSeconds * 5f;
            Margin = new Thickness(0, (_angle % 2f) < 1f ? 0 : 0.01f, 0, 0);
        }

        protected override void Draw(DrawingHandleScreen handle) {
            var size = PixelSize;
            if (size.X < 4 || size.Y < 4)
                return;

            var cx = size.X / 2f;
            var cy = size.Y / 2f;
            var radius = Math.Min(cx, cy) - 3f;
            for (var i = 0; i < 8; i++) {
                var t = _angle + i * MathF.PI / 4f;
                var alpha = 0.18f + 0.82f * (i / 7f);
                var x = cx + MathF.Cos(t) * radius;
                var y = cy + MathF.Sin(t) * radius;
                handle.DrawRect(UIBox2.FromDimensions(new Vector2(x - 2.5f, y - 2.5f), new Vector2(5, 5)),
                    new Color(0.70f, 0.85f, 1f, alpha));
            }
        }
    }
}
