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

    /// <summary>When true the round timer never counts down or expires — a free-roam mode for testing
    /// movement/animation. Combined with 0 chasers (see <see cref="taggerCount"/>) it lets the player
    /// wander the arena indefinitely with nothing hunting them. Toggled from the main menu.</summary>
    public bool unlimitedTime = false;

    /// <summary>Chase-me mode: the local player is the SOLE Runner and every bot is a Tagger. For the
    /// 11-agent RooftopArena that's 10 chasers (1v10, matching roundDuration's design note). AssignRoles
    /// clamps this to roster-1 on smaller scenes; the main menu's Chasers row can lower it (fewer
    /// chasers leaves the surplus bots as fellow runners). Was briefly 1 during free-roam work, which
    /// left 9 stray bot-runners — the "there are other runners" bug. Free-roam is 0 chasers via the menu.</summary>
    public int taggerCount = 10;
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
    // The lunge is now a COMMITTED DIVE owned by CharacterMotor.BeginDive: it redirects existing
    // momentum forward, locks the player in briefly, and never nets speed. The dive-lock is the rate
    // limiter, so there is no cooldown timer any more.
    public float lungeCooldown = 0f;   // dormant: the dive-lock (see CharacterMotor.BeginDive) is the limiter now, not a timer. Kept at 0 so the existing cooldown gate/HUD plumbing is a harmless no-op.
    // Committed dive tuning (consumed by TagAgent.TryLunge -> CharacterMotor.BeginDive):
    public float diveSpeed = 9f;          // horizontal speed the dive redirects momentum to (sprint is 7); arriving faster is preserved instead
    public float diveDuration = 0.8f;     // locked-in dive window; also the tagger contact-tag window (kept == CharacterAnimatorBridge.DiveHoldSeconds so the roll animation and the lock end together)
    public float diveRecovery = 0.35f;    // ease speed back down to pre-dive speed over this — zero net momentum gain
    public float diveSteeringScale = 0.15f; // steering authority during the dive (committed, minimal correction)
    public float catchRange = 4.5f;       // a Tagger lunging AT a victim within this (and ahead) plays the DivingCatch finishing move instead of the generic roll (animation only); matches the bots' lungeRange so a bot's committed dive at someone is exactly a catch

    /// <summary>Tag reach radius is a binary still-vs-moving check, not a continuous function of speed — sprinting or jumping shouldn't extend it beyond the same "moving" value.</summary>
    [Header("Tag reach")]
    // Tightened (2.0/1.2 -> 1.6/1.0): the old center-to-center 2.0 landed tags with ~1.2m of visible
    // daylight between two 0.4-radius bodies (user: bots "catch" from really far away). Reach is now
    // measured HORIZONTALLY, with the vertical band below as a separate gate (see TryTagInRange).
    public float tagReachStill = 1.0f;
    public float tagReachMoving = 1.6f;
    // Max height difference for a ranged tag — stops tags landing on someone on a different roof
    // level who merely passes within reach horizontally.
    public float tagReachVerticalTolerance = 1.5f;

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

    [Header("Trash")]
    public int trashPointsToWin = 3;    // team trash points for an instant runner win
    public int activeCanCount = 3;      // random subset of CanAnchors activated per round
    public float canMinSpacing = 12f;   // min XZ distance between two active bins each round
    public float eatRadius = 1.6f;      // runner must be within this of a can to eat (proximity only — no stand-still gate)
    public float eatDurationSmall = 2.5f; // tier-1 small can eat time (+1 pt)
    public float eatDurationLarge = 5f;   // tier-2 dumpster eat time (+2 pts)

    [Header("Street fall")]
    /// <summary>Falling off the rooftops now lands you on a real street (SceneStyler.CreateRoads'
    /// ground slab) instead of the void, so the round consequence waits for that little sequence to
    /// play out rather than firing the instant you cross RoundController.FallResetY mid-air. This is
    /// the hard backstop: however the sequence goes (or doesn't — the cars that make it interesting
    /// are a later phase), the consequence lands this long after the fall. Long enough for a car to
    /// arrive and matter, short enough that a bot standing in the road doesn't stall the round.</summary>
    public float streetSequenceTimeout = 4f;
    /// <summary>How long a ragdolled body is left lying in the street before the consequence lands —
    /// the sequence is over the moment something hits you, so this replaces the full
    /// <see cref="streetSequenceTimeout"/> wait once CharacterRagdoll.IsActive goes true. Inert until
    /// something actually activates a ragdoll down there.</summary>
    public float ragdollLingerSeconds = 1.5f;

    [Header("Kill cam")]
    /// <summary>Bot names for the kill cam's "CAUGHT BY ..." caption, handed out in registration order
    /// (wrapping if there are more bots than names). Deadpan municipal pest-control roster — the joke is
    /// that the thing which just dove across a rooftop to catch you files paperwork about it.</summary>
    public string[] botNames =
    {
        "DALE", "UNIT 3", "AGENT PIGEON", "BARRY FROM PEST CONTROL", "THE INSPECTOR", "GARY, PROBABLY",
        "CONTRACTOR #7", "MIDGE", "SENIOR TECHNICIAN KEVIN", "THE SUPERVISOR", "UNIT 12", "DOUG (TEMP)",
    };
}
