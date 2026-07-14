#nullable enable

using UnityEngine;

namespace Game.Rules;

[CreateAssetMenu(fileName = "TagRulesConfig", menuName = "RooftopTag/Tag Rules Config")]
public sealed class TagRulesConfig : ScriptableObject
{
    [Header("Round")]
    // 120s (was 300s): a 5-minute ceiling on an 11-agent 1v10 chase-me round left "survive to the
    // timer" as a near-impossible win condition in practice (self-play sweeps land well under 30s
    // once a tag cascade starts) — 2 minutes keeps timer-survival a real, reachable win state.
    public float roundDuration = 120f;

    /// <summary>Current mode: the player is the runner and a single bot is the tagger chasing them.</summary>
    public int taggerCount = 1;
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
    // Heavily nerfed per feel-test: a short, infrequent hop rather than a big velocity-scaled dash.
    public float lungeCooldown = 3f;
    public float lungeBaseImpulse = 1.5f;
    public float lungeVelocityScale = 0.2f;

    /// <summary>Tag reach radius is a binary still-vs-moving check, not a continuous function of speed — sprinting or jumping shouldn't extend it beyond the same "moving" value.</summary>
    [Header("Tag reach")]
    public float tagReachStill = 1.2f;
    public float tagReachMoving = 2.0f;

    [Header("Late-game tagger speed curve")]
    /// <summary>Flat base speed edge taggers get at all times (a small pursuit advantage over runners). The late-game curve below multiplies on top of this, so taggers run at this early game and this * lateGameMaxSpeedMultiplier late.</summary>
    public float taggerBaseSpeedMultiplier = 1.04f;
    public float lateGamePhaseDuration = 75f;
    public float lateGameMaxSpeedMultiplier = 1.10f;

    [Header("Role telegraphing")]
    public Color taggerColor = new Color32(0xFF, 0x3D, 0x2E, 0xFF);
    public Color runnerColor = new Color32(0xFF, 0xE9, 0xC4, 0xFF);
    public Color conversionGraceColor = new(0.9f, 0.7f, 0.1f);
    /// <summary>Emission multiplier for Taggers. Zeroed for the "chase me" rooftop mode per
    /// feel-test feedback — bots are rigged (non-procedural) pest_control models, so with this at 0
    /// TagAgent.UpdateColor leaves the model's own texture untouched (no red tint/glow at all).
    /// Was 0.5 (silhouette-at-range red glow); left the field here rather than deleting it in case
    /// the glow comes back for a future mode.</summary>
    public float taggerEmissiveIntensity = 0f;
    public float runnerEmissiveIntensity = 0f;
    public float graceEmissiveIntensity = 1.2f;
    public float gracePulseHz = 2.5f;
}
