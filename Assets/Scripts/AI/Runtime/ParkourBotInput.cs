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

    [SerializeField] private float maxSteeringJitterDegrees = 30f;

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

    private TagAgent _agent = null!;
    private RoundController _roundController = null!;
    private ParkourGraph? _graph;
    private BotConfig.DifficultyTuning _tuning;

    private TagAgent? _target;
    private IReadOnlyList<ParkourEdge>? _path;
    private int _pathIndex;
    private float _nextDecisionTime;
    private MatchMetrics? _metrics;

    // Jump-landing telemetry: capture the target node at takeoff, measure horizontal miss on landing.
    private bool _jumpInFlight;
    private Vector3 _jumpTargetPos;

    // True while approaching a short Jump edge — drop sprint so the jump doesn't overshoot.
    private bool _approachShortGap;
    private bool _jumpWasShort;   // was the in-flight jump a short (walk) one — for landing telemetry
    private float _jumpTargetZ;
    private bool _airJumpRequested; // edge-trigger: one mid-air double-jump press per airborne period

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

    public void Tick(float deltaTime)
    {
        JumpPressed = false;
        InteractPressed = false;

        if (_agent.Motor.CurrentState == MotorState.Grounded)
            _airJumpRequested = false; // double-jump recharges on the ground (mirrors the motor's _doubleJumpUsed reset)

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
                _committed = false; // deadline — the maneuver stalled; let planning resume
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

        Vector3 rawDir = RawDirectionTo(steerPoint);
        Vector3 finalDir = ApplySteeringSafety(rawDir);

        Move = new Vector2(finalDir.x, finalDir.z);
        ExecuteEdgeButtons(rawDir);

        if (_agent.Role == Role.Tagger)
        {
            _agent.TryTagInRange();

            // Lunge to close the last stretch: a committed forward dive when the target is within
            // range AND roughly ahead (so the bot dives AT it, not sideways). The dive's contact-tag
            // window can land the tag, and a bot diving after you reads exactly as intended.
            Vector3 toTarget = _target.transform.position - transform.position;
            bool targetAhead = Vector3.Dot(transform.forward, toTarget.normalized) > 0.6f;
            if (toTarget.magnitude <= lungeRange && targetAhead)
                _agent.TryLunge();
        }
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
            predicted += new Vector3(Random.Range(-jitter, jitter), 0f, Random.Range(-jitter, jitter));

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
        // corner. Unity Random (not Time-seeded), matching PredictPosition's existing jitter.
        if (bestScore == float.NegativeInfinity) return best;
        var pool = new List<int>();
        for (int i = 0; i < scores.Length; i++)
            if (scores[i] >= bestScore - FleeGoalSpread) pool.Add(i);
        return pool.Count > 0 ? pool[Random.Range(0, pool.Count)] : best;
    }

    private Vector3 ComputeSteerPoint()
    {
        if (_path == null || _path.Count == 0)
            return _target!.transform.position;

        while (_pathIndex < _path.Count)
        {
            ParkourEdge edge = _path[_pathIndex];
            Vector3 toNodePos = _graph!.Nodes[edge.ToNode].Position;

            if (Vector3.Distance(transform.position, toNodePos) > nodeArrivalRadius)
                return toNodePos;

            _metrics?.RecordEdgeUsage(edge.Type);
            if (_committed && ReferenceEquals(edge, _committedEdge))
            {
                _committed = false; // committed edge completed — arrived at its ToNode
                _committedEdge = null;
            }
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
            dir = Quaternion.Euler(0f, Random.Range(-jitterDeg, jitterDeg), 0f) * dir;

        return dir;
    }

    /// <summary>Cliff-avoidance is only wanted where solid ground is actually expected — suppress it while executing an edge that's a deliberate gap-crossing, or it would prevent the very jump the route calls for.</summary>
    private Vector3 ApplySteeringSafety(Vector3 dir)
    {
        bool crossingGapIsExpected = _path != null && _pathIndex < _path.Count && _path[_pathIndex].Type
            is ParkourEdgeType.Jump or ParkourEdgeType.SlideHop
               or ParkourEdgeType.Vault or ParkourEdgeType.Mantle or ParkourEdgeType.Drop
               or ParkourEdgeType.Swing;

        return crossingGapIsExpected ? dir : FindSafeDirection(dir);
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
                if (_agent.Motor.CurrentState == MotorState.Grounded && IsAboutToRunOffEdge(steeringDir))
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
    /// the edge completes (ToNode arrival), a fall-respawn teleport, or the 4s deadline. Records
    /// exactly ONE attempt per execution here — swing/climb/ladder previously counted per-FixedUpdate
    /// (frame-inflated: 333/471/54 attempts, 0 completions); this matches Jump's per-event semantics.
    /// No-op if already committed, so neither the counter nor the deadline re-arm each frame.</summary>
    private void Commit(ParkourEdge edge)
    {
        if (_committed) return;
        _committed = true;
        _commitDeadline = Time.time + 4f;
        _committedEdge = edge;
        _metrics?.RecordEdgeAttempt(edge.Type);
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

    private bool IsSafe(Vector3 direction)
    {
        Vector3 lookAheadPoint = transform.position + direction.normalized * lookAheadDistance;
        Vector3 rayOrigin = new(lookAheadPoint.x, transform.position.y + upwardClearance, lookAheadPoint.z);
        return Physics.Raycast(rayOrigin, Vector3.down, upwardClearance + maxSafeDrop);
    }
}
