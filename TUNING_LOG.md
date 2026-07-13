# Tuning Log

Running log of movement/bot/map changes: hypothesis, metric outcome, decision. Append entries
in the same session-as-iteration format used below.

## M4 loop — jump power selection (real gap, not foreign platform constant) (2026-07-13)

**Hypothesis:** `ParkourBotInput.IsShortJumpEdge` estimated the gap as `centerDist −
TagArenaLayout.PlatformLength` (=4m, the WRONG map's platform length), so every roof jump read as a
~9m gap → always sprint → overshoot the ~5m roof gaps (only 27% landed within 1.75m, avg err 3.66m,
8 stuck). Fixing it to the real per-roof gap should let the bot walk-jump short gaps and land clean.

**Change:** extracted `RooftopArena.EdgeGap(from,to)` (center distance minus each roof's facing
half-extent along the link direction — the same true-gap the connectivity fix uses; `JumpMakeable`
now reuses it too, de-duping). `IsShortJumpEdge` now uses `EdgeGap(Roofs[edge.FromNode],
Roofs[edge.ToNode])` (a Jump edge is always roof→roof, node id == roof index by construction; the
live TagArena scene also builds the RooftopArena graph — TagArenaBootstrap:40 — so this indexing is
always valid). Threshold 4.5→1.5m (≈ where sprint range exceeds an 8m roof, i.e. would overshoot the
far edge). Also suppressed steering jitter while committing a gap-crossing edge (extracted
`IsCrossingGapEdge`, reused by cliff-avoidance too) — jitter models imperfect pursuit, not a reason
to sabotage a committed jump.

**Measured (before = connectivity fix; after = this):**
- before: `total_stuck=8 total_fallen=0 total_edge_usage=[Jump=7, Run=4] jump_land_within_1.75m=0.27 jump_landing_err_avg=3.66`
- after:  `total_stuck=4 total_fallen=0 total_edge_usage=[Jump=6, Run=32] total_edge_attempts=[Jump=612, Climb=97, WallRun=11, Swing=142] jump_land_within_1.75m=0.28 jump_landing_err_avg=3.52 max_distance_from_spawn=45.8`

**Gates:** `total_fallen` low ✓ (0); edge usage nonzero/broadened ✓ (Run 4→32, attempts now span
Jump/Climb/WallRun/Swing); `total_stuck` down 8→4 (noisy, not 0); `jump_land_within_1.75m` >0.6 ✗
(0.28); `jump_landing_err_avg` <2.0 ✗ (3.52).

**Diagnosed (instrumented, then reverted) — the accuracy gate is NOT reachable by jump-execution
tuning:**
- Takeoff telemetry: median takeoff angle **0°** at full sprint (8.0) — bots take off perfectly
  aligned. Neither power selection nor jitter moved landing accuracy (0.27→0.31→0.30→0.28 across the
  variants) because takeoffs were already clean.
- The residual node-miss is (1) the frequent spawn-roof **3m-gap jumps whose far-roof centre is only
  ~7.5m out** — sprint's ~9.5m range overshoots the node ~2m, and a walk jump (~4.4m) undershoots ~3m;
  a **binary walk/sprint can't hit a 7.5m target** (needs ~6.3 m/s), so hitting node-centre would
  require continuous takeoff-speed control (out of scope); (2) air-control 0.9 lets bots **air-steer
  toward the moving runner** after clearing the gap, landing off-node **by design**; (3) it's all
  measured in a 12-agent close-quarters cascade that collapses in ~13s with targets churning every
  0.3s. Crucially `total_fallen=0` — jumps functionally land on roofs; node-distance is a poor skill
  proxy in a chase scrum.
- Stuck wedge locations (instrumented): 2 of 3 stuck bots were in **`Mantling`** state on roofs 1 & 3,
  one a `Grounded` close-quarters stall on roof 4. The mantle stalls are bots looping against roof
  **rims (`AddTopRim`) / props (`RoofPropDresser`)**, not a jump problem — a separate geometry/mantle-
  execution snag.

**Decision:** kept the power-selection fix (correct, requested, removes the cross-map constant bug,
de-dups the gap math, broadened edge usage) and the jump-commit jitter suppression (correct behavior,
harmless, will matter once runners spread). But the landing-accuracy / err gates are **blocked
upstream**, not by jump execution: they need the flee/cascade fix (task A3, so bots do clean solo
traversals instead of scrum jumps) and, for true node-centring, continuous takeoff-speed control.
The remaining stuck is a mantle-vs-rim/prop geometry snag (separate fix), not jump power. STOPPING
here per LOOP.md rather than tuning blind against a scrum-bound metric.

## M4 loop — jump reachability / graph connectivity (2026-07-13)

**Hypothesis:** the previous entry found the roof graph was disconnected — `JumpMakeable` measured
roof *center-to-center* distance (~13m spacing) against its 9m-up / 11m-drop cap, dropping 30 of the
~34 Jump links even though the true edge-to-edge gap between the 8-12m-wide roofs is only ~3-5m
(easily sprint-jumpable). Every spawn roof was an island → `FindPath` always empty → beeline →
`total_edge_usage=[]`. Measuring the TRUE gap should connect neighbours and unblock edge usage.

**Change (`RooftopGraphBuilder.JumpMakeable`):** now takes the two `Roof` structs and measures
`gap = centerDist − extentFrom − extentTo`, where each roof's facing half-extent is its axis-aligned
box support width along the horizontal link direction `0.5·(|dir.x|·SizeX + |dir.z|·SizeZ)`. Caps
calibrated to the motor's real jump (jumpSpeed 6.5, sprint 8, fall ×1.6 → ~2.15m apex, ~1.19s air,
~9.5m flat range): **gap ≤ 6.5m** flat (margin under the physical max, keeps the ~10m WallRun/Swing
chasms out), **rise ≤ 2.2m** (near apex), **gap ≤ 8m** for drops (extra air time). Added
`RooftopGraphTests.Graph_AllRoofsReachableFromSpawn` (asserts every roof node reachable from node 0)
as the island regression guard.

**Skipped links: 30 → 0.** All neighbour Jump links now emit; graph fully connected (reachability
test green). No link over-connected — the genuinely-far crossings were already typed WallRun/Swing/
Ramp/Ladder, not Jump, so `JumpMakeable` never widens them.

