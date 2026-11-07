using System;
using Godot;
using Chickensoft.GoDotTest;
using TankGame.GameLogic;
using TankGame.Presentation;

namespace TankGame.Tests.Presentation;

// Smoke test for the 3D arena (ADR-0017): the scene must instantiate, load the tank GLB, and wire the
// player + AI as 3D views under a 3D camera without crashing headless.
public class Arena3DSceneTests : TestClass
{
    private Node _arena = default!;

    public Arena3DSceneTests(Node testScene) : base(testScene) { }

    private string _previousPlayerName = "";

    [Setup]
    public void Setup()
    {
        _previousPlayerName = GameSetup.PlayerName;
        GameSetup.PlayerName = "Tester";
        _arena = GD.Load<PackedScene>("res://src/Presentation/Arena/Arena3D.tscn").Instantiate();
        TestScene.AddChild(_arena); // runs Arena3DScene._Ready
    }

    // Free the scene immediately (not QueueFree, which defers) and force a GC so no managed wrapper to one
    // of the scene's freed resources (Environment, meshes, materials) lingers to the engine's C# shutdown —
    // that lingering binding is what trips the fatal "Leaked unsafe reference" teardown crash once the scene
    // holds enough resources (the teleport pad rings pushed it over).
    [Cleanup]
    public void Cleanup()
    {
        GameSetup.PlayerName = _previousPlayerName;
        if (GodotObject.IsInstanceValid(_arena))
        {
            _arena.Free();
        }

        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
    }

    [Test]
    public void TheHud_CountsTheAliveEnemies_FromTheFirstFrame()
    {
        TankGame.Infrastructure.TranslationLoader.EnsureLoaded();
        var scene = (Arena3DScene)_arena;
        scene._Process(1d / 60d);

        var label = _arena.FindChild("EnemiesLeft", recursive: true, owned: false) as Label
            ?? throw new Exception("The arena must show the alive-enemies counter.");
        if (!label.Text.Contains(scene.AliveEnemyTanks.ToString(System.Globalization.CultureInfo.InvariantCulture))
            || scene.AliveEnemyTanks == 0)
        {
            throw new Exception($"The counter must show {scene.AliveEnemyTanks} enemies; showed '{label.Text}'.");
        }
    }

    // Victory screen (owner ask 2026-06-11/14, rebuilt): a generated celebration backdrop with the
    // screen composed from real UI controls — the champion's name on a wood ribbon (a team win names
    // the top-killer member), a metal plaque of up to eight ranked rows, and nav arrows switching the
    // ranking sheet.
    [Test]
    public void ShowMatchOver_PresentsTheBannerArt_Champion_AndPannableLeaderboard()
    {
        var scene = (Arena3DScene)_arena;
        scene.ShowMatchOver(new MatchResult(Decided: true, WinningTeam: 0)); // the player team survived

        if (!scene.IsMatchOver)
        {
            throw new System.Exception("Showing match-over must freeze the match.");
        }

        var banner = _arena.FindChild("Backdrop", recursive: true, owned: false) as TextureRect;
        if (banner?.Texture is null)
        {
            throw new System.Exception("The match-over screen must show the victory backdrop artwork.");
        }

        // A fresh match has no kills — the tie-break lands on the first winning-team member, the
        // player, whose chosen name is "Tester" (set in Setup).
        var winner = _arena.FindChild("WinnerName", recursive: true, owned: false) as Label
            ?? throw new System.Exception("The banner must carry the champion's name.");
        if (winner.Text != "Tester")
        {
            throw new System.Exception($"The winning team's top member must headline; got '{winner.Text}'.");
        }

        foreach (var name in new[] { "NewGame", "BackToMenu", "ExitGame", "PrevView", "NextView" })
        {
            if (_arena.FindChild(name, recursive: true, owned: false) is not Button)
            {
                throw new System.Exception($"The match-over screen must offer a '{name}' button.");
            }
        }

        var rows = _arena.FindChild("LeaderboardRows", recursive: true, owned: false) as Control
            ?? throw new System.Exception("The match-over screen must show the leaderboard rows.");
        if (rows.GetChildCount() != 4)
        {
            throw new System.Exception($"All four tanks must rank on the plates; saw {rows.GetChildCount()} rows.");
        }

        if (rows.GetParent() is not ScrollContainer)
        {
            throw new System.Exception("The leaderboard rows must sit in a ScrollContainer so the full eight-tank cap scrolls.");
        }

        var title = _arena.FindChild("ViewTitle", recursive: true, owned: false) as Label
            ?? throw new System.Exception("The leaderboard must name its current view.");
        if (title.Text != "stats.kills")
        {
            throw new System.Exception($"The board opens on kills; got '{title.Text}'.");
        }

        scene.SwitchView(1);
        if (title.Text != "stats.deaths")
        {
            throw new System.Exception($"The arrows must pan to the next ranking view; got '{title.Text}'.");
        }

        scene.SwitchView(-2); // wraps backward past the first view
        if (title.Text != "stats.assists")
        {
            throw new System.Exception($"Panning must wrap around the view ring; got '{title.Text}'.");
        }
    }

