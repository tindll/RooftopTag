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

        // Hold still until the round-start grace lifts — the taggers are "unleashed" only after the
        // runner's head start, rather than chasing from t=0.
        if (_roundController != null && !_roundController.IsPastStartGrace)
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

        if (_target == null)
        {
            Move = Vector2.zero;
            return;
        }

        Vector3 steerPoint = ComputeSteerPoint();
        _approachShortGap = IsShortJumpAhead();
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
        int goalNode = isTagger
            ? _graph.NearestNode(predicted)
            : FleeGoalNode(predicted, startNode);
        _path = _graph.FindPath(startNode, goalNode);
        _pathIndex = 0;
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
    /// Runners flee toward the graph node lying farthest in the away-from-threat direction, so they
    /// escape INTO the parkour arena (traversing its jump/climb/swing edges) instead of steering
    /// to a raw radial point that lands off the small spawn pad — NearestNode would snap that point
    /// to a platform-edge node and march the runner over the edge. This was the M4-loop root cause
    /// of the spawn-scrum collapse: radial flee dumped runners off the pad (high fall count) before
    /// they ever reached the arena, so the infection cascade cleared everyone in &lt;10s.
    /// Falls back to the start node (→ empty path → direct steering) when no node lies away from the
    /// threat, e.g. a runner already cornered at the arena's far end.
    /// </summary>
    private int FleeGoalNode(Vector3 threatPredictedPos, int startNode)
    {
        Vector3 self = transform.position;
        Vector3 fleeDir = self - threatPredictedPos;
        fleeDir.y = 0f;
        fleeDir = fleeDir.sqrMagnitude > 0.0001f ? fleeDir.normalized : transform.forward;

        IReadOnlyList<ParkourNode> nodes = _graph!.Nodes;
        int best = startNode;
        float bestScore = 0f; // strictly-positive projection required, so a node must lie away from the threat
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i == startNode) continue;
            Vector3 toNode = nodes[i].Position - self;
            toNode.y = 0f;
            float alongFlee = Vector3.Dot(toNode, fleeDir);
            if (alongFlee <= bestScore) continue;
            // Only flee somewhere actually reachable — an unreachable farthest node yields a null
            // path, whose fallback steers the runner straight at its threat (see ComputeSteerPoint).
            if (_graph.FindPath(startNode, i) == null) continue;
            bestScore = alongFlee;
            best = i;
        }
        return best;
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

        // Path fully walked — home in on the actual (possibly-moved) target directly.
        return _target!.transform.position;
    }

    // ---------------------------------------------------------------- Steering / execution

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
                // Jump exactly when the ground is about to run out underfoot, rather than at a
                // fixed distance from the landing node — robust regardless of the actual gap size.
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
