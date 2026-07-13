# ROADMAP — WP1-WP7 (bots first)

Prioritized work packages to get the prototype to its target state, produced 2026-07-13 from two
fresh audits (spec-gap audit + mechanism-level bot-execution forensics). Each WP is one
executor-routed dispatch wave + one verification gate. Pick up from WP1.

## Context

The user asked for the most important next steps for the game overall, with explicit emphasis:
"make the bot pathfinding better, and teach them to use the swing ropes." Two fresh audits ground
this plan. **Spec audit**: M1 (movement) and M2 (tag loop) are MET; M3 (scary bots, self-play in
target bands) is the only blocked milestone; M4 (juice) loops healthily. Concrete spec violations:
the fall rule diverged without the spec's required "pick one and justify" decision; Roof_Tower has
exactly one route (dead end, violates the ≥2-routes map rule); Con_West has one inbound route;
SlideHop/Drop exist in bot vocabulary but not map data; landing/wind audio missing (procedural
synthesis is BANNED by project policy — must be sourced recordings); no round summary/score memory.
**Bot forensics** (mechanism-verified): three bugs explain the "bots can't use special edges" wall —
(1) `Replan()` runs every 0.3s and unconditionally rebuilds `_path`, discarding in-progress edge
bookkeeping (RecordEdgeUsage only fires on ToNode arrival of the SAME path object) AND dropping the
approach steering + held Interact for swings mid-approach — a real behavior failure; (2)
`IsShortJumpEdge` computes gaps via `TagArenaLayout.PlatformLength` (a const from the retired
corridor map), misclassifying nearly EVERY rooftop jump as "short" → walk-speed takeoffs (measured
4.2 m/s vs sprint 8) → 3.3m average landing miss; (3) attempt counters are frame-inflated for
Swing/Climb (fire per-FixedUpdate) vs per-event for Jump, so ratios lie. Swing/climb geometry itself
verified good.

Divergences-by-design NOT re-proposed: wall-run removed; procedural audio banned; IMGUI-only HUD.

**Execution:** executor-routed waves per work package (opus for motor/bot/design-judgment tasks,
sonnet for mechanical/data/HUD tasks); orchestrator (fable) runs gates/commits on `main-game`,
one Unity instance. Each WP = one dispatch wave + one gate. Verified anchors are in the audit
reports; key signatures re-verified by the design agent (ParkourBotInput fields :61-72, Replan
:165-196, RecordEdgeUsage :259, RecordEdgeAttempt :307/:341/:354, IsShortJumpEdge :374-383,
ParkourEdge immutable ctor, RoundController fall branch :208-223).

## WP1 — Bot commit-to-edge + short-jump fix + honest instrumentation (UNBLOCKS M3; user's emphasis)

1. **Commit-to-edge latch** (`ParkourBotInput.cs`) — opus. Fields `bool _committed; float
   _commitDeadline;`. Latch sets inside `ExecuteEdgeButtons` at the first `JumpPressed`/
   `InteractPressed` for a committed-kind edge (Jump/Swing/Climb/Ladder/Vault/Mantle), deadline
   `Time.time + 4f`. Split `Replan()` into `SelectTarget()` (always runs — tag logic/prediction
   stays fresh) and `RecomputePath()` (SKIPPED while `_committed`). Clear the latch: on the
   committed edge's ToNode arrival (same site as `RecordEdgeUsage`), on the fall-respawn teleport
   guard (the existing >12m displacement check), and on deadline timeout. This preserves approach
   steering, the held Interact for swings, and completion bookkeeping across reaction ticks.
2. **Per-edge gap metadata** (`ParkourEdge.cs`, `ParkourGraph.cs`, `RooftopGraphBuilder.cs`,
   `ParkourBotInput.cs`) — opus (same wave as task 1, shared files). `ParkourEdge` gains
   `public readonly float EmptyGap;` threaded through `AddEdge` (optional param, default 0). The
   Jump case in `RooftopGraphBuilder` computes it from the resolved lip positions (true lip-to-lip
   distance minus insets). `IsShortJumpEdge` reads `edge.EmptyGap` — deleting the last dependency
   on retired-corridor geometry. Jumps stop misclassifying → sprint takeoffs.
