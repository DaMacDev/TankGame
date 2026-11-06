using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TankGame.Domain;
using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

/// <summary>Full headless matches: bots on a real large map, a player who stands still. A bot that
/// wanders in place, or only reacts to what it sees, never arrives.</summary>
public class AiRoamingTests
{
    private const float Tile = 64f;
    private const float StepSeconds = 1f / 60f;

    private sealed class Still : IInputSource
    {
        public TankInput Read() => new(Vector2.Zero, 0f, Fire: false);
    }

    // Seconds until the first bot gets within firing range of the player, or the limit.
    private static float SecondsToFirstContact(int seed, float limitSeconds)
    {
        var layout = new ArenaGenerator().Generate(new ArenaGenParams(60, 60, seed, EnemyCount: 7, PickupCount: 0));
        var grid = layout.Map.BuildGrid();
        var arena = new GridArena(grid, Tile, Vector2.Zero);
        var world = new World(); // no combat: this measures travel, not who wins

        static Vector2 Centre((int X, int Y) c) => new((c.X + 0.5f) * Tile, (c.Y + 0.5f) * Tile);

        var player = new Tank(new Still(), world, arena, Centre(layout.PlayerSpawn), 200f, 0.6f, 600f, maxHp: 8, team: 0);
        world.Spawn(player);
        var players = new ITank[] { player };
        var botIndex = 0;
        foreach (var spawn in layout.EnemySpawns)
        {
            var ai = new AiInputSource(world, arena, grid: grid, tileSize: Tile, origin: Vector2.Zero,
                seed: seed + (botIndex++ * 31), players: players);
            var bot = new Tank(ai, world, arena, Centre(spawn), 200f, 0.6f, 600f, maxHp: 8, team: 1);
            ai.Bind(bot);
            world.Spawn(bot);
        }

        var steps = (int)(limitSeconds / StepSeconds);
        for (var step = 0; step < steps; step++)
        {
            world.Step(StepSeconds);
            foreach (var bot in world.Entities.OfType<ITank>().Where(t => !ReferenceEquals(t, player)))
            {
                if (Vector2.Distance(bot.Position, player.Position) <= 420f)
                {
                    return step * StepSeconds;
                }
            }
        }

        return limitSeconds;
    }

    [Fact]
    public void BotsReachAStillPlayer_Quickly_OnALargeMap()
    {
        foreach (var seed in Enumerable.Range(1, 8))
        {
            var seconds = SecondsToFirstContact(seed, 45f);
            Assert.True(seconds < 45f, $"seed {seed}: no bot reached the player in 45 s");
        }
    }

    [Fact]
    public void ARoamingBot_CoversTheMap_InsteadOfStayingInOnePlace()
    {
        // A lone bot and nobody to find: it can only explore.
        var grid = WallGrid.FromMaterials(new CellMaterial[60, 60]);
        var arena = new GridArena(grid, Tile, Vector2.Zero);
        var world = new World();
        var ai = new AiInputSource(world, arena, grid: grid, tileSize: Tile, origin: Vector2.Zero, seed: 11);
        var bot = new Tank(ai, world, arena, new Vector2(1920f, 1920f), 200f, 0.6f, 600f);
        ai.Bind(bot);
        world.Spawn(bot);

        var visited = new HashSet<(int, int)>();
        for (var step = 0; step < 60 * 60; step++)
        {
            world.Step(StepSeconds);
            visited.Add(((int)(bot.Position.X / (Tile * 10f)), (int)(bot.Position.Y / (Tile * 10f))));
        }

        Assert.True(visited.Count >= 8, $"one minute of roaming touched only {visited.Count} of 36 map regions");
    }

    [Fact]
    public void AfterAQuietSpell_ABotHeadsStraightForItsNearestEnemy_EvenOutOfSight()
    {
        // 'seek chance' cannot explain this: the only other tank sits behind a full steel wall gap.
        var materials = new CellMaterial[60, 10];
        for (var y = 0; y < 9; y++)
        {
            materials[30, y] = CellMaterial.Steel;
        }

        var grid = WallGrid.FromMaterials(materials);
        var arena = new GridArena(grid, Tile, Vector2.Zero);
        var world = new World();
        var prey = new Tank(new Still(), world, arena, new Vector2(3200f, 96f), 200f, 0.6f, 600f, team: 0);
        world.Spawn(prey);
        var ai = new AiInputSource(world, arena, grid: grid, tileSize: Tile, origin: Vector2.Zero, seed: 5);
        var bot = new Tank(ai, world, arena, new Vector2(96f, 96f), 200f, 0.6f, 600f, team: 1);
        ai.Bind(bot);
        world.Spawn(bot);

        var quiet = (int)((EnemyCompass.QuietSeconds + 1f) / StepSeconds);
        for (var step = 0; step < quiet + (60 * 25); step++)
        {
            world.Step(StepSeconds);
        }

        Assert.True(Vector2.Distance(bot.Position, prey.Position) < 500f,
            $"the bot ended {Vector2.Distance(bot.Position, prey.Position):F0} away from its enemy");
    }

