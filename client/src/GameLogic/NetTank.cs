using System;
using System.Numerics;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>A view-model tank for networked play: a mutable <see cref="ITank"/> whose transform and
/// health are pushed in each frame from authority — the local player's <see cref="PredictedTank"/>
/// or a remote slot's snapshot <c>TankState</c>. The server owns the simulation, so this holds no
/// rules: <see cref="Step"/> and <see cref="TakeDamage"/> are no-ops and <see cref="IsAlive"/> is
/// always true (the scene removes a tank by slot, not by reaping). It exists only so the existing
/// <c>TankView</c>, which binds an <see cref="ITank"/>, can render network state unchanged.</summary>
public sealed class NetTank : ITank
{
    public NetTank(int maxHp = 3) => MaxHp = maxHp;

    public Guid Id { get; } = EntityId.Next();
    public Vector2 Position { get; set; }
    public float Rotation { get; set; }
    public float TurretRotation { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; }
    public int Team { get; set; }
    public int Shield { get; set; }
    public int Layer { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public bool IsAlive => true;

    /// <summary>Lives left including the current one, pushed in from the snapshot.</summary>
    public int Lives { get; set; }

    public int LivesRemaining => Lives;

    public void Step(float deltaSeconds) { }

    public void TakeDamage(int amount) { }
}
