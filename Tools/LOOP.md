# M4 Self-Improvement Loop — playbook

Autonomous improvement loop for the Tag Arena bots + movement tuning. Each iteration is one
hypothesis → change → measure → log cycle. Follow the established methodology in `TUNING_LOG.md`:
**tune numbers when the metric says so; STOP and report when the problem is structural.**

## Measure primitive
```
bash Tools/selfplay.sh
```
Runs `SelfPlayTests` headless (12 bots, no human, 8x timescale, 3 matches). Prints per-match +
batch `METRIC` lines. Editor GUI must be CLOSED (project lock) — `taskkill //IM Unity.exe //F`
first if a run errors with a lock.

## Target bands (from CLAUDE.md / TUNING_LOG.md)
- `runner_avg_survival` **0.50–0.70** at Skilled difficulty — the primary tuning target now (see
  "runner_win_rate wall" below for why).
- `runner_win_rate` tracked, not primarily tuned against — a strict all-or-nothing metric (every
  single Runner must survive independently) that compounds too harshly across 10 agents to give a
  usable gradient; CLAUDE.md's 0.40–0.60 band for it is realistically a human-playtest target, not
  a bot-self-play one. Still logged for visibility; a healthy `runner_avg_survival` should pull it
  off zero over time even if it never hits the full band via bots alone.
- `total_edge_usage` — every edge type the current map actually has, used at least a few times per
  batch. **As of the branching-arena change below, that's Run, Jump, Ladder only** — RooftopArena's
  `Links` table has no WallRun/Vault/Mantle/Climb/Swing/SlideHop entries at all, so those showing
  zero is expected, not a bot-tuning gap. Adding them is a map-content change (`RooftopArena.cs`),
  not something bot tuning can produce.
- `total_stuck` **0**.
- `total_fallen` — low single digits per match at most.
- `speed_p90` ~ sprint speed (8 m/s), not pinned to the 13 m/s cap.

### runner_win_rate wall (resolved 2026-07-12)
Earlier loops (see "M3 self-play loop — 1/2" below) fixed real structural issues (spawn clustering,
a late-game-phase test confound) but `runner_win_rate` stayed at 0.00 for 3 straight batches — the
LOOP.md stop condition triggered. Root cause: a Runner-win requires ALL 10 Runners to survive
independently, so even a 90% per-agent survival chance only yields ~35% for all ten (and the band's
low end, 40%, needs ~95% per-agent survival — a very high bar). Decision (user call): keep
`runner_win_rate` as a tracked/reported number but tune against the new `runner_avg_survival`
metric instead, which has an actual gradient to iterate against.

### Branching-arena change (2026-07-12) — baseline below is STALE
The `runner_avg_survival` metric, once added, measured **0.00** too — not partial credit, every
single Runner got tagged in every single match. Investigated further: self-play and the real
`TagArena.unity` scene both built on `TagArenaMapGeometry.BuildMainCorridor`, a single linear
corridor with no branching — a Runner could only go forward or get caught, never juke or double
back, violating CLAUDE.md's own "no dead ends... reachable and leavable by at least two routes" map
rule. Self-play and Tag Arena now build on `RooftopArena.cs`'s branching topology instead (13
rooftops, real loop routes, 4-way branching from spawn) — see `TUNING_LOG.md` for the full change.
**Everything below this point (baseline + M3 loop history) describes the retired linear-corridor
map and is kept only as a historical record — do not compare new measurements against it.** A fresh
baseline needs capturing on the branching map before tuning resumes.

**Do not resume tuning against self-play numbers on this map yet.** Fixing spawn crowding (agents
now spread across 5 roofs, not 1) surfaced a deeper, separate problem: `RooftopGraphBuilder` puts
one node per roof, which is too coarse once many agents are close together — `ParkourGraph.FindPath`
returns an empty path whenever two agents' nearest nodes coincide, so bots mostly fall back to raw
beeline steering that fights its own cliff-avoidance instead of using the graph at all (near-zero
edge usage even as agents move and fall around). See TUNING_LOG.md's full entry for the diagnostic
trace. This needs a real fix (denser graph nodes, or different fallback behavior) before self-play
numbers on this map mean anything — don't tune bot execution numbers against them until then.

## Baseline (linear-corridor era — STALE, see above)
`matches=3 runner_win_rate=0.00 speed_p50=4.09 speed_p90=8.00 total_stuck=0 total_fallen=10
total_edge_usage=[Run=26]` — taggers always win, no parkour, ~3 falls/match, 5–9s matches.

## Tunable surfaces
- `Assets/Scripts/Movement/Runtime/MovementConfig.cs` — movement feel/speeds.
- `Assets/Scripts/Rules/Runtime/TagRulesConfig.cs` — grace, tag reach, round rules.
- `Assets/Scripts/AI/Runtime/BotConfig.cs` + `ParkourBotInput.cs` — bot decision logic / graph use.
- `Assets/Scripts/AI/Runtime/RooftopGraphBuilder.cs` + `Assets/Scripts/MapGeometry/Runtime/
  RooftopArena.cs` — the parkour graph bots navigate and the map data it's built from.

## Each iteration
1. `bash Tools/selfplay.sh` → read `METRIC selfplay_batch`.
2. Pick the single worst gap vs bands.
3. Diagnose numeric-vs-structural. If structural (e.g. bots never touch the graph, round collapses
   at spawn), INVESTIGATE the code path, don't guess a number.
4. Make ONE change.
5. Re-run. Record hypothesis + metric delta + decision as a new `## M4 loop — <n>` entry at the
   TOP of `TUNING_LOG.md`.
6. Stop the loop and report to the user when: all bands met, OR the next step needs a human
   decision (design change, feel-test, ambiguous tradeoff).

## Stop conditions
- All target bands met for 2 consecutive batches → done, report.
- Same metric fails 3 iterations with no movement → stop, report the wall (don't tune blind).
- Any change that needs human feel-testing or a design call → stop, report.
