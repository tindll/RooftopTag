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
- `total_edge_usage` — every reachable edge type used at least a few times per batch
  (Run, Jump, WallRun, Vault, Mantle, Climb, Ladder, Swing). Baseline uses **Run only**.
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

## Baseline (this loop's start)
`matches=3 runner_win_rate=0.00 speed_p50=4.09 speed_p90=8.00 total_stuck=0 total_fallen=10
total_edge_usage=[Run=26]` — taggers always win, no parkour, ~3 falls/match, 5–9s matches.

## Tunable surfaces
- `Assets/Scripts/Movement/Runtime/MovementConfig.cs` — movement feel/speeds.
- `Assets/Scripts/Rules/Runtime/TagRulesConfig.cs` — grace, tag reach, round rules.
- `Assets/Scripts/AI/Runtime/BotConfig.cs` + `ParkourBotInput.cs` — bot decision logic / graph use.
- `Assets/Scripts/AI/Runtime/TagArenaParkourGraphBuilder.cs` — the parkour graph bots navigate.

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
