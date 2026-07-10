#nullable enable

using System;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Difficulty scales decision-making quality — reaction time, how far ahead a bot predicts a
/// target's trajectory, and how precisely it executes (aim/timing jitter) — never movement stats.
/// Bots always share the player's <see cref="Game.Movement.CharacterMotor"/> and speeds; a Scary
/// bot is scary because it reacts instantly and predicts well, not because it moves faster.
/// </summary>
[CreateAssetMenu(fileName = "BotConfig", menuName = "RooftopTag/Bot Config")]
public sealed class BotConfig : ScriptableObject
{
    [Serializable]
    public struct DifficultyTuning
    {
        /// <summary>Seconds of delay before a bot updates its plan/target after the situation changes.</summary>
        public float reactionTime;

        /// <summary>Seconds ahead a bot extrapolates a target's trajectory when choosing an intercept point.</summary>
        public float predictionHorizon;

        /// <summary>0..1 execution quality: scales down aim/timing jitter as it approaches 1 (perfect).</summary>
        [Range(0f, 1f)] public float executionPrecision;
    }

    public DifficultyTuning casual = new()
    {
        reactionTime = 0.6f,
        predictionHorizon = 0.3f,
        executionPrecision = 0.4f,
    };

    public DifficultyTuning skilled = new()
    {
        reactionTime = 0.3f,
        predictionHorizon = 0.8f,
        executionPrecision = 0.7f,
    };

    public DifficultyTuning scary = new()
    {
        reactionTime = 0.08f,
        predictionHorizon = 1.5f,
        executionPrecision = 1.0f,
    };

    public DifficultyTuning Get(BotDifficulty difficulty) => difficulty switch
    {
        BotDifficulty.Casual => casual,
        BotDifficulty.Skilled => skilled,
        BotDifficulty.Scary => scary,
        _ => skilled,
    };
}