    [Fact]
    public void AnUnreachableGoal_NeverSendsABotStraightOverLava()
    {
        // The player sits inside a brick ring (no route), behind solid lava: a straight line is fatal.
        var materials = new CellMaterial[30, 7];
        for (var y = 0; y < 7; y++)
        {
            materials[12, y] = CellMaterial.Lava;
        }

        for (var x = 19; x <= 21; x++)
        {
            materials[x, 2] = CellMaterial.Brick;
            materials[x, 4] = CellMaterial.Brick;
        }

        materials[19, 3] = CellMaterial.Brick;
        materials[21, 3] = CellMaterial.Brick;
        var grid = WallGrid.FromMaterials(materials);
        var arena = new GridArena(grid, Tile, Vector2.Zero);
        var world = new World();
        var prey = new Tank(new Still(), world, arena, new Vector2(20.5f * Tile, 3.5f * Tile), 200f, 0.6f, 600f, team: 0);
        world.Spawn(prey);
        var ai = new AiInputSource(world, arena, grid: grid, tileSize: Tile, origin: Vector2.Zero, seed: 3,
            players: new ITank[] { prey });
        var bot = new Tank(ai, world, arena, new Vector2(96f, 3.5f * Tile), 200f, 0.6f, 600f, team: 1);
        ai.Bind(bot);
        world.Spawn(bot);

        for (var step = 0; step < 60 * 40; step++)
        {
            world.Step(StepSeconds);
        }

        Assert.True(bot.Hp > 0, "the bot drove into lava");
    }

    [Fact]
    public void InTeamMode_ABotLeavesItsHumanAllyAlone_AndFightsTheOtherSide()
    {
        var world = new World();
        var arena = new GridArena(WallGrid.FromMaterials(new CellMaterial[20, 20]), Tile, Vector2.Zero);
        var ally = new Tank(new Still(), world, arena, new Vector2(400f, 300f), 200f, 0.6f, 600f, team: 0);
        var foe = new Tank(new Still(), world, arena, new Vector2(100f, 300f), 200f, 0.6f, 600f, team: 1);
        world.Spawn(ally);
        world.Spawn(foe);
        var ai = new AiInputSource(world, arena, seed: 1, players: new ITank[] { ally }, respectTeams: true);
        var bot = new Tank(ai, world, arena, new Vector2(300f, 300f), 200f, 0.6f, 600f, team: 0);
        ai.Bind(bot);
        world.Spawn(bot);

        var intent = ai.Read();

        Assert.True(intent.Fire, "the enemy bot in range must draw fire: the only human is an ally");
        Assert.True(MathF.Abs(MathF.Abs(intent.Aim) - MathF.PI) < 0.01f, "aimed at the foe, not the ally");
    }

    [Fact]
    public void RoamingBots_RouteAroundLava()
    {
        // A lava band with one bridge gap: straight lines and lava-blind routes die here.
        var materials = new CellMaterial[30, 20];
        for (var y = 0; y < 20; y++)
        {
            materials[15, y] = y == 10 ? CellMaterial.Bridge : CellMaterial.Lava;
        }

        var grid = WallGrid.FromMaterials(materials);
        var arena = new GridArena(grid, Tile, Vector2.Zero);
        var world = new World();
        var prey = new Tank(new Still(), world, arena, new Vector2(1824f, 160f), 200f, 0.6f, 600f, team: 0);
        world.Spawn(prey);
        var ai = new AiInputSource(world, arena, grid: grid, tileSize: Tile, origin: Vector2.Zero, seed: 9,
            players: new ITank[] { prey });
        var bot = new Tank(ai, world, arena, new Vector2(96f, 96f), 200f, 0.6f, 600f, team: 1);
        ai.Bind(bot);
        world.Spawn(bot);

        for (var step = 0; step < 60 * 40; step++)
        {
            world.Step(StepSeconds);
        }

        Assert.True(bot.Hp > 0, "the bot drove into lava");
        Assert.True(bot.Position.X > 15f * Tile, "the bot never crossed the bridge");
    }
}
