using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TankGame.Domain;
using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

public class RespawnPlacerTests
{
    private const float Tile = 64f;

    private sealed class Still : IInputSource
    {
        public TankInput Read() => new(Vector2.Zero, 0f, Fire: false);
    }

    private sealed class Driving : IInputSource
    {
        public Vector2 Move { get; set; }

        public TankInput Read() => new(Move, 0f, Fire: false);
    }

    private static WallGrid Open(int width, int height, Action<CellMaterial[,]>? paint = null)
    {
        var materials = new CellMaterial[width, height];
        paint?.Invoke(materials);
        return WallGrid.FromMaterials(materials);
    }

    private static RespawnPlacer Placer(IWallGrid grid, int seed = 1, Func<Vector2, bool>? reserved = null, params (int X, int Y)[] anchors) =>
        new(grid, Tile, Vector2.Zero, anchors.Length > 0 ? anchors : new[] { (0, 0) }, new Random(seed), reserved: reserved);

    private static (int X, int Y) CellOf(Vector2 point) => ((int)(point.X / Tile), (int)(point.Y / Tile));

    private static Tank TankAt(World world, Vector2 position, IInputSource? input = null, Func<Vector2>? respawnPoint = null, int lives = 1)
    {
        var tank = new Tank(input ?? new Still(), world, new GridArena(Open(60, 60), Tile, Vector2.Zero), position,
            200f, 0.5f, 600f, lives: lives, respawnPoint: respawnPoint);
        world.Spawn(tank);
        return tank;
    }

    [Fact]
    public void Pick_NeverLandsOnWaterLavaWallOrBuilding()
    {
        var grid = Open(20, 20, m =>
        {
            for (var y = 0; y < 20; y++)
            {
                m[5, y] = CellMaterial.Bridge;
                m[6, y] = y % 2 == 0 ? CellMaterial.Water : CellMaterial.Lava;
                m[7, y] = y % 2 == 0 ? CellMaterial.Building : CellMaterial.Steel;
                m[8, y] = CellMaterial.Brick;
                m[9, y] = y % 2 == 0 ? CellMaterial.Mountain : CellMaterial.Crate;
            }
        });

        var placer = Placer(grid);
        for (var i = 0; i < 300; i++)
        {
            var (x, y) = CellOf(placer.Pick(Array.Empty<IEntity>()));
            Assert.True(grid.GetCell(x, y).Material is CellMaterial.Floor or CellMaterial.Bridge, $"picked ({x},{y})");
        }
    }

    [Fact]
    public void Pick_KeepsTheSafeRadiusFromEveryLiveTank()
    {
        var world = new World();
        for (var i = 0; i < 8; i++)
        {
            TankAt(world, new Vector2((i * 6 * Tile) + 32f, 960f));
        }

        var placer = Placer(Open(50, 30));
        for (var i = 0; i < 300; i++)
        {
            var point = placer.Pick(world.Entities);
            foreach (var tank in world.Entities.OfType<ITank>())
            {
                Assert.True(Vector2.Distance(point, tank.Position) >= RespawnPlacer.SafeRadiusCells * Tile);
            }
        }
    }

    [Fact]
    public void Pick_OnACrowdedMap_StillAvoidsLandingOnATank()
    {
        var world = new World();
        for (var x = 0; x < 3; x++)
        {
            TankAt(world, new Vector2((x * Tile) + 32f, 32f));
        }

        var point = Placer(Open(4, 1)).Pick(world.Entities);

        Assert.Equal((3, 0), CellOf(point)); // the only cell not under a tank
    }

    [Fact]
    public void Pick_SkipsSealedPockets_ButOpensThemWhenABreakableWallIsTheOnlyBarrier()
    {
        // Column 5 is steel: the far side can never be reached from the anchor.
        var sealedOff = Open(10, 3, m =>
        {
            for (var y = 0; y < 3; y++)
            {
                m[5, y] = CellMaterial.Steel;
            }
        });
        // Column 5 is brick: a tank can shoot through, so the far side is in play.
        var breakable = Open(10, 3, m =>
        {
            for (var y = 0; y < 3; y++)
            {
                m[5, y] = CellMaterial.Brick;
            }
        });

        var sealedPlacer = Placer(sealedOff);
        var breakablePlacer = Placer(breakable);
        var reachedFarSide = false;
        for (var i = 0; i < 200; i++)
        {
            Assert.True(CellOf(sealedPlacer.Pick(Array.Empty<IEntity>())).X < 5);
            reachedFarSide |= CellOf(breakablePlacer.Pick(Array.Empty<IEntity>())).X > 5;
        }

        Assert.True(reachedFarSide);
    }

