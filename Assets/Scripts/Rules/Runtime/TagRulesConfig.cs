#nullable enable

using UnityEngine;

namespace Game.Rules;

[CreateAssetMenu(fileName = "TagRulesConfig", menuName = "RooftopTag/Tag Rules Config")]
public sealed class TagRulesConfig : ScriptableObject
{
    [Header("Mode")]
    /// <summary>Which ruleset the round runs — see <see cref="GameMode"/>. Deliberately defaults to
    /// PestControl so every existing scene, PlayMode test and the headless self-play harness keep
    /// today's rules without touching anything; only the main menu's Mode row moves it.</summary>
    public GameMode mode = GameMode.PestControl;

    /// <summary>Tag mode's catch range: the horizontal distance a Tagger's touch tag reaches (see
    /// TagAgent.TryTouchTag). Well under <see cref="netThrowRange"/> (6) because you're meant to
    /// actually touch them, but above <see cref="tagReachMoving"/> (1.6) because two 0.4-radius
    /// bodies at sprint speed need real forgiveness or every tag whiffs. Height is gated separately
    /// by <see cref="tagReachVerticalTolerance"/>, via TagAgent.HasTagLineOfSight.</summary>
    public float tagTouchRange = 2.2f;

    /// <summary>Seconds between touch tags — the touch tag's own rate limiter, the counterpart to
    /// <see cref="netThrowCooldown"/>. Deliberately short: a missed touch should cost tempo in a
    /// footrace, not a whole turn.</summary>
    public float tagTouchCooldown = 0.6f;

    [Header("Round")]
    // 2 minutes keeps timer-survival a real, reachable win state for an 11-agent 1v10 chase-me round
    // (self-play sweeps land well under 30s once a tag cascade starts).
    public float roundDuration = 120f;

    /// <summary>When true the round timer never counts down or expires — a free-roam mode for testing
    /// movement/animation. Combined with 0 chasers (see <see cref="taggerCount"/>) it lets the player
    /// wander the arena indefinitely with nothing hunting them. Toggled from the main menu.</summary>
    public bool unlimitedTime = false;

    /// <summary>Chase-me mode: the local player is the SOLE Runner and every bot is a Tagger. For the
    /// 11-agent RooftopArena that's 10 chasers (1v10, matching roundDuration's design note). AssignRoles
    /// clamps this to roster-1 on smaller scenes; the main menu's Chasers row can lower it, but every
    /// bot beyond the tagger count is benched rather than left as a fellow runner (see AssignRoles).
    /// Free-roam is 0 chasers via the menu.</summary>
    public int taggerCount = 10;

    /// <summary>Caps the number of Runners in Tagger mode (menu's "Runners" row) so co-taggers and
    /// runners can be picked independently instead of runners always soaking up whatever's left of
    /// the roster. 0 means UNCAPPED — every non-tagger bot becomes a Runner, exactly today's
    /// behavior. Headless self-play, the debug TagArena scene, and chase-me mode all leave this 0.
    /// Only takes effect with forcePlayerAsTagger (see AssignRoles); chase-me benching is unrelated.</summary>
    public int runnerCount = 0;

    /// <summary>Guarantees the local player is always assigned Tagger (useful while feel-testing tagger-specific mechanics like the lunge). Flip off for a "real" fully-random round.</summary>
    public bool forcePlayerAsTagger = false;

    /// <summary>Guarantees the local player is always assigned Runner — the "chase me" mode: player flees, the taggerCount bots hunt. Takes priority over <see cref="forcePlayerAsTagger"/>.</summary>
    public bool forcePlayerAsRunner = true;

    /// <summary>
    /// No tag can land for this many seconds after the round starts, so a tight spawn grid with
    /// taggers assigned at t=0 can't end a match before anyone has a real chance to react. Mirrors
    /// the existing per-agent conversion grace, but applies to the whole round at once.
    /// </summary>
    public float roundStartGraceDuration = 3f;

    [Header("Conversion")]
    public float conversionGraceDuration = 2.5f;

    [Header("Lunge")]
    // The lunge is a COMMITTED DIVE owned by CharacterMotor.BeginDive; the dive-lock is the Tagger's
    // only rate limiter (no cooldown timer). Committed dive tuning (consumed by
    // TagAgent.TryLunge -> CharacterMotor.BeginDive):
    public float diveSpeed = 9f;          // horizontal speed the dive redirects momentum to (sprint is 7); arriving faster is preserved instead
    public float diveDuration = 0.8f;     // locked-in dive window; also the tagger contact-tag window (kept == CharacterAnimatorBridge.DiveHoldSeconds so the roll animation and the lock end together)
    public float diveRecovery = 0.35f;    // ease speed back down to pre-dive speed over this — zero net momentum gain
    public float diveSteeringScale = 0.15f; // steering authority during the dive (committed, minimal correction)
    public float catchRange = 4.5f;       // a Tagger lunging AT a victim within this (and ahead) plays the DivingCatch finishing move instead of the generic roll (animation only); matches the bots' lungeRange so a bot's committed dive at someone is exactly a catch

