# Movement Capabilities (M1 baseline)

Measured automatically by `Assets/Tests/PlayMode/MovementMetricsTests.cs`, run via:

```
Unity.exe -batchmode -nographics -projectPath <path> -runTests -testPlatform PlayMode -testResults results.xml
```

All values below come from `Debug.Log("METRIC ...")` lines in that run, using default
`MovementConfig` values (see `Assets/Scripts/Movement/Runtime/MovementConfig.cs`). These are
the numbers the playground's gap distances and ledge heights in `PlaygroundBuilder.cs` are
sized against.

| Capability | Measured value | Notes |
|---|---|---|
| Sprint speed | 8 m/s | `MovementConfig.ground.sprintSpeed` |
| Jump vertical speed | 6.5 m/s | `MovementConfig.jump.jumpSpeed` |
| **Max sprint-jump horizontal distance** | **9.60 m** | Straight sprint + single jump, measured takeoff → landing. |
| **Slide-hop chained distance** | **9.57 m** | Sprint → slide (0.25s hold) → buffered jump keeping full horizontal speed, measured on flat ground. Matches plain sprint-jump almost exactly — the slide's entry boost is downhill-gated (see `TUNING_LOG.md`, feel-test round 1), so a flat-ground slide-hop no longer adds free speed. Expect a real boost only when sliding downhill. |
| **Ladder climb-up duration** | 1.78 s | For a 6m ladder at `MovementConfig.ladder.climbSpeed` (3.5 m/s) plus mantle hand-off at the top. |
| **Swing release speed (apex-timed)** | **11.93 m/s** (Jump-release; E-release measured 8.00 m/s; lateral push covers 3.49m in 1.5s) | Reworked 2026-07-12: omnidirectional velocity-state pendulum — WASD force projected on the rope's tangent plane, E/Jump both release after a 0.15s post-attach grace, release velocity = swing velocity × 1.15, Jump adds +1.5 up. Old fixed-plane value (2.13 m/s) superseded — 5.6× faster; measured by `Swing_MeasuresApexReleaseSpeed`. |
| **Climb (wall-scramble) threshold height** | 2.60 m | Reaches ledge and mantles off in 1.98s with no stutter. Threshold range is `mantleVault.mantleMaxHeight` (2.2m) to `climb.climbMaxHeight` (3.0m). |
| Mantle height range | 0.5 m – 2.2 m | `MovementConfig.mantleVault.mantleMinHeight` / `mantleMaxHeight` |
| Vault height range | 0 m – 1.1 m (requires ≥3 m/s approach) | `MovementConfig.mantleVault.vaultMaxHeight` / `vaultMinApproachSpeed` |
| Coyote time | 0.1 s | `MovementConfig.jump.coyoteTime` |
| Jump buffer | 0.15 s | `MovementConfig.jump.jumpBufferTime` |
| Walk speed (Sprint not held) | 4 m/s | `MovementConfig.ground.walkSpeed` — Shift is the sprint key as of feel-test round 4. |
| **Running down a 20m/8m-drop ramp** | 6.96 m/s, 0 airborne transitions | No longer bounces (feel-test round 5 fix). |
| **Sliding down the same ramp** | 13.00 m/s (hits `maxHorizontalSpeed` cap) | Confirms slide-down-a-slope is meaningfully faster than running it. |

**Wall-run — REMOVED 2026-07-12.** The auto-attach wall-run mechanic was cut by design: it
grabbed players who didn't want it. The wall grab (WallHook, `E`) plus jump combo is the
intended wall traversal.

## Playground gap/ledge calibration

`PlaygroundBuilder.cs` builds a gap gauntlet at `[3, 5, 7, 9, 11, 13]` m — bracketing the
measured 9.6m max jump distance so players discover the limit and where slide-hop/swing routes
become necessary. Ledge heights are computed directly from the live `MovementConfig` defaults
(vault/mantle/climb boundaries and a "too tall" control wall above the climb threshold that
should remain a real obstacle).

## Known tuning gaps for the M4 improvement loop

1. **Swing weakness — superseded by the 2026-07-12 rework.** The old fixed-plane pendulum
   measured 2.13 m/s release, far below the 8 m/s sprint speed and the "fastest move in the game"
   design intent. Replaced with an omnidirectional velocity-state pendulum (WASD force projected
   on the rope's tangent plane; E/Jump both release after a 0.15s grace; release = swing velocity
   × 1.15, Jump adds +1.5 up), covered by `Swing_MeasuresApexReleaseSpeed` (constant-hold pump,
   asserts release in (6, 15) m/s), `Swing_EReleasesWithoutJump`, and
   `Swing_OmnidirectionalLateralPush` in `MovementMetricsTests.cs`. Re-measured: **11.93 m/s**
   Jump-release / 8.00 m/s E-release / 3.49m lateral push (first green run, 2026-07-12). Remaining:
   a manual feel-test pass (grab, circle-swing with WASD, pump, release with E vs Jump) before
   closing this item out.
2. `ground.maxHorizontalSpeed` (13 m/s hard cap, added in feel-test round 1) is a first guess —
   watch for it feeling too restrictive on legitimate high-momentum chains (wall-jump into swing
   release, etc.) during further feel-testing.