**Measured — before = routing-only (graph still disconnected), after = graph connected:**
- before: `matches=10 runner_win_rate=0.00 runner_avg_survival=0.00 speed_p50=4.22 speed_p90=8.00 total_stuck=1 total_fallen=0 total_edge_usage=[] total_edge_attempts=[] max_distance_from_spawn=33.6`
- after:  `matches=10 runner_win_rate=0.00 runner_avg_survival=0.00 speed_p50=8.00 speed_p90=8.00 total_stuck=8 total_fallen=0 total_edge_usage=[Jump=7, Run=4] total_edge_attempts=[Jump=586, Climb=152, WallRun=3] max_distance_from_spawn=44.9 jump_land_within_1.75m=0.27 jump_landing_err_avg=3.66`

**Gates:** edge-usage NONZERO ✓ (`[]`→`[Jump=7, Run=4]`, attempts `[]`→`[Jump=586,…]`); no fall
spike ✓ (`total_fallen=0`); `total_stuck`=0 ✗ (**8**); `runner_avg_survival` off 0.00 ✗ (still 0.00).

**Decision:** graph-connectivity objective achieved — the parkour system is unblocked (586 jump
attempts vs 0, bots sprint-traverse to the far map edge) and NOT over-connected (zero falls). The
two remaining misses are downstream of this fix, not caused by it: (1) `total_stuck=8` — bots
sprint-jump the ~5m gaps and overshoot (only 27% land within 1.75m, avg 3.66m error), wedging on far
geometry; `ParkourBotInput.IsShortJumpEdge` gates walk-vs-sprint on `TagArenaLayout.PlatformLength`,
the WRONG map's platform constant, so RooftopArena's gaps never read as "short" → always sprint →
overshoot. (2) survival still 0.00 — the spawn-cascade / `FleeGoalNode` collapse is unchanged.
Both are bot-EXECUTION/flee tuning, explicitly separate tasks. Committing the connectivity fix;
NEXT task should fix jump-power selection for RooftopArena's true gaps (per-roof extent, not
`TagArenaLayout.PlatformLength`) and the runner flee goal, which together should drop stuck→0 and
move survival off 0.00.

## M4 loop — roof-identity routing (2026-07-13)

