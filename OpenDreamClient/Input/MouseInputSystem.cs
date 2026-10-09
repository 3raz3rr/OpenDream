using OpenDreamClient.Input.ContextMenu;
using OpenDreamClient.Interface;
using OpenDreamClient.Interface.Controls.UI;
using OpenDreamShared.Interface.Descriptors;
using OpenDreamClient.Rendering;
using OpenDreamShared.Dream;
using OpenDreamShared.Input;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using Matrix3x2 = System.Numerics.Matrix3x2;

namespace OpenDreamClient.Input;

internal sealed partial class MouseInputSystem : SharedMouseInputSystem {
    [Dependency] private IInputManager _inputManager = default!;
    [Dependency] private IUserInterfaceManager _userInterfaceManager = default!;
    [Dependency] private SharedMapSystem _mapManager = default!;
    [Dependency] private IOverlayManager _overlayManager = default!;
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private MapSystem _mapSystem = default!;
    [Dependency] private IConfigurationManager _configurationManager = default!;
    [Dependency] private IDreamInterfaceManager _dreamInterfaceManager = default!;
    [Dependency] private ClientAppearanceSystem _appearanceSystem = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private ILogManager _logManager = default!;
    [Dependency] private IGameTiming _timing = default!;
    private ISawmill _sawmill = default!;

    private DreamViewOverlay? _dreamViewOverlay;
    private ContextMenuPopup _contextMenu = default!;
    private EntityClickInformation? _selectedEntity;
    private TimeSpan _lastMouseSample;
    private Vector2 _lastMouseSamplePos;
    private (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? _lastUnderMouse;
    private bool _hasUnderMouse;
    private bool _hoverMapRequested;

    private sealed class EntityClickInformation(ClientObjectReference atom, ScreenCoordinates initialMousePos, ClickParams clickParams) {
        public readonly ClientObjectReference Atom = atom;
        public readonly ScreenCoordinates InitialMousePos = initialMousePos;
        public readonly ClickParams ClickParams = clickParams;
        public bool IsDrag; // If the current click is considered a drag (if the mouse has moved after the click)
    }

    public override void Initialize() {
        UpdatesOutsidePrediction = true;
        _sawmill = _logManager.GetSawmill("opendream.mouseinput");

        _contextMenu = new ContextMenuPopup();
        _userInterfaceManager.ModalRoot.AddChild(_contextMenu);
    }

    public override void Update(float frameTime) {
        if (_selectedEntity == null)
            return;

        if (!_selectedEntity.IsDrag) {
            var currentMousePos = _inputManager.MouseScreenPosition.Position;
            var distance = (currentMousePos - _selectedEntity.InitialMousePos.Position).Length();

            if (distance > 3f) {
                _selectedEntity.IsDrag = true;
                if (_dreamInterfaceManager.DefaultMap is { } map)
                    UpdateMouseCursor(map.Viewport, _selectedEntity.Atom);
            }
        }
    }

    public override void Shutdown() {
        CommandBinds.Unregister<MouseInputSystem>();
    }

    public bool HandleViewportEvent(ScalingViewport viewport, GUIBoundKeyEventArgs args, ControlDescriptor descriptor) {
        if (args.State == BoundKeyState.Down)
            return OnPress(viewport, args, descriptor);
        else
            return OnRelease(viewport, args);
    }

    public void HandleStatClick(string atomRef, bool isRight, bool isMiddle) {
        bool shift = _inputManager.IsKeyDown(Keyboard.Key.Shift);
        bool ctrl = _inputManager.IsKeyDown(Keyboard.Key.Control);
        bool alt = _inputManager.IsKeyDown(Keyboard.Key.Alt);

        RaiseNetworkEvent(new StatClickedEvent(atomRef, isRight, isMiddle, shift, ctrl, alt));
    }

    public void HandleAtomMouseEntered(ScalingViewport viewport, Vector2 relativePos, ClientObjectReference atomRef, Vector2i iconPos) {
        UpdateMouseCursor(viewport, atomRef);
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Enter))
            return;

