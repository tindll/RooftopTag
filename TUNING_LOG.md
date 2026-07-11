# Tuning Log

Running log of movement/bot/map changes: hypothesis, metric outcome, decision. Append entries
in the same session-as-iteration format used below.

## Session — rooftop playground + movement/feature work

Beyond the M4 self-play loop, a run of feature + feel work driven by live testing:

- **New map — Rooftop Arena** (`RooftopArena` + `RooftopGraphBuilder`, `RooftopTag/Build Rooftop Arena`):
  7 rooftops at varied heights linked by jumps + a ramp + a ladder, full bot nav graph, chase mode
  (player + 2 bot taggers). Single-source layout like TagArenaLayout so geometry and graph can't
  desync. Everyone respawns at spawn on falling off (nothing below the gaps).
- **Movement**: vault/mantle now require E (player only; bots keep auto-clamber). Slide reworked —
  slides down any slope on CTRL, A/D *steers* (was flinging sideways), small flat-ground boost, leans
  back. Ladder top-dismount fixed (launch up-and-forward onto the platform, was stranding climbers).
  Swing physics strengthened + graph-connected + bot auto-release (still needs feel work).
- **Feel/juice**: lunge dive (arms + body pitch) + slide dive-lean; lunge contact-tag (dive window,
  max 1/lunge); bots lunge at the player. Capsule mesh moved to an aligned child (fixed floor-clip).
- **Polish loop (this session)**: facing turned from a constant-rate 540°/s RotateTowards (a "whip")
  to an exponential ease (`turnResponsiveness`) — smoother, and a nice side effect: bot jump accuracy
  `jump_land_within_1.75m` climbed to **0.79**. Camera `positionSmoothTime` 0.06→0.09. Respawn grace
  on fall. Remaining polish (camera on height changes, rooftop chase tuning) is feel-gated on the user.

---

## M4 loop — 11 — Live-test round 2: contact-tag, lunge dive, first-gap widen

More human feel-test findings.

**Contact-tag removed:** `TagAgent.OnCollisionEnter -> TryTag` tagged on any physical body contact —
so landing on or brushing a runner tagged them with no input, contradicting the right-click-only
design. Deleted; tagging is now exclusively the ranged attempt (player right-click / bot `TryTagInRange`).

**Lunge dive (arms + body):** `TryLunge` now drives both arms up-and-forward and pitches the whole
model forward (`DiveDuration` 0.45s, `DiveMaxPitchDeg` 32) via `LateUpdate` — purely visual, since
`CharacterMotor` rewrites the transform's yaw-only facing every FixedUpdate before physics, so the
pitch never reaches the collider.

**First jump fixed by widening the opening gap:** the 3m opening gap was below the bots' controllable
range — a fixed ~8.5m sprint jump flew clean over it, and walk-modulation couldn't decelerate in the
~6m of run-up available (tried `IsShortJumpAhead` current-or-next: overshot; refined to walk only a
short jump or its Run approach, never a long jump: recovered the long-jump regression but the 3m gap
stayed unmakeable). Root fix: widened `TagArenaLayout.Gaps[0]` 3→6m (sprint-jumpable) and rebuilt the
saved scene headlessly (`-executeMethod Game.EditorTools.PlaygroundBuilder.BuildTagArena` — the scene
geometry is baked, so a layout change needs this or the editor desyncs from the runtime graph).
Metric: `jump_land_within_1.75m 0.30→0.50`, `jump_landing_err_avg 7.3→4.84`, jump attempts 129→321,
Drop completions appearing. win_rate still ~0.1-0.2 (noisy) — falls (~94) now from the wide 9m gap and
the wall-run chasm, the next targets.

---

## M4 loop — 10 — Live-test fixes: vault deadlock + jump-commit modulation

Driven by a human feel-test (bots cleared jumps + wall-run but got stuck on the orange vault ledges,
and many failed the very first jump).

**Vault/mantle deadlock (fixed):** `CharacterMotor.TryMantleOrVaultOrClimb` early-returns when the
bot is stopped (`CurrentSpeed < 0.1`) unless jump/interact is held — so a bot that ran into the ledge
and stopped could never trigger and stuck forever. `ParkourBotInput.ExecuteEdgeButtons` also pressed
interact only for Climb/Ladder/Swing, never Vault/Mantle. Fix: bots now hold interact on Vault/Mantle
edges (the ~0.55m/1.05m ledges fall in the mantle band, no speed gate, so a stalled bot pops over) and
Mantle joins the steering-safety gap-crossing exemption. (Self-play under-reports this — its motors
skip `Configure`, and vault completions record poorly through the mantle transition; validated live.)

**Jump-commit modulation (fixed the "first jump" failures):** bots jumped at fixed ~8.5m sprint power
regardless of gap, so the 3m opening gap made them overshoot into the next pit (same root as loop-9's
gap-narrowing backfire). `ParkourBotInput` now walks the approach (`SprintHeld` false) when the current
Jump edge's empty gap ≤ `shortJumpGapThreshold` (4.5m) — walk range ~4.4m matches short gaps; longer
gaps still sprint. Metric: `jump_landing_err_avg 9.3→6.64`, `jump_land_within_1.75m 0.30→0.40`,
takeoff avg 8.2→6.7 (the walk-approaches). win_rate 0.20 (still in the noisy 0.1-0.3 band).

---

## M4 loop — 9 — Layout single-source refactor; gap-size tuning isn't the lever

**Refactor (the desync class of bug, killed):** added `Game.MapGeometry.TagArenaLayout` — one class
that walks the corridor sequence once and records every walk-surface anchor. `TagArenaMapGeometry`
now renders its boxes/ramps at those anchors, and `TagArenaParkourGraphBuilder` places its graph
nodes at the same anchors (added a `Game.AI -> Game.MapGeometry` asmdef reference). Gap edge costs
dropped their hardcoded strides — `ParkourGraph` derives them from node distance. Net: the graph can
no longer drift from the physical map; changing a section length moves boxes and nodes together.
Verified behaviour-preserving: `max_z_reached` 192.1 → 193.0, same traversal, compiles clean.