**Hypothesis:** bots path with `ParkourGraph.NearestNode`, a raw 3D nearest-distance scan. In
`ParkourBotInput.Replan` start = NearestNode(self) and goal = NearestNode(predicted), where
predicted is a noisy (≤12m extrapolation + ≤4m jitter) point. That noisy point snaps to the wrong
roof (or the agent's own), so `startNode == goalNode` → `FindPath` returns `Array.Empty` → empty
path → bots beeline straight at the target, fighting cliff-avoidance. Routing by roof IDENTITY
(which physical roof footprint a position sits over) instead of proximity should give real
cross-roof paths and produce nonzero edge usage.

**Change:** added `RooftopArena.RoofIndexAt(Vector3)` (XZ-footprint scan over the 26 roofs, height
tie-break for overlapping roofs; roof index == graph node id by construction, enforced with a new
`Debug.Assert` in `RooftopGraphBuilder.Build`). `ParkourBotInput.Replan` now uses
`RoofIndexAt(self)` for the start node and, for taggers, `RoofIndexAt(predicted) → RoofIndexAt(target)
→ NearestNode(target)` for the goal (NearestNode only as the mid-air fallback). Added instant-repath
on target roof change (cached `_lastTargetRoof`, one throttle-bypass per reaction window). Runner
`FleeGoalNode` left as-is (its rewrite is a separate task) but now shares the roof-identity start node.

**Measured (before == after — no delta):**
- before (main): `matches=10 runner_win_rate=0.00 runner_avg_survival=0.00 total_stuck=0 total_fallen=0 total_edge_usage=[] total_edge_attempts=[]`
- after (this branch): `matches=10 runner_win_rate=0.00 runner_avg_survival=0.00 total_stuck=1 total_fallen=0 total_edge_usage=[] total_edge_attempts=[]`

**GATE FAILED — but root cause is upstream of routing, not the routing fix.** Instrumented
`Replan` with a one-shot diagnostic: `RoofIndexAt` returns the correct roof for every agent every
time (start nodes are right), yet paths are still empty. The reason is the graph itself is
**disconnected**: `RooftopGraphBuilder` logs **30 `ROOFTOP_LINK_SKIPPED` warnings** — `JumpMakeable`
measures roof *center-to-center* distance (13m, the roof spacing) against its 9m-up / 11m-drop cap,
so nearly every Jump link fails and is dropped, even though the actual gap between the 8m-wide roofs
is only ~5m (trivially sprint-jumpable). Every spawn roof (0,1,3,4,12,13) ends up an isolated island
with no Jump edges, so `FindPath` returns null/empty regardless of how start/goal are chosen. No bot
routing change can produce edge usage on a graph with almost no edges. (The lone stuck agent in
match 6 is noise from a beeline collision, not the routing.)

**Decision:** keep the roof-identity routing (correct, tested, and a prerequisite for edge usage
once the graph connects) but STOP per LOOP.md — the edge-usage gate is blocked by a structural
graph-connectivity bug, not tunable numbers. **Next task (out of this task's scope — "do NOT change
graph node/edge construction"):** fix `RooftopGraphBuilder.JumpMakeable` to measure the true gap
(center distance minus the two roofs' facing half-extents) instead of center-to-center, OR reduce
roof spacing / add Ramp/Ladder links where the plan-view gap genuinely exceeds jump range. Until the
graph connects, self-play edge usage on this map stays empty.

## Swing polish — follow rope visual, single occupancy, height cap (2026-07-12, same session)

**Feel-test round 2:** rope visual never moves with the swinger (there was none — only an editor
gizmo + a static chain box); one rope should hold one user at a time; pumping could climb ABOVE the
pivot ("a bit too crazy").

**Changes:** `ChainSwingInteractable` now owns a runtime `LineRenderer` rope — pivot→occupant hands
while held, straight-down rest hang otherwise (static chain-box visuals removed from the builders);
claim/release occupancy (`TryClaim`/`ReleaseClaim`, destroyed-occupant-safe) gates attach, released
on E/Jump release AND `ResetState` (a round reset would otherwise leak a permanent claim and brick
the rope); polar-angle cap `swing.maxSwingAngleDegrees = 95` — position clamps onto the cap cone
and the climbing velocity component is cancelled while the orbital component is preserved (you can
still swing around the rim, just not over the top). Subtle math note: the climb direction is built
analytically (`up·sinθ + azimuth·cosθ`) because the naive projection formula flips sign past 90°,
which a 95° cap crosses.

**Measured:** aggressive 6s pump maxes at 49° polar / 4.6m height (pivot at 8m) — cap holds with
huge margin under test pumping; release speeds unchanged (11.93 / 8.00 E). One test iteration: the
occupancy test's second player fell out of grab range while its grabs were being denied (freefall
during the denial loop) — test now teleports it back before the re-attach; implementation was
correct. 19/19 movement suite; self-play stuck 103 ≤ baseline.

## Swing-rope rework — omnidirectional, E-release, momentum-true launches (2026-07-12)

**Feel-test report:** E should release (was a no-op — Jump only); the rope only swung in one
plane (frozen at grab time); pumping was far too hard; release should carry built momentum.

**Root causes found before touching anything:** the pendulum was a strictly-planar 1D angle ODE
with the plane locked at grab; the pump only applied within ±30° of the arc bottom, in one axis,
only reinforcing the existing swing sign; and damping was 2%/tick at 50Hz = **64% velocity loss per
second** — the single biggest reason momentum wouldn't build (historical measured release:
2.13 m/s vs the "one of the fastest moves in the game" design goal).

**Rework (plan `.claude/plans/woolly-soaring-teapot.md`, 3 executor-routed tasks — opus on the
motor physics, sonnet on the bot gate + tests):** velocity-state spherical pendulum. Gravity + a
camera-relative WASD tangential force (inputAcceleration=20 m/s²) integrate a world-space swing
velocity; taut-rope constraint by projecting velocity onto the rope's tangent plane and snapping
position onto the sphere; exponential damping 0.15/s (~14%/s loss); clamp 12 m/s. Velocity-driven
rigidbody (linearVelocity = swing velocity; MovePosition only corrects radial drift — avoids the
double-integration 2× bug, which the new test's <15 upper bound guards). E and Jump both release
after a 0.15s post-attach grace: release = swingVelocity × 1.15, Jump adds +1.5 up (E = flat
momentum bail, Jump = higher arc). Entry momentum seeds the swing (sprint-entry ≈ 8 m/s
immediately). Bots stop holding Interact once attached (they'd instantly E-bail otherwise); their
steer-toward-exit input now naturally pumps the spherical swing; ExitDirection auto-release
unchanged. Config surface rebuilt (dead grabRange + 3 obsolete pump fields deleted). Defaults were
derived from pendulum math (tilted-gravity equilibrium), not guessed — predicted ~10 m/s peak /
11.5 release.

**Measured outcome (first green run):** `swing_release_speed_mps=11.93` (was 2.13 — **5.6×**),
`swing_e_release_speed_mps=8.00`, `swing_lateral_push_displacement_m=3.49` (old planar model ≈ 0 —
the omnidirectionality regression test). 34/34 tests; self-play: stuck 100 (≤107 baseline),
fallen 0, max distance 41.4. Discovery bonus: the old shared `_swingPlaneAxis` field was secretly
reused by the climb path — split into a dedicated `_climbApproachDir` during the rework.

**Decision:** kept all derived defaults untouched — measurements landed on prediction. Human
feel-test pending (circle-swing with WASD, pump-up time, E vs Jump release feel). Follow-ups noted,
not built: rope/chain visual while swinging, a hang/grab arm pose (TagAgent has no OnSwing branch).

## Map expansion — construction-site zone + WallRun/Swing/ClimbWall/VaultWall links (2026-07-12)

**What was built** (plan: `.claude/plans/woolly-soaring-teapot.md`, executed as 7 executor-routed
subagent tasks, each gated on rebuild×3 + suites + self-play): the rooftop map doubled from 13
roofs/~39×39m to **26 roofs/~72×62m**, adding urban growth south/west and a distinct low, dense
construction-site zone (SW: yard, deck, ramps, crane, 20m scaffold alley). Four new `LinkKind`s
wired end-to-end (data → geometry → parkour graph → bot execution → headless self-play):
- **WallRun** — 10m un-jumpable crossing along a 6m wall panel; `ParkourEdge` gained `LateralDir`
  so bots hug the KNOWN wall side (legacy Tag Arena world-X fallback preserved).
- **Swing** — 10m chasm on a 5.5m chain; `ChainSwingInteractable` gained a per-swing
  `ExitDirection` (motor bot-release is now frame-relative, not hardcoded +Z). Swing graph edge is
  deliberately one-directional (reverse release would oppose the exit dir); no dead end — Con_West
  keeps its WallRun exit.
- **ClimbWall** — Crane climbable from Deck via its own 2.8m building face (climb band 2.2-3.0).
- **VaultWall** — 1m flow-through wall on the Yard/Alley seam (mantle from the low side, vault from
  the high side; bots execute both identically).
Also: `RooftopInteractableBuilder` (runtime construction of ladder/swing interactables) closes the
long-standing gap where Editor-only marker construction left Ladder edges untraversable in headless
self-play. Spawns spread across 7 roofs. A `ROOFTOP_LINK_REDUNDANT` validator warns if a
WallRun/Swing link is flat-jumpable (design-intent check). ScafHi's deliberately-overlapping
footprint emits Vault-type bot edges for its two Jump links so bots mantle instead of stalling.

**Metrics** (final batch, now the new `Tools/baseline-metrics.txt`): 31/31 tests (3 new graph tests
verify WallRun routing + one-directional Swing), no `ROOFTOP_LINK_SKIPPED`/`REDUNDANT`. Self-play:
`total_stuck` 100 vs old-map 107 (batch variance 27-107 all day), `total_fallen` 0,
`max_distance_from_spawn` 25.4 → 35.7 (agents genuinely roam the new territory).

**Honest caveat:** `total_edge_usage` is still `[]` — the new content is traversable and the graph
routes through it (proven by the new tests), but bots rarely PLAN multi-edge paths because of the
separately-flagged one-node-per-roof graph-density problem (paths collapse to empty when nearest
nodes coincide). Edge-usage numbers will stay hollow until that's fixed — it remains the single
highest-leverage bot-nav fix and was explicitly out of scope for this pass.

**Visual review:** construction zone reads clearly distinct (low dense floors, swing chain over the
chasm, wall-run panel, orange vault seam) at both aerial and player level; screenshots in
`Tools/screenshots/` (shot_4/shot_5_TagArena are the new vantages). Human feel-test pending.

## Visual pass — golden hour over the construction site (2026-07-12)

**What was added** (plan: `docs/superpowers/plans/2026-07-12-visual-pass.md`, executed task-by-task
via executor-routed subagents — sonnet for mechanical/specified tasks, opus for shader/URP/nav-risk
tasks): gradient dusk skybox shader + `SceneStyler` (sun, trilight ambient, exponential fog, street
haze planes, URP post volume with bloom/warm grade/vignette, crane + skyline silhouettes — all
Editor-build-time only, never in headless self-play); semantic `SurfaceRole` materials (concrete
palette with seeded per-building value jitter, safety-orange emissive strictly for interactables);
emissive rim trims (collider-less) outlining every walkable roof/platform edge; tagger red emissive
glow / pale non-emissive runners / pulsing conversion-grace; seeded rooftop props (`RoofPropDresser`
— physical AC units/vents + visual-only antennas/pipes) gated by a unit-tested nav-clearance rule
(6/6 `PropClearanceTests`) keeping link corridors, graph anchors and spawn points free; headless
`ScreenshotTool` for the visual-review loop.

**Self-play metric deltas (props are the only physical change — gate from the plan):** baseline →
final: `total_stuck` 107 → 107, `total_fallen` 0 → 0, `runner_avg_survival` 0.00 → 0.00,
`max_distance_from_spawn` 25.4 → 25.0, `speed_p50` 1.80 → 2.37 (known batch noise). Gate passed —
props do not disturb navigation. (Survival remaining at 0.00 is the pre-existing graph-density
problem documented below under "Tag Arena rebuilt on branching RooftopArena geometry" — not a
visual-pass regression.) Full regression: 27/27 across MovementMetrics/TagRules/PropClearance.

**Visual review (5 headless screenshots, `Tools/screenshots/`):** gameplay-range shots match the
approved mockup — crimson→orange dusk gradient, muted purple-grey concrete, warm rim trims reading
as sunset rim-light on every edge, safety-orange ladder panels, haze drowning the street, cranes +
skyline silhouettes, no z-fighting, no missing-material magenta. Far high vista shots wash out into
uniform warm fog — consistent with the spec's "street drowns in warm haze" and not a view players
reach; if it reads too thick in the feel-test, `VisualThemeConfig.fogDensity` (0.010) is the single
knob to lower.

**Decision:** kept all theme defaults as-designed; nothing reverted. Human feel-test pending —
metrics catch broken, only humans catch un-fun.

## Tag Arena rebuilt on branching RooftopArena geometry — infrastructure solid, uncovered a deeper bot-pathing gap

**Change (user decision: extend RooftopArena into the real Tag Arena, per the previous entry's
finding that the old linear corridor structurally couldn't support Runner evasion):**
- `PlaygroundBuilder.BuildTagArena()` now builds on `RooftopArena.cs`'s branching topology (13
  roofs, real loop routes, 4-way branching from spawn) instead of `TagArenaMapGeometry.
  BuildMainCorridor`, sized for the real 12-agent (2 Tagger / 10 Runner) ruleset —
  `TagArenaAgentCount` 3→12, `forcePlayerAsRunner=false` (player is assigned a role like any other
  agent, not forced Runner). `RooftopArena.unity`'s 3-agent "chase me" scene is untouched, kept as a
  separate lighter scene.
- `TagArenaBootstrap`'s now-dead `useRooftopGraph` toggle removed (both scenes use the rooftop graph).
- `TaggerSpawnBackOffset` recalibrated (-6 → -1.5) for the smaller rooftop spawn geometry.
- `SelfPlayTests` points at the same branching geometry/graph/spawn helpers as the real scene.
- `MatchMetrics.MaxZReached` → `MaxDistanceFromSpawn` (straight-line, not corridor-axis-specific).
- `TagRulesTests.TagArenaScene_SpawnsWithCorrectRoleDistribution` updated to 12/2/10.
- Verified: compile clean, all 3 scenes rebuild without error, full PlayMode suite 23/23 passing.

**First self-play measurement was a regression, not an improvement — diagnosed, not guessed at:**
`runner_avg_survival` stayed 0.00 and match durations got FASTER (3.7-6.3s) with `edges=[]`
entirely and `speed_p50=0.00`. Root-caused to spawn crowding: 12 agents on one 12x12 roof with only
a small tagger offset reproduced the exact "instant cascade before anyone can flee" bug already
fixed once on the linear corridor. Fixed by spreading spawns across the spawn roof and its 4 direct
neighbours (`RooftopArena.SpawnPoints` redesigned, golden-angle ring offset per agent sharing a
roof) instead of cramming everyone onto one platform — a fix that uses the branching topology
itself rather than trying to out-tune a single small platform.

**Re-measuring surfaced a second, deeper, distinct problem** (`total_stuck` jumped to ~11-12 of 12
agents almost every match, `edges=[]`/`edge_attempts=[]` stayed empty even though match durations
got longer and agents clearly moved far — `max_distance_from_spawn=32m`). Added temporary diagnostic
logging (`ParkourBotInput.Tick`, removed after) to one agent and confirmed via the trace: `_path` is
`null` or an **empty array** on nearly every decision cycle, for both Taggers and Runners. Root
cause: `RooftopGraphBuilder` puts exactly **one node per roof** — fine for a single bot crossing the
whole map (its original purpose), but `ParkourGraph.FindPath` returns an empty path whenever start
and goal resolve to the *same nearest node*, which now happens constantly with 12 agents clustered
close together (multiple agents sharing or standing near the same roof). With no path, bots fall
back to beelining straight at the target's raw position (`ComputeSteerPoint`'s null-path branch) —
and because that fallback isn't a recognized "expected gap crossing" edge type, `ApplySteeringSafety`
keeps cliff-avoidance (`FindSafeDirection`) active the whole time, fighting the beeline at every
roof edge instead of committing to a jump. That's why edge usage and jump attempts stayed at zero
even as agents visibly moved and fell around — CharacterMotor's own auto-mantle/wall-run/climb
detection was firing semi-randomly off the chaotic fallback steering, not the graph.

**Stopping here to report rather than patching further.** This is a real, separate finding from the
spawn-crowding fix above (that fix is correct and stays) — it's a graph-density problem, not
something another spawn/offset tweak can solve. Likely needs one of: denser graph nodes (e.g. a few
edge/corner waypoints per roof instead of one center node), a different `FindPath`/fallback
behavior for the "already adjacent, same effective node" case, or a rethink of the coarse
per-building graph now that it's serving 12 densely-packed agents instead of one bot crossing the
whole map. Flagging for a decision rather than guessing blind — matches this project's own stated
discipline (stop and report when the problem is structural).

**Net effect of this session's branching-arena work:** the map/scene/config/test infrastructure is
solid, verified, and committed — TagArena.unity genuinely is a branching 12-agent arena now, no
linear corridor, no 3-vs-1 "chase me" default. The self-play *balance* numbers are not yet
meaningful (bots aren't really using the graph) and shouldn't be compared against target bands until
the pathing-density issue above is resolved.

## Added runner_avg_survival metric (user decision on the win-rate wall) — reveals a sharper finding

**Decision:** per the earlier-flagged design question, added `MatchMetrics.RunnerSurvivalFraction`
(fraction of agents that started as Runner and were still Runner at round end) alongside the
existing strict `runner_win_rate`, both logged now (`selfplay_batch runner_win_rate=... runner_avg_
survival=...`). `Tools/LOOP.md` updated to tune against `runner_avg_survival` (target 0.50-0.70)
going forward, with `runner_win_rate` kept only for visibility.

**First measurement is worse than the old theory predicted:** `runner_avg_survival=0.00` across all
10 batch matches — not partial credit, literally every single Runner got tagged in every single
match. The earlier "compounding probability" theory (even 90% per-Runner survival only yields ~35%
all-survive) implied a healthy per-Runner survival rate hiding behind a harsh AND-of-ten win
condition — but 0.00 average survival means there's no hidden healthy rate; Runners aren't
individually surviving at any real rate in this test scenario at all.

**Likely real root cause, found while investigating (not yet acted on):** `SelfPlayTests` and the
actual `TagArena.unity` scene both build their geometry from the same
`TagArenaMapGeometry.BuildMainCorridor` — a single linear corridor, not a branching map. CLAUDE.md's
map design explicitly calls for "no dead ends... every area reachable and leavable by at least two
parkour routes," which a single corridor cannot satisfy — there's nowhere for a Runner to double
back or juke, only forward or caught. Separately, `PlaygroundBuilder.BuildTagArena` builds the real
scene as a 3-agent "chase me" mode (1 player + 2 bot Taggers), not the 12-agent 2-Tagger/10-Runner
configuration self-play tests — so self-play's whole scenario may not correspond to any actual
playable mode as currently built. This is a map/mode-design gap, not a bot-tuning problem; flagged
for a decision rather than guessed at further.

**Report:** the AudioLowPassFilter-based redo from the previous entry was worse, not better —
described directly as "ear rape." Two attempts at procedural wind synthesis in a row have both
failed the feel-check, and there's no way to audition audio output in this loop to iterate blind a
third time responsibly.

**Change:** removed the wind audio system completely from `TagAgent.cs` — the `AudioSource`,
`AudioLowPassFilter`, `GetWindClip()`, and all associated fields/constants. Landing thump/squash and
the tag boop are untouched (not flagged as a problem). If wind/speed audio feedback is wanted again
later, it likely needs an actual authored/recorded asset rather than another from-scratch synthesis
attempt, given the track record here.

**Verified:** compile-check clean, all 3 scenes rebuilt without error, full PlayMode suite 23/23
passing.

## Wind audio redo (real-time filter, not hand-rolled), wall-run grab animation, slide-strafe speed exploit fix

**Wind audio — reported as "terrible, just grey noise":** the previous version hand-shaped white
noise into a "brown noise" rumble via a leaky integrator, which read as a flat, droning hum rather
than wind. Replaced the approach: the clip is now plain crossfaded white noise, and shaping happens
live via an `AudioLowPassFilter` on the wind `AudioSource`, whose cutoff sweeps from 400Hz (muffled)
to 9000Hz (bright/full hiss) with speed — real DSP instead of an approximation, and it gives the
sound a speed-reactive character (opens up as you speed up) that a static clip couldn't. Volume
scaling unchanged. Not personally auditioned (no ears in this loop) — needs your feel-check again.

**Wall-run grab animation:** `TagAgent.Update()`'s state-transition block gained a `WallRunning`
branch reusing the same raise-then-push arm gesture as Mantling/Vaulting (same angles), held longer
on the way back to rest (0.9s vs 0.35s) since a wall-run typically lasts well past a mantle's brief
transition — reads as "catching and holding onto the wall" through most of the run instead of
snapping back to idle almost immediately.

**Slide + A/D "drift and build speed" exploit, still present after the earlier duration-cap fix:**
root-caused to two compounding issues in `TickSliding`'s downhill acceleration:
1. `downhill` was a normalized direction vector, discarding actual slope steepness — a floor barely
   past `IsOnSlope`'s ~8-degree gate got the exact same full-strength accel bonus as a real ramp,
   since only *alignment* (downhillDot), never *grade*, fed into the formula.
2. A/D actively steers (rotates) the travel direction every tick, and nothing stopped a player from
   continuously re-aiming back onto the fall line to keep downhillDot pinned near 1 indefinitely —
   sustaining max accel far longer/more reliably than the natural fall-line auto-correction would
   allow on its own.

Fixed both: accel now scales by the ground normal's actual steepness (un-normalized
`ProjectOnPlane` magnitude = sin of the slope angle), normalized against `ReferenceSlopeSteepness`
(≈sin(22°), the test suite's ramp grade) so a real ramp's boost is unchanged from before — only
shallower/near-flat floors lose most of the boost, steeper slopes gain some. Also scaled by
`(1 - |strafe|)`, so actively steering and accelerating hard are now a trade-off rather than both
free — straight-line downhill sliding keeps its full boost, pumping A/D to keep re-centering on the
fall line no longer does.

**Verified:** compile-check clean, all 3 scenes rebuilt without error. First full PlayMode pass
caught a real regression from the steepness scaling (`SlideDownRamp_FasterThanRunningDownSameRamp`
failed — 6.96 m/s tie, the scaling had crushed the ramp's boost too, not just the exploit) —
recalibrated via the reference-angle normalization above, re-ran, 23/23 passing.

## Player facing decoupled from movement input (S no longer turns you around)

**Report:** pressing S span the character 180° to face away from the camera instead of just
backpedaling — same for pure A/D strafing, which faced the body sideways.

**Root cause:** `CharacterMotor.UpdateFacing` always faced the camera-relative wish direction
(`ComputeWishDirection()`), so the body's facing was slaved to WASD input rather than to the
camera — W faced forward, S faced backward, A/D faced sideways, exactly like a "move in the
direction you're pressing" controller, not a strafe one.

**Fix:** for the local player (`cameraYaw != null`), `UpdateFacing` now always targets the flattened
camera forward, full stop — movement input never rotates the body anymore. WASD is pure translation
(forward/back/strafe); only the camera (mouse-look) turns the character. This also fixes lunge/tag
reach, which uses `transform.forward` — a Tagger can now backpedal or circle while keeping the
reach aimed at their target, instead of it spinning away on S. Bots (`cameraYaw == null`) are
unchanged — they have no camera to aim with, so they still face their steering direction as before.

**Verified:** compile-check clean, all 3 scenes rebuilt without error, full PlayMode suite 23/23
passing (no existing test configures a non-null `cameraYaw`, so this only affects real player
control, not bots/self-play). Needs your own feel-check like the other presentation changes.

## Movement audio feedback — wind + landing thump/squash

**Change:** the movement spec calls for "wind audio scaling with velocity" and "landing effects" as
part of making high speed *feel* fast — a survey of the codebase confirmed these were the two
genuinely missing items from that list (FOV widening and landing camera shake already existed).
Added to `TagAgent.cs`, all procedurally generated (`AudioClip.Create`, same technique already used
for the tag "boop") — no external audio assets:
- **Wind**: a looping, crossfaded leaky-integrated-noise clip on the local player only, volume/pitch
  scaled by `CurrentSpeed` between a `WindMinSpeed` floor and the character's actual
  `MovementConfig.ground.maxHorizontalSpeed` (so retuning movement speed keeps the wind in sync for
  free, rather than a second hand-picked "max speed" number going stale).
- **Landing**: a body squash-and-stretch pulse (LateUpdate, same one-shot sin(0..pi) pattern already
  used for the lunge dive pitch) plus a short pitch-dropping "thump" `PlayClipAtPoint`, both gated on
  `CharacterMotor.Landed` — the same `minAirTimeForLandingEffects`-gated event camera shake already
  uses, so tiny ground-probe seams don't trigger it.

**Headless guard:** both audio paths (wind AudioSource creation, landing thump) are skipped when
`SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null` (the same check the minimap uses) —
self-play runs 12 bots landing constantly across 10 matches, and `PlayClipAtPoint` allocates a
throwaway GameObject per call, real churn with no payoff when there's no audio device to hear it.
The squash visual itself stays unconditional since it's a cheap Vector3 write with no allocation.

**Verified:** compile-check clean, all 3 scenes rebuilt without error, full PlayMode suite 23/23
passing (none of these paths are hit by self-play's all-bots matches, so no test coverage gap
introduced — same as the minimap). Not unit-testable beyond that; needs a manual feel-check like the
minimap did.

## Minimap visual polish

**Change:** cleaned up the circular minimap's readability, purely visual — no gameplay/simulation
changes: widened the circular crop mask's edge antialiasing from ~1px to ~3px (the hard 1px cutout
edge looked jagged), added a thin light ring texture (`BuildRingTexture`) drawn last as a border
frame around the circle, and gave the triangle/dot icons a dark outline pass (draw a full-size dark
copy underneath, then a smaller inset copy in the real color on top — `BuildTriangleTexture`/
`BuildDotTexture` gained an `inset` parameter for this) so icons stay legible against any background
color under them. Not unit-testable (OnGUI rendering); verified via compile-check, all 3 scenes
rebuilt clean, and the full PlayMode suite (23/23 passing, unaffected — self-play still never builds
the minimap since it never registers a local player).

## M3 self-play loop — 2 — late-game-phase confound fixed; win_rate is now a wall, stopping per LOOP.md

**Diagnosis:** with spawn clustering fixed (loop 1), tags were now landing across a wide spread —
mostly z=4-18 (still the ramp valley), but several z=29-32, and one match's last tag at **z=193.9**
(near the full corridor's reachable end) — real evidence bots are now actually running the gauntlet,
not dying at spawn. Yet `runner_win_rate` stayed 0.00.

**Found a second real confound, not a tuning target:** `TagRulesConfig.lateGamePhaseDuration`
defaults to 75s, tuned against the real 300s round (kicks in for the final quarter only). Self-play
shortens the round to 60s but never scaled this down — since 60 < 75, the "final phase" Tagger
speed boost (`lateGameMaxSpeedMultiplier`, up to +10%) was active from **t=0**, growing throughout
the *entire* match instead of just the end. This silently made every self-play match harder than a
real round would be, confounding the win-rate measurement itself. Fixed by scaling
`lateGamePhaseDuration` by the same 75/300 proportion of whatever round length the test uses
(`RoundDurationSeconds * (75f / 300f)` = 15s for the current 60s test round) — a test-harness
correctness fix, not a gameplay change.

**Metric outcome:** ran three consecutive batches after the two fixes above (spawn separation +
this one). `max_z_reached` varies a lot batch-to-batch (75.0, then 204.3, then back down) — self-play
has real variance, not a regression. But **`runner_win_rate` was 0.00 in all three**, with zero
movement despite both fixes being real, verified improvements to match quality and map traversal.

**Stopping the loop here per `Tools/LOOP.md`'s own rule** ("same metric fails 3 iterations with
no movement → stop, report the wall, don't tune blind"). Suspected underlying cause, worth a human
decision rather than another blind numeric guess: **the win condition itself may be structurally
hard to hit with equal-skill bots.** A Runner-win requires ALL 10 Runners to survive the whole
round independently — even a modest per-Runner catch probability compounds brutally across 10
agents (e.g. a 90% chance each individually survives still only yields ~35% for all ten; anything
below ~95% per-Runner survival pushes the *whole-round* win rate well under the 40-60% target
band). Bots also can't replicate the creative/deceptive evasion a human Runner would bring, which
the design likely leans on. This may not be fixable by tuning bot execution further — it may need
a design call: e.g. a different self-play success metric (avg. Runners surviving, not just "all
survived"), a difficulty rebalance for bot-vs-bot testing specifically, or accepting that this
target band is meant to be validated with human Runners, not bot self-play.

**Decision:** Keep both fixes (spawn separation, late-game-phase scaling) — genuine, verified
improvements regardless of the win-rate wall. Flagging the win-rate gap itself to the user rather
than continuing to guess at numbers against it.

---

## M3 self-play loop — 1 — spawn clustering was the dominant cause of instant sweeps

Continuing the M4/M3 self-play loop (`Tools/LOOP.md`) after the movement/minimap round above.
Baseline going in: `runner_win_rate=0.00 speed_p50=1.98 total_edge_attempts=[Jump=36]
max_z_reached=65.8` — matches ending in 5-16s.

**Diagnosis first, not a number guess:** added temporary per-tag logging (position + timestamp)
rather than assuming. Result was unambiguous: **every single tag in a full batch landed within
~8m of spawn (z: -4 to +8), all within 2-3 seconds of the round-start grace ending.** Taggers
weren't out-navigating anyone — roles are shuffled independently of spawn position, so a Tagger
routinely started immediately adjacent to a Runner in the same tight cluster and tagged them the
instant grace lifted, before anyone had moved meaningfully at all.

**Root cause, structural:** the spawn platform (`TagArenaLayout.SpawnSize`, 8m) was too small for
any amount of clever spacing to create real separation between a Tagger and the Runner cluster —
even pulling Taggers back along -Z (the map's only unoccupied direction; nothing exists behind
spawn) risked pushing them off the platform's edge into the void at the old size.

**Fix (one hypothesis, three small parts):**
- `TagArenaLayout.SpawnSize` 8m → 24m (single-source layout constant — propagates to the visual
  geometry and the layout's own z-cursor automatically, no desync risk).
- Spawn grid spacing (`BuildSpawnGrid` call sites in `PlaygroundBuilder.BuildTagArena` and
  `SelfPlayTests.cs`) 2.5m → 5m, now that there's room for it.
- `RoundController.AssignRoles`: Taggers now spawn offset -6m in Z from their originally-registered
  grid position (Runners keep theirs) — checked against the new platform size so even the
  widest-spread agent stays inside it (12 agents at 5m spacing spans ±5m; -8 would have pushed the
  extreme case to -13m, just off a ±12m platform — used -6m for margin).

**Metric outcome:** `time_to_first_tag` pushed out slightly (~2.9-4.3s vs a hard 2.94-3.06s
before). Match duration roughly tripled (5.7-36.3s vs 4.7-15.9s). **`max_z_reached` 65.8 → 203.9**
— runners are now getting through the ramp valley, the full gap gauntlet, and into the ledge row,
not dying at spawn. `total_edge_attempts` Jump 36→152, plus Climb 44 and WallRun 2 newly appearing.
`runner_win_rate` is still **0.00** — every match still ends in a full sweep, just a lot later and
after a lot more parkour.

**Decision:** Keep — this was a real, high-impact fix (3x match length, 3x map depth), not a wash.
Continuing the loop: something is still guaranteeing a 100% Tagger sweep given enough time, which
now that spawn clustering is fixed, is probably the *actual* structural issue underneath it — this
map is one single linear corridor with no branching routes, so a Runner has nowhere to lose a
Tagger once seen, only to out-pace them. Next: check whether tags are now clustering at a specific
later bottleneck (a choke point) before deciding whether this needs a bigger map-topology change.

---

## Bunny-hop, slide duration cap + facing-bug fix, circular minimap

Three requests from live playtesting, planned up front (design review via a second pass before
implementing) then built in order: bunny-hop feel, slide duration cap + a real facing bug, and a
circular minimap.

**Bunny-hop:** no hard-coded jump cooldown existed — `jump.coyoteTime`/`jumpBufferTime` already
allowed a near-instant re-jump, and jumps already retained 100% horizontal speed. But
`JumpSettings.bunnyHopWindow` was declared and never read anywhere — a dead field. Wired it up for
real: new `_lastLandingTime` (separate from `_lastGroundedTime`, which refreshes every grounded
tick and would've made the window almost always true), set on landing, reset in `ResetState`. `PerformJump`
now applies a small multiplicative speed bonus (`bunnyHopSpeedBonus`, 1.05) when a jump lands
within `bunnyHopWindow` of the last landing, bounded by the existing global speed cap. Verified:
chaining a jump immediately after landing measured 8.00 → 8.40 m/s, exactly the 5% bonus.

**Slide duration cap:** holding CTRL on a slope let the player slide indefinitely while
`downhillAccelMultiplier` kept adding speed and A/D kept steering — "I can just keep hold of CTRL
and slide forever whilst gaining momentum." Added `SlideSettings.maxSlideDuration` (1.75s) and
`_slideElapsed` tracking, folded into the existing `wantsExit` check (same pattern as
`TickWallRunning`'s `maxDuration`). Deliberately did NOT just lengthen the existing
`slideReentryCooldown` (0.5s) uniformly — `ExitSliding` fires on every exit path including the
slide-hop jump-out, which the rest of the tuning (`slideHopRetention=1`, the flat-ground boost) is
clearly built to reward via fast chaining; a longer cooldown across the board would've throttled
that too. Instead, only a forced max-duration exit sets a separate, longer
`forcedExitCooldown` (1.5s) deadline — voluntary release and slide-hops keep the original 0.5s.
First test attempt for this used the same 20m ramp as an existing test and produced a false pass
(the character reached the ramp's *end* and fell off before the duration cap even triggered, an
unrelated exit path) — caught by checking which state it exited into, fixed by using a much longer
(100m) ramp so the duration cap is unambiguously what's being tested.

**Facing bug — a real rotation-ownership conflict, not cosmetic:** `TagAgent.LateUpdate()`'s
slide-lean/lunge-dive pitch effect was applied by directly overwriting `transform.rotation` on the
same root GameObject whose Rigidbody `CharacterMotor.UpdateFacing` drives every FixedUpdate via
`MoveRotation`. `Physics.autoSyncTransforms` (default true) synced that manual write into the
Rigidbody's authoritative pose, so `UpdateFacing`'s next `RotateTowards` call started from a
pitch-contaminated quaternion — spherically interpolating from a pitched+yawed rotation toward a
pure-yaw target doesn't cleanly subtract the pitch, and can introduce off-axis roll. This recurred
every LateUpdate tick a slide/dive was active, worse the longer you slide — exactly "the player
model bugs out and doesn't face the right direction anymore." Fix: apply the pitch to
`_bodyRenderer.transform.localRotation` (the visible mesh, a child GameObject) instead of the
root — composes with the root's yaw for free through the transform hierarchy, never touches the
Rigidbody-owned root. Verified via a dedicated regression test (triggered through the lunge dive,
simpler than needing slope geometry): max root pitch/roll drift over the whole dive was 0.00°.
Known follow-up, not fixed here: the lunge arm pivots are still root-parented, so torso/legs will
now lean during a slide/dive but the arms won't — worth a look, not blocking.

**Circular minimap:** lives inside `RoundController` (`Game.Rules`) — the only viable home, since
`Game.Rules` already references `Game.Camera` and the reverse would be a cyclic asmdef reference.
This project's whole HUD is OnGUI/IMGUI (zero Canvas/UGUI anywhere), so the minimap stays
consistent: a second orthographic top-down camera renders into a small RenderTexture, drawn via
`GUI.DrawTexture` and cropped into a circle with a procedurally-generated circular mask texture
(cached once, not regenerated per `OnGUI` call — it runs at least twice a frame). Blue triangles
(rotated to facing) for agents sharing the local player's role, red dots for the opposing role.
North-up (doesn't rotate with player facing) and no edge-clamping for off-map agents in this pass —
both reasonable future refinements, not required now. Built lazily inside `RegisterAgent`'s
`isLocalPlayer` branch rather than unconditionally in `Awake`/`Start`, specifically so the
self-play harness's 10 headless bot-only matches (never a local player) skip camera/RenderTexture
setup entirely — confirmed via the design review that building it unconditionally would've leaked
a Camera + RenderTexture per self-play match with no teardown between them.

**Found and fixed along the way:** headless batch-mode (`-nographics`, including this project's
own scene-load PlayMode tests that legitimately register a real local player) reports a Null
graphics device — `RenderTexture.Create` throws there, which surfaced as two real test failures
the moment the minimap shipped. Guarded `SetupMinimap` on `SystemInfo.graphicsDeviceType !=
GraphicsDeviceType.Null` — skips gracefully in headless contexts (nothing to display a minimap on
anyway), same spirit as the self-play lazy-init guard above.

**Metric outcome:** Full suite 23/23 passing, zero warnings, all three scenes rebuilt clean.

**Decision:** Keep all four. The minimap specifically needs your own eyes — orthographic size (25),
icon size, colors, and whether north-up vs. player-rotating feels right are all first-guess values
with zero visual feel-testing behind them yet.

---

## Post-office-session sync — test suite triage (5 failures after pull)

Pulled the office session's work (Rooftop Arena, movement rework, M4 loop entries below) onto a
second machine. Compiled clean, all three scenes rebuilt clean, but the test suite went 14/19 —
triaged each failure against what actually changed rather than guessing.

**Two were stale assertions against intentional redesigns, updated to match:**
- `TagArenaScene_SpawnsWithCorrectRoleDistribution` expected 12 agents/1 tagger/11 runners — the
  scene is now a deliberate 3-agent "chase me" mode (`TagArenaAgentCount = 3`,
  `TagRulesConfig.forcePlayerAsRunner = true`, `taggerCount = 2`): player is the runner, 2 bots
  hunt them. Updated the assertions to 3/2/1.
- `Tag_OnContact_ConvertsRunnerToTaggerWithGrace` / `TaggedAgent_CannotTagAnyoneDuringGracePeriod`
  assumed passive body-contact tags on its own. It doesn't anymore — contact only tags during a
  brief 0.45s window right after a lunge (`TagAgent`'s `_lungeTagWindowRemaining`/`_lungeTagUsed`,
  the "dive tackle"), one runner max per lunge. Both tests now call `TryLunge()` before relying on
  contact; the grace test also asserts `LungeCooldownRemaining == 0` after the in-grace lunge
  attempt, to actually verify it was a full no-op (grace blocks `TryLunge` itself) rather than
  "happened not to tag."
- `Slide_SelfCorrectsTowardTrueDownhillDirection` held the same sideways input through the whole
  slide — but A/D now actively *steers* the slide while held (`SlideSteerDegPerSec`), which fights
  the fall-line self-correction by design. Test held sideways input throughout, so it was measuring
  "does self-correction win against continuous opposing steering input" (no) instead of its actual
  intent, "does letting off return you to the fall line" (yes). Released the sideways input once
  sliding, matching a player letting off A/D.

**Two were real regressions, found via diagnostics and fixed at the source:**
- `WallRun_MeasuresSustainedDuration` returned a **negative** duration. Added per-tick diagnostic
  logging rather than guessing: revealed *two* wall-run cycles in the 6s window — a harmless
  spurious ~0.12s attach during spawn-settle (still within the ground platform's footprint, ends
  the instant it actually lands), then the real one after falling off the ledge, which *also* only
  lasted ~0.14s. Root cause of the real one: the character enters wall-run already falling at
  ~5 m/s (carried over from the jump arc); `wallRun.gravityMultiplier` only slows *further*
  acceleration, not the speed already carried in, so it fell out from under the 4m-tall wall almost
  immediately — nowhere near a sustained run. This is very likely the same root cause behind the
  office session's already-flagged "WallRun attempts ~10, ~0 completions" self-play finding (loop 9
  below), not a new issue. **Fix:** clamp the vertical velocity to a small downward speed when
  wall-run engages (new `MovementConfig.wallRun.maxEntryFallSpeed`, 1.5 m/s) — catches the fall
  instead of carrying it through. The test's own event-recording was *also* fragile (paired "first
  end" with "most recent start" across multiple cycles, which is exactly how you get a negative
  duration) — rewrote it to track the longest single start→end cycle instead.
- The above; no second unrelated regression turned up once the wall-run fix landed — the two
  "real" failures shared one root cause.

**Metric outcome:** Full suite 19/19 passing, zero warnings, all three scenes rebuilt clean.

**Decision:** Keep all fixes. The wall-run entry-speed clamp should be re-validated against the
self-play harness next (`Tools/selfplay.sh` — note its `PROJ` path is hardcoded to the office
machine, needs making portable before it'll run correctly here or on any other machine) to see
whether `WallRun attempts/completions` actually improves now, since that was flagged as a "next
target" and this fix directly addresses the mechanism likely causing it.

---

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
