using System;

namespace TankGame.GameLogic;

/// <summary>Bot skill level, chosen in Settings. Normal is the game's original tuning.</summary>
public enum Difficulty
{
    Easy,
    Normal,
    Hard,
}

/// <summary>Pure per-difficulty tuning for AI tanks: a scale on how far a bot sees, a scale the
/// scenes apply to its fire interval, a seeded aim error (in degrees) added when it fires, and the
/// chance a roaming bot heads for the players' area instead of a far corner of the map.</summary>
public readonly record struct DifficultyPreset(
    float VisionScale, float FireIntervalScale, float AimJitterDegrees, float PlayerSeekChance)
{
    public static DifficultyPreset For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Easy => new(VisionScale: 0.6f, FireIntervalScale: 1.5f, AimJitterDegrees: 4f, PlayerSeekChance: 0.6f),
        Difficulty.Hard => new(VisionScale: 1.3f, FireIntervalScale: 0.85f, AimJitterDegrees: 0f, PlayerSeekChance: 0.9f),
        _ => new(VisionScale: 1f, FireIntervalScale: 1f, AimJitterDegrees: 0f, PlayerSeekChance: 0.75f),
    };

    /// <summary>The aim error bound in radians (half-width of the jitter interval).</summary>
    public float AimJitterRadians => AimJitterDegrees * MathF.PI / 180f;
}