**Then used it — narrowed the gaps in one edit** (`Gaps {3,5,7,9,8,7} -> {3,5,6,7,7,6}`, ≤7m):
hypothesis was that the 8-9m gaps (past bots' ~7.5m reliable range) were the dominant fall source.
**Result: falls got worse** (92 -> 100), win_rate 0.30 -> 0.10 — reverted. Bots jump at a fixed
~8.5m power regardless of gap width, so *narrower* gaps make them **overshoot** the platform into the
next pit. Gap geometry isn't the lever; the bot needs to modulate its jump to the gap.

**State after direction 3:** map fully traversable (max_z ~193, runners reach the climb section),
graph/geometry unified behind `TagArenaLayout`, jump landing ~30% on-target. **win_rate still ~0.1-0.3
and noisy.** Falls (~90-100) are now spread across the whole route — gauntlet over/undershoots, the
10m wall-run chasm (WallRun attempts ~10, ~0 completions), ledge drops. The remaining win_rate work is
per-technique bot *execution* (jump commit sizing, wall-run entry/timing), not layout or pathfinding —
a deeper AI pass. Refactor leaves that work safe to iterate: geometry and graph move together now.

---

## M4 loop — 8 — Root cause of the jump failures: a self-inflicted graph disconnect

**Direction 3 (fix jumps + restore parkour). Observation first:** instrumented per-jump landing
error (`JumpLandingErrors`: horizontal distance from where the bot lands to the Jump edge's target
node). Ruled out the short-run-up theory earlier (takeoff ~7.5-8.2 m/s, enough). Landing error came
back **9.17m avg, only 19% within 1.75m** — jumps landing a full gap off.

**Root cause — graph/geometry cost confusion, self-inflicted in loop step 3:** the gauntlet's graph
nodes sit on platform centres (z=36,43,52,63,76,88), and the Jump edge `cost` is the centre-to-centre
*stride* (platformLength 4m + gap) = 7,9,11,13,12,11. The **actual empty gap** a bot jumps is
`TagArenaMapGeometry`'s `{3,5,7,9,8,7}`m — all inside the ~9.6m ceiling, i.e. every gap is crossable
(the geometry was already tapered down from the old 11/13m). Loop step 3's `AddGapJump` gate compared
the *stride cost* (7-13) against 9.5 as if it were the jump distance, and so **severed jumpable 7-8m
gaps**, disconnecting the entire wall-run/vault/mantle/climb section and leaving runners aiming at
phantom/severed routes.

**Change:** reverted the disconnect — all six gap edges restored (real gaps all ≤9.6m). Removed the
`PathHasGap` no-gap flee workaround (step 6) since gaps are crossable again. Tightened
`edgeLookahead` 1.3→0.6 (less wasted takeoff range).

**Metric outcome:** `max_z_reached 36→192` — runners now traverse the **whole** map (gauntlet → wall-
run alley → vault/mantle/climb at z189). Edge variety returning (`Drop=2` completed, `WallRun=12`
attempted), takeoff at full sprint (8.16), jump landing within 1.75m **0.19→0.31**. **But win_rate
still 0.00**, falls 78: the 9m gap (gap1→gap2) is at the bots' ragged jump edge, so ~69% of jumps
there still fall short. (Also noted: Jump *completions* under-record because bots replan mid-air —
the 0.31 landing-within-1.75m is the true success rate, not the edge-usage count.)

**Decision / handoff:** structural blocker fixed and parkour restored — the map is fully traversable
and edge types are reappearing. Remaining gap to win_rate is (a) jump reliability on the 8-9m gaps
(narrow those gaps toward ~7m in geometry *and* re-derive the graph node z's from the geometry so they
can't desync again — the desync is the recurring hazard here), and (b) runner evasion. Both are
iterative tuning on a now-correct foundation, not blocked.

---

## M4 loop — 6-7 — Falls halved, but the win_rate wall is runner evasion

**6 — runners stop routing across gaps (kept):** jump execution is ~2% reliable, so any flee path
crossing a gap was a near-certain death — the dominant fall source. `FleeGoalNode` now rejects
candidate goals whose path contains a Jump/SlideHop/Drop edge (`PathHasGap`); runners flee only
along the contiguous safe corridor and survive by evasion, not by gambling on a jump. Outcome:
`total_fallen 53->21`, jump attempts 87->8, max_z 59->46. **But win_rate still 0.00** — the
runners that no longer fall simply get tagged instead.

**7 — cascade-slow (reverted):** with falls controlled, isolated tagging as the remaining killer:
one tagger's infection cascade clears all 11 runners inside 60s. Tried `conversionGraceDuration
2.5->5s` to keep fewer taggers active early. No effect — `win_rate 0.00`, `total_fallen=17`.
Reverted (rule: keep only changes that raise win_rate). A single *active* tagger catching ~1
runner every few seconds clears 11 in 60s regardless of how slowly the cascade grows.

**Diagnostic added (kept):** `jump_takeoff_speed_avg` — ruled out the short-run-up theory (bots
take off at ~6.5-7.4 m/s, enough for the 7m gap), confirming the jump failure is aim/landing, not
approach speed.

**The wall (stopping the batch, per "skip if stuck"):** win_rate is 0.00 on a stable 10-match
measure and has not moved across 7 iterations. The chain is fully mapped — spawn-scrum collapse
(fixed) -> flee-off-pad falls (fixed) -> air-braked jumps (fixed) -> gap-death falls (avoided) ->
**runner evasion can't survive a competent tagger for 60s**. That last one is not a tweak: it needs
either much deeper runner evasion AI (juke/lead-breaking/route variety) or a structural balance/
level change (bigger map, more escape routes, tagger handicap, or restoring the stranded parkour
section so runners have real movement outs). Both are design-level calls for the user.

---

## M4 loop — 2-5 — Jump execution is the real blocker; measurement was lying

**2 (diagnostic):** added `MatchMetrics.EdgeAttemptCounts` (button-press attempts) alongside the
existing completion counts, logged as `total_edge_attempts`. First run resolved the "edges=Run
only" mystery: `Jump=45` attempts, **0** completions — bots *try* to jump but never land on the
target node. Not a "never attempts" bug; an execution bug.

**3 — un-jumpable gaps are a false affordance:** the graph's gap gauntlet has costs 7,9,11,13,12,
11m but max sprint-jump is ~9.6m (MOVEMENT_CAPABILITIES.md). `TagArenaParkourGraphBuilder` now only
emits Jump edges for gaps ≤9.5m (`AddGapJump`); wider gaps get no edge. Made `FleeGoalNode`
reachability-aware (skip nodes with a null path — an unreachable farthest node fell back to
steering at the threat). Outcome: `Jump=92` attempts, still **0** completions — so the *jumpable*
7m/9m gaps fail too. Gap width wasn't the (only) cause. **Debt:** disconnecting the wide gaps
strands the wall-run/vault/mantle/climb section beyond the gauntlet — no bot can reach it, so those
edge types are currently unreachable. Restoring them needs a level pass adding a crossable route
(swing / downhill slide-hop) over the 11-13m gaps.

**4 — air-brake misfire (real bug, fixed):** `CharacterMotor.ApplyAirAcceleration` treated
`_input.Move.y < -0.1` as a deliberate air-brake ("press S"). That's a *player* camera-relative
intent; AI feeds `Move` as a world-space direction (cameraYaw == null), so its `Move.y` is just the
world-Z steering component — any bot fleeing or aiming toward -Z got air-braked mid-jump and
dropped into the gap. Gated the brake on `cameraYaw != null`. Outcome: falls 34→19, and the
first-ever Jump *completions* appeared — but still only a trickle.

**5 — stabilized measurement + depth probe:** `MatchCount` 3→10 (3 matches was pure noise: the
same code read 0.00 or 0.33 run-to-run), added `max_z_reached`. **Stable 10-match result:**
`runner_win_rate=0.00 total_fallen=77 total_edge_usage=[Run=140, Jump=2] total_edge_attempts=
[Jump=101] max_z_reached=60.6`.

**Conclusion / the wall:** the earlier 0.33 readings were noise — true win_rate is 0.00. Runners
*do* flee deep into the gauntlet (z=60.6) and *do* attempt jumps (101), but complete only ~2%
(2/101), so fleeing into the parkour arena = death by falling (77 falls). **Jump execution
reliability is the single lever gating win_rate** and it isn't cracked yet: needs a jump-landing
observation (takeoff speed/position vs the gap, landing vs target node) to see whether bots take
off too early, aim off-axis (steering jitter), or the `nodeArrivalRadius` (1.75m) is too tight to
record near-misses. Two real bugs fixed and kept along the way (radial-flee-off-pad, air-brake
misfire); measurement now trustworthy. Stopping the N-iteration batch here to report — next
iteration is a focused jump-execution investigation, not a blind tweak.

---

## M4 loop — 1 — Runners flee into the arena instead of off the spawn pad

**Baseline (loop start):** `runner_win_rate=0.00 speed_p90=8.00 total_stuck=0 total_fallen=10
total_edge_usage=[Run=26]` — every match a <10s tagger sweep, no parkour, ~3 falls/match.

**Diagnosis (structural, per M3 "stop-and-investigate" decision):** infection-tag cascade from the
tight 2.5m spawn grid. `ParkourBotInput.ComputeFleePoint` fled *radially* to `pos + awayDir*10`,
an off-map point; `Graph.NearestNode` snapped it to a platform-edge node, so runners marched off
the spawn pad (the ~3 falls) instead of escaping into the corridor where their movement kit would
buy survival. One tagger then cleared the packed remainder in seconds.

**Change:** replaced radial flee with `FleeGoalNode` — runners now path to the graph node lying
farthest in the away-from-threat direction (strictly-positive projection), guaranteeing an on-map
goal inside the arena. Removed the now-dead `fleeDistance` field.

**Metric outcome:** `runner_win_rate=0.00 -> 0.33` (one match survived the full 60s), matches no
longer collapse (durations 59s/27s/27s vs 5-9s), Run traversals up (26 -> 68). **But** falls
regressed hard (`total_fallen 10 -> 34`) and stuck appeared (`total_stuck 0 -> 11`).

**Decision:** net-positive on the headline metric, so kept. New worst gap is edge *execution*:
`total_edge_usage` is still `[Run=...]` only — runners flee into the corridor but walk off its
drops/jumps (never pressing Jump) rather than traversing them, which is exactly the new falls, and
jam at corridor ends (the stuck). Next iteration targets why no non-Run edge is ever executed.

---

## M3 — Self-play harness built; first improvement cycle

**Change:** Built the "self-playtest loop" from CLAUDE.md — `Assets/Tests/PlayMode/SelfPlayTests.cs`
runs a batch of full bot-only Tag Arena matches (12 `ParkourBotInput` agents, no human, no camera)
at 8x `Time.timeScale`, entirely in-memory (map geometry + agents built and torn down per match,
nothing touches a scene file). Logs per-match and aggregate metrics: winner, time-to-first-tag,
parkour edge-type usage, stuck-agent count (displacement < 0.75m over a 3s window), fall count
(y < -20), and speed p50/p90. Runnable headlessly via `-runTests -testFilter SelfPlayTests` — no
Editor UI needed, so this is the tool for iterating on bot behavior without a human watching.

Enabled by a refactor first: moved `PlaygroundBuilder`'s geometry-creation code (boxes, ramps, map
sections) into a new runtime-safe `Game.MapGeometry` asmdef, since none of it actually touches
UnityEditor APIs — only the outer scene-save orchestration does. Both the Editor scene builder and
the self-play harness now build the physical map from the same source instead of two hand-synced
copies. (Ladder/swing geometry stays in `PlaygroundBuilder` — it attaches an `InteractableMarker`,
which has to stay namespace-free to survive scene serialization, so a custom asmdef can't touch it.)

**Target bands (first pass, not yet validated against real feel — revise once matches look sane):**
- Runner win rate: 40-60% at Skilled difficulty (CLAUDE.md's own example band).
- Every reachable edge type used at least a few times per match (currently only `Run` — expected,
  see below).
- Stuck agents: 0 per match. Falls: low single digits per match at most.

**First batch result — round-start instant-tag cascade (real bug, found immediately):** every
match ended in under 3 seconds, `time_to_first_tag=0.00`. Root cause: 12 agents spawn on a tight
1.8m grid with taggers assigned at t=0 and no grace before the first tag is even possible —
physically adjacent taggers were landing contact-tags before any bot had taken a single meaningful
action. Fixed both contributing factors: added `TagRulesConfig.roundStartGraceDuration` (3s, no
tag can land before it elapses — mirrors the existing per-agent conversion grace but for the whole
round) via a new `RoundController.IsPastStartGrace` that both `TagAgent.TryTag` (collision) and
`TryTagInRange` (ranged) now check; and widened the spawn grid 1.8m -> 2.5m (still fits the 8x8
spawn platform: a 4x3 grid now spans 7.5x5.0).

**Second batch result — improved but still far outside target, flagged rather than blindly
iterated on:** time-to-first-tag now lands right at ~2.8-2.9s (immediately as grace lifts, as
expected), but matches still end with every runner tagged within 4.7-6.3s total —
`runner_win_rate=0.00` against a 40-60% target, and `edges=[Run=...]` only, meaning bots never
used a single Jump/WallRun/Vault/Mantle/Climb edge before the match ended. That's consistent with
the collapse happening while everyone's still clustered near spawn, before anyone reaches the gap
gauntlet at all. Also 1-2 fallen agents per match — worth a look (bots pushed off the spawn
platform mid-scrum, or a genuine cliff-avoidance gap).

**Decision:** Stopping here to report rather than continuing to tune blind. The round-collapse
pattern (chase resolves almost instantly, no edge-type variety, some falls) points at something
more structural than a numbers tweak — likely needs actual investigation (does fleeing work when
bots are packed together in a scrum? is contact-tagging too generous with no reach limit at all?
is the spawn platform too small for 10 runners to evade 2 taggers without colliding into each
other?) rather than another guess-and-recheck pass.

---

## M3 — Smart bots (first pass)

**Change:** Replaced `ChaseFleeBotInput` (straight-line chase/flee + cliff-avoidance only) with
`ParkourBotInput`, backed by a new waypoint/edge parkour graph, per the M3 spec.

- `Game.AI/ParkourGraph` + `ParkourNode`/`ParkourEdge`/`ParkourEdgeType`: typed edges (run, jump,
  slide-hop, wall-run, mantle, vault, climb, ladder, swing, drop) each carrying a required entry
  speed, with Dijkstra shortest-path (linear-scan "priority queue" — these graphs are a few dozen
  nodes at most, a binary heap isn't worth the complexity).
- `TagArenaParkourGraphBuilder`: hand-authored graph matching the Tag Arena's shared greybox
  layout (spawn → ramp valley → gap gauntlet → wall-run alley → ledge row). Coordinates are
  duplicated from `PlaygroundBuilder`'s layout math rather than shared with it, because
  `PlaygroundBuilder` lives in an Editor-only assembly that a runtime assembly like `Game.AI`
  can't reference — if the map layout changes, these need updating too.
- `BotConfig`: Casual/Skilled/Scary tiers scaling reaction time, prediction horizon, and execution
  precision only — never movement stats, per the architecture constraint.
- `ParkourBotInput`: follows the graph, executing each edge's technique — presses Jump exactly
  when the ground is about to run out underfoot (a short raycast check, robust regardless of the
  actual gap size) for Jump/SlideHop/WallRun edges, presses Interact near Climb/Ladder/Swing
  edges. Falls back to the old direct-line-plus-cliff-avoidance behavior when there's no graph, no
  path, or the path's been fully walked. Taggers predict the target's position
  (`position + horizontalVelocity * predictionHorizon`, with positional noise scaled by `1 -
  executionPrecision`) and path toward that intercept point instead of the target's current spot.
  Runners do the same for their nearest threat, then flee directly away from the predicted point.
- `RoundController.ClaimTarget`/`FindNearestUnclaimedRunner`: loose coordination — taggers prefer
  an unclaimed runner over piling onto the same one, falling back to the plain nearest if every
  runner's already claimed.
- `ParkourDebugVisualizer`: toggle with **G** — draws graph edges (colored by type), each bot's
  current planned path, and tagger intercept-prediction lines. Hidden by default.

**Known map issue found, not fixed (out of scope for bot work):** the ledge row's final obstacle,
`TooTall_Control`, is a deliberate control wall taller than the climb threshold (see
`PlaygroundBuilder.BuildLedgeRow` — it exists to verify "walls stay meaningful obstacles" for
manual feel-testing). It fully blocks the only corridor, which means the ladder and swing-chasm
sections built after it are currently **unreachable from spawn by any route, for the player or
bots alike**. The parkour graph stops at the last reachable ledge (`Climb_Mid`) rather than
silently routing around a wall that can't actually be crossed. Worth fixing next: either split the
control wall out of the main corridor into its own dead-end spur, or move it after a branch point
so the ladder/swing sections stay reachable.

**Metric outcome:** Full suite 18/18 passing, zero warnings, both scenes rebuilt.
`ParkourBotInput_AvoidsRunningOffCliff` (renamed from the old class) still passes — the fallback
cliff-avoidance path is unchanged, still uses the corrected 3m-above/2m-below raycast band.

**Decision:** This is a first pass, not a tuned/feel-tested one — no self-playtest metrics harness
(headless bot-only matches, per-round metrics, target-band comparison) has been built yet, and
wall-run edge execution in particular (the lateral offset needed to stay within the wall's short
detection range) is untested against the real map. Needs a manual feel-test round before going
further (smarter runner route variety, the self-playtest loop, or fixing the ladder/swing
reachability issue above).

---

## M1 feel-test round 9 — air-brake never actually reversed direction

**Feedback:** "pressing 'S' mid air doesn't really send you backwards when it really should."

**Root cause — real bug:** `ApplyAirAcceleration`'s back-brake branch exponentially damped
horizontal velocity *toward zero* only — `braked = horizontal * exp(-rate*dt)`. That converges
asymptotically to a standstill but mathematically never crosses zero into reverse, no matter how
long S is held.

**Fix:** damp toward a genuine backward target velocity instead of zero:
`braked = target + (horizontal - target) * exp(-rate*dt)`, where `target = -transform.forward *
config.ground.airBrakeReverseSpeed` (new field, 3 m/s). First attempt derived the target
direction from the *current* velocity direction instead of the character's facing — caught before
shipping by reasoning through the equilibrium: once velocity crossed zero into reverse, that
target would itself flip back to "forward," oscillating instead of settling. Fixed to anchor the
target to `-transform.forward`, which stays fixed regardless of current velocity, converging
cleanly.

**Metric outcome:** new regression test `AirBrake_HoldingBackEventuallyReversesDirection` —
holding back after a sprint-jump reverses horizontal direction at 0.38s (hand-calculated estimate
before running it: ~0.37s, using the closed-form equilibrium of the exponential decay — matched).
Full suite 18/18 passing, zero warnings.

**Decision:** Keep. 3 m/s reverse speed is a first guess, not feel-tested yet.

---

## M2 feel-test round 5 — arm-trigger bug, binary reach, mantle-push animation, wall-hook redesign

**Feedback:** arms were animating on lunge (left click) instead of tag (right click); tag reach
shouldn't grow with sprint/jump, only still-vs-moving; confirm tag is nearest-first, one at a
time; a mantle/vault "push down" arm animation; and a request to replace the automatic wall-climb
with an explicit E-grab ledge climb plus a new E-triggered "wall hook" for a second aerial jump.

**Arm-trigger bug — real bug:** `TryLunge` (left click) was starting the reach-arm coroutine;
right click (`TryTagInRange`) didn't touch the arms at all. Moved the animation trigger to
`TryTagInRange`, playing on every attempted tag (hit or not) as feedback that the input
registered — lunge no longer touches the arms.

**Reach radius — redesigned per feedback:** replaced the stopping-distance physics estimate
(which scaled with `CurrentSpeed`, so sprinting and mid-air horizontal speed both inflated it)
with a flat binary check: `TagRulesConfig.tagReachStill` (1.2) vs `tagReachMoving` (2.0), picked
by whether `CharacterMotor.CurrentSpeed` (horizontal only, so a vertical-only jump doesn't count
as "moving") is above a small threshold. The debug ring reuses the same value, so what you see is
exactly what can hit.

**One-at-a-time tag — already correct, no change:** `TryTagInRange` only ever resolves
`RoundController.FindNearestOpposingAgent`, i.e. the single closest opposing agent, and a single
button press calls it once. Confirmed via code read rather than assumed.

**Mantle/vault push animation:** `TagAgent.Update()` now watches `CharacterMotor.CurrentState` and
plays an arm sweep the tick it transitions into `Mantling`/`Vaulting`. Reworked the arm rig to
support this: arms used to be a single capsule whose *position* animated (extend/retract along Z);
a rotation-based "elbow" sweep needs a fixed hinge point, so arms are now a small pivot at the
shoulder (`ArmShoulderY`, never moves) with the visible capsule offset outward from it — animating
the pivot's rotation swings the whole arm like a rigid rod hinged at the shoulder. Tag-reach and
mantle-push are the same underlying sweep (`AnimateArmSweep`) with different angle pairs, so the
"arms fly forward" and "arms push down" gestures share one mechanism instead of two. Note: exact
angles (`ArmMantleRaisedDeg`/`ArmMantlePushedDeg` etc.) are a best-guess approximation of a real
elbow bend using a single rigid segment (no separate upper-arm/forearm), tune-by-feel like
everything else here.

**Wall-climb redesign:** per "the player shouldn't just be able to climb any wall" — the old climb
branch in `CharacterMotor.TryMantleOrVaultOrClimb` auto-triggered just by holding Jump into a
wall within the climb height band. Changed the trigger to `_input.InteractPressed` (E), and
`TickClimbing` no longer requires holding Jump to keep climbing — one E-press now commits to the
whole climb-to-mantle sequence, same as a deliberate ledge grab rather than an automatic scramble.
Mantle and vault are untouched (still automatic) since the complaint was specifically about climb
letting the player scale walls with no real gate.

**New: wall hook.** Separate from wall-running (continuous, speed-gated, for moving *along* a
wall) and from the climb-to-ledge above (requires a ledge within reach) — this is for a sheer wall
with nothing to grab: jump at it, press E to catch a brief stationary hold
(`MovementConfig.wallHook`, default 1.2s max), then press Jump to launch off
(`wallHook.jumpOutSpeed`/`jumpUpSpeed`, away-from-wall + upward), landing back in Airborne. There's
no existing multi-jump in this game (`TickAirborne`'s jump path is coyote-gated), so this is the
only way to get a second aerial jump — deliberately something the player has to reach a wall to
earn, not an unconditional double-jump. New `MotorState.WallHook`, new `MovementConfig.WallHookSettings`.
Bots don't use E for anything yet (ladders, swings, and now climb/wall-hook are all player-only
until M3's parkour-graph navigation), so this is a player-only mechanic for now, matching the
existing ladder/swing limitation.

**Test updates:** `Climb_ReachesThresholdHeightLedge` now calls `ScriptedCharacterInput
.PressInteract()` every tick while approaching (instead of holding `JumpHeld`), matching the new
trigger.

**Metric outcome:** Full suite 17/17 passing, zero warnings, both scenes rebuilt.

**Decision:** Keep all of the above. Wall-hook detection range/hold duration/launch impulse are
first-guess defaults with no feel-test yet — flagged as the first thing to tune next round.

---

## M2 feel-test round 4 — split lunge/tag buttons, fix arm attachment/shape/color

**Feedback:** left click already lunges; tag should be right click and land on whoever's within
the reach ring instead of requiring an actual body collision. Also the lunge arms weren't
connecting to the model, looked like plain boxes, and should share the player model's color.

**Lunge/tag split:** left click still only lunges (pure movement burst via
`CharacterMotor.AddImpulse`, arms punch out). Added a genuinely separate `TagAgent.TryTagInRange()`
bound to right click (left trigger on gamepad): finds the nearest opposing agent via the same
`RoundController.FindNearestOpposingAgent` bots already use, and tags it if it's within
`CurrentReachRadius()` — the same stopping-distance estimate that already drove the debug ring,
now doing double duty as the actual hit-detection radius. Collision-based tagging (`OnCollisionEnter`)
is left in place as a fallback for an actual body bump; both paths funnel through a shared
`PerformTag` so the grace/color/boop side effects only live in one place. Bots got the same
split (`ChaseFleeBotInput` now calls `TryTagInRange()` every tick in addition to lunging to close
distance), since before this they had the identical "only tags on collision" limitation the
player was complaining about.

**Arms not connecting — real bug, not a tuning nit:** `CharacterMotor.Awake()` resizes the
*CapsuleCollider* to `config.ground.capsuleHeight` (1.8) and re-centers it so its base sits at
the object's pivot — but the *visible mesh* is the untouched default Unity capsule primitive,
which is always exactly 2 units tall and vertically centered *on* the pivot (-1..+1), independent
of that collider resizing. So the true physical collider top sits at +1.8 (invisible — there's no
mesh there) while the render mesh's actual visible top is only +1. My original arm placement
(`y=1.3`) was calibrated against the collider, landing above the visible mesh entirely — floating
disconnected boxes above what looked like a headless torso. Recalibrated arm attachment height
against the *mesh's* fixed bounds instead (`ArmShoulderY=0.5`, comfortably inside -1..+1), which
fixes the visual attachment without touching any collider/physics code (this is presentation-only;
did not attempt the larger fix of splitting the visible mesh onto its own child transform to
match the collider exactly, since that's more invasive than this specific complaint needed).

**Shape and color:** arms are now `PrimitiveType.Capsule` (long, thin, rounded — "bean" arms)
scaled down and rotated on their side to point forward, instead of `PrimitiveType.Cube`. They now
share the exact same `Material` *instance* as the body (`armRenderer.sharedMaterial =
_materialInstance`) rather than a separate fixed dark-grey material, so they always match the
current role color (blue/red/grace-yellow) automatically through every role change with no extra
bookkeeping.

**Metric outcome:** Full suite 17/17 passing, zero warnings.

**Decision:** Keep all of the above. Ready for another playtest round.

---

## M2 feel-test round 3 — reset key, solo tagger, bots stuck on ramps, tag juice

**Feedback:** "R doesn't reset the scene... Can I also be the only Tagger please, the bots get
stuck on the first ramp, and which key do I press to tag them" + a follow-up ask for a debug
tagging-reach ring, lunge arms, and a tag "boop" sound.

**R not resetting — real bug:** `RoundController.Update()` only checked for the R key press
*inside* the `if (_roundOver)` branch, so it did nothing mid-round. Moved the check to the top of
`Update()` so it fires unconditionally, calls `StartRound()` (which already teleports every agent
back to its registered spawn transform), and now also snaps the camera via a new
`RoundController.SetCameraRig()` hook so the view doesn't smooth-lag across the map after the
teleport.

**Solo tagger:** `TagRulesConfig.taggerCount` default changed 2 → 1. Combined with the existing
`forcePlayerAsTagger`, the player is now the only Tagger by default. Updated
`TagArenaScene_SpawnsWithCorrectRoleDistribution` to expect 1 tagger / 11 runners to match.

**Bots stuck on the first ramp — real bug, same family as last round's cliff-avoidance leak:**
the cliff-avoidance raycast (added last round) fired from only 0.5m above the bot's current
height. The playground's ramps rise ~1m over the 2.5m look-ahead distance (10m ramp, 4m drop ≈
22°), so the real ground ahead sat *above* the ray's start point — a downward raycast can't hit
ground that's above where it started. Every uphill ramp read as a cliff, so bots just paced at
the base of the first one. Fixed by raising the ray origin to 3m above the bot (`upwardClearance`)
and casting the full `upwardClearance + maxSafeDrop` distance down, so the check covers ground
both above and below the bot's current height.

**Tag key:** confirmed — Left Mouse Button (right trigger on gamepad), already wired in
`TagAgent.Configure`. No change needed, just answering the question.

**Tag juice (new, on `TagAgent`):**
- Debug reach ring: local-player-only `LineRenderer` circle, visible while the player is an
  active Tagger, radius = contact radius + an estimated lunge stopping distance recomputed every
  frame from current speed. It's a stopping-distance approximation, not the exact lunge
  trajectory — good enough to gauge "am I close enough," not meant to be pixel-precise.
- Lunge arms: two small cubes parented to every tagger (bots included, since it's a shared
  presentation detail like the existing color telegraph, not a local-player-only debug aid) that
  punch forward on `TryLunge` and ease back over ~0.3s.
- Tag boop: a procedurally generated sine-wave `AudioClip` (no external asset, consistent with
  "no final sound design" scope) played via `AudioSource.PlayClipAtPoint` at the moment a tag
  actually lands.

**Metric outcome:** Full suite 17/17 passing, zero warnings, after each change (compile → rebuild
both scenes → full PlayMode run).

**Decision:** Keep all of the above. Ready for another playtest round.

---

## M2 feel-test round 2 — force player as Tagger; bots running off the map edge

**Feedback:** "Start me as a tagger, also all the bots just kinda ran off the edge of the map."

**Change 1 — force player as Tagger:** added `TagRulesConfig.forcePlayerAsTagger` (default
`true`). `RoundController.AssignRoles()` now pulls the local player out of the Fisher-Yates
shuffle pool and re-inserts it at index 0 before assigning roles, guaranteeing the player is
always a Tagger while `taggerCount > 0`. Flip the flag off for a fully-random round later.

**Change 2 — bot cliff avoidance:** `ChaseFleeBotInput` had zero terrain awareness — it steered
by a raw chase/flee direction vector with no notion of "is there ground there," so bots
confidently walked straight off rooftop edges chasing or fleeing in a straight line. Added
`FindSafeDirection`/`IsSafe`: before committing to a direction, raycast down from a point
`lookAheadDistance` (2.5m) ahead; if there's no ground within `maxSafeDrop` (2m), rotate the
desired direction in 20° steps (both ways, up to 160°) until a safe heading is found, else stop.

**Regression test + a real test-isolation bug found along the way:** added
`ChaseFleeBotInput_AvoidsRunningOffCliff` (bot chases a target parked across a gap on an isolated
10x10 test platform; asserts the bot's Y never drops below -1 over 150 fixed steps). It failed
the first two attempts with an identical `minY=-3.90`, which looked like the avoidance logic was
simply broken — but the exact same test passed standalone via `-testFilter`. Diagnostic logging
of the bot's per-tick position revealed continuous sinking well before the test's own platform
edge, which meant the platform wasn't the only ground in the physics world. Root cause: NUnit
runs PlayMode fixtures/tests alphabetically, so `SceneLoadPlayModeTests
.MovementPlaygroundScene_ComponentsResolveInRealPlayMode` (which loads the real
`MovementPlayground.unity` via `LoadSceneInPlayMode(..., Single)`) runs immediately before
`ChaseFleeBotInput_AvoidsRunningOffCliff` (alphabetically first in `TagRulesTests`) — and never
unloaded that scene. Unity keeps simulating physics and ticking `Update`/`FixedUpdate` on every
*loaded* scene regardless of which one is "active," so the real playground's gaps/ramps/walls at
world origin were still live and interacting with the test's own tiny platform sitting at the
same coordinates. Fixed by having that test (and, for the same reason, `TagArenaScene_
SpawnsWithCorrectRoleDistribution`, which has the identical leak) swap to a freshly created blank
scene and unload the one it loaded once its assertions are done.

**Metric outcome:** Full suite 17/17 passing, zero warnings, after the scene-isolation fix (was
16/17, same test failing identically both before and after an unrelated first fix attempt — the
bit-for-bit identical failure across differently-seeded runs was the tell that this was a fully
deterministic leak, not a flaky physics/logic bug in the avoidance code itself).

**Decision:** Keep both changes. Ready for another playtest round — bots should now hold role-
appropriate lines along rooftop edges instead of diving off, and the player will always spawn as
Tagger for tagger-mechanic feel-testing.

---

## M1 feel-test round 8 — ladder jitter + general smoothing (M1 closed out)

**Feedback:** "Going up the ladder is a bit jittery... let's make everything a little smoother
still."

**Root cause (ladder jitter) — real bug:** the ladder's climb line was placed only 0.3m from the
solid wall behind it; with a 0.4m capsule radius, the character's capsule was ~0.1m *inside* the
wall the entire climb, causing continuous PhysX collision push-back that fought the scripted
`MovePosition` call every tick. Moved the climb line to 0.6m clearance (0.2m clear gap). Also
found and fixed a related bug while investigating: the ladder's detach push-off direction
defaulted to `Vector3.forward`, which here pointed *into* the wall rather than away from it —
nothing had ever set it explicitly. Added an `outwardDirection` field on `InteractableMarker` so
the builder can specify it per ladder.

**General smoothing:** reduced character turn speed 720°→540°/s for a less instantaneous snap
when changing direction.

**Metric outcome:** all 11 tests still pass, zero warnings.

**M1 status:** closing out after 8 feel-test rounds. Remaining known gap: swing release speed
(2.13 m/s) is still weak relative to the "fastest move in the game" design intent — not addressed
this session; flagged for the M4 improvement loop. Proceeding to M2 (tag loop) per the user's
explicit go-ahead.

## M1 feel-test round 7 — slide veering with camera direction; gauntlet gate

**Feedback:** "When I'm sliding down, it's sending me down off the ramp to the left or right...
if my camera is pointed straight ahead I'll slide normally, if I turn the camera right I'll slide
off to the right. It should be based on character direction." Also: the gap gauntlet's last jump
before the wall-run section was too far to make.

**Root cause (slide veering) — real bug:** normal running steers via camera-relative WASD, so
turning the camera while holding forward curves the character's actual velocity to follow it.
Sliding then locked onto whatever heading resulted at the exact moment Ctrl was pressed and held
it for the rest of the slide, with nothing pulling it back toward the slope's true downhill line.

**Fix (took two attempts — worth recording why the first one didn't work):**
- First attempt: `Slerp` the travel direction toward the true downhill direction each tick.
  Looked correct in isolation but failed in testing — a Slerp-based angle nudge shrinks the
  *ratio* of lateral-to-total velocity, but if downhill acceleration is growing total speed
  faster than the angle narrows, the *absolute* lateral speed can still creep up even while the
  proportion improves. Diagnostic logging showed exactly this: the angle nudged down for a tick
  or two, then the growing total speed dragged absolute lateral velocity back up.
- Working fix: decompose velocity into along-slope/across-slope components and exponentially
  decay the across-slope component directly (`slide.downhillAlignment`, a decay rate). Even this
  needed a second correction: the decay rate must comfortably exceed the natural per-tick growth
  rate from downhill acceleration (~1.13x/tick at these tuning values, i.e. rate > ~6.1) or it's
  still a slow-diverging knife-edge instead of converging — the initial default (6) was almost
  exactly at that threshold. Raised to 25 for solid margin.

**Fix (gap gauntlet):** the last two gaps (11m, 13m) exceeded the ~9.6m measured sprint-jump/
slide-hop ceiling with no alternate route available at that point in the level (ladders/swings
come later), making the gauntlet's final jump — gating entry to the wall-run section right after
it — literally impossible. Tapered to `{3, 5, 7, 9, 8, 7}` so the whole gauntlet stays completable.

**Verified with a new regression test** (`Slide_SelfCorrectsTowardTrueDownhillDirection`) that
simulates camera-induced lateral drift during the run-up, then confirms lateral velocity decays
to ~0 while still on the slope. Needed generous test geometry (60m wide) — the *original* test
ramp (10m wide) let the character run off the side before the fix could even engage, which is a
good sign this reproduces the exact real-world failure mode.

**Metric outcome:** all 11 tests pass (9 previous + 2 new), zero warnings.

**Decision:** awaiting feel-test round 8.

## M1 feel-test round 6 — slide wouldn't trigger without sprint

**Feedback:** "When I press CTRL, I'm still not sliding down the ramp. It only kinda works when
I'm also pressing SHIFT to sprint."

**Root cause:** round 4 introduced `walkSpeed` (4 m/s, the default ground speed when Sprint isn't
held) but `slide.minEntrySpeed` was also 4 m/s — exactly equal. Not just marginal: `MoveTowards`
continuously re-targets exactly `walkSpeed` every tick, so ground speed rarely, if ever, clears an
*equal* threshold. This is exactly the kind of case the automated tests didn't catch, because
`ScriptedCharacterInput` (the test double) defaults `SprintHeld = true` — every existing test was
implicitly sprinting. Fixed: `slide.minEntrySpeed` 4→3 m/s, comfortably below `walkSpeed`.

**Verified with a new regression test** (`SlideHeld_TriggersAtWalkSpeedWithoutSprint`) that
explicitly sets `SprintHeld = false` — confirms slide now triggers at exactly walk speed (4 m/s)
with no sprint held at all.

**Metric outcome:** all 10 tests pass (9 previous + 1 new), zero warnings.

**Decision:** awaiting feel-test round 7. If sliding still feels inconsistent while sprinting too,
that's a separate issue (possibly slope-alignment-dependent downhill acceleration) worth digging
into further — the physics itself is confirmed working via `SlideDownRamp_FasterThanRunningDownSameRamp`
(13.00 m/s sliding vs 6.96 m/s running on the same slope), so a further report of "still off" would
point at feel/timing rather than the mechanic being broken.

## M1 feel-test round 5 — ramp bounce, air-brake softening, air-strafing

**Feedback:** air-brake (S mid-air) shouldn't fully kill momentum, just slow down a lot; ramps
bounce/jitter going down (not while sliding); sliding down a ramp should be faster than running
down it but isn't; can't meaningfully air-strafe with A/D.

**Root cause (ramp bounce) — a real bug, not tuning:** `ApplyGroundedAcceleration` and
`TickSliding` both computed a properly slope-projected velocity but then **discarded its vertical
component** and reconstructed `_rb.linearVelocity` using the *previous* frame's Y (later
overwritten by `SnapToGround`'s flat, non-slope-aware -0.5 push). On a real incline that mismatch
meant the forced vertical bias couldn't keep pace with the slope's actual descent rate at speed,
so the character repeatedly separated from the surface and re-landed — bouncing. Fixed by letting
both methods produce a full 3D velocity that genuinely follows the slope (`MoveTowards` over the
whole vector in the grounded case; explicit slope-projected direction in the sliding case).
`SnapToGround` now only clamps stray *upward* velocity residue from a seam-crossing collision
artifact, rather than forcing a downward push every tick that fights the (now-correct) slope
velocity.

**Verified with two new dedicated tests** (`RampDescent_DoesNotBounceRepeatedly`,
`SlideDownRamp_FasterThanRunningDownSameRamp`) added specifically because this exact class of bug
had already resurfaced once — trusting the math alone wasn't enough:
- Zero airborne transitions descending a 20m/8m-drop ramp (was bouncing repeatedly before).
- Sliding down that ramp reaches 13.00 m/s (the `maxHorizontalSpeed` cap) vs. 6.96 m/s running —
  nearly double, confirming slide-down-a-slope is now meaningfully faster.

**Air-brake softened:** replaced the linear "MoveTowards zero" (which fully stops you if held)
with exponential damping (`airBrakeDampingRate`, 3.5/s) — slows you sharply without a hard floor
at zero, matching "slow down a lot" rather than "kill momentum."

**Air-strafing increased:** now that the speed-cap bug is fixed (previous round), redirection
force can be raised safely without reintroducing unbounded speed gain — `airAcceleration` 10→26,
`airControlMultiplier` 0.25→0.5, so A/D genuinely changes your trajectory mid-air instead of a
barely-perceptible nudge.

**Metric outcome:** all 9 tests pass (7 previous + 2 new), zero warnings. Core distances
unchanged (sprint-jump 9.60m, slide-hop 9.59m, climb 1.94s).

**Decision:** awaiting feel-test round 6.

## M1 feel-test round 4 — reset button, shake, air-brake, sprint/slide keybinds

**Requests:** a reset button for the playground (falling off a ledge otherwise means a slow
climb back or scene reload); landing/ramp jitter "looks like camera shake, turn it down"; holding
S in the air should heavily kill momentum so a bad jump can be corrected; Shift = sprint,
Ctrl = slide.

**Changes:**
1. **Reset button (R key).** `CharacterMotor.ResetState(position, rotation)` hard-teleports:
   clears ladder/swing attachment, restores default capsule height (in case mid-slide), resets
   state to `Airborne` (self-corrects via the ground probe next tick), zeros velocity, and moves
   the rigidbody directly. `PlaygroundBootstrap` captures the player's spawn transform at Awake
   and calls this on `R`; also calls a new `ThirdPersonCameraRig.SnapToTarget()` so the camera
   jumps straight to the new position instead of smoothing into it (which would look wrong right
   after a teleport, given last round's position-smoothing addition).
2. **Landing shake turned down.** `CameraConfig.landingShakeAmplitude` 0.12→0.035,
   `landingShakeDuration` 0.18→0.12s, and `MovementConfig.jump.minAirTimeForLandingEffects`
   0.15→0.3s so ordinary landings and ramp-transition hiccups don't trigger it, only real falls.
3. **Air-brake.** Holding raw S (regardless of camera facing, same convention as ladder climbing)
   while airborne now aggressively kills horizontal speed via a new `ground.airBrakeDeceleration`
   (45 m/s²) instead of normal air control — lets a misjudged jump be aborted rather than
   committing to the overshoot.
4. **Shift = sprint, Ctrl = slide.** Slide was already bound to Left Ctrl. Added a genuine Sprint
   input (`ICharacterInput.SprintHeld`, bound to Left Shift / gamepad left-stick-click) and a new
   `ground.walkSpeed` (4 m/s) — the target ground speed is now `walkSpeed` unless Sprint is held,
   in which case it's the existing `sprintSpeed` (8 m/s). This is a deliberate departure from the
   original "sprint by default" spec, per direct request during feel-testing — full parkour speed
   now requires holding Shift. `ScriptedCharacterInput` (test double) defaults `SprintHeld = true`
   so the existing movement-metrics tests keep measuring capability at full sprint speed
   unchanged.

**Metric outcome:** all 7 tests pass (6 movement metrics + scene-resolution), zero warnings, core
distances unchanged (sprint-jump 9.60m, slide-hop 9.55m) since tests always sprint.

**Decision:** awaiting feel-test round 5.

## M1 feel-test round 3 — air momentum + jitter

**Feedback:** camera now follows correctly, but movement still feels clunky; too much air
momentum/velocity gained from jumping; camera jitters ("meant to be smooth"), and the character's
own movement jitters too.

**Root causes found:**
1. `ApplyAirAcceleration`'s speed cap only applied when already at or below sprint speed —
   if you entered the air faster (slide-hop, wall-jump), air control could keep adding free
   speed with **no ceiling at all**. Fixed: the cap is now `max(speedBefore, sprintSpeed)`, so
   air control can only redirect momentum you already have, never manufacture more.
2. `SnapToGround` forced a constant -2 m/s downward velocity every single grounded tick to stay
   glued to slopes. That's strong enough to fight collision resolution at every seam between
   adjacent ground pieces, reading as visible micro-jitter in the character's own motion.
   Softened to -0.5 m/s — still enough to prevent bouncing off small steps, much less fighting.
3. The camera copied the target's raw position 1:1 every frame with zero smoothing, so any tiny
   physics noise in the player's position (ground-snap bias, seam crossings) was directly visible
   as camera jitter. Added `CameraConfig.positionSmoothTime` (0.06s) and smooth the followed pivot
   with `Vector3.SmoothDamp` before computing the orbit offset.

**Tuning changes:** reduced air control further (`airAcceleration` 14→10, `airControlMultiplier`
0.35→0.25) for a less floaty jump, on top of the cap fix.

**Metric outcome:** all 7 tests pass (6 movement metrics + the scene-resolution test), zero
warnings. Core distances/durations unchanged (sprint-jump 9.60m, slide-hop 9.55m, climb 2.06s) —
these fixes target control feel and visual smoothness, not core speed tuning.

**Decision:** awaiting feel-test round 4. If jitter persists in the *player's own* visible motion
after this (as opposed to just camera), the next suspect is `UpdateFacing`'s rotation speed
(720°/s) or a deeper look at Rigidbody interpolation across the multi-collider ground pieces.

## M1 feel-test round 2 — camera stopped following (environment defect, not gameplay)

**Feedback:** "The camera doesn't follow the character anymore when I press play."

**Root cause:** this environment's Unity cannot reliably resolve custom-asmdef script types when
deserializing persisted data — confirmed directly: `MonoScript.GetClass()` returns null for
`CharacterMotor` even via a plain path lookup, completely unrelated to scenes. This breaks
attaching scene-embedded components of custom-asmdef types on load, **non-deterministically**:
verified across repeated rebuild/reload cycles that the same scene structure would sometimes
resolve a given component and sometimes not, with no code difference between runs. It reproduced
in genuine Play Mode (via a PlayMode test using `EditorSceneManager.LoadSceneInPlayMode`), not
just headless batch-mode diagnostics — so this was a real bug affecting actual play, not a
testing artifact. It's the same underlying defect as the earlier `Resources.Load`/`AssetDatabase`
ScriptableObject issue (M1 environment note above), just manifesting for scene-embedded
components instead of standalone assets. Rewriting the broken references to direct GUIDs did
*not* fix it, which ruled out "reference format" as the cause.

**Fix:** stopped relying on the scene file to persist any custom-asmdef component at all.
`CharacterMotor`, `PlayerInputProvider`, `ThirdPersonCameraRig`, `LadderInteractable`, and
`ChainSwingInteractable` are no longer attached at scene-build time. Instead:
- `InteractableMarker` and `PlaygroundBootstrap` (both deliberately namespace-free, living
  outside any custom asmdef, in the default assembly — same category as the stock `Readme.cs`,
  which resolved reliably every single time this was tested) are what the scene actually persists.
- `PlaygroundBootstrap.Awake()` calls `AddComponent<T>()` for the real components live, at
  runtime — which resolves the type directly from the loaded assembly rather than through
  Unity's serialization bridge, sidestepping the broken path entirely.
- Added `Configure`/`Initialize` public methods to `CharacterMotor`, `ThirdPersonCameraRig`,
  `LadderInteractable`, and `ChainSwingInteractable` for this runtime wiring.

**Verified:** rebuilt and reloaded the scene 3 times in fresh processes; a dedicated PlayMode
test (`SceneLoadPlayModeTests`) asserting `CharacterMotor`/`PlayerInputProvider`/
`ThirdPersonCameraRig` are all present passed consistently across all 3, plus all 6 movement
metrics tests still pass with zero warnings. This is meaningfully more confidence than the
earlier "it resolved once" result that turned out to be non-deterministic luck.

**Note for later milestones:** any *new* custom-asmdef component that needs to live in a saved
scene (M2 tag rules, M3 bot spawns, etc.) will need the same bootstrap-attachment treatment,
not direct scene attachment — until/unless this environment defect is understood or resolved
(a Unity reinstall or license re-activation might be worth trying at some point, given the
persistent licensing/entitlement errors present in every log this session, though a causal link
hasn't been confirmed).

---

## M1 — Movement playground build

**Change:** Initial `MovementConfig` defaults + full traversal system (ground movement, slide,
jump w/ coyote+buffer, wall-run, mantle/vault, climb, ladder, swing) built from scratch.

**Hypothesis:** Titanfall-adjacent momentum values (8 m/s sprint, 6.5 m/s jump, etc.) would give
a reasonably fast, chainable feel as a starting point for the manual feel-test.

**Metric outcome:** All 6 movement-metrics PlayMode tests pass (see `MOVEMENT_CAPABILITIES.md`).
Notable: swing release speed (2.13 m/s) is far below sprint speed, undermining the "fastest move
in the game" design goal for a well-timed release. Slide-hop distance (18.5m) is roughly double
plain sprint-jump distance (9.6m).

**Decision:** Ship as the M1 baseline for manual feel-test rather than pre-tuning further —
metrics catch broken, only a human catches un-fun, per the milestone's own philosophy. Flagged
both swing weakness and slide-hop distance as the first two things to tune once feel-tested.

---

## M1 feel-test round 1 — bug fixes + feel tightening

**Feedback:** character sinks through the floor at times; ramps feel identical uphill/downhill
(no slope speed difference at all, even non-sliding); a small Y-seam between the first flat tile
and the first ramp causes an annoying camera shake and requires an extra jump to cross; overall
movement feels too "gliding"/loose; jumping + sliding on flat ground produces unbounded ("crazy
crazy") momentum gain.

**Root causes found (real bugs, not just tuning):**
1. `PlaygroundBuilder.CreateRamp` placed the ramp box by its *center*, not its *top surface* —
   the walkable surface ended up offset below the adjoining flat platform by roughly half the
   box's thickness, leaving a real gap. This explains both the floor-sinking (ground probe
   briefly loses contact at speed) and the seam camera-shake (a real, if tiny, fall + landing).
   Fixed by computing box placement from the desired top-surface line via `LookRotation`, which
   also avoids relying on Euler-angle sign guesses.
2. The valley's "up" ramp was called with the wrong sign and was actually continuing to
   *descend* rather than ascend back to spawn height — a second, independent geometry bug hiding
   behind the same symptom.
3. `EnterSliding` added `entryBoostImpulse` unconditionally on every slide, regardless of slope.
   Chaining slide → hop → land → slide → hop compounded this indefinitely since slide-hop
   preserves 100% of horizontal speed — a genuine unbounded-speed bug, not intended "momentum
   preservation." Fixed by scaling the boost by how downhill the entry actually is (zero on flat
   ground) and adding a 0.5s slide re-entry cooldown as a second safeguard, plus a global
   `ground.maxHorizontalSpeed` (13 m/s) hard cap applied every tick regardless of source.

**Tuning changes (feel, not correctness):**
- Added slope-aligned gravity to normal (non-sliding) ground movement, so uphill is now slower
  and downhill faster even without sliding — previously slope had zero effect outside of sliding.
- Tightened ground accel/decel (55/75, was 45/55) and reduced air control (14 accel / 0.35
  multiplier, was 20 / 0.5) for a snappier, less "gliding" feel per feedback that movement was
  too loose.
- Landing effects (camera shake) now require ≥0.15s of air time (`jump.minAirTimeForLandingEffects`)
  before firing, so a one-tick ground-probe miss can't trigger a shake even in the rare case it
  still happens.

**Metric outcome:** all 6 tests still pass. Slide-hop distance dropped from 18.50m to **9.57m**
— now matching plain sprint-jump distance (9.60m) almost exactly, confirming the flat-ground
exploit is closed (the test scene is flat ground, so the boost correctly evaluates near zero).

**Decision:** rebuilt the playground scene and awaiting a second manual feel-test pass. Swing
weakness (2.13 m/s) is still unaddressed — next candidate once this round is confirmed fixed.

---

## Environment note (not a gameplay change)

Discovered and fixed two significant headless-CLI-specific issues during M1 that are **not**
gameplay bugs but are worth knowing about before any future automation work in this project:

1. **Friction was silently killing all horizontal movement.** `SnapToGround()` drives a small
   constant downward velocity to stay glued to slopes; combined with the default `PhysicsMaterial`
   friction, this created a continuous micro-collision with the floor that zeroed horizontal
   velocity every physics step. Fixed with a zero-friction `PhysicsMaterial` (`Minimum` combine)
   on the character capsule — velocity is fully script-driven, so PhysX friction was never doing
   anything useful for locomotion anyway.
2. **Headless batch-mode `AssetDatabase`/`Resources.Load` cannot deserialize persisted
   ScriptableObject assets of namespaced types from custom asmdefs** in this environment (verified
   with a minimal reproduction — reproducible whenever the C# `namespace` is set, regardless of
   `rootNamespace`; `CreateInstance` in-memory works fine, and normal interactive Editor sessions
   are expected to be unaffected). Worked around by having `CharacterMotor`/`ThirdPersonCameraRig`
   fall back to `ScriptableObject.CreateInstance<T>()` defaults at `Awake()` when no config asset
   is assigned. A human can still create a real tunable `MovementConfig`/`CameraConfig` asset via
   `Assets > Create > RooftopTag` in their own Editor session and assign it in the Inspector —
   that path is expected to work normally since it doesn't go through headless batch mode.
