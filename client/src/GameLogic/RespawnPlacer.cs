using System;
using System.Collections.Generic;
using System.Numerics;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>Picks where a fallen tank comes back: any allowed cell on the map rather than a fixed
/// spawn point. A cell is allowed when a tank can stand on it (no wall, water, lava, building or
/// scaled prop footprint), it is not a ramp, and it can be driven to from the map's spawn cells (so
/// nobody lands in a sealed pocket). Among allowed cells it picks at random, keeping
/// <see cref="SafeRadiusCells"/> from every live tank, and shrinks that radius only when the map is
/// too crowded to honour it. Re-reads the grid on each pick, so a broken wall frees its cell.</summary>
public sealed class RespawnPlacer
{
    /// <summary>The distance (in cells) a respawn keeps from every live tank when the map allows it.</summary>
    public const int SafeRadiusCells = 6;

    private readonly IWallGrid _grid;
    private readonly float _tileSize;
    private readonly Vector2 _origin;
    private readonly IReadOnlyList<(int X, int Y)> _anchors;
    private readonly Random _rng;
    private readonly int _layer;
    private readonly Func<Vector2, bool>? _reserved;

    /// <param name="grid">The live map.</param>
    /// <param name="tileSize">World size of one cell.</param>
    /// <param name="origin">World position of cell (0,0)'s minimum corner.</param>
    /// <param name="anchors">Cells known to be in play (the map's spawns). Only cells connected to
    /// one of them can be picked; breakable walls count as connected because tanks can break them.</param>
    /// <param name="rng">The seeded source of the random picks.</param>
    /// <param name="layer">The elevation layer revived tanks stand on.</param>
    /// <param name="reserved">Extra forbidden points, such as teleport pads. Null when there are none.</param>
    public RespawnPlacer(
        IWallGrid grid,
        float tileSize,
        Vector2 origin,
        IReadOnlyList<(int X, int Y)> anchors,
        Random rng,
        int layer = 0,
        Func<Vector2, bool>? reserved = null)
    {
        _grid = grid;
        _tileSize = tileSize;
        _origin = origin;
        _anchors = anchors;
        _rng = rng;
        _layer = layer;
        _reserved = reserved;
    }

    /// <summary>Picks a respawn position for a tank.</summary>
    /// <param name="entities">The world's entities; every tank with hit points is kept clear of.</param>
    /// <returns>The centre of the chosen cell. When no cell is allowed at all, the first anchor.</returns>
    public Vector2 Pick(IEnumerable<IEntity> entities)
    {
        var tanks = new List<Vector2>();
        foreach (var entity in entities)
        {
            if (entity is ITank { Hp: > 0 } tank)
            {
                tanks.Add(tank.Position);
            }
        }

        var cells = AllowedCells();
        if (cells.Count == 0)
        {
            return Centre(_anchors[0]);
        }

        for (var radius = SafeRadiusCells; radius >= 2; radius--)
        {
            var clear = cells.FindAll(cell => ClearanceTo(cell, tanks) >= radius * _tileSize);
            if (clear.Count > 0)
            {
                return Centre(clear[_rng.Next(clear.Count)]);
            }
        }

        // Too crowded for any margin: the roomiest cell left still avoids landing on a tank.
        var best = cells[0];
        foreach (var cell in cells)
        {
            if (ClearanceTo(cell, tanks) > ClearanceTo(best, tanks))
            {
                best = cell;
            }
        }

        return Centre(best);
    }

    /// <summary>Every cell a tank may be placed on right now, found by flooding out from the anchors
    /// over everything a tank could drive or break through.</summary>
    /// <returns>The allowed cells, in a fixed order for a given grid.</returns>
    public List<(int X, int Y)> AllowedCells()
    {
        var allowed = new List<(int X, int Y)>();
        var seen = new HashSet<(int X, int Y)>();
        var frontier = new Queue<(int X, int Y)>();
        foreach (var anchor in _anchors)
        {
            if (seen.Add(anchor))
            {
                frontier.Enqueue(anchor);
            }
        }

        while (frontier.Count > 0)
        {
            var cell = frontier.Dequeue();
            if (CanStandOn(cell))
            {
                allowed.Add(cell);
            }

            foreach (var next in new[]
            {
                (cell.X + 1, cell.Y), (cell.X - 1, cell.Y), (cell.X, cell.Y + 1), (cell.X, cell.Y - 1),
            })
            {
                if (CanCross(next) && seen.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return allowed;
    }

    private bool CanStandOn((int X, int Y) cell) =>
        !_grid.IsBlocked(cell.X, cell.Y)
        && !_grid.IsRamp(cell.X, cell.Y)
        && _grid.LayerAt(cell.X, cell.Y) == _layer
        && !CellMaterials.IsLethal(_grid.GetCell(cell.X, cell.Y).Material)
        && _reserved?.Invoke(Centre(cell)) != true;

    private bool CanCross((int X, int Y) cell)
    {
        if (_grid.LayerAt(cell.X, cell.Y) != _layer)
        {
            return false;
        }

        var material = _grid.GetCell(cell.X, cell.Y).Material;
        if (material is CellMaterial.Brick or CellMaterial.Crate)
        {
            return true;
        }

        return !_grid.IsBlocked(cell.X, cell.Y) && !CellMaterials.IsLethal(material);
    }

    private float ClearanceTo((int X, int Y) cell, List<Vector2> tanks)
    {
        var centre = Centre(cell);
        var nearest = float.PositiveInfinity;
        foreach (var tank in tanks)
        {
            nearest = MathF.Min(nearest, Vector2.Distance(centre, tank));
        }

        return nearest;
    }

    private Vector2 Centre((int X, int Y) cell) =>
        _origin + new Vector2((cell.X + 0.5f) * _tileSize, (cell.Y + 0.5f) * _tileSize);
}
