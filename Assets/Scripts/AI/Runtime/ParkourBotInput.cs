#nullable enable

using System.Collections.Generic;
using Game.Movement;
using Game.Rules;
using UnityEngine;

namespace Game.AI;

/// <summary>
/// Smart M3 bot: routes through a <see cref="ParkourGraph"/> instead of steering straight at its
/// target, and executes each edge type's required input (jump near a genuine drop-off, Interact
/// near a climb/ladder/swing) rather than relying purely on <see cref="CharacterMotor"/>'s own
/// auto-detection. Implements <see cref="ICharacterInput"/> exactly like the player — same
/// speeds, same abilities — so skill comes entirely from routing/prediction/timing, matching the
/// "bots must be scary good through decisions, not cheating" architecture constraint.
///
/// Taggers predict the target's future position (current position + velocity * prediction
/// horizon) and path toward that intercept point instead of the target's current position, and
/// loosely coordinate through <see cref="RoundController.ClaimTarget"/> so multiple taggers
/// prefer splitting up over piling onto the same runner. Runners flee the predicted position of
/// their nearest threat. Difficulty (<see cref="BotConfig"/>) scales reaction time (how often the
/// bot re-evaluates its target/plan), prediction horizon, and execution precision (steering
/// jitter) — never movement stats.
///
/// Falls back to direct-line steering (with the same raycast-based cliff avoidance the original
/// dumb bots used) whenever no graph is supplied, no path exists, or the planned path has been
/// fully walked — e.g. for the final close-quarters approach once routing has done its job.
/// </summary>
public sealed class ParkourBotInput : MonoBehaviour, ICharacterInput
{
    [SerializeField] private float lungeRange = 4.5f; // lunge to close this gap (a committed dive), not just when already on top of the target
    [SerializeField] private float nodeArrivalRadius = 1.75f;
    [SerializeField] private float interactTriggerDistance = 2f;
    // Takeoff fires when ground runs out this far ahead — every metre here is wasted jump range
    // (the bot leaves the ledge that much before the lip). M4 loop measured jumps landing ~9m short
    // with the old 1.3m; tightened so the bot commits nearer the edge and keeps its reach.
    [SerializeField] private float edgeLookahead = 0.6f;

    // Below this empty-gap distance a sprint jump (~8.5m of range) overshoots the far platform into
    // the next pit, so the bot approaches at walk speed (~4.4m range) instead — the M4 loop's
    // "fails the first jump" (the 3m opening gap) was this fixed-power overshoot. Above it, sprint.
    [SerializeField] private float shortJumpGapThreshold = 4.5f;

    // Shortfall recovery: while descending mid-Jump-edge with no floor in reach below, if the bot is
    // still farther than this from the target lip it spends its double-jump (runners only — the motor's
    // CanDoubleJump gate makes this a no-op for taggers). The double-jump adds roughly 5-6m of flight
    // (doubleJumpSpeed 5 at ~6.5m/s horizontal), so firing with less than ~4m remaining converts a jump
    // that would have made it into an overshoot past the node — measured: threshold 2m dropped
    // jump_land_within from 0.77 to 0.75; only genuine shortfalls should trigger it.
    [SerializeField] private float doubleJumpShortfallDistance = 4f;

    // Cliff-avoidance tuning — see ChaseFleeBotInput's original fix notes: the raycast must cover
    // a band both above and below the bot's current height (ramps rise above a shallow check),
    // not just straight down from a fixed small offset.
    [SerializeField] private float lookAheadDistance = 2.5f;
    [SerializeField] private float maxSafeDrop = 2f;
    [SerializeField] private float upwardClearance = 3f;

    // Momentum brake horizon. Cliff-avoidance only STEERS, and steering cannot beat momentum: the
    // fan's shallowest deflection (20°) still carries most of the forward velocity, so a sprinting bot
    // "avoids" a lip and slides over it anyway — measured falling at x=11.22 past a lip at x=10 while
    // chasing a target parked at the edge. That's the void-bait from manual play: it needs a runway to
    // reach sprint, which is why bot-vs-bot self-play (runners juke away) never reproduced it.
    //
    // So when our CURRENT VELOCITY would carry us off within this many seconds, stop instead of
    // steering. Scaling the check by speed (not a flat look-ahead) is the point: a flat value big
    // enough to stop a sprint makes bots halt ~5m short of EVERY edge (measured: lookAheadDistance 6
    // parked the bot at x=5.15, unable to reach a target on the lip at x=9.5), which just swaps the
    // void exploit for a ledge-camping one. Speed-scaled, a slowed bot's horizon shrinks with it, so
    // it creeps right up to the lip — charge, brake, close on foot (measured: settles at x=9.00,
    // 0.5m from a target at the edge, inside tagReachMoving).
    //
    // TUNED, do not lower without re-running ParkourBotInput_BaitedToLipByTargetAtEdge: real stopping
    // distance from sprint is ~2-2.4m, NOT the ~0.33m that deceleration=75 suggests on paper (the
    // config value isn't raw m/s²). 0.15s (1.05m) and 0.25s (1.75m) BOTH still went over the lip;
    // 0.35s (2.45m) is the first that holds. Cost is real and measured: it drops tagger pressure
    // enough to move bot-vs-bot runner_win_rate 0.00 -> 0.20.
    [SerializeField] private float edgeBrakeSeconds = 0.35f;

    // Takeoff cone — see ApplySteeringSafety. Cliff-avoidance is suppressed for a gap-crossing edge
    // ONLY within this radius of the takeoff node and only while heading along the edge, instead of
    // for the edge's whole duration. MUST stay above lookAheadDistance: below that, avoidance would
    // already be steering the bot away from the lip before it could reach the cone, and it would
    // never take off at all — which is why the original suppression was edge-wide.
    [SerializeField] private float takeoffConeRadius = 4f;
    // Cosine of the cone half-angle (0.5 = 60°). Kept well wider than the 18-30° steering jitter so an
    // imprecise bot doesn't flip between suppress and avoid on approach and oscillate at the lip.
    [SerializeField] private float takeoffConeAlignment = 0.5f;

    [SerializeField] private float maxSteeringJitterDegrees = 30f;

    // Fall recovery. A bot that has dropped this far BELOW the height it was last standing at, with
    // nothing inside fallRecoveryGroundProbe underneath it, has missed — not jumped. Both conditions are
    // needed: mid-jump there's also no ground below (so the probe alone would make a bot grab a wall in
    // the middle of a perfectly good jump), and a Drop edge descends deliberately (so the height alone
    // would fire on a routed descent). Only "below where I took off AND nothing to land on" is a fall.
    [SerializeField] private float fallRecoveryDropThreshold = 4f;
    [SerializeField] private float fallRecoveryGroundProbe = 5f;
    // How far to look for a wall to steer at while falling. Facades run down to buildingBaseY (-25) and
    // the map resets a fallen agent at -15, so there is always wall alongside a chasm — the bot just has
    // to reach it, and wallHook.detectionDistance is only 1m.
    [SerializeField] private float fallRecoveryWallSearchRange = 6f;

    // Break-contact juke (runners only). When the nearest tagger is within jukeTriggerRange AND closing,
    // the runner cuts ~90° off the tagger axis for jukeDuration, then can't juke again for jukeCooldown.
    // This is the human "juke" the deterministic flee lacks: taggers predict linearly with a reaction
    // lag, so a sharp perpendicular cut breaks their intercept where running straight never opens a gap.
    [SerializeField] private float jukeTriggerRange = 3.5f;
    [SerializeField] private float jukeDuration = 0.5f;
    [SerializeField] private float jukeCooldown = 2f;

