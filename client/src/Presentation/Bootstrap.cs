using Godot;
using TankGame.Infrastructure;
#if DEBUG && !GODOT_WEB
using System.Reflection;
using Chickensoft.GoDotTest;
#endif

namespace TankGame.Presentation;

// Composition-root entry point. In a test run (`--run-tests`) it hands the
// executing assembly to GoDotTest and quits with the result's exit code;
// otherwise it boots the real game scene. Kept separate from MainScene so the
// scene-under-test (Main.tscn) can be instantiated by tests without re-entering
// the runner. Test wiring is DEBUG-only, so ExportRelease builds exclude it.
public partial class Bootstrap : Node
{
#if DEBUG && !GODOT_WEB
    private TestEnvironment _environment = default!;
#endif

    public override void _Ready()
    {
#if DEBUG && !GODOT_WEB
        _environment = TestEnvironment.From(OS.GetCmdlineArgs());
        if (_environment.ShouldRunTests)
        {
            Callable.From(RunTests).CallDeferred();
            return;
        }

        if (TankGame.Tests.Presentation.NetE2EDriver.TryStart(GetTree()))
        {
            TranslationLoader.EnsureLoaded();
            return;
        }
#endif
        // App init happens once here (the composition root), then the play scene loads.
#if !GODOT_WEB
        SentryBootstrap.Init(); // Sentry's .NET SDK isn't WASM-compatible; web builds skip it.
#endif
        TranslationLoader.EnsureLoaded();

        // Deferred: the scene tree is mid-add during _Ready and rejects an
        // immediate scene swap ("parent node is busy adding/removing children").
        GetTree().CallDeferred(
            SceneTree.MethodName.ChangeSceneToFile, "res://src/Presentation/Title.tscn");
    }

#if DEBUG && !GODOT_WEB
    private void RunTests()
        => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment);
#endif
}
