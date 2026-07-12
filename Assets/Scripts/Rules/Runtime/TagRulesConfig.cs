#nullable enable

using UnityEngine;

namespace Game.Rules;

[CreateAssetMenu(fileName = "TagRulesConfig", menuName = "RooftopTag/Tag Rules Config")]
public sealed class TagRulesConfig : ScriptableObject
{
    [Header("Round")]
    public float roundDuration = 300f;

    /// <summary>Current mode: the player is the runner and 2 bots are the taggers chasing them.</summary>
    public int taggerCount = 2;
    public int runnerCount = 1;

    /// <summary>Guarantees the local player is always assigned Tagger (useful while feel-testing tagger-specific mechanics like the lunge). Flip off for a "real" fully-random round.</summary>
    public bool forcePlayerAsTagger = false;

    /// <summary>Guarantees the local player is always assigned Runner — the "chase me" mode: player flees, the taggerCount bots hunt. Takes priority over <see cref="forcePlayerAsTagger"/>.</summary>
    public bool forcePlayerAsRunner = true;

    /// <summary>
    /// No tag can land for this many seconds after the round starts. Found via the first
    /// self-play batch: 12 agents on a tight spawn grid with taggers assigned at t=0 produced
    /// matches ending in under 3 seconds, tags landing before anyone could react — not bot
    /// intelligence, just an unfair starting configuration. Mirrors the existing per-agent
    /// conversion grace, but applies to the whole round at once.
    /// </summary>
    public float roundStartGraceDuration = 3f;

    [Header("Conversion")]
    public float conversionGraceDuration = 2.5f;

    [Header("Lunge")]
    public float lungeCooldown = 1.5f;
    public float lungeBaseImpulse = 4f;
    public float lungeVelocityScale = 0.6f;

    /// <summary>Tag reach radius is a binary still-vs-moving check, not a continuous function of speed — sprinting or jumping shouldn't extend it beyond the same "moving" value.</summary>
    [Header("Tag reach")]
    public float tagReachStill = 1.2f;
    public float tagReachMoving = 2.0f;

    [Header("Late-game tagger speed curve")]
    public float lateGamePhaseDuration = 75f;
    public float lateGameMaxSpeedMultiplier = 1.10f;

    [Header("Role telegraphing")]
    public Color taggerColor = new Color32(0xFF, 0x3D, 0x2E, 0xFF);
    public Color runnerColor = new Color32(0xFF, 0xE9, 0xC4, 0xFF);
    public Color conversionGraceColor = new(0.9f, 0.7f, 0.1f);
    /// <summary>Emission multipliers per role — taggers must read as a red glow in silhouette
    /// at range (spec: gameplay color language). Runners stay non-emissive.</summary>
    public float taggerEmissiveIntensity = 1.8f;
    public float runnerEmissiveIntensity = 0f;
    public float graceEmissiveIntensity = 1.2f;
    public float gracePulseHz = 2.5f;
}