    // Flee dead-end avoidance: a low-degree goal node (corner/spur/ladder stub) is a trap the runner
    // gets swept into, so its escape-lead is docked DeadEndPenalty metres — unless the lead there is
    // already huge (DeadEndLeadOverride), in which case the distance is worth the risk. The lead
    // differential itself does the heavy lifting; this only breaks ties away from obvious traps.
    private const float DeadEndPenalty = 8f;
    private const float DeadEndLeadOverride = 15f;
    // Runners flee to a RANDOM goal within this many metres of the best escape-lead, so a cluster of
    // runners fans across several exits instead of funneling into one easily-swept node.
    private const float FleeGoalSpread = 6f;

    // Runner eat-objective (only when ActiveCans is non-empty). Low pressure = the nearest tagger's
    // graph path-distance to the runner exceeds CanSeekPressureDistance; then the runner detours to the
    // nearest active can instead of fleeing. An eat-commit freezes planning while the eat channel fills,
    // so it MUST stay interruptible: a tagger closing within CanAbortDistance clears the commit and the
    // runner flees next tick rather than channelling into a face-tag.
    private const float CanSeekPressureDistance = 12f;
    private const float CanAbortDistance = 6f;

    // Horizontal directions swept when hunting for a wall to fall toward — matches the motor's own
    // BotWallProbeDirections so the "steer at it" and "grab it" probes agree about what's reachable.
    private const int WallSearchDirections = 8;

    // Commit-latch lifetimes. Swing gets its own because the motor's swing.maxHangSeconds is 8 — a
    // flat 4s deadline expires mid-arc and replans the bot off its own edge.
    private const float CommitSeconds = 4f;
    private const float SwingCommitSeconds = 10f;
    // How far from a Swing edge's midpoint to look for the rope. The pivot hangs over the gap, and the
    // two authored swings span ~11m chasms, so this only has to cover half a span plus slack.
    private const float SwingSearchRadius = 12f;
    // Below this tangential speed there's no meaningful direction of travel to pump along, so the pump
    // seeds off the swing's exit direction instead of amplifying noise.
    private const float SwingPumpMinSpeed = 0.5f;
    // Horizontal range for the edge-stalk creep at a hanging target. Beyond this, normal routing closes
    // the distance first; inside it, the direct walk owns steering.
    private const float EdgeStalkRange = 7f;

    private TagAgent _agent = null!;
    private RoundController _roundController = null!;
    private ParkourGraph? _graph;
    private BotConfig.DifficultyTuning _tuning;

    private TagAgent? _target;
    private IReadOnlyList<ParkourEdge>? _path;
    private int _pathIndex;
    private float _nextDecisionTime;
    private MatchMetrics? _metrics;
    private System.Random _rng = new(0);

    // Jump-landing telemetry: capture the target node at takeoff, measure horizontal miss on landing.
    private bool _jumpInFlight;
    private Vector3 _jumpTargetPos;

    // True while approaching a short Jump edge — drop sprint so the jump doesn't overshoot.
    private bool _approachShortGap;
    private bool _jumpWasShort;   // was the in-flight jump a short (walk) one — for landing telemetry
    private float _jumpTargetZ;
    private bool _airJumpRequested; // edge-trigger: one mid-air double-jump press per airborne period

    // Fall recovery: the height we were last standing at (the reference for "have I dropped?"), and the
    // earliest time we'll launch off a wall we're clinging to.
    private float _lastGroundedY;
    private float _wallHangLaunchTime;

    // The rope for the Swing edge currently being executed — resolved from the world once per edge
    // (the graph only knows node ids) so steering can aim at it. Cleared when the edge completes.
    private ChainSwingInteractable? _edgeSwing;

    // TEMP DIAG (remove once swings land) — see the Swing case in ExecuteEdgeButtons.
    private float _swingMinRopeDist = float.MaxValue;
    private float _swingMinPivotDrop = float.MaxValue;
    private bool _swingAttached;
    private bool _swingSeenOccupied;
    private bool _swingRopeSearchFailed;

    // Commit-to-edge latch. Once the bot presses the button that starts a special edge
    // (jump/swing/climb/ladder/vault/mantle), planning freezes so the every-reactionTime replan can't
    // reset _pathIndex and drop the held approach steering + Interact mid-maneuver — the root cause of
    // Swing/Climb/Ladder showing hundreds of attempts and zero completions. Cleared when the committed
    // edge's ToNode is reached, on a fall-respawn teleport, or when the deadline expires (a maneuver
    // that stalled must not latch the bot forever).
    private bool _committed;
    private float _commitDeadline;
    private ParkourEdge? _committedEdge;

    // Juke state: while Time.time < _jukeEndTime the runner holds _jukeDir; can't re-juke until _jukeCooldownEnd.
    private float _jukeEndTime;
    private float _jukeCooldownEnd;
    private Vector3 _jukeDir;

    // Runner eat-objective: the active can this runner is currently detouring to eat (null = fleeing
    // normally). Set in RecomputePath when pressure is low, steered at via ComputeSteerPoint's fallback,
    // and cleared on arrival-abort, when the can is eaten/deactivated, or when flee is chosen again.
    private TrashCanInteractable? _targetCan;

    public Vector2 Move { get; private set; }
    public Vector2 Look => Vector2.zero;
    public bool JumpHeld => false;
    public bool JumpPressed { get; private set; }
    public bool SlideHeld => false;
    public bool SprintHeld => !_approachShortGap;
    public bool InteractPressed { get; private set; }

    /// <summary>Current planned path, exposed read-only for debug visualization.</summary>
    public IReadOnlyList<ParkourEdge>? CurrentPath => _path;

    /// <summary>Predicted target/threat position from the last replan, exposed read-only for debug visualization.</summary>
    public Vector3? LastPredictedPoint { get; private set; }

    public void Configure(TagAgent agent, RoundController roundController, ParkourGraph? graph, BotConfig botConfig, BotDifficulty difficulty)
    {
        _agent = agent;
        _roundController = roundController;
        _graph = graph;
        _tuning = botConfig.Get(difficulty);
    }

    /// <summary>Optional — only the self-play harness sets this, to record which edge types actually get traversed during a match.</summary>
    public void SetMetrics(MatchMetrics metrics) => _metrics = metrics;

    /// <summary>Optional — seeds this bot's decision RNG so self-play batches reproduce. Per-bot (not global Random) so bots don't couple through shared RNG call order. Unseeded bots all share seed 0.</summary>
    public void SetSeed(int seed) => _rng = new System.Random(seed);