        RaiseNetworkEvent(new MouseEnteredEvent(atomRef, CreateClickParams(viewport, relativePos, iconPos)));
    }

    public void HandleAtomMouseExited(ScalingViewport viewport, ClientObjectReference atomRef) {
        UpdateMouseCursor(viewport, null);
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Exit))
            return;

        RaiseNetworkEvent(new MouseExitedEvent(atomRef, CreateClickParams(viewport, Vector2.Zero, Vector2i.Zero)));
    }

    public void HandleAtomMouseMove(ScalingViewport viewport, Vector2 relativePos, ClientObjectReference atomRef, Vector2i iconPos) {
        if (!HasMouseEventEnabled(atomRef, AtomMouseEvents.Move))
            return;

        RaiseNetworkEvent(new MouseMoveEvent(atomRef, CreateClickParams(viewport, relativePos, iconPos)));
    }

    public (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? GetAtomUnderMouse(ScalingViewport viewport, Vector2 relativePos, ScreenCoordinates globalPos, bool sampleGpu = false) {
        _dreamViewOverlay ??= _overlayManager.GetOverlay<DreamViewOverlay>();
        if (_dreamViewOverlay == null || viewport.PixelSize.X <= 0 || viewport.PixelSize.Y <= 0)
            return null;

        var viewportBox = viewport.GetDrawBox();
        if (viewportBox.Width <= 0 || viewportBox.Height <= 0)
            return null;
        if (!viewportBox.Contains((int)relativePos.X, (int)relativePos.Y))
            return null; // Was outside of the viewport

        var mapCoords = viewport.ScreenToMap(globalPos.Position);
        var mousePos = (relativePos - viewportBox.TopLeft) / viewportBox.Size * viewport.ViewportSize;

        // Hover used to redraw the whole map and then read it back from the GPU.
        // That stall is what made the client unable to keep up while the cursor was on the map.
        if (!sampleGpu) {
            if (_dreamViewOverlay.MouseMap == null && !_hoverMapRequested) {
                _dreamViewOverlay.MouseMapRequested = true;
                _hoverMapRequested = true;
            }

            return _lastUnderMouse;
        }

        if (_dreamViewOverlay.MouseMap == null) {
            _dreamViewOverlay.MouseMapRequested = true;
            return _lastUnderMouse;
        }

        var now = _timing.RealTime;

        if (mousePos.X < 0 || mousePos.Y < 0 || mousePos.X >= _dreamViewOverlay.MouseMap.Size.X || mousePos.Y >= _dreamViewOverlay.MouseMap.Size.Y)
            return null;

        if(_configurationManager.GetCVar(CVars.DisplayCompat))
            return null; //Compat mode causes crashes with RT's GetPixel because OpenGL ES doesn't support GetTexImage()
        var lookupColor = _dreamViewOverlay.MouseMap.GetPixel((int)mousePos.X, (int)mousePos.Y);
        var underMouse = _dreamViewOverlay.MouseMapLookup.GetValueOrDefault(lookupColor);
        (ClientObjectReference Atom, Vector2i IconPosition, bool IsScreen)? result = null;
        if (underMouse == null) {
            result = null;
        } else if (underMouse.ClickUid == EntityUid.Invalid) { // A turf
            var turf = GetTurfUnderMouse(mapCoords, out _);
            if (turf != null)
                result = (turf.Value.Atom, turf.Value.IconPosition, false);
        } else {
            ImmutableAppearance? appearance = null;
            if (_entityManager.TryGetComponent(underMouse.ClickUid, out DMISpriteComponent? sprite))
                appearance = sprite.Icon.Appearance;

            var iconPosition = IconPixelPosition(mapCoords.Position - underMouse.Position, appearance);
            var reference = new ClientObjectReference(_entityManager.GetNetEntity(underMouse.ClickUid));
            result = (reference, iconPosition, underMouse.IsScreen);
        }

        _lastMouseSample = now;
        _lastMouseSamplePos = mousePos;
        _hasUnderMouse = true;
        _lastUnderMouse = result;
        return result;
    }

    public (ClientObjectReference Atom, Vector2i IconPosition)? GetTurfUnderMouse(MapCoordinates mapCoords, out uint? turfId) {
        // Grid coordinates are half a meter off from entity coordinates
        mapCoords = new MapCoordinates(mapCoords.Position + new Vector2(0.5f), mapCoords.MapId);

        if (_mapManager.TryFindGridAt(mapCoords, out var gridEntity, out var grid)) {
            Vector2i position = _mapSystem.CoordinatesToTile(gridEntity, grid, _mapSystem.MapToGrid(gridEntity, mapCoords));
            _mapSystem.TryGetTile(grid, position, out Tile tile);
            turfId = (uint)tile.TypeId;
            var turfAppearance = _appearanceSystem.GetTurfIcon(turfId.Value).Appearance;
            Vector2i turfIconPosition = IconPixelPosition(mapCoords.Position - position, turfAppearance);
            MapCoordinates worldPosition = _mapSystem.GridTileToWorld(gridEntity, grid, position);

            return (new(position, (int)worldPosition.MapId), turfIconPosition);
        }

        turfId = null;
        return null;
    }

    private bool OnPress(ScalingViewport viewport, GUIBoundKeyEventArgs args, ControlDescriptor descriptor) {
        //either turf or atom was clicked, and it was a right-click, and the popup menu is enabled, and the right-click parameter is disabled
        if (args.Function == EngineKeyFunctions.UIRightClick && _dreamInterfaceManager.ShowPopupMenus && !descriptor.RightClick.Value) {
            _contextMenu.RepopulateEntities(viewport, args.RelativePosition, args.PointerLocation);
            if (_contextMenu.EntityCount != 0) { //don't open a 1x1 empty context menu
                var contextMenuLocation = args.PointerLocation.Position / _userInterfaceManager.ModalRoot.UIScale; // Take scaling into account

                _contextMenu.Measure(_userInterfaceManager.ModalRoot.Size);
                _contextMenu.Open(UIBox2.FromDimensions(contextMenuLocation, _contextMenu.DesiredSize));
            }

            return true;
        }

        var underMouse = GetAtomUnderMouse(viewport, args.RelativePixelPosition, args.PointerLocation, sampleGpu: true);
        if (underMouse == null)
            return false;

        var atom = underMouse.Value.Atom;
        var clickParams = CreateClickParams(viewport, args, underMouse.Value.IconPosition); // If client.show_popup_menu is disabled, this will handle sending right clicks

        _selectedEntity = new(atom, args.PointerLocation, clickParams);
        return true;
    }

    private bool OnRelease(ScalingViewport viewport, GUIBoundKeyEventArgs args) {
        if (_selectedEntity == null) {
            UpdateMouseCursor(viewport, null);
            return false;
        }

        var overAtom = GetAtomUnderMouse(viewport, args.RelativePixelPosition, args.PointerLocation, sampleGpu: true);
        if (!_selectedEntity.IsDrag) {
            RaiseNetworkEvent(new AtomClickedEvent(_selectedEntity.Atom, _selectedEntity.ClickParams));
        } else {
            RaiseNetworkEvent(new AtomDraggedEvent(_selectedEntity.Atom, overAtom?.Atom, _selectedEntity.ClickParams));
        }

        _selectedEntity = null;
        UpdateMouseCursor(viewport, overAtom?.Atom);
        return true;
    }

    private void UpdateMouseCursor(ScalingViewport viewport, ClientObjectReference? mouseOver) {
        var isDragging = _selectedEntity?.IsDrag ?? false;
        if (!mouseOver.HasValue || !_appearanceSystem.TryGetAppearance(mouseOver.Value, out var mouseOverAppearance)) {
            if (!isDragging)
                SetCursorFromDefine(1, _dreamInterfaceManager.Cursors.BaseCursor, viewport);
            return;
        }

        if (isDragging) {
            if (!_appearanceSystem.TryGetAppearance(_selectedEntity!.Atom, out var draggingAppearance)) {
                SetCursorFromDefine(1, _dreamInterfaceManager.Cursors.DragCursor, viewport);
                return;
            }

            var define = mouseOverAppearance.MouseDropZone
                ? mouseOverAppearance.MouseDropPointer
                : draggingAppearance.MouseDragPointer;
            var cursor = mouseOverAppearance.MouseDropZone
                ? _dreamInterfaceManager.Cursors.DropCursor
                : _dreamInterfaceManager.Cursors.DragCursor;
            SetCursorFromDefine(define, cursor, viewport);
        } else {
            SetCursorFromDefine(mouseOverAppearance.MouseOverPointer, _dreamInterfaceManager.Cursors.OverCursor, viewport);
        }
    }

    private void SetCursorFromDefine(int define, ICursor? activeCursor, ScalingViewport viewport) {
        _sawmill.Verbose($"SetCursor {define} {activeCursor}");

        if (_dreamInterfaceManager.Cursors.AllStateSet) {
            viewport.CustomCursorShape = _dreamInterfaceManager.Cursors.BaseCursor;
        } else {
            viewport.CustomCursorShape = define switch {
                0 => _dreamInterfaceManager.Cursors.BaseCursor, //MOUSE_INACTIVE_POINTER
                1 => activeCursor, //MOUSE_ACTIVE_POINTER
                //skipping 2 is intentional, it's what byond does
                3 => _clyde.GetStandardCursor(StandardCursorShape.Crosshair), //MOUSE_DRAG_POINTER
                4 => _clyde.GetStandardCursor(StandardCursorShape.Hand), //MOUSE_DROP_POINTER
                5 => _clyde.GetStandardCursor(StandardCursorShape.Arrow), //MOUSE_ARROW_POINTER
                6 => _clyde.GetStandardCursor(StandardCursorShape.Crosshair), //MOUSE_CROSSHAIRS_POINTER
                7 => _clyde.GetStandardCursor(StandardCursorShape.Hand), //MOUSE_HAND_POINTER
                _ => null
            };
        }

        _clyde.SetCursor(viewport.CustomCursorShape);
    }

    private ClickParams CreateClickParams(ScalingViewport viewport, GUIBoundKeyEventArgs args, Vector2i iconPos) {
        bool right = args.Function == EngineKeyFunctions.UIRightClick;
        bool middle = args.Function == OpenDreamKeyFunctions.MouseMiddle;
        bool shift = _inputManager.IsKeyDown(Keyboard.Key.Shift);
        bool ctrl = _inputManager.IsKeyDown(Keyboard.Key.Control);
        bool alt = _inputManager.IsKeyDown(Keyboard.Key.Alt);
        UIBox2i viewportBox = viewport.GetDrawBox();
        Vector2 screenLocPos = (args.RelativePixelPosition - viewportBox.TopLeft) / viewportBox.Size * viewport.ViewportSize;
        float screenLocY = viewport.ViewportSize.Y - screenLocPos.Y; // Flip the Y
        ScreenLocation screenLoc = new ScreenLocation((int)screenLocPos.X, (int)screenLocY, IconSize);

        return new(screenLoc, right, middle, shift, ctrl, alt, iconPos.X, iconPos.Y);
    }

    /// <summary>
    /// <see cref="CreateClickParams(OpenDreamClient.Interface.Controls.UI.ScalingViewport,Robust.Client.UserInterface.GUIBoundKeyEventArgs,Robust.Shared.Maths.Vector2i)"/>
    /// but without information about mouse/keyboard buttons
    /// </summary>
    private ClickParams CreateClickParams(ScalingViewport viewport, Vector2 relativePos, Vector2i iconPos) {
        UIBox2i viewportBox = viewport.GetDrawBox();
        Vector2 screenLocPos = (relativePos - viewportBox.TopLeft) / viewportBox.Size * viewport.ViewportSize;
        float screenLocY = viewport.ViewportSize.Y - screenLocPos.Y; // Flip the Y
        ScreenLocation screenLoc = new ScreenLocation((int) screenLocPos.X, (int) screenLocY, IconSize);

        return new(screenLoc, false, false, false, false, false, iconPos.X, iconPos.Y);
    }

    private int IconSize => Math.Max(_dreamInterfaceManager.IconSize, 1);

    /// <summary>
    /// Pixel offset inside the icon. Icon transforms are inverted around the icon center, matching BYOND's icon-x/icon-y.
    /// </summary>
    private Vector2i IconPixelPosition(Vector2 worldDelta, ImmutableAppearance? appearance) {
        var pixels = worldDelta * IconSize;
        var transform = appearance?.Transform;
        if (transform is not { Length: >= 6 })
            return (Vector2i)pixels;

        var identity = transform[0] == 1f && transform[1] == 0f && transform[2] == 0f &&
                       transform[3] == 1f && transform[4] == 0f && transform[5] == 0f;
        if (identity)
            return (Vector2i)pixels;

        var matrix = new Matrix3x2(transform[0], transform[1], transform[2], transform[3], transform[4], transform[5]);
        if (!Matrix3x2.Invert(matrix, out var inverse))
            return (Vector2i)pixels;

        var center = new Vector2(IconSize / 2f, IconSize / 2f);
        return (Vector2i)(Vector2.Transform(pixels - center, inverse) + center);
    }

    private bool HasMouseEventEnabled(ClientObjectReference atomRef, AtomMouseEvents mouseEvent) {
        if (!_appearanceSystem.TryGetAppearance(atomRef, out var appearance))
            return false;

        return appearance.EnabledMouseEvents.HasFlag(mouseEvent);
    }
}