3. **Normalize attempt counters** (`ParkourBotInput.cs`) — sonnet, after 1+2 land. Swing and
   Climb/Ladder `RecordEdgeAttempt` fire once per commitment (piggyback the latch transition),
   matching Jump's per-event semantics.

**Gate:** rebuild ×3 + all four suites + `Tools/selfplay.sh`. Expected movement: takeoff 4.2→~8,
land-within-1.75m 0.24→up, landing error 3.3→down, `Swing` completions 0→>0, attempts drop to sane
per-event counts, `runner_avg_survival` 0.00→>0 (any movement counts).

## WP2 — Balance iteration to `runner_avg_survival` 0.50-0.70 (M3 sign-off)

Only after WP1: sweep one knob-set per self-play run — `BotConfig` Skilled (reactionTime,
predictionHorizon, executionPrecision), `TagRulesConfig` (tagReach 1.2/2.0, lungeCooldown 1.5,
roundStartGrace 3, taggerCount, lateGamePhaseDuration). Sonnet iterates with the LOOP.md protocol
(ONE change per run, delta log in TUNING_LOG); opus only if three iterations move nothing
(structural stop-rule). **Gate:** survival in band across a 10-match batch, stuck/fallen not
regressed. This signs off M3.

## WP3 — Fall-rule decision + Tower/Con_West route fixes (spec correctness)

1. **Fall rule** — opus implements the RECOMMENDATION (user can override at plan review): delayed
   nearest-rooftop respawn — keep the existing Runner→Tagger conversion (the "map tags you"
   pacing), but respawn at the NEAREST roof `Walk` anchor after a short (~2s) delay instead of the
   jarring instant teleport to original spawn. Satisfies the spec's "pick one and justify"
   (justification: preserves infection pacing, removes teleport disorientation, no new climb-back
   state machine). `RoundController` fall branch.
2. **Map routes** — sonnet. `RooftopArena.Links`: add a second link to Tower(11) (Jump/Swing from
   an adjacent roof so it's not ladder-only) and a second INBOUND to Con_West(22).
   `JumpMakeable` auto-validates; watch for `ROOFTOP_LINK_SKIPPED`.

**Gate:** suites + self-play (respawn sane, ≥2 routes to nodes 11/22, survival still in band).

## WP4 — Round summary + session score memory (small, high-visibility)

Sonnet, `RoundController` only: session counters (rounds played, runner-wins/tagger-wins, last
round's survivor count, maybe tags-by-you) incremented in the round-end path, rendered in the end
screen under the banner; `R` preserves the tally. IMGUI convention. **Gate:** TagRulesTests +
manual: tally survives ≥2 rounds.

## WP5 — Landing/impact + wind audio via SOURCED clips (feel)

Opus sources landing-thud + soft wind-loop recordings via the established pipeline (Wikimedia/CC0,
`Assets/Audio/` + ATTRIBUTION.md — same as the city ambience; synthesis remains banned). Sonnet
wires playback: landing thud on the motor's existing `Landed` event (already air-time gated), wind
bed volume tied to `CurrentSpeed` (the ORIGINAL spec ask, now with a real recording). Volumes
conservative (city-bed precedent: quiet but present). **Gate:** clips import (no AMBIENCE_SKIPPED-
class failures), suites green, manual listen.

## WP6 — (Optional, cut freely) SlideHop/Drop map content

Sonnet: one SlideHop and one Drop link ONLY where they open a genuinely new route; bots must
traverse them in self-play (`total_edge_usage` nonzero) without survival regression — otherwise cut.

## WP7 — Merge `main-game` → main + canonical baseline (closing)

Merge (opus judgment only if conflicts), full suite green on main, one final self-play batch
recorded in TUNING_LOG + `Tools/baseline-metrics.txt` as the canonical post-roadmap baseline.

## Verification (shared)

Per-WP gates as above; the standard loop: close Editor (pre-authorized), rebuild 3 scenes headless
(`*_BUILD_OK`, no `error CS`), `MovementMetricsTests|TagRulesTests|PropClearanceTests|
RooftopGraphTests`, `bash Tools/selfplay.sh` vs current baseline. STOP for a human feel-test after
WP1 (bot behavior change is player-visible: real sprint jumps, swing crossings) and at the end.