    public void Tick(float deltaTime)
    {
        JumpPressed = false;
        InteractPressed = false;

        if (_agent.Motor.CurrentState == MotorState.Grounded)
        {
            _airJumpRequested = false; // double-jump recharges on the ground (mirrors the motor's _doubleJumpUsed reset)
            _lastGroundedY = transform.position.y; // reference height for the fall check
        }

        // Fall recovery owns the tick when it fires: a bot in the void has no route to plan, and its
        // path steering would just aim it at a target it can't reach.
        if (UpdateFallRecovery()) return;

        // Swinging owns the tick too — a pendulum is driven, not steered at. See UpdateSwingPump.
        if (UpdateSwingPump()) return;

        // Jump-shortfall recovery: descending mid-flight on a committed Jump edge, still short of the
        // target lip, with no floor within safe-drop reach below → press jump once for the double-jump
        // boost, converting a near-miss into a landing. Runner-only via the motor's CanDoubleJump gate;
        // the motor's _doubleJumpUsed already caps it at one per airborne period, _airJumpRequested just
        // stops the bot re-buffering the press every tick.
        if (_jumpInFlight && !_airJumpRequested && _agent.Motor.CanDoubleJump
            && _agent.Motor.CurrentState == MotorState.Airborne && _agent.Motor.Velocity.y < 0f)
        {
            Vector3 toLip = _jumpTargetPos - transform.position;
            toLip.y = 0f;
            if (toLip.magnitude > doubleJumpShortfallDistance
                && !Physics.Raycast(transform.position, Vector3.down, maxSafeDrop + 1f))
            {
                JumpPressed = true;
                _airJumpRequested = true;
            }
        }

        if (_jumpInFlight && _agent.Motor.CurrentState == MotorState.Grounded)
        {
            Vector3 d = transform.position - _jumpTargetPos;
            d.y = 0f;
            // Ignore implausibly large "landings" — a fall-respawn teleports the bot to spawn
            // mid-flight, which would otherwise register as a huge phantom miss and wreck the stats.
            if (d.magnitude <= 12f)
            {
                _metrics?.JumpLandingErrors.Add(d.magnitude);
                float signedZ = transform.position.z - _jumpTargetZ;
                if (_jumpWasShort) _metrics?.ShortJumpSignedOvershoot.Add(signedZ);
                else _metrics?.LongJumpSignedOvershoot.Add(signedZ);
            }
            else _committed = false; // a >12m "landing" means a fall-respawn teleported us mid-jump — drop the commitment
            _jumpInFlight = false;
        }

        // Round-start grace: only TAGGERS hold still — they're "unleashed" once the grace lifts.
        // Runners keep fleeing during grace so the window actually builds a head start; freezing
        // everyone (the old behaviour) gave runners zero separation, so taggers spawning adjacent
        // tagged the instant grace lifted (time_to_first_tag pinned at the grace boundary).
        if (_roundController != null && !_roundController.IsPastStartGrace && _agent.Role == Role.Tagger)
        {
            Move = Vector2.zero;
            return;
        }

        if (Time.time >= _nextDecisionTime)
        {
            _nextDecisionTime = Time.time + Mathf.Max(_tuning.reactionTime, 0.05f);
            if (_committed && Time.time >= _commitDeadline)
            {
                // TEMP DIAG (remove once swings land): a swing edge whose commit expired never reached
                // its exit node — report how close to the rope it got and whether it ever attached.
                if (_committedEdge is { Type: ParkourEdgeType.Swing })
                    ReportSwingDiag("deadline");
                _committed = false; // deadline — the maneuver stalled; let planning resume
            }
            SelectTarget();
            if (!_committed)
                RecomputePath(); // frozen while committed so a mid-edge bot keeps its approach + held Interact
        }

        // Runner eat-objective: stand on the target can to feed the eat channel, and bail if a tagger
        // closes. Runs EVERY tick (even while committed) so the eat-commit stays interruptible by threat.
        if (UpdateEatObjective())
            return; // standing still on the can; planning frozen by the commit latch

        if (_target == null)
        {
            Move = Vector2.zero;
            return;
        }

        Vector3 steerPoint = ComputeSteerPoint();
        _approachShortGap = IsShortJumpAhead();

        // Break-contact juke wins over normal path steering when it fires: cut sideways and skip
        // edge-button execution (no committing a jump mid-cut — that launches sideways into the void).
        Vector3 jukeDir = UpdateJuke();
        if (jukeDir != Vector3.zero)
        {
            _approachShortGap = false; // cut at sprint, not the short-jump walk
            Move = new Vector2(jukeDir.x, jukeDir.z);
            return;
        }

        // Edge-stalk: a hanging target (pipe/ladder/rope/wall over the void) breaks BOTH normal layers —
        // cliff-avoidance fans its below-lip direction into sideways milling so the pack never reaches
        // the lip where the ranged tag could connect, and the pre-lunge landing check vetoes every dive
        // at it because a hanging target is by definition over void. Net effect (reported from manual
        // play, screenshot of the whole pack huddled a few metres from the lip): camping a pipe top made
        // you untouchable. Stalking walks straight at the target's XZ at WALK speed and stops dead at
        // the lip — close enough for TryTagInRange's reach, and the lunge below gets its own hanging
        // exception.
        bool stalkingHangingTarget = UpdateEdgeStalk();

        if (!stalkingHangingTarget)
        {
            Vector3 rawDir = RawDirectionTo(steerPoint);
            Vector3 finalDir = ApplySteeringSafety(rawDir);

            Move = new Vector2(finalDir.x, finalDir.z);
            ExecuteEdgeButtons(rawDir);
        }

        if (_agent.Role == Role.Tagger)
        {
            _agent.TryTagInRange();

            // Lunge to close the last stretch: a committed forward dive when the target is within
            // range AND roughly ahead (so the bot dives AT it, not sideways). The dive's contact-tag
            // window can land the tag, and a bot diving after you reads exactly as intended.
            Vector3 toTarget = _target.transform.position - transform.position;
            bool targetAhead = Vector3.Dot(transform.forward, toTarget.normalized) > 0.6f;
            // LungeLandsOnGround is the void-bait fix: the dive is committed and outranges lungeRange,
            // so a target baiting from the lip's edge would otherwise pull the bot straight off it with
            // steering authority too low to recover. Refusing the dive just means closing on foot.
            //
            // EXCEPT at a hanging target: their position is over void, so the landing check would veto
            // every dive at them forever — that veto is exactly what made pipe-camping safe. Diving at
            // a hanger is a deliberate trade now that fall recovery exists: the dive's contact window
            // tags them mid-flight, and the tagger grabs a wall on the way down (worst case it eats the
            // fall-respawn — the tag still landed). Pest control hurling itself off a roof to peel you
            // off a pipe is precisely the "hunted" feel this bot is for.
            bool targetHanging = _target.Motor.CurrentState
                is MotorState.OnLadder or MotorState.OnSwing or MotorState.WallHook or MotorState.Climbing;
            if (toTarget.magnitude <= lungeRange && targetAhead && (LungeLandsOnGround() || targetHanging))
                _agent.TryLunge();
        }
    }

    // ---------------------------------------------------------------- Edge stalk

    /// <summary>
    /// Close-quarters approach to a HANGING target (ladder/pipe/rope/wall). Returns true while it owns
    /// steering. Walks — never sprints — straight at the target's XZ and freezes at the lip the moment
    /// the ground is about to run out, which parks the bot at exactly the spot TryTagInRange's
    /// chest-to-chest linecast clears the roof corner from. The walk matters twice: momentum at the lip
    /// is what the brake exists to prevent, and a creeping bot at the edge above you reads scarier than
    /// a sprinting one anyway.
    ///
    /// Deliberately NOT gated on difficulty: standing still doing nothing was never the intended Casual
    /// behaviour, it was a hole. Tier scaling still applies through reaction time (how fast they notice
    /// you hanging) and the lunge's aim jitter.
    /// </summary>
    private bool UpdateEdgeStalk()
    {
        if (_agent.Role != Role.Tagger || _target == null) return false;

        bool targetHanging = _target.Motor.CurrentState
            is MotorState.OnLadder or MotorState.OnSwing or MotorState.WallHook or MotorState.Climbing;
        if (!targetHanging) return false;

        Vector3 toTarget = _target.transform.position - transform.position;
        Vector3 flat = new(toTarget.x, 0f, toTarget.z);
        if (flat.magnitude > EdgeStalkRange) return false; // too far for the direct creep — keep routing

        _approachShortGap = true; // walk: SprintHeld is !_approachShortGap
        Vector3 dir = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.forward;
        Move = _agent.Motor.CurrentState == MotorState.Grounded && IsAboutToRunOffEdge(dir)
            ? Vector2.zero                  // parked at the lip — reach/lunge take it from here
            : new Vector2(dir.x, dir.z);
        return true;
    }