    [Fact]
    public void Pick_SkipsReservedPoints_SuchAsTeleportPads()
    {
        var placer = Placer(Open(3, 1), reserved: p => CellOf(p).X != 2);

        Assert.Equal((2, 0), CellOf(placer.Pick(Array.Empty<IEntity>())));
    }

    [Fact]
    public void Pick_StaysOnItsLayer_AndOffRamps()
    {
        var materials = new CellMaterial[6, 1];
        var layers = new int[6, 1];
        var ramps = new bool[6, 1];
        layers[4, 0] = layers[5, 0] = 1;
        ramps[3, 0] = true;
        var grid = WallGrid.FromMaterials(materials, layers, ramps);

        var placer = Placer(grid);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(CellOf(placer.Pick(Array.Empty<IEntity>())).X < 3);
        }
    }

    [Fact]
    public void TanksThatDieTogether_ComeBackApart()
    {
        var world = new World();
        var placer = Placer(Open(40, 40), seed: 7);
        var tanks = Enumerable.Range(0, 8)
            .Select(i => TankAt(world, new Vector2(32f + (i * 50f), 32f), respawnPoint: () => placer.Pick(world.Entities), lives: 2))
            .ToList();

        foreach (var tank in tanks)
        {
            tank.TakeDamage(tank.Hp);
        }

        for (var i = 0; i < 200; i++)
        {
            world.Step(1f / 60f);
        }

        for (var a = 0; a < tanks.Count; a++)
        {
            Assert.True(tanks[a].Hp > 0);
            for (var b = a + 1; b < tanks.Count; b++)
            {
                Assert.True(Vector2.Distance(tanks[a].Position, tanks[b].Position) >= RespawnPlacer.SafeRadiusCells * Tile);
            }
        }
    }

    [Fact]
    public void TanksExactlyOnTopOfEachOther_CanBothDriveApart()
    {
        var world = new World();
        var mover = TankAt(world, new Vector2(500f, 500f), new Driving { Move = new Vector2(1f, 0f) });
        var other = TankAt(world, new Vector2(500f, 500f), new Driving { Move = new Vector2(-1f, 0f) });

        for (var i = 0; i < 30; i++)
        {
            world.Step(1f / 60f);
        }

        Assert.True(Vector2.Distance(mover.Position, other.Position) >= 2f * Tank.CollisionRadius - 1f);
    }

    [Fact]
    public void OverlappingTank_DrivingAway_Escapes_AndCannotDriveDeeperIn()
    {
        var world = new World();
        var input = new Driving { Move = new Vector2(-1f, 0f) };
        var mover = TankAt(world, new Vector2(500f, 500f), input);
        var other = TankAt(world, new Vector2(510f, 500f));

        world.Step(1f / 60f);
        Assert.True(mover.Position.X < 500f, "driving away from an overlap must work");

        input.Move = new Vector2(1f, 0f);
        var before = mover.Position;
        world.Step(1f / 60f);
        Assert.Equal(before, mover.Position); // toward the other tank stays blocked
        _ = other;
    }

    [Theory]
    [InlineData("Forest")]
    [InlineData("Volcano")]
    [InlineData("City")]
    [InlineData("Frozen")]
    [InlineData("Canyon")]
    [InlineData("Donut")]
    [InlineData("Cross")]
    [InlineData("Archipelago")]
    public void EveryBuiltInArena_OffersRoomToRespawn_OnlyOnSafeFooting(string arenaId)
    {
        Assert.True(ArenaBuilders.TryGet(arenaId, out var builder));
        var layout = builder.Build(3);
        var grid = layout.Map.BuildGrid();
        var anchors = new List<(int X, int Y)>(layout.EnemySpawns) { layout.PlayerSpawn };
        var placer = new RespawnPlacer(grid, Tile, Vector2.Zero, anchors, new Random(5));
        var arena = new GridArena(grid, Tile, Vector2.Zero);

        var distinct = new HashSet<(int, int)>();
        for (var i = 0; i < 100; i++)
        {
            var point = placer.Pick(Array.Empty<IEntity>());
            var (x, y) = CellOf(point);
            distinct.Add((x, y));
            Assert.False(arena.IsBlocked(point, 0), $"{arenaId}: blocked ({x},{y})");
            Assert.False(CellMaterials.IsLethal(grid.GetCell(x, y).Material), $"{arenaId}: lethal ({x},{y})");
        }

        Assert.True(distinct.Count > 20, $"{arenaId}: only {distinct.Count} distinct respawn cells");
    }
}
