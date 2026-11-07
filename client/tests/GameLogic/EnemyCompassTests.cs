using System.Collections.Generic;
using System.Numerics;
using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

public class EnemyCompassTests
{
    private static NetTank Tank(float x, int team, int hp = 8) =>
        new(8) { Position = new Vector2(x, 0f), Team = team, Hp = hp };

    private static void Pass(EnemyCompass compass, float seconds, NetTank self, List<NetTank> tanks)
    {
        var all = new List<TankGame.Domain.ITank>(tanks);
        for (var elapsed = 0f; elapsed < seconds; elapsed += 0.5f)
        {
            compass.Update(0.5f, self, all);
        }
    }

    [Fact]
    public void AfterTheQuietSpell_PointsAtTheNearestEnemiesFirst_CappedAtThree()
    {
        var self = Tank(0f, team: 0);
        var tanks = new List<NetTank>
        {
            self, Tank(5000f, 1), Tank(1000f, 2), Tank(3000f, 3), Tank(2000f, 4), Tank(4000f, 5),
        };
        var compass = new EnemyCompass();

        Pass(compass, EnemyCompass.QuietSeconds - 1f, self, tanks);
        Assert.Empty(compass.Targets);

        Pass(compass, 2f, self, tanks);
        Assert.Equal(EnemyCompass.MaxTargets, compass.Targets.Count);
        Assert.Equal(1000f, compass.Targets[0].Position.X);
        Assert.Equal(2000f, compass.Targets[1].Position.X);
        Assert.Equal(3000f, compass.Targets[2].Position.X);
    }

    [Fact]
    public void GettingHit_RestartsTheQuietSpell()
    {
        var self = Tank(0f, team: 0);
        var tanks = new List<NetTank> { self, Tank(5000f, 1) };
        var compass = new EnemyCompass();
        Pass(compass, EnemyCompass.QuietSeconds + 1f, self, tanks);
        Assert.NotEmpty(compass.Targets);

        self.Hp--;
        Pass(compass, 1f, self, tanks);

        Assert.Empty(compass.Targets);
    }

    [Fact]
    public void ANearbyEnemyLosingHealth_CountsAsFighting_ButAFarAwayOneDoesNot()
    {
        var self = Tank(0f, team: 0);
        var near = Tank(EnemyCompass.NearbyFightRange - 50f, 1);
        var far = Tank(EnemyCompass.NearbyFightRange + 2000f, 2);
        var tanks = new List<NetTank> { self, near, far };
        var compass = new EnemyCompass();
        Pass(compass, EnemyCompass.QuietSeconds + 1f, self, tanks);

        far.Hp--;
        Pass(compass, 0.5f, self, tanks);
        Assert.NotEmpty(compass.Targets);

        near.Hp--;
        Pass(compass, 0.5f, self, tanks);
        Assert.Empty(compass.Targets);
    }

    [Fact]
    public void Teammates_AreNeitherCountedNorPointedAt()
    {
        var self = Tank(0f, team: 0);
        var tanks = new List<NetTank> { self, Tank(100f, team: 0), Tank(9000f, team: 1) };
        var compass = new EnemyCompass();

        Pass(compass, EnemyCompass.QuietSeconds + 1f, self, tanks);

        Assert.Equal(1, compass.AliveEnemies);
        Assert.Equal(9000f, Assert.Single(compass.Targets).Position.X);
    }

    [Fact]
    public void AliveEnemies_DropsWhenATankIsOut_AndNoArrowPointsAtIt()
    {
        var self = Tank(0f, team: 0);
        var doomed = Tank(9000f, team: 1);
        var tanks = new List<NetTank> { self, doomed, Tank(9500f, team: 2) };
        var compass = new EnemyCompass();
        Pass(compass, EnemyCompass.QuietSeconds + 1f, self, tanks);
        Assert.Equal(2, compass.AliveEnemies);

        doomed.Hp = 0;
        Pass(compass, 0.5f, self, tanks);

        Assert.Equal(1, compass.AliveEnemies);
        Assert.Equal(9500f, Assert.Single(compass.Targets).Position.X);
    }

    [Fact]
    public void ADownedPlayer_GetsNoArrows_ButStillSeesTheCount()
    {
        var self = Tank(0f, team: 0);
        var tanks = new List<NetTank> { self, Tank(9000f, team: 1) };
        var compass = new EnemyCompass();
        Pass(compass, EnemyCompass.QuietSeconds + 1f, self, tanks);

        self.Hp = 0;
        Pass(compass, 0.5f, self, tanks);

        Assert.Empty(compass.Targets);
        Assert.Equal(1, compass.AliveEnemies);
    }

    [Fact]
    public void ADownedEnemyWithALifeLeft_StillCounts_ButIsNotPointedAt()
    {
        var world = new World();
        var self = Tank(0f, team: 0);
        var enemy = new TankGame.GameLogic.Tank(new Idle(), world, new Open(), new Vector2(9000f, 0f),
            200f, 0.5f, 600f, maxHp: 1, team: 1, lives: 2);
        var all = new List<TankGame.Domain.ITank> { self, enemy };
        var compass = new EnemyCompass();

        enemy.TakeDamage(1);
        for (var i = 0; i < 20; i++)
        {
            compass.Update(0.5f, self, all);
        }

        Assert.Equal(1, compass.AliveEnemies);
        Assert.Empty(compass.Targets);
    }

    private sealed class Idle : TankGame.Domain.IInputSource
    {
        public TankGame.Domain.TankInput Read() => new(Vector2.Zero, 0f, Fire: false);
    }

    private sealed class Open : TankGame.Domain.IArena
    {
        public TankGame.Domain.RaycastHit? RaycastFirstHit(Vector2 origin, Vector2 direction, float maxDistance) => null;

        public bool IsBlocked(Vector2 point) => false;

        public void DamageAt(Vector2 point, Vector2 direction, int amount)
        {
        }
    }
}