    // ---------------------------------------------------------------- Swing

    /// <summary>
    /// Drive the pendulum while on a rope. Returns true while it owns the input.
    ///
    /// Steering at the exit NODE — which is what normal path steering does, and what bots did before —
    /// is a CONSTANT force on a pendulum. Constant force doesn't add energy to a swing; it just shifts
    /// where the thing hangs. A bot that drops onto the rope out of a fall arrives with almost no
    /// tangential speed, so it hung near the bottom of the arc forever and the motor's auto-release
    /// (velocity toward ExitDirection > 5 AND rising > 1) could never be satisfied — measured: every
    /// swing ended how=deadline, attached=True, min_rope_dist=0.02-0.08. They caught the rope fine;
    /// nothing was building the arc.
    ///
    /// Pumping means pushing along the CURRENT direction of travel, which adds energy every tick, so
    /// amplitude grows until an exit-ward upswing satisfies the release. Seeded with the exit direction
    /// when velocity is ~0, otherwise a bot hanging dead-still has no direction to pump along.
    ///
    /// EXIT POLICY: never strands. The motor auto-releases when the arc finally carries it exit-ward,
    /// and force-releases at swing.maxHangSeconds (8) regardless; this only decides where to push.
    /// </summary>
    private bool UpdateSwingPump()
    {
        if (_agent.Motor.CurrentState != MotorState.OnSwing) return false;

        Vector3 velocity = _agent.Motor.HorizontalVelocity;
        Vector3 pump = velocity.sqrMagnitude > SwingPumpMinSpeed * SwingPumpMinSpeed
            ? velocity.normalized
            : (_edgeSwing != null ? _edgeSwing.ExitDirection : transform.forward);

        pump.y = 0f;
        if (pump.sqrMagnitude < 0.0001f) return false;
        pump.Normalize();

        Move = new Vector2(pump.x, pump.z);
        return true;
    }

    // ---------------------------------------------------------------- Fall recovery

    /// <summary>
    /// "I'm falling — find a wall, grab it, climb back up." Returns true while recovery owns the input.
    ///
    /// The chain is grab → launch → grab → launch, each cycle netting height: LaunchOffWallHook fires
    /// jumpUpSpeed (7.5) up and jumpOutSpeed (6) away along the wall normal, which across a rooftop
    /// chasm lands the bot on the FACING facade — a chimney climb. Runners additionally get the air
    /// jump (grabbing recharges it), so they climb faster than taggers; that asymmetry is real and
    /// intended, since RoundController only grants CanDoubleJump to Runners and a human tagger is under
    /// exactly the same restriction. No movement stat is touched here.
    ///
    /// Scales by tier through reaction time alone: a Scary bot hangs a beat and goes, a Casual bot
    /// dithers on the wall long enough to slide (slideDownSpeed 1.5) and can still lose the recovery.
    /// </summary>
    private bool UpdateFallRecovery()
    {
        MotorState state = _agent.Motor.CurrentState;

        if (state == MotorState.WallHook)
        {
            // TickWallHook pulls up into a mantle the instant a ledge is within reach, so if we're still
            // hanging there isn't one — launch and grab again higher. The reaction-time delay keeps the
            // launch off the same frame as the grab and is what makes the tiers differ.
            if (Time.time >= _wallHangLaunchTime) JumpPressed = true;
            Move = Vector2.zero; // no steering authority while clung; the launch does the work
            return true;
        }

        if (state is not MotorState.Airborne) return false;

        // Reference height for "have I dropped?". While a planned jump is in flight the bot is SUPPOSED
        // to be below its takeoff (a Drop edge descends deliberately, and every jump arcs down onto its
        // lip), so the comparison has to be against the landing it's aiming at — not where it jumped
        // from. Getting this wrong made recovery fire in the middle of perfectly good traversals:
        // measured self-play stuck 7 -> 14 and double-jumps 7 -> 30 as bots grabbed walls and burned
        // air-jumps mid-arc. Below the LANDING with nothing underneath is a miss; below the takeoff is
        // just a jump.
        float landingReference = _jumpInFlight ? _jumpTargetPos.y : _lastGroundedY;
        bool falling = _agent.Motor.Velocity.y < -2f;
        bool belowLanding = transform.position.y < landingReference - fallRecoveryDropThreshold;
        bool nothingBelow = !Physics.Raycast(transform.position, Vector3.down, fallRecoveryGroundProbe);
        if (!(falling && belowLanding && nothingBelow)) return false;

        if (_agent.Motor.TryBotWallGrab())
        {
            _wallHangLaunchTime = Time.time + Mathf.Max(_tuning.reactionTime, 0.05f);
            _airJumpRequested = false; // the grab recharged the motor's air jump; re-arm our own edge-trigger to match
            _jumpInFlight = false;     // whatever jump this was, it's over — don't score it as a landing later
            Move = Vector2.zero;
            return true;
        }

        // Nothing in grab range yet: steer at the nearest wall so the fall carries us TO one, and spend
        // the air jump (runners only — CanDoubleJump is role-gated) to buy height while we close on it.
        Vector3 toWall = NearestWallDirection();
        if (toWall != Vector3.zero) Move = new Vector2(toWall.x, toWall.z);

        if (_agent.Motor.CanDoubleJump && !_airJumpRequested)
        {
            JumpPressed = true;
            _airJumpRequested = true;
        }
        return true;
    }

    /// <summary>Nearest grabbable wall face around us, as a flat direction (zero if none in range).
    /// Rejects near-horizontal hits the same way the motor's grab does, so we never steer at a floor.</summary>
    private Vector3 NearestWallDirection()
    {
        Vector3 origin = transform.position + Vector3.up * 0.5f;
        float bestDistance = float.MaxValue;
        Vector3 bestDir = Vector3.zero;

        for (int i = 0; i < WallSearchDirections; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, i * (360f / WallSearchDirections), 0f) * Vector3.forward;
            if (!Physics.Raycast(origin, dir, out RaycastHit hit, fallRecoveryWallSearchRange)) continue;
            if (Mathf.Abs(hit.normal.y) > 0.3f) continue;
            if (hit.distance >= bestDistance) continue;
            bestDistance = hit.distance;
            bestDir = dir;
        }

