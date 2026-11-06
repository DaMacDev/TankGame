# Web (WASM) export + Lundrea Arcade deploy

Status as of 2026-07-17: the web layer is **reconciled into `main`** — the WASM export builds
from `main` alone, no side branch. All web-only code is `#if GODOT_WEB`-guarded or additive, so
desktop/Android/test builds are unaffected. The live single-player build is at
<https://lundrea-arcade.web.app/tank/index.html> (arcade home: <https://lundrea-arcade.web.app>).

The old branches `feat/web-export`, `web-export-deploy`, `web-reconcile`, and `web-reconcile-256`
are fully reconciled (ported or superseded) and can be deleted.

---

## 1. Toolchain (what another machine needs)

- **Custom Godot editor with C# web export** — stock Godot can't export .NET to web. Use the
  ComplexRobot fork: <https://github.com/ComplexRobot/godot-dotnet-web-export>. Download the
  `*_mono_web_export_win64` release, extract it, run its `install.bat` (adds the web export
  templates to `%AppData%\Godot\export_templates\4.6.2.stable.mono\` and a local NuGet source). On
  the reference machine it lives at `C:\godot-web-export\Godot_v4.6.2-stable_mono_web_export_win64\`.
- **.NET `wasm-tools` workload** — `dotnet workload install wasm-tools`. **VERIFY with
  `dotnet workload list`** — a prior install silently failed to take; without it the export still
  produces a bundle but the managed C# is never compiled into WASM (tiny ~4 MB `.pck`, black screen).
  When present, `.pck` is ~48 MB.

## 2. Build & export

```sh
EDITOR="C:/godot-web-export/.../Godot_v4.6.2-stable_mono_web_export_win64.exe"
# 1. Reimport ONCE after a fresh pull — regenerates .import/.uid/.translation (all gitignored):
"$EDITOR" --headless --path client --import
# 2. Export (output goes to build/web/, gitignored):
"$EDITOR" --headless --path client --export-release "Web" "$(pwd)/build/web/index.html"
```

The export needs the .NET 9 SDK. If a newer SDK is the machine default, the build stops with
`NETSDK1147 ... wasm-tools-net9`: copy the web-export editor's `global.json` into `client/` for the
export, and don't commit it.

Serve locally with COOP/COEP to test (the build needs cross-origin isolation): any server sending
`Cross-Origin-Opener-Policy: same-origin` + `Cross-Origin-Embedder-Policy: require-corp` works.

## 3. The web layer on `main` (what makes the WASM build work)

| File | Change | Why |
|---|---|---|
| `client/TankGame.csproj` | web `PropertyGroup`: `OutputType=Exe` + `Program.cs`, `WasmEnableThreads`, `GODOT_WEB` define, Sentry+GoDotTest excluded, `TrimmerRootAssembly` for `System.Private.CoreLib`, `System.Runtime`, **`TankGame`, `GodotSharp`** | wasm needs an entry point; the trimmer would otherwise prune the whole game (the engine calls `GodotPlugins.Game.Main` natively, so it's invisible to the trimmer) |
| `client/Program.cs`, `client/TankGame.sln` | `{}` entry point + hand-written solution | the static-mono runtime needs a top-level entry + a solution to publish |
| `client/src/GameLogic/EntityId.cs` | crypto-free counter-based entity ids on web (`#if GODOT_WEB`; desktop keeps `Guid.NewGuid`) | **`Guid.NewGuid()` returns the SAME value every call on this runtime** (no crypto RNG) → all tanks shared one id → `tank.Id == shot.Owner` / `== self.Id` always true → no damage + AI never fires. This was *the* combat bug. Every entity id call site (`Tank`, `Projectile`, `Airstrike`, `Powerup`, `NetTank`, `NetProjectile`, `NetPowerup`, `PowerupDirector`) goes through `EntityId.Next()`. |
| `client/src/Presentation/GameMode.cs` | `ArenaSeed` from `HashCode.Combine(EntityId.Next(), DateTime.UtcNow.Ticks)` | `Guid.NewGuid` is constant on WASM, and EntityId's web counter restarts at 0 every page load — either alone rolls the identical "random" arena every web session |
| `client/src/Infrastructure/TranslationLoader.cs`, `SfxPool.LoadOgg` | `GD.Load` fallback when raw `FileAccess` fails | exports ship only the *imported* resources, not raw source files (csv/ogg) |
| `client/src/Presentation/Bootstrap.cs` | Sentry + test-runner skipped on web | neither works on the WASM runtime |
| `client/src/Infrastructure/PlatformExit.cs` | Exit → `JavaScriptBridge.Eval` back to the arcade on web | `GetTree().Quit()` is a no-op in a browser (button reads "Back to Arcade") |
| `client/src/Infrastructure/Net/GodotHttpLobbyClient.cs` | web lobby HTTP over Godot `HttpRequest` nodes | .NET `HttpClient` dies with an NRE in `BrowserHttpInterop` on the threaded WASM runtime; desktop keeps `HttpLobbyClient`, both parse via the shared `LobbyWire` |
| `client/export_presets.cfg` | `[preset.1]` Web preset, `canvas_resize_policy=2` (adaptive) | policy 1 locked the canvas to base resolution — rendered in a corner of the iframe |
| `client/audio/sfx/fire.ogg` | short cartoon "pew" (4.4 KB) | replaced a harsh 40 KB continuous tone; also shrinks the web bundle. `SfxPool.FireOffsetDb` re-levelled −20 → −6 dB for the quieter clip (final level owner-ear-gated) |

### The 5 web-only bugs that had to be fixed (in order discovered)
1. `wasm-tools` not actually installed → managed C# never compiled → black screen.
2. Trimmer pruned the game (rooted `TankGame`/`GodotSharp`).
3. Exports drop raw `FileAccess` resources → untranslated menu, no SFX (GD.Load fallbacks).
4. `Guid.NewGuid()` collision → no combat damage, AI never fires (counter-based `EntityId`).
5. GitHub LFS bandwidth quota broke the CI deploy → store the bundle as **plain git blobs** (both files < 100 MB).

Superseded-along-the-way (never ported; `main`'s versions won): the old branch's inline
fixed-timestep loop (main has swept-collision fixed-step combat, #243), its time-less
`KillStreakTracker`, its TitleScene web gating (main's slim menu + `PlatformExit`, #248),
and its .NET-HttpClient-era lobby plumbing (host-authoritative redesign, ADR-0019/0021, #245+).

## 4. Deploy to Lundrea Arcade (ProjectX repo)

Deploy is **CI-on-push to ProjectX `main`** (GitHub Actions → `vite build` → Firebase Hosting). To
ship a new bundle:

```sh
cp -r build/web/* C:/programmering/ProjectX/public/tank/      # overwrite the vendored bundle
# in ProjectX: branch off main, commit, PR, merge → auto-deploys
```

ProjectX integration (already in place on `main`): `public/tank/` bundle as **plain git blobs**
(NOT LFS — quota); `firebase.json` scoped `/tank/**` COOP/COEP headers; vite PWA
`globIgnores:['tank/**']`; `src/arcade/games.ts` `tank` entry with `external:true`; contract/e2e
suites skip `external` games; tile `public/tiles/tank.webp`.

**Caveats:** Spark free tier ≈ 15–20 cold loads/day (≈58 MB bundle; repeat visits cached/free).
One harmless web-audio `sample_set_pause` console warning. Map editor is desktop-only.

## 5. Multiplayer status

The 2026-06-27 multiplayer spec is **delivered on `main`**: slim title menu
(Solo · Multiplayer · Settings · Back-to-Arcade), lobby browser + lobby room for up to 8 players
(`LobbyProtocol.MaxPlayers`), countdown start, host-authoritative relay netcode over the
`tankgame-worker` Cloudflare Durable Objects (ADR-0019 relay, ADR-0021 lobby directory), net
pickups, victory screen + online rematch (#259/#262). See those ADRs for design; this doc only
covers the web build/deploy.

## 6. Resume checklist (fresh machine)

1. Pull TankGame `main`; pull ProjectX `main`.
2. Install the ComplexRobot Godot web-export editor + `dotnet workload install wasm-tools` (verify with `dotnet workload list`).
3. `--import` once, then `--export-release "Web"` (section 2).
4. To deploy: copy `build/web/*` → `ProjectX/public/tank/`, then PR→main in ProjectX (section 4).
