using OpenDreamShared.Dream;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace OpenDreamClient.Rendering;

internal partial class DreamViewOverlay {
    private ViewAlgorithm.Tile?[,]? _tileInfo;
    private bool _tileInfoDirty;
    private EntityUid _visibilityGrid;
    private MapGridComponent? _visibilityGridComp;
    private Vector2i _visibilityEye;

    /// <summary>
    /// Whether the last computed view can see this world position. False before the first frame.
    /// </summary>
    public bool IsWorldPositionVisible(Vector2 worldPos) {
        if (_tileInfo == null || _visibilityGridComp == null)
            return false;

        var tilePos = _mapSystem.WorldToTile(_visibilityGrid, _visibilityGridComp, worldPos)
                      - _visibilityEye + _interfaceManager.View.Center + 1;
        if (tilePos.X < 0 || tilePos.Y < 0 || tilePos.X >= _tileInfo.GetLength(0) || tilePos.Y >= _tileInfo.GetLength(1))
            return false;

        return _tileInfo[tilePos.X, tilePos.Y]?.IsVisible is true;
    }

    public void DirtyTileVisibility() {
        _tileInfoDirty = true;
    }

    private ViewAlgorithm.Tile?[,] CalculateTileVisibility(EntityUid gridUid, MapGridComponent grid, TileRef eyeTile, int seeVis) {
        using var _ = _prof.Group("visible turfs");

        _visibilityGrid = gridUid;
        _visibilityGridComp = grid;
        _visibilityEye = eyeTile.GridIndices;

        var viewRange = _interfaceManager.View;
        if (_tileInfo == null || _tileInfo.GetLength(0) != viewRange.Width + 2 ||
            _tileInfo.GetLength(1) != viewRange.Height + 2) {
            // _tileInfo hasn't been created yet or view range has changed, so create a new array.
            // Leave a 1 tile buffer on each side
            _tileInfo = new ViewAlgorithm.Tile[viewRange.Width + 2, viewRange.Height + 2];
            _tileInfoDirty = true;
        }

        if (!_tileInfoDirty)
            return _tileInfo;

        var eyeWorldPos = _mapSystem.GridTileToWorld(gridUid, grid, eyeTile.GridIndices);
        var tileRefs = _mapSystem.GetTilesEnumerator(gridUid, grid,
            Box2.CenteredAround(eyeWorldPos.Position, new Vector2(_tileInfo.GetLength(0), _tileInfo.GetLength(1))));

        // Gather up all the data the view algorithm needs
        while (tileRefs.MoveNext(out var tileRef)) {
            var delta = tileRef.GridIndices - eyeTile.GridIndices;
            var appearance = _appearanceSystem.GetTurfIcon((uint)tileRef.Tile.TypeId).Appearance;
            if (appearance == null)
                continue;

            int xIndex = delta.X + viewRange.CenterX + 1;
            int yIndex = delta.Y + viewRange.CenterY + 1;
            if (xIndex < 0 || yIndex < 0 || xIndex >= _tileInfo.GetLength(0) || yIndex >= _tileInfo.GetLength(1))
                continue;

            var tile = new ViewAlgorithm.Tile {
                Opaque = appearance.Opacity,
                Luminosity = 0,
                DeltaX = delta.X,
                DeltaY = delta.Y
            };

            _tileInfo[xIndex, yIndex] = tile;
        }

        // Apply entities' opacity
        foreach (EntityUid entity in EntitiesInView) {
            // TODO use a sprite tree.
            if (!_spriteQuery.TryGetComponent(entity, out var sprite))
                continue;

            var transform = _xformQuery.GetComponent(entity);
            if (!_spriteSystem.IsVisible(sprite, transform, seeVis, null))
                continue;
            if (sprite.Icon.Appearance == null) //appearance hasn't loaded yet
                continue;

            var worldPos = _transformSystem.GetWorldPosition(transform);
            var tilePos = _mapSystem.WorldToTile(gridUid, grid, worldPos) - eyeTile.GridIndices + viewRange.Center + 1;
            if (tilePos.X < 0 || tilePos.Y < 0 || tilePos.X >= _tileInfo.GetLength(0) ||
                tilePos.Y >= _tileInfo.GetLength(1))
                continue;

            var tile = _tileInfo[tilePos.X, tilePos.Y];
            if (tile != null)
                tile.Opaque |= sprite.Icon.Appearance.Opacity;
        }

        ViewAlgorithm.CalculateVisibility(_tileInfo);
        _tileInfoDirty = false;
        return _tileInfo;
    }
}