        return bestDir;
    }

    // ---------------------------------------------------------------- Planning

    /// <summary>Refresh the target and its predicted intercept/flee point. ALWAYS runs — tag logic and
    /// prediction must stay live even while committed to an edge; only the path rebuild is gated.</summary>
    private void SelectTarget()
    {
        bool isTagger = _agent.Role == Role.Tagger;
        TagAgent? target = isTagger
            ? _roundController.FindNearestUnclaimedRunner(_agent)
            : _roundController.FindNearestOpposingAgent(_agent);

        _target = target;
        if (target == null) return; // leave _path alone; a committed bot finishes its edge, deadline clears it

        if (isTagger) _roundController.ClaimTarget(_agent, target);

        Vector3 predicted = PredictPosition(target);
        LastPredictedPoint = predicted;
    }

    /// <summary>Rebuild the planned path to the current target. SKIPPED while committed to an edge, so
    /// a mid-maneuver bot preserves its approach steering, held Interact, and completion bookkeeping
    /// across reaction ticks instead of resetting _pathIndex every 0.3s.</summary>
    private void RecomputePath()
    {
        if (_target == null || _graph == null)
        {
            _path = null;
            _pathIndex = 0;
            return;
        }

        bool isTagger = _agent.Role == Role.Tagger;
        Vector3 predicted = LastPredictedPoint ?? _target.transform.position;
        int startNode = _graph.NearestNode(transform.position);

        int goalNode;
        if (isTagger)
        {
            goalNode = _graph.NearestNode(predicted);
        }
        else
        {
            // Reuse ONE tagger scan for both the eat-pressure gate and the flee scorer.
            float[] threatDist = TaggerDistances();
            goalNode = TrySeekCanGoal(startNode, threatDist, out int canNode)
                ? canNode
                : FleeGoalNode(startNode, threatDist);
        }

        _path = _graph.FindPath(startNode, goalNode);
        _pathIndex = 0;
        _edgeSwing = null; // the new path's Swing edges resolve their own rope
    }

    /// <summary>Nearest-tagger graph path-distance to EVERY node (MIN over all taggers = pincer
    /// awareness). Shared by the flee scorer and the eat-pressure gate so the taggers are swept once.</summary>
    private float[] TaggerDistances()
    {
        var threatDist = new float[_graph!.Nodes.Count];
        for (int i = 0; i < threatDist.Length; i++) threatDist[i] = float.MaxValue;
        foreach (TagAgent agent in _roundController.Agents)
        {
            if (agent.Role != Role.Tagger) continue;
            float[] td = _graph.DistancesFrom(_graph.NearestNode(agent.transform.position));
            for (int i = 0; i < td.Length; i++)
                if (td[i] < threatDist[i]) threatDist[i] = td[i];
        }
        return threatDist;
    }

    /// <summary>Low-pressure eat detour: if any active can exists AND the nearest tagger's path-distance
    /// to the runner exceeds CanSeekPressureDistance, target the nearest active can and route to its node.
    /// Returns false (→ normal flee) with no cans or under pressure, gating this so a can-free scene keeps
    /// runner behaviour exactly as before. threatDist[startNode] is the shared scan — no fresh sweep.</summary>
    private bool TrySeekCanGoal(int startNode, float[] threatDist, out int canNode)
    {
        canNode = startNode;
        _targetCan = null;

        IReadOnlyList<TrashCanInteractable> cans = _roundController.ActiveCans;
        if (cans.Count == 0) return false;                                  // no cans → flee as before
        if (threatDist[startNode] <= CanSeekPressureDistance) return false; // tagger near → flee

        TrashCanInteractable? nearest = null;
        float bestSqr = float.MaxValue;
        Vector3 pos = transform.position;
        for (int i = 0; i < cans.Count; i++)
        {
            float sqr = (cans[i].Position - pos).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; nearest = cans[i]; }
        }
        if (nearest == null) return false;

        _targetCan = nearest;
        canNode = _graph!.NearestNode(nearest.Position);
        return true;
    }

    /// <summary>Runner eat-objective, evaluated every tick so it can interrupt the eat-commit. Drops a
    /// vanished can, ABORTS (clears the eat-commit) if a tagger is within CanAbortDistance, else — once
    /// within EatRadius of the can — stands still and latches the commit for the eat duration (+slack) so
    /// RoundController's eat channel fills without replanning. Returns true only while standing to eat.</summary>
    private bool UpdateEatObjective()
    {
        if (_targetCan == null) return false;

        bool eatCommit = _committed && _committedEdge == null; // the eat-commit has no backing edge

        // Can eaten/deactivated (by us or another runner, or a round reset) → resume normal planning.
        if (!_targetCan.IsActive || _targetCan.IsEaten)
        {
            _targetCan = null;
            if (eatCommit) _committed = false;
            return false;
        }

        float taggerDist = _target != null
            ? Vector3.Distance(transform.position, _target.transform.position)
            : float.MaxValue;

        // Abort hatch: a tagger this close means channelling = a face-tag. Clear the eat-commit only
        // (never a mid-jump edge commit) and force an immediate flee replan.
        if (taggerDist <= CanAbortDistance)
        {
            _targetCan = null;
            if (eatCommit) _committed = false;
            _nextDecisionTime = Time.time; // replan next tick instead of coasting on the stale can route
            return false;
        }

        // On the can → stop and freeze planning for the eat (mirrors Commit's latch, but with the
        // eat-duration deadline and no backing edge).
        if (Vector3.Distance(transform.position, _targetCan.Position) <= _roundController.EatRadius)
        {
            Move = Vector2.zero;
            if (!_committed)
            {
                _committed = true;
                _commitDeadline = Time.time + _targetCan.EatDuration + 1f;
                _committedEdge = null;
            }
            return true;
        }

        return false; // still walking toward the can — let normal steering carry it there
    }

    private Vector3 PredictPosition(TagAgent target)
    {
        Vector3 predicted = target.transform.position + target.Motor.HorizontalVelocity * _tuning.predictionHorizon;

        // Lower-precision bots predict sloppily: add positional noise scaled by (1 - precision).
        float jitter = (1f - _tuning.executionPrecision) * 4f;
        if (jitter > 0.01f)
            predicted += new Vector3(RandRange(-jitter, jitter), 0f, RandRange(-jitter, jitter));

        return predicted;
    }

    /// <summary>
    /// Threat-aware flee: pick the reachable graph node that maximizes the runner's ESCAPE LEAD —
    /// its own path-distance to the node minus the nearest tagger's path-distance to that same node.
    /// A node the runner reaches well before any tagger (large positive lead) is real safety; a node
    /// closer to some tagger (a pincer, or one the runner would arrive at second) scores low even if
    /// it lies in the away-from-threat direction. Using MIN over ALL taggers folds pincer-awareness in
    /// for free. Replaces the old "farthest node in the away-vector direction" heuristic that routed
    /// runners straight into corners, dead-end roofs, and the second tagger's arms — the diagnosed
    /// reason survival was pinned at 0.00. Falls back to the start node (→ empty path → direct
    /// steering) only if nothing is reachable.
    /// </summary>
    private int FleeGoalNode(int startNode, float[] threatDist)
    {
        float[] selfDist = _graph!.DistancesFrom(startNode);

        int best = startNode;
        float bestScore = float.NegativeInfinity;
        var scores = new float[selfDist.Length];
        for (int i = 0; i < selfDist.Length; i++)
        {
            if (i == startNode || selfDist[i] == float.MaxValue)
            {
                scores[i] = float.NegativeInfinity; // skip self + unreachable
                continue;
            }
            float lead = threatDist[i] - selfDist[i]; // how far ahead of the nearest tagger the runner arrives
            if (_graph.OutgoingEdges(i).Count <= 2 && lead < DeadEndLeadOverride) lead -= DeadEndPenalty;
            scores[i] = lead;
            if (lead > bestScore)
            {
                bestScore = lead;
                best = i;
            }
        }

        // Spread the flee: 10 runners scoring near-identically all funnel to the SAME best node and get
        // swept as one cluster. Pick RANDOMLY among goals within FleeGoalSpread metres of the best lead,
        // so runners fan out across several good exits — deterministic flee is trivial for a pincer to
        // corner. Per-bot seeded RNG (_rng), matching PredictPosition's existing jitter.
        if (bestScore == float.NegativeInfinity) return best;
        var pool = new List<int>();
        for (int i = 0; i < scores.Length; i++)
            if (scores[i] >= bestScore - FleeGoalSpread) pool.Add(i);
        return pool.Count > 0 ? pool[RandIndex(pool.Count)] : best;
    }

    private Vector3 ComputeSteerPoint()
    {
        if (_path == null || _path.Count == 0)
            return _target!.transform.position;

        while (_pathIndex < _path.Count)
        {
            ParkourEdge edge = _path[_pathIndex];
            Vector3 toNodePos = _graph!.Nodes[edge.ToNode].Position;

            // Swing approach: aim at the ROPE, not the far lip. The grab volume is a radius-1.2 capsule
            // hanging down the rope line, and the exit node is metres past it — steering at the exit
            // sends the bot off the entry lip on a trajectory that only clips the rope by chance, which
            // is exactly why swings measured 9 attempts / 0 completions. Once attached, the pendulum
            // owns the motion and the exit node is the right target again.
            if (edge.Type == ParkourEdgeType.Swing && _edgeSwing != null
                && _agent.Motor.CurrentState != MotorState.OnSwing)
            {
                Vector3 pivot = _edgeSwing.PivotPosition;
                return new Vector3(pivot.x, transform.position.y, pivot.z);
            }

            if (Vector3.Distance(transform.position, toNodePos) > nodeArrivalRadius)
                return toNodePos;

            _metrics?.RecordEdgeUsage(edge.Type);
            if (edge.Type == ParkourEdgeType.Swing) ReportSwingDiag("completed"); // TEMP DIAG
            if (_committed && ReferenceEquals(edge, _committedEdge))
            {
                _committed = false; // committed edge completed — arrived at its ToNode
                _committedEdge = null;
            }
            _edgeSwing = null; // next Swing edge resolves its own rope; a stale one would misaim the approach
            _pathIndex++;
        }

        // Path fully walked — home in on the eat-target can if seeking one (final approach onto the
        // can so UpdateEatObjective can trigger), else the actual (possibly-moved) target directly.
        return _targetCan != null ? _targetCan.Position : _target!.transform.position;
    }

    // ---------------------------------------------------------------- Steering / execution

    /// <summary>
    /// Break-contact juke for runners. Returns the sideways cut direction while a juke is active (or
    /// starts one this tick), else Vector3.zero. Starts a juke only when grounded, off cooldown, NOT
    /// mid-committed-edge (a cut off a jump takeoff = the chasm — respects the WP1 commit latch), and
    /// the nearest tagger is within jukeTriggerRange AND closing (relative velocity shrinking the gap).
    /// Cuts ~90° off the tagger→runner axis toward whichever side is safe (never off a lip); if both
    /// sides are cliffs, doesn't juke. Human juke: the tagger's reaction lag + linear prediction can't
    /// track the perpendicular break, so a gap opens where running straight never did.
    /// </summary>
    private Vector3 UpdateJuke()
    {
        if (_agent.Role != Role.Runner || _target == null) return Vector3.zero;

        // Hold an in-progress juke (re-validating safety each tick — the world moved under us).
        if (Time.time < _jukeEndTime)
        {
            Vector3 held = FindSafeDirection(_jukeDir);
            if (held != Vector3.zero) return held;
            _jukeEndTime = 0f; // nowhere safe to cut — bail out of the juke rather than run off a roof
            return Vector3.zero;
        }

        if (_committed || _jumpInFlight) return Vector3.zero;
        if (_agent.Motor.CurrentState != MotorState.Grounded) return Vector3.zero;
        if (Time.time < _jukeCooldownEnd) return Vector3.zero;

        Vector3 toTagger = _target.transform.position - transform.position;
        toTagger.y = 0f;
        float dist = toTagger.magnitude;
        if (dist > jukeTriggerRange || dist < 0.1f) return Vector3.zero;

        Vector3 toTaggerDir = toTagger / dist;
        Vector3 relVel = _target.Motor.HorizontalVelocity - _agent.Motor.HorizontalVelocity;
        if (Vector3.Dot(relVel, toTaggerDir) >= 0f) return Vector3.zero; // gap not shrinking — no need to juke

        // ~90° off the flee axis; pick the safe side, tie-broken toward current momentum.
        Vector3 fleeAxis = -toTaggerDir;
        Vector3 right = Quaternion.Euler(0f, 90f, 0f) * fleeAxis;
        Vector3 left = Quaternion.Euler(0f, -90f, 0f) * fleeAxis;
        bool rightSafe = IsSafe(right), leftSafe = IsSafe(left);
        Vector3 chosen;
        if (rightSafe && leftSafe)
        {
            Vector3 vel = _agent.Motor.HorizontalVelocity;
            chosen = Vector3.Dot(right, vel) >= Vector3.Dot(left, vel) ? right : left;
        }
        else if (rightSafe) chosen = right;
        else if (leftSafe) chosen = left;
        else return Vector3.zero; // both sides are cliffs — hold the path route

        _jukeDir = chosen;
        _jukeEndTime = Time.time + jukeDuration;
        _jukeCooldownEnd = _jukeEndTime + jukeCooldown;
        return chosen;
    }

    private Vector3 RawDirectionTo(Vector3 point)
    {
        Vector3 toPoint = point - transform.position;
        Vector3 flat = new(toPoint.x, 0f, toPoint.z);
        Vector3 dir = flat.sqrMagnitude > 0.0001f ? flat.normalized : transform.forward;

        float jitterDeg = (1f - _tuning.executionPrecision) * maxSteeringJitterDegrees;
        if (jitterDeg > 0.01f)
            dir = Quaternion.Euler(0f, RandRange(-jitterDeg, jitterDeg), 0f) * dir;

        return dir;
    }

    private float RandRange(float min, float max) => min + (float)_rng.NextDouble() * (max - min);
    private int RandIndex(int countExclusive) => _rng.Next(0, countExclusive);

    /// <summary>
    /// Cliff-avoidance is only wanted where solid ground is actually expected, which means two
    /// separate suppressions:
    ///
    /// (1) Never off the ground. IsSafe probes for a floor, so over a gap it fails in EVERY direction,
    /// FindSafeDirection returns zero, and Move goes to zero mid-flight — air control dies and a jump
    /// that would have landed drops into the void instead. Airborne/rope/ladder/transition states have
    /// no floor to probe and nothing to steer away from.
    ///
    /// (2) On approach to a deliberate gap-crossing edge — but only inside the TAKEOFF CONE (near the
    /// takeoff node AND heading along the edge), not for the edge's whole duration. Outside the cone
    /// there's no takeoff to protect, so avoidance (and the momentum brake) stay on. Note this is
    /// hardening, NOT the void-fall fix: the old edge-wide suppression was measured NOT to cause falls,
    /// because steering jitter is re-rolled per tick and so is zero-mean noise that never accumulates
    /// into a side-lip departure. The actual void fall was momentum — see SteerSafely.
    /// </summary>
    private Vector3 ApplySteeringSafety(Vector3 dir)
    {
        // Grounded and Sliding are the only states standing on a floor worth probing (Sliding matters:
        // a slide-hop chain must still not slide off a roof).
        if (_agent.Motor.CurrentState is not (MotorState.Grounded or MotorState.Sliding))
            return dir;

        if (_graph == null || _path == null || _pathIndex >= _path.Count)
            return SteerSafely(dir);

        ParkourEdge edge = _path[_pathIndex];
        bool crossingGapIsExpected = edge.Type
            is ParkourEdgeType.Jump or ParkourEdgeType.SlideHop
               or ParkourEdgeType.Vault or ParkourEdgeType.Mantle or ParkourEdgeType.Drop
               or ParkourEdgeType.Swing;
        if (!crossingGapIsExpected) return SteerSafely(dir);

        return InTakeoffCone(edge, dir) ? dir : SteerSafely(dir);
    }

    /// <summary>
    /// Is the bot actually in position to take off along this edge — within takeoffConeRadius of its
    /// takeoff node AND heading along it? This is the landing check: the takeoff node is the only
    /// spot on the roof whose arc is known to reach the far node, because the graph builder validated
    /// exactly that pair (RooftopGraphBuilder.JumpMakeable). Anywhere else on the lip, the same jump
    /// leaves the map.
    ///
    /// Shared deliberately by ApplySteeringSafety (suppress cliff-avoidance only here) and
    /// ExecuteEdgeButtons (press Jump only here). Both previously trusted "the current edge is a
    /// Jump" on its own, which fires at ANY lip the bot happens to reach — so a bot nudged toward a
    /// side lip by steering jitter, a shove, or a bait would launch itself into the void with
    /// avoidance switched off. One guard covering both is why the fix holds.
    ///
    /// The cone opens naturally: the Run edge preceding a Jump ends AT its takeoff node, so when the
    /// path advances onto the Jump the bot is already within nodeArrivalRadius of it — well inside.
    /// </summary>
    private bool InTakeoffCone(ParkourEdge edge, Vector3 dir)
    {
        if (_graph == null) return false;

        Vector3 takeoff = _graph.Nodes[edge.FromNode].Position;
        Vector3 toTakeoff = takeoff - transform.position;
        toTakeoff.y = 0f;
        if (toTakeoff.sqrMagnitude > takeoffConeRadius * takeoffConeRadius)
            return false; // too far out to be taking off — a lip here is a fall, not a route

        Vector3 alongEdge = _graph.Nodes[edge.ToNode].Position - takeoff;
        alongEdge.y = 0f;
        if (alongEdge.sqrMagnitude < 0.0001f) return true; // degenerate edge — nothing to align against

        return Vector3.Dot(dir.normalized, alongEdge.normalized) >= takeoffConeAlignment;
    }

    private void ExecuteEdgeButtons(Vector3 steeringDir)
    {
        if (_path == null || _pathIndex >= _path.Count) return;

        ParkourEdge edge = _path[_pathIndex];
        switch (edge.Type)
        {
            case ParkourEdgeType.Jump:
            case ParkourEdgeType.SlideHop:
            case ParkourEdgeType.Drop:
                // Jump exactly when the ground is about to run out underfoot, rather than at a
                // fixed distance from the landing node — robust regardless of the actual gap size.
                // Drop (a one-way descent edge, e.g. Tower's second exit) reuses this verbatim: a
                // jump press off a ledge you're descending from is still the correct way to clear
                // the gap, it just lands lower than it took off.
                // InTakeoffCone is the landing check: without it this fires at ANY lip the ground
                // runs out at, so a bot carrying a Jump edge that drifts to a side lip launches into
                // the void on faith. Only the validated takeoff node's arc is known to reach ToNode.
                if (_agent.Motor.CurrentState == MotorState.Grounded && IsAboutToRunOffEdge(steeringDir)
                    && InTakeoffCone(edge, steeringDir))
                {
                    JumpPressed = true;
                    Commit(edge);
                    _metrics?.JumpTakeoffSpeeds.Add(_agent.Motor.HorizontalVelocity.magnitude);
                    _jumpInFlight = true;
                    _jumpTargetPos = _graph!.Nodes[edge.ToNode].Position;
                    _jumpTargetZ = _jumpTargetPos.z;
                    _jumpWasShort = _approachShortGap;
                }
                break;

            case ParkourEdgeType.Vault:
            case ParkourEdgeType.Mantle:
                // Vault/mantle are motor-auto when the bot arrives with speed, but a bot that runs
                // into the ledge and stops deadlocks: CharacterMotor bails its mantle/vault check
                // when stopped unless jump or interact is down (see TryMantleOrVaultOrClimb's early
                // return). Hold interact while executing the edge so a stalled bot still pops over —
                // the vault ledges (~0.55m / ~1.05m) sit in the mantle band, which has no speed gate.
                // Harmless while moving: no ladder/swing/wall-hook exists on these ledges to trigger.
                InteractPressed = true;
                Commit(edge);
                // If it has actually stalled against the ledge, hop: a jump clears the low boxes
                // outright (jump height ~2m exceeds every vault/mantle ledge) or re-triggers the
                // mantle check airborne (TickAirborne runs it too), breaking a dead stop the interact
                // alone doesn't — the "bots get stuck on the orange boxes" case.
                HopIfStalled();
                break;

            case ParkourEdgeType.Swing:
                // Hold interact the whole way across: the bot runs off the entry platform toward the
                // exit and must grab the rope while airborne mid-chasm — the grab point is nowhere
                // near either node, so it just keeps interact down until it catches the rope. Once ON
                // the swing, interact must stop — E now releases from a swing (see TickSwing), so
                // holding it in would grab and instantly bail. Letting go once attached is safe: the
                // motor's ExitDirection auto-release still handles the actual departure toward the exit.
                if (_agent.Motor.CurrentState != MotorState.OnSwing)
                    InteractPressed = true;
                // Resolve the actual rope so ComputeSteerPoint can aim AT it. Holding interact is
                // useless if the bot's arc never enters the grab capsule, which is what steering at the
                // far exit node produced: measured 9 swing attempts, 0 completions — it was catching the
                // rope only by luck of trajectory.
                CacheEdgeSwing(edge);
                // TEMP DIAG (remove once swings land): attempts>>usage says bots start swing edges and
                // never finish them. Track how close we actually get to the rope and whether we ever
                // attach, so the failure stage is data rather than another guess.
                if (_edgeSwing != null)
                {
                    Vector3 pv = _edgeSwing.PivotPosition;
                    Vector3 me = transform.position;
                    float ropeDist = new Vector2(me.x - pv.x, me.z - pv.z).magnitude;
                    _swingMinRopeDist = Mathf.Min(_swingMinRopeDist, ropeDist);
                    _swingMinPivotDrop = Mathf.Min(_swingMinPivotDrop, pv.y - me.y);
                    if (_agent.Motor.CurrentState == MotorState.OnSwing) _swingAttached = true;
                    if (_edgeSwing.IsOccupied && _agent.Motor.CurrentState != MotorState.OnSwing) _swingSeenOccupied = true;
                }
                Commit(edge);
                break;

            case ParkourEdgeType.Climb:
            case ParkourEdgeType.Ladder:
                // Press interact when near EITHER end of the edge — a ladder's target node is 8m up at
                // the top, so gating on the ToNode alone meant the bot never pressed E at the base and
                // just stood there. The nearer endpoint is the attach point (ladder base, climb wall).
                float distFrom = Vector3.Distance(transform.position, _graph!.Nodes[edge.FromNode].Position);
                float distTo = Vector3.Distance(transform.position, _graph.Nodes[edge.ToNode].Position);
                if (Mathf.Min(distFrom, distTo) <= interactTriggerDistance)
                {
                    InteractPressed = true;
                    Commit(edge);
                }
                break;
        }
    }

    /// <summary>Latch onto the special edge the bot has just started executing: freeze planning until
    /// the edge completes (ToNode arrival), a fall-respawn teleport, or the deadline. Records
    /// exactly ONE attempt per execution here — swing/climb/ladder previously counted per-FixedUpdate
    /// (frame-inflated: 333/471/54 attempts, 0 completions); this matches Jump's per-event semantics.
    /// No-op if already committed, so neither the counter nor the deadline re-arm each frame.
    ///
    /// The deadline is per-edge-kind because a flat 4s is SHORTER than a swing's own designed hang
    /// budget (swing.maxHangSeconds = 8): a bot that attached correctly would have its commit expire
    /// mid-arc, replan, steer at some unrelated point while the pendulum fought it, and orphan the edge
    /// so even a good landing never scored as usage.</summary>
    private void Commit(ParkourEdge edge)
    {
        if (_committed) return;
        _committed = true;
        _commitDeadline = Time.time + (edge.Type == ParkourEdgeType.Swing ? SwingCommitSeconds : CommitSeconds);
        _committedEdge = edge;
        _metrics?.RecordEdgeAttempt(edge.Type);
    }

    /// <summary>TEMP DIAG (remove once swings land): dump what happened to one swing edge execution.
    /// min_rope_dist is the closest the bot's XZ ever got to the rope line — the grab capsule is radius
    /// 1.2 and the motor's own overlap adds ladderGrabRange 1.2, so anything under ~2.4 should have been
    /// catchable. min_pivot_drop is how far BELOW the pivot it passed: a negative value means it never
    /// got below the pivot at all, i.e. it never reached the chain's height to grab it.</summary>
    private void ReportSwingDiag(string how)
    {
        _swingRopeSearchFailed = false; // re-arm the rope-missing one-shot for the next execution
        if (_swingMinRopeDist == float.MaxValue) return;
        Debug.Log($"METRIC swingdiag how={how} attached={_swingAttached} occupied_seen={_swingSeenOccupied} " +
                  $"min_rope_dist={_swingMinRopeDist:0.00} min_pivot_drop={_swingMinPivotDrop:0.00}");
        _swingMinRopeDist = float.MaxValue;
        _swingMinPivotDrop = float.MaxValue;
        _swingAttached = false;
        _swingSeenOccupied = false;
        _swingRopeSearchFailed = false;
    }

    /// <summary>Find the rope this Swing edge actually refers to, once per execution. The graph only
    /// carries node ids, and the pivot is nowhere near either of them, so the rope has to be located in
    /// the world — it hangs over the gap, hence the search from the edge's midpoint.</summary>
    private void CacheEdgeSwing(ParkourEdge edge)
    {
        if (_edgeSwing != null || _graph == null) return;

        Vector3 midpoint = (_graph.Nodes[edge.FromNode].Position + _graph.Nodes[edge.ToNode].Position) * 0.5f;
        Collider[] hits = Physics.OverlapSphere(midpoint, SwingSearchRadius, ~0, QueryTriggerInteraction.Collide);
        foreach (Collider col in hits)
            if (col.TryGetComponent(out ChainSwingInteractable swing)) { _edgeSwing = swing; return; }

        // TEMP DIAG (remove once swings land): a failed lookup was previously SILENT, and the attach
        // diagnostics are all gated behind _edgeSwing != null — so "rope not found" and "no data" were
        // indistinguishable. Once per execution: the retry runs every tick until the edge ends.
        if (!_swingRopeSearchFailed)
        {
            _swingRopeSearchFailed = true;
            Debug.Log($"METRIC swingdiag how=rope_missing midpoint=({midpoint.x:0.0},{midpoint.y:0.0},{midpoint.z:0.0}) overlaps={hits.Length}");
        }
    }

    /// <summary>Should the bot approach at walk speed to avoid overshooting a short gap? Only for a
    /// short jump the bot is *about to take* — either the current edge is that short jump, or the bot
    /// is on the Run approach immediately before one (it needs the run-up to decelerate sprint→walk,
    /// which takes more than the short takeoff platform alone). Crucially it does NOT walk a *long*
    /// jump whose next edge happens to be short — doing so made long jumps fall short into the pit.</summary>
    private bool IsShortJumpAhead()
    {
        if (_path == null || _pathIndex >= _path.Count) return false;
        ParkourEdgeType current = _path[_pathIndex].Type;
        if (current == ParkourEdgeType.Jump) return IsShortJumpEdge(_pathIndex);
        if (current == ParkourEdgeType.Run) return IsShortJumpEdge(_pathIndex + 1);
        return false;
    }

    private bool IsShortJumpEdge(int index)
    {
        if (_path == null || index < 0 || index >= _path.Count) return false;
        ParkourEdge edge = _path[index];
        if (edge.Type != ParkourEdgeType.Jump) return false;

        // Real per-edge void distance (lip-to-lip, insets removed) baked in by RooftopGraphBuilder —
        // no longer the retired corridor's PlatformLength const that misclassified nearly every gap.
        return edge.EmptyGap <= shortJumpGapThreshold;
    }

    /// <summary>Jump if the bot has effectively stopped while grounded — used at ledges to break a
    /// dead stall against an obstacle the mantle/vault auto-detect couldn't resolve from standstill.</summary>
    private void HopIfStalled()
    {
        if (_agent.Motor.CurrentState == MotorState.Grounded && _agent.Motor.HorizontalVelocity.magnitude < 1.5f)
            JumpPressed = true;
    }

    private bool IsAboutToRunOffEdge(Vector3 moveDir)
    {
        Vector3 aheadPoint = transform.position + moveDir.normalized * edgeLookahead;
        Vector3 rayOrigin = new(aheadPoint.x, transform.position.y + 0.5f, aheadPoint.z);
        return !Physics.Raycast(rayOrigin, Vector3.down, 1.0f);
    }

    /// <summary>
    /// Cliff-avoidance with a momentum brake in front of it. Braking must be checked against our
    /// VELOCITY, not the desired direction: steering away from a lip does nothing about the 7m/s
    /// already carrying us at it, and FindSafeDirection happily returns a 20°-off heading that keeps
    /// nearly all of that forward speed. Returning zero hands the motor no wish direction, so its
    /// deceleration does the work.
    ///
    /// The horizon is speed-scaled on purpose (see edgeBrakeSeconds): as the bot slows, its horizon
    /// shrinks with it, so it settles into a creep and can still reach a target parked on the lip.
    /// </summary>
    private Vector3 SteerSafely(Vector3 desired)
    {
        Vector3 velocity = _agent.Motor.HorizontalVelocity;
        float speed = velocity.magnitude;
        if (speed > 0.5f && !HasGroundAt(velocity, speed * edgeBrakeSeconds))
            return Vector3.zero;

        return FindSafeDirection(desired);
    }

    private Vector3 FindSafeDirection(Vector3 desired)
    {
        if (IsSafe(desired)) return desired;

        for (int angle = 20; angle <= 160; angle += 20)
        {
            Vector3 right = Quaternion.Euler(0f, angle, 0f) * desired;
            if (IsSafe(right)) return right;

            Vector3 left = Quaternion.Euler(0f, -angle, 0f) * desired;
            if (IsSafe(left)) return left;
        }

        return Vector3.zero;
    }

    private bool IsSafe(Vector3 direction) => HasGroundAt(direction, lookAheadDistance);

    /// <summary>Is there floor within the safe-drop band at <paramref name="distance"/> along
    /// <paramref name="direction"/>? The shared probe behind both steering cliff-avoidance (which asks
    /// at lookAheadDistance) and the pre-lunge landing check (which asks at the dive's full reach).</summary>
    private bool HasGroundAt(Vector3 direction, float distance)
    {
        Vector3 point = transform.position + direction.normalized * distance;
        Vector3 rayOrigin = new(point.x, transform.position.y + upwardClearance, point.z);
        return Physics.Raycast(rayOrigin, Vector3.down, upwardClearance + maxSafeDrop);
    }

    /// <summary>
    /// Landing check for the lunge — the one committed decision cliff-avoidance CANNOT undo.
    /// BeginDive locks the motor for diveDuration and cuts steering authority to diveSteeringScale
    /// (0.15), and avoidance only shapes Move, so once the dive starts the bot goes where it was
    /// pointed. Reach (~diveSpeed*diveDuration, 7.2m) exceeds lungeRange (4.5m), so diving at a target
    /// standing near a lip overshoots straight past the edge: bait the tagger, step aside, watch it
    /// commit into the void. Probing the dive's END POINT (not the target's position) is the fix.
    ///
    /// Endpoint-only on purpose: a dive that clears a small gap and lands on the far side is a good
    /// diving catch, and this still allows it. Arriving faster than diveSpeed is preserved rather than
    /// clamped (see TagRulesConfig.diveSpeed), so reach takes the greater of the two.
    /// </summary>
    private bool LungeLandsOnGround() =>
        HasGroundAt(transform.forward, _agent.DiveReachAt(_agent.Motor.HorizontalVelocity.magnitude));
}