    // Every tank carries a name tag above its bars (owner feedback 2026-06-11): the player's chosen
    // name, derpy generated ones for the AI. The tag is a child of the view, so concealment (which
    // hides the whole view) hides the name too — a bush-lurker's name never floats over the bush.
    [Test]
    public void Arena3D_NamesEveryTank_PlayerFromSetup_EnemiesFromTheGenerator()
    {
        var names = new System.Collections.Generic.List<string>();
        var sawPlayerName = false;
        foreach (var child in _arena.GetChildren())
        {
            if (child is not Tank3DView view)
            {
                continue;
            }

            var tag = view.FindChild("NameTag", recursive: true, owned: false) as Label3D
                ?? throw new System.Exception("Every tank view must carry a 'NameTag' label.");
            if (string.IsNullOrWhiteSpace(tag.Text))
            {
                throw new System.Exception("Every tank in a solo match must be named.");
            }

            // The real visibility recipe (owner: invisible twice): Label3D's default PixelSize makes
            // any font sub-unit tall at this world scale — the tag must stand tens of world units —
            // and a dark backing panel keeps the text readable against any terrain.
            if (tag.FontSize * tag.PixelSize < 18f || !tag.NoDepthTest)
            {
                throw new System.Exception(
                    $"A name tag must stand tens of world units tall; this one is {tag.FontSize * tag.PixelSize:0.0}.");
            }

            if (view.FindChild("NameBacking", recursive: true, owned: false) is not MeshInstance3D)
            {
                throw new System.Exception("A name tag needs its dark backing panel to read against terrain.");
            }

            names.Add(tag.Text);
            sawPlayerName |= tag.Text == "Tester";
        }

        if (!sawPlayerName)
        {
            throw new System.Exception("The player's tank must wear the name chosen at the title screen.");
        }

        if (names.Count != new System.Collections.Generic.HashSet<string>(names).Count)
        {
            throw new System.Exception("Tank names must be unique within a match.");
        }
    }

    [Test]
    public void Arena3D_WiresTanksAndGround_UnderA3DCamera()
    {
        var tankViews = 0;
        var teleportPads = 0;
        Camera3D? camera = null;
        MeshInstance3D? ground = null;
        Terrain3DView? terrain = null;
        foreach (var child in _arena.GetChildren())
        {
            switch (child)
            {
                case Tank3DView:
                    tankViews++;
                    break;
                case TeleportPad3DView:
                    teleportPads++;
                    break;
                case Camera3D cam:
                    camera = cam;
                    break;
                case Terrain3DView t:
                    terrain = t;
                    break;
                case MeshInstance3D g when g.Name.ToString() == "Ground":
                    ground = g;
                    break;
            }
        }

        if (tankViews < 2)
        {
            throw new System.Exception($"3D arena must instance the player plus AI; saw {tankViews} tanks.");
        }

        if (camera is null || camera.Projection != Camera3D.ProjectionType.Orthogonal)
        {
            throw new System.Exception("3D arena must have an orthographic Camera3D.");
        }

        if (ground is null)
        {
            throw new System.Exception("3D arena must lay a ground mesh.");
        }

        // The generated arena has a steel border, so the terrain view must hold wall meshes.
        if (terrain is null || terrain.GetChildCount() == 0)
        {
            throw new System.Exception("3D arena must render terrain (walls) via a Terrain3DView.");
        }

        // Teleport pads ship as a linked pair of glowing ground rings (teleport pads T1).
        if (teleportPads != 2)
        {
            throw new System.Exception($"3D arena must place a linked pair of teleport pad rings; saw {teleportPads}.");
        }

        // A custom map with authored pads overrides the auto-placement (teleport pads T2): two authored
        // links must yield four rings. Folded into this test (not a new one) to keep the leak-prone scene
        // instantiations to a minimum; the extra scene is freed here and the class's Cleanup GCs.
        AssertAuthoredPadsOverrideAutoPlacement();
    }

