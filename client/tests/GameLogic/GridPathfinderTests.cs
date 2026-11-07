using System.Collections.Generic;
using System.Linq;
using TankGame.Domain;
using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

public class GridPathfinderTests
{
    // Builds a wall grid from a row-major char map: '#' = steel wall, '.' = floor.
    // The map is given top row first; cell (x, y) is map[y][x].
    private static IWallGrid Grid(params string[] rows)
    {
        var height = rows.Length;
        var width = rows[0].Length;
        var materials = new CellMaterial[width, height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                materials[x, y] = rows[y][x] == '#' ? CellMaterial.Steel : CellMaterial.Floor;
            }
        }

        return WallGrid.FromMaterials(materials);
    }

    [Fact]
    public void ACliffFace_BlocksARoute_UnlessARampJoinsTheLayers()
    {
        var materials = new CellMaterial[5, 3];
        var layers = new int[5, 3];
        for (var x = 0; x < 5; x++)
        {
            layers[x, 0] = 1; // the whole top row is a plateau
        }

        var noRamp = WallGrid.FromMaterials(materials, layers);
        Assert.Empty(GridPathfinder.FindPath(noRamp, (0, 2), (0, 0)));

        var ramps = new bool[5, 3];
        ramps[4, 1] = true;
        var withRamp = WallGrid.FromMaterials(materials, layers, ramps);
        Assert.Contains((4, 1), GridPathfinder.FindPath(withRamp, (0, 2), (0, 0)));
    }

    [Fact]
    public void ReturnsJustTheStart_WhenStartEqualsGoal()
    {
        var grid = Grid(
            "...",
            "...",
            "...");

        var path = GridPathfinder.FindPath(grid, (1, 1), (1, 1));

        Assert.Equal(new[] { (1, 1) }, path);
    }

    [Fact]
    public void FindsAStraightPath_AcrossOpenGround()
    {
        var grid = Grid(
            ".....",
            ".....",
            ".....");

        var path = GridPathfinder.FindPath(grid, (0, 1), (4, 1));

        Assert.Equal((0, 1), path[0]);
        Assert.Equal((4, 1), path[^1]);
        AssertContiguousPassable(grid, path);
    }

    [Fact]
    public void RoutesAroundAWall_BetweenTwoOpenCells()
    {
        // A vertical wall in column 2 with a gap at the bottom row forces a detour.
        var grid = Grid(
            "..#..",
            "..#..",
            ".....");

        var path = GridPathfinder.FindPath(grid, (0, 0), (4, 0));

        Assert.NotEmpty(path);
        Assert.Equal((0, 0), path[0]);
        Assert.Equal((4, 0), path[^1]);
        AssertContiguousPassable(grid, path);
        // It had to dip to the open bottom row to get around the wall.
        Assert.Contains(path, cell => cell.Y == 2);
    }

    [Fact]
    public void ReturnsEmpty_WhenTheGoalIsWalledOff()
    {
        // The goal at (2, 1) is fully enclosed by steel.
        var grid = Grid(
            "#####",
            "#.#.#",
            "#####");

        var path = GridPathfinder.FindPath(grid, (1, 1), (3, 1));

        Assert.Empty(path);
    }

    [Fact]
    public void ReturnsEmpty_WhenTheStartItselfIsBlocked()
    {
        var grid = Grid(
            ".....",
            "..#..",
            ".....");

        var path = GridPathfinder.FindPath(grid, (2, 1), (0, 0));

        Assert.Empty(path);
    }

    [Fact]
    public void ReturnsEmpty_WhenTheGoalCellIsItselfAWall()
    {
        var grid = Grid(
            ".....",
            "..#..",
            ".....");

        var path = GridPathfinder.FindPath(grid, (0, 0), (2, 1));

        Assert.Empty(path);
    }

    [Fact]
    public void EveryStep_MovesToAnAdjacentPassableCell()
    {
        var grid = Grid(
            ".....",
            ".###.",
            ".....",
            ".###.",
            ".....");

        var path = GridPathfinder.FindPath(grid, (0, 0), (4, 4));

        Assert.NotEmpty(path);
        AssertContiguousPassable(grid, path);
    }

    [Fact]
    public void IsDeterministic_AcrossRepeatedRuns()
    {
        var grid = Grid(
            "..#..",
            "..#..",
            ".....",
            "..#..",
            "..#..");

        var a = GridPathfinder.FindPath(grid, (0, 0), (4, 4));
        var b = GridPathfinder.FindPath(grid, (0, 0), (4, 4));

        Assert.Equal(a, b);
    }

    // Asserts the path starts where asked, ends where asked, and every consecutive pair are
    // 4-adjacent cells that are passable (no diagonal hops, no stepping into a wall).
    private static void AssertContiguousPassable(IWallGrid grid, IReadOnlyList<(int X, int Y)> path)
    {
        foreach (var (x, y) in path)
        {
            Assert.False(CellMaterials.BlocksMovement(grid.GetCell(x, y).Material),
                $"path stepped into a blocking cell ({x},{y})");
        }

        for (var i = 1; i < path.Count; i++)
        {
            var dx = System.Math.Abs(path[i].X - path[i - 1].X);
            var dy = System.Math.Abs(path[i].Y - path[i - 1].Y);
            Assert.True(dx + dy == 1, $"non-adjacent step from {path[i - 1]} to {path[i]}");
        }
    }
}
