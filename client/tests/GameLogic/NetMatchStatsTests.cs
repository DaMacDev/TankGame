using TankGame.GameLogic;
using Xunit;

namespace TankGame.Tests.GameLogic;

// The networked match's stat book: both roles feed it the same per-tank hp stream (the host from
// its authoritative world each tick, a guest from every snapshot), so both derive the identical
// leaderboard without any stats bytes on the wire — damage taken, repairs, and the standing all
// fall out of observed hp deltas.
public class NetMatchStatsTests
{
    // Respawns: a rise from zero is a respawn (not a repair), and a tank is out only when its lives
    // run out, so a winner who died and came back still ranks above the tank it finished off.
    [Fact]
    public void Respawn_IsNotARepair_AndOnlyTheLastLifeEliminates()
    {
        var stats = new NetMatchStats();
        stats.Register(0, "Ada", 0);
        stats.Register(1, "Bea", 1);
        stats.Observe(0, 8, 6);
        stats.Observe(1, 8, 6);

        stats.Observe(0, 0, 5); // Ada down, waiting to respawn
        stats.Observe(0, 8, 5); // Ada back
        stats.Observe(1, 0, 0); // Bea out

        var ada = stats.Tallies[0];
        Assert.Equal(0, ada.Repairs);
        Assert.Equal(8, ada.DamageTaken);
        Assert.True(ada.Alive);
        Assert.Equal("Ada", stats.Standings()[0].Name);
    }

    [Fact]
    public void FirstObservation_IsTheBaseline_NotDamage()
    {
        var stats = new NetMatchStats();
        stats.Register(0, "Ada", 0);

        stats.Observe(0, 5); // joined the stream mid-match at 5 hp — nothing observed yet

        Assert.Equal(0, stats.Tallies[0].DamageTaken);
        Assert.Equal(0, stats.Tallies[0].Repairs);
    }

    [Fact]
    public void HpDrops_AccumulateAsDamageTaken_AndRises_AsRepairs()
    {
        var stats = new NetMatchStats();
        stats.Register(0, "Ada", 0);

        stats.Observe(0, 8);
        stats.Observe(0, 5); // -3
        stats.Observe(0, 7); // +2 (repair pickup)
        stats.Observe(0, 6); // -1

        Assert.Equal(4, stats.Tallies[0].DamageTaken);
        Assert.Equal(2, stats.Tallies[0].Repairs);
        Assert.Equal(6, stats.Tallies[0].Hp);
        Assert.True(stats.Tallies[0].Alive);
    }

    [Fact]
    public void Standings_RankSurvivorsFirst_ThenLaterDeaths()
    {
        var stats = new NetMatchStats();
        stats.Register(0, "Ada", 0);
        stats.Register(1, "Bea", 1);
        stats.Register(2, "Cid", 2);

        stats.Observe(0, 8);
        stats.Observe(1, 8);
        stats.Observe(2, 8);

        stats.Observe(1, 0); // Bea falls first
        stats.Observe(2, 0); // Cid falls second — outlived Bea

        var standings = stats.Standings();
        Assert.Equal("Ada", standings[0].Name); // the survivor wins
        Assert.Equal("Cid", standings[1].Name); // died later = placed higher
        Assert.Equal("Bea", standings[2].Name);
        Assert.False(standings[2].Alive);
    }

    [Fact]
    public void Standings_BreakSurvivorTies_ByRemainingHp()
    {
        var stats = new NetMatchStats();
        stats.Register(0, "Ada", 0);
        stats.Register(1, "Bea", 0); // same team survives together

        stats.Observe(0, 8);
        stats.Observe(1, 8);
        stats.Observe(0, 3);

        var standings = stats.Standings();
        Assert.Equal("Bea", standings[0].Name); // healthier survivor tops the sheet
        Assert.Equal("Ada", standings[1].Name);
    }

    [Fact]
    public void UnregisteredSlot_AutoRegisters_SoAStraySnapshotNeverCrashesTheScreen()
    {
        var stats = new NetMatchStats();

        stats.Observe(3, 8);
        stats.Observe(3, 6);

        Assert.Single(stats.Tallies);
        Assert.Equal(2, stats.Tallies[0].DamageTaken);
        Assert.False(string.IsNullOrEmpty(stats.Tallies[0].Name));
    }

    [Fact]
    public void Register_KeepsTheRosterOrder_AndNamesTeams()
    {
        var stats = new NetMatchStats();
        stats.Register(1, "Bea", 1);
        stats.Register(0, "Ada", 0);

        Assert.Equal("Bea", stats.Tallies[0].Name);
        Assert.Equal(1, stats.Tallies[0].Team);
        Assert.Equal("Ada", stats.Tallies[1].Name);
    }
}