    private void AssertAuthoredPadsOverrideAutoPlacement()
    {
        var map = MapDefinition.CreateBlank("Authored Pads", 12, 12);
        var custom = new MapDefinition(
            map.Name, map.Materials, map.Bushes, map.Sandbags,
            (1, 1),
            new (int X, int Y)[] { (10, 10) },
            System.Array.Empty<PowerupSpawn>(),
            new[] { new TeleportPadLink(2, 2, 9, 9), new TeleportPadLink(2, 9, 9, 2) });

        Node custscene = default!;
        try
        {
            GameSetup.StartNewMatch(GameMode.OnePlayer);
            GameSetup.CustomMap = custom;
            custscene = GD.Load<PackedScene>("res://src/Presentation/Arena/Arena3D.tscn").Instantiate();
            TestScene.AddChild(custscene); // runs _Ready with the authored pads

            var rings = 0;
            foreach (var child in custscene.GetChildren())
            {
                if (child is TeleportPad3DView)
                {
                    rings++;
                }
            }

            if (rings != 4)
            {
                throw new System.Exception($"Two authored pad links must place four rings; saw {rings}.");
            }
        }
        finally
        {
            GameSetup.CustomMap = null;
            if (GodotObject.IsInstanceValid(custscene))
            {
                custscene.Free();
            }
        }
    }

    [Test]
    public void Arena3D_EscapeMenu_PausesAndFreezesTheMatch()
    {
        var scene = (Arena3DScene)_arena;
        if (scene.IsPaused)
        {
            throw new System.Exception("The match should start unpaused.");
        }

        scene.TogglePause();
        if (!scene.IsPaused)
        {
            throw new System.Exception("Opening the Escape menu should pause the match.");
        }

        var pauseLayer = _arena.FindChild("PauseMenu", recursive: true, owned: false) as CanvasLayer
            ?? throw new System.Exception("3D arena must build a PauseMenu overlay.");
        if (!pauseLayer.Visible)
        {
            throw new System.Exception("The pause overlay should be visible while paused.");
        }

        foreach (var name in new[] { "Resume", "MainMenu", "ExitGame" })
        {
            if (pauseLayer.FindChild(name, recursive: true, owned: false) is not Button)
            {
                throw new System.Exception($"The pause menu must offer a '{name}' button.");
            }
        }

        scene.TogglePause();
        if (scene.IsPaused || pauseLayer.Visible)
        {
            throw new System.Exception("Resuming should unpause and hide the overlay.");
        }
    }

    [Test]
    public void Arena3D_FogsTheField_WithALightAroundThePlayer()
    {
        // Setup() loads the single-player 3D arena. Fog darkens the world (dark ambient) and lights only
        // a circle around the player via a SpotLight3D that follows the player on the ground plane.
        var scene = (Arena3DScene)_arena;

        var fogLight = _arena.FindChild("FogLight", recursive: true, owned: false) as SpotLight3D
            ?? throw new System.Exception("3D fog must add a SpotLight3D 'FogLight' around the player.");

        var player = scene.PlayerWorldPosition;
        var lit = fogLight.Position;
        if (Mathf.Abs(lit.X - player.X) > 1f || Mathf.Abs(lit.Z - player.Z) > 1f)
        {
            throw new System.Exception(
                $"The fog light must be centred over the player (light {lit} vs player {player}).");
        }

        if (lit.Y <= player.Y)
        {
            throw new System.Exception("The fog light must hang above the player to light a ground circle.");
        }

        WorldEnvironment? worldEnv = null;
        foreach (var child in _arena.GetChildren())
        {
            if (child is WorldEnvironment we)
            {
                worldEnv = we;
            }
        }

        if (worldEnv?.Environment is null || worldEnv.Environment.AmbientLightColor.Luminance > 0.3f)
        {
            throw new System.Exception("Fog must darken the world's ambient so only the lit circle reads as visible.");
        }
    }

    [Test]
    public void Arena3D_SpawnsThePickups_AsPowerupViews()
    {
        var powerups = 0;
        foreach (var child in _arena.GetChildren())
        {
            if (child is Powerup3DView)
            {
                powerups++;
            }
        }

        if (powerups != 9)
        {
            throw new System.Exception($"3D arena must spawn the nine field pickups; saw {powerups}.");
        }
    }
}
