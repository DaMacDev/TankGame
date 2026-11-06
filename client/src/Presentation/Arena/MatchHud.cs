using System.Collections.Generic;
using System.Globalization;
using Godot;
using TankGame.Domain;
using TankGame.GameLogic;

namespace TankGame.Presentation;

/// <summary>The in-match overlay shared by the solo and networked arenas: an always-on count of alive
/// enemies, and arrows around the player's tank pointing at the nearest enemies once the player has
/// gone a while without fighting (<see cref="EnemyCompass"/>).</summary>
public partial class MatchHud : CanvasLayer
{
    private const float ArrowOrbit = 120f; // screen units from the tank; clears its name tag and health bar
    private static readonly Color NearestArrow = new(1f, 0.85f, 0.15f, 0.95f);
    private static readonly Color FartherArrow = new(1f, 0.85f, 0.15f, 0.5f);

    private readonly EnemyCompass _compass = new();
    private readonly Polygon2D[] _arrows = new Polygon2D[EnemyCompass.MaxTargets];
    private Label _enemies = null!;
    private int _shownCount = -1;

    public override void _Ready()
    {
        var backing = new PanelContainer { Name = "EnemiesBacking" };
        backing.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.6f),
            ContentMarginLeft = 12f,
            ContentMarginRight = 12f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
        });
        backing.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        backing.Position = new Vector2(16f, 16f);

        _enemies = new Label { Name = "EnemiesLeft" };
        _enemies.AddThemeFontSizeOverride("font_size", 24);
        backing.AddChild(_enemies);
        AddChild(backing);

        for (var i = 0; i < _arrows.Length; i++)
        {
            _arrows[i] = new Polygon2D
            {
                Name = $"EnemyArrow{i}",
                Visible = false,
                Color = i == 0 ? NearestArrow : FartherArrow,
                Polygon = new[]
                {
                    new Vector2(26f, 0f), new Vector2(-16f, -18f), new Vector2(-6f, 0f), new Vector2(-16f, 18f),
                },
            };
            AddChild(_arrows[i]);
        }
    }

    /// <summary>Refreshes the count and arrows for this frame. The camera follows the player, so the
    /// arrows orbit the middle of the screen.</summary>
    /// <param name="deltaSeconds">Time since the previous refresh.</param>
    /// <param name="camera">The match camera, used to turn world directions into screen directions.</param>
    /// <param name="self">The local player's tank.</param>
    /// <param name="tanks">Every tank in the match, including <paramref name="self"/>.</param>
    public void Refresh(float deltaSeconds, Camera3D camera, ITank self, IReadOnlyList<ITank> tanks)
    {
        _compass.Update(deltaSeconds, self, tanks);
        if (_compass.AliveEnemies != _shownCount)
        {
            _shownCount = _compass.AliveEnemies;
            _enemies.Text = string.Format(CultureInfo.InvariantCulture, Tr("hud.enemies_left"), _shownCount);
        }

        var selfOnScreen = camera.UnprojectPosition(GroundProjection.ToWorld(self.Position));
        var centre = GetViewport().GetVisibleRect().Size / 2f;
        for (var i = 0; i < _arrows.Length; i++)
        {
            var arrow = _arrows[i];
            arrow.Visible = i < _compass.Targets.Count;
            if (!arrow.Visible)
            {
                continue;
            }

            var enemyOnScreen = camera.UnprojectPosition(GroundProjection.ToWorld(_compass.Targets[i].Position));
            var heading = (enemyOnScreen - selfOnScreen).Angle();
            arrow.Rotation = heading;
            arrow.Position = centre + (Vector2.FromAngle(heading) * ArrowOrbit);
        }
    }
}