    [Header("Dodge")]
    // The "clutch dodge" mechanic — a LOCAL-PLAYER-ONLY (raccoon runner) assist layer sitting on top
    // of the shared lunge. Bots never see any of it; it's a deliberate 1-vs-10 assist asymmetry, not a
    // rule both sides play by. See TagAgent.PerformTag / RoundController's Dodge region for the wiring.
    public float runnerDiveSpeed = 10.5f;   // a Runner's lunge redirects to THIS (vs the Tagger's diveSpeed=9) — a real net forward escape burst. BeginDive still preserves a faster entry speed and the global 12 m/s cap still applies.
    public float runnerRollCooldown = 2f;   // Runners get a real 2s cooldown on the lunge/roll (Taggers keep the dive-lock as their only limiter). Reuses the existing _lungeCooldownRemaining plumbing + HUD spinner.
    public float dodgeIFrames = 0.3f;        // for this long at the START of the Runner's own committed dive, any tag on them is auto-dodged for free (no window budget) — they're already rolling clear.
    // Per-use reactive dodge window duration (unscaled seconds), indexed by dodges already pulled
    // off THIS round — first dodge is generous and consistent, second still comfortably doable, third
    // genuinely hard. Past the array's end dodgeWindowFloor is all that's left: miracle-tier only.
    public float[] dodgeWindowDurations = { 0.45f, 0.35f, 0.15f };
    public float dodgeWindowFloor = 0.08f;   // window duration once dodgeWindowDurations runs out — never shrinks below this, so "miracle" dodges stay possible no matter how many you've already pulled off this round.
    public float dodgeSlowMoScale = 0.3f;    // Time.timeScale during an open dodge window (a heavier dip than the 0.35 tag slow-mo — this is a reaction test, not just juice).
    public float taggerWhiffLockout = 1f;    // on a successful dodge the Tagger who whiffed can't lunge again for this long (gated in TryLunge via the same _lungeCooldownRemaining as the runner cooldown).

    /// <summary>Tag reach radius is a binary still-vs-moving check, not a continuous function of speed — sprinting or jumping shouldn't extend it beyond the same "moving" value.</summary>
    [Header("Net throw")]
    // The ranged tag is now a thrown bug net (replaces the instant hand-tag on right-click): the tagger
    // winds up, hurls the net in a ballistic arc, and on a hit a trap dome drops over the victim before
    // the normal tag flow runs. Aimed at the LOCAL player, the flight time doubles as the clutch-dodge
    // reaction window (see RoundController's Dodge region / NetThrower).
    public float netThrowRange = 6f;       // max horizontal distance to acquire a target (a touch past catchRange)
    public float netThrowCooldown = 1.2f;  // seconds between throws (the net's own rate limiter, independent of the lunge). A miss already costs netWindupSeconds + netFlightTime (0.9s) of committed animation before this even starts counting down, so the old 2.0 left over a second of dead air on top of that and a whiffed throw effectively ended the chase. 1.2 keeps a real rate limit while letting a tagger stay in the pursuit.
    public float netWindupSeconds = 0.45f; // wind-up before release: the net is raised, then hurled — long enough for the overhead-load telegraph to read at sprint speed
    public float netFlightTime = 0.45f;    // ballistic flight from hand to landing point; also the local-player dodge-window length
    public float netHitRadius = 1.1f;      // a target still within this of the landing point is caught. Purely a gameplay radius — the trap-dome VISUAL uses NetThrower.TrapDomeVisualRadius instead.
    public float netTrapDuration = 1.2f;   // struggle-under-the-dome time before the tag actually lands
    public bool netCarryVisible = true;    // show the handheld net in a Tagger's hand between throws

    [Header("Tag reach")]
    // These values must stay tight enough that a tag needs genuine proximity — center-to-center reach
    // leaves only a small margin of daylight between two 0.4-radius bodies. Reach is measured
    // HORIZONTALLY, with the vertical band below as a separate gate (see TryTagInRange).
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
    /// <summary>Falling off the rooftops lands you on a real street (SceneStyler.CreateRoads' ground
    /// slab) instead of the void, so the round consequence waits for that little sequence to play out
    /// rather than firing the instant you cross RoundController.FallResetY mid-air. This is the hard
    /// backstop: however the sequence goes, the consequence lands this long after the fall — long
    /// enough for a car to arrive and matter, short enough that a bot standing in the road doesn't
    /// stall the round.</summary>
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

    /// <summary>Names for the raccoon (Runner) side of the same roster — the player is hunting raccoons,
    /// not filing paperwork with them, so "YOU CAUGHT DALE" is wrong for a victim who was still a Runner
    /// when caught. Handed out in the same registration-order/wrapping scheme as <see cref="botNames"/>
    /// (see RoundController.NextRaccoonName); every agent gets one of EACH pool at registration and
    /// TagAgent.DisplayName picks between them by current role.</summary>
    public string[] raccoonNames =
    {
        "BANDIT", "TRASH PANDA", "SCRAPPER", "MASK", "NIBBLES", "ROCKET",
        "RINGTAIL", "DUMPSTER KING", "WHISKERS", "SLINKY", "CHONK", "MOONPIE",
    };
}
