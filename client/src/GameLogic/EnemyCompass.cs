using System;
using System.Collections.Generic;
using System.Numerics;
using TankGame.Domain;

namespace TankGame.GameLogic;

/// <summary>What the match HUD shows one player: how many enemies are alive and, once that player has
/// gone <see cref="QuietSeconds"/> without fighting, which enemies to head for. Reads only tank state,
/// so the host and a guest (who sees mirrored tanks) work it out the same way. A fight means the player
/// lost health or an enemy near them did; another tank's fight across the map does not count, because
/// that must not hide the arrows from a player who has met nobody. Pure C#, reuses its buffers: it runs
/// every frame.</summary>
public sealed class EnemyCompass
{
    /// <summary>Seconds without fighting before enemies are pointed out.</summary>
    public const float QuietSeconds = 8f;

    /// <summary>How close (world units) an enemy losing health must be to count as this player's fight.</summary>
    public const float NearbyFightRange = 700f;

    /// <summary>The most enemies pointed at, at once.</summary>
    public const int MaxTargets = 3;

    private readonly Dictionary<Guid, int> _lastHealth = new();
    private readonly List<ITank> _targets = new(MaxTargets);
    private readonly List<ITank> _candidates = new();
    private Vector2 _sortOrigin;
    private float _quiet;

    /// <summary>Enemies to point at, nearest first. Empty while fighting or when the player is down.</summary>
    public IReadOnlyList<ITank> Targets => _targets;

    /// <summary>Enemies still in the match, counting those waiting to respawn.</summary>
    public int AliveEnemies { get; private set; }

    /// <summary>Advances the quiet timer and refreshes <see cref="Targets"/> and <see cref="AliveEnemies"/>.</summary>
    /// <param name="deltaSeconds">Time since the previous update.</param>
    /// <param name="self">The player this HUD belongs to.</param>
    /// <param name="tanks">Every tank in the match, including <paramref name="self"/>.</param>
    public void Update(float deltaSeconds, ITank self, IReadOnlyList<ITank> tanks)
    {
        _quiet += deltaSeconds;
        _candidates.Clear();
        AliveEnemies = 0;

        for (var i = 0; i < tanks.Count; i++)
        {
            var tank = tanks[i];
            var health = tank.Hp + tank.Shield;
            var wasHurt = _lastHealth.TryGetValue(tank.Id, out var last) && health < last;
            _lastHealth[tank.Id] = health;

            if (ReferenceEquals(tank, self))
            {
                _quiet = wasHurt ? 0f : _quiet;
                continue;
            }

            if (tank.Team == self.Team)
            {
                continue;
            }

            if (wasHurt && Vector2.Distance(tank.Position, self.Position) <= NearbyFightRange)
            {
                _quiet = 0f;
            }

            if (tank.Hp > 0 || tank.LivesRemaining > 0)
            {
                AliveEnemies++;
            }

            if (tank.Hp > 0)
            {
                _candidates.Add(tank);
            }
        }

        _targets.Clear();
        if (self.Hp <= 0)
        {
            _quiet = 0f;
            return;
        }

        if (_quiet < QuietSeconds)
        {
            return;
        }

        _sortOrigin = self.Position;
        _candidates.Sort(CompareByDistance);
        for (var i = 0; i < _candidates.Count && i < MaxTargets; i++)
        {
            _targets.Add(_candidates[i]);
        }
    }

    private int CompareByDistance(ITank a, ITank b) =>
        Vector2.DistanceSquared(a.Position, _sortOrigin).CompareTo(Vector2.DistanceSquared(b.Position, _sortOrigin));
}
