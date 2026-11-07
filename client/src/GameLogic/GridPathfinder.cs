using System;
using System.Collections.Generic;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>Pure, deterministic A* over the passable cells of an <see cref="IWallGrid"/>. A cell is
/// passable when it does not block movement (<see cref="CellMaterials.BlocksMovement"/>) and is not
/// lethal — so floor and bridges are walkable, brick/steel/crate/water/mountain/building/lava are not. Movement is
/// 4-connected (orthogonal steps only), so a returned path never cuts a wall corner diagonally.
/// No Godot, no randomness: the same grid, start and goal always yield the same path, which keeps
/// AI steering reproducible across host and clients. Returns an empty list when the goal is
/// unreachable (start or goal blocked, or walled off).</summary>
public static class GridPathfinder
{
    // Orthogonal neighbours, in a fixed order so ties resolve deterministically.
    private static readonly (int Dx, int Dy)[] Neighbours =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    /// <summary>Shortest 4-connected path of passable cells from <paramref name="start"/> to
    /// <paramref name="goal"/> inclusive, or an empty list when none exists. When
    /// <paramref name="start"/> equals <paramref name="goal"/> (and is passable) the path is just
    /// that one cell.</summary>
    public static IReadOnlyList<(int X, int Y)> FindPath(IWallGrid grid, (int X, int Y) start, (int X, int Y) goal)
    {
        if (!Passable(grid, start.X, start.Y) || !Passable(grid, goal.X, goal.Y))
        {
            return Array.Empty<(int, int)>();
        }

        if (start == goal)
        {
            return new[] { start };
        }

        var open = new PriorityQueue<(int X, int Y), (int F, int Order)>();
        var cameFrom = new Dictionary<(int X, int Y), (int X, int Y)>();
        var gScore = new Dictionary<(int X, int Y), int> { [start] = 0 };
        var order = 0; // tie-breaker: earlier-discovered nodes pop first, for stable output

        open.Enqueue(start, (Heuristic(start, goal), order++));

        while (open.Count > 0)
        {
            var current = open.Dequeue();
            if (current == goal)
            {
                return Reconstruct(cameFrom, current);
            }

            var currentG = gScore[current];
            foreach (var (dx, dy) in Neighbours)
            {
                var next = (X: current.X + dx, Y: current.Y + dy);
                if (!Passable(grid, next.X, next.Y) || !LayersConnect(grid, current, next))
                {
                    continue;
                }

                var tentativeG = currentG + 1;
                if (gScore.TryGetValue(next, out var knownG) && tentativeG >= knownG)
                {
                    continue;
                }

                cameFrom[next] = current;
                gScore[next] = tentativeG;
                open.Enqueue(next, (tentativeG + Heuristic(next, goal), order++));
            }
        }

        return Array.Empty<(int, int)>();
    }

    private static bool Passable(IWallGrid grid, int x, int y)
    {
        return !grid.IsBlocked(x, y) && !CellMaterials.IsLethal(grid.GetCell(x, y).Material);
    }

    // A cliff face is a wall to a tank on the ground: only a ramp joins two different layers.
    private static bool LayersConnect(IWallGrid grid, (int X, int Y) a, (int X, int Y) b) =>
        grid.LayerAt(a.X, a.Y) == grid.LayerAt(b.X, b.Y) || grid.IsRamp(a.X, a.Y) || grid.IsRamp(b.X, b.Y);

    // Manhattan distance — admissible for 4-connected unit-cost movement.
    private static int Heuristic((int X, int Y) a, (int X, int Y) b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static IReadOnlyList<(int X, int Y)> Reconstruct(
        Dictionary<(int X, int Y), (int X, int Y)> cameFrom, (int X, int Y) current)
    {
        var path = new List<(int X, int Y)> { current };
        while (cameFrom.TryGetValue(current, out var prev))
        {
            current = prev;
            path.Add(current);
        }

        path.Reverse();
        return path;
    }
}
