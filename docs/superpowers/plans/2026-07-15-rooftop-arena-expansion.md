# RooftopArena Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Grow the RooftopArena with a new connected eastern building cluster, more ramps, more swings, and more void-escape pipes — with swings placed by a *generic* pivot derivation instead of a hardcoded literal, and every ramp guaranteed to connect.

**Architecture:** All map data lives in one source of truth, `RooftopArena` (`Roofs[]`, `Links[]`, `VoidPipes[]`), consumed by three independent readers: the saved-scene builder (`PlaygroundBuilder`), the headless self-play builder (`RooftopInteractableBuilder`), and the bot pathfinding graph (`RooftopGraphBuilder`). We extend the data tables and fix the one piece of per-link geometry that is currently hand-coded for a single roof pair — the swing pivot — so new swings land sensibly. New roofs are appended to `Roofs[]` at the END to preserve index stability (existing links reference roofs by array index).

**Tech Stack:** Unity 6 LTS, C# (nullable enabled, file-scoped namespaces), URP. Headless validation via `Unity.exe -batchmode -quit -executeMethod`. PlayMode tests via the Unity Test Runner.

**Invariants that MUST stay green (the whole point of the validation tasks):**
- `ROOFTOP_LINK_SKIPPED` count == **0** (a declared Jump/Drop that isn't makeable silently vanishes from the bot graph).
- `RooftopGraphTests` all pass — especially `Graph_EmitsAllNewEdgeTypes` (a Swing/Climb/Vault/Ladder edge each still exists), `Graph_HasDenseNodesPerRoof` (`Nodes >= Roofs.Length * 5`), and the route-count guards (`Links_TowerHasSecondExit` / `Links_ConWestHasSecondInbound`).
- Every NEW roof has **≥2 routes in and out** (accounting for one-way `Swing`/`Drop`), i.e. no soft dead-ends (CLAUDE.md map design rule).
- No new `ROOFTOP_LADDER_RAMP_CLIP`, `ROOFTOP_LINK_REDUNDANT`, or (new this plan) `ROOFTOP_RAMP_STEEP` warnings.

**Reference — makeability thresholds** (`RooftopGraphBuilder.JumpMakeable`, hardcoded, NOT auto-derived from `MovementConfig`): going up, horizontal `dist <= 9m` AND `rise <= 2.5m`; dropping, `dist <= 11m`. Gaps are measured **lip-to-lip** (closest of the 5 nodes per roof), not centre-to-centre. New roofs below sit on the existing ~13m-centre grid with small height steps so Jump links are makeable by construction; the validation task confirms it.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` | Map source of truth + per-link geometry builders | Modify: refactor `BuildSwing` pivot → generic `SwingPivot`; add ramp-grade warning in `BuildRamp`; extend `Roofs[]`, `Links[]`, `VoidPipes[]`, `SpawnRoofIndices`, `CanAnchors` |
| `Assets/Tests/PlayMode/RooftopGraphTests.cs` | Graph/route invariants | Modify: add swing-pivot test + new-roof route-count guards |
| `Assets/Tests/PlayMode/MapGeometryTests.cs` *(create if absent — else add to RooftopGraphTests)* | Pure-geometry unit tests for `SwingPivot` | Create/Modify |

No changes needed in `PlaygroundBuilder`, `RooftopInteractableBuilder`, or `RooftopGraphBuilder`: they are pure consumers of the tables above and pick up new roofs/links/pipes automatically (roof loop over `Roofs.Length`, link loop over `Links`, pipe loop over `VoidPipes`).

---

## Task 1: Generic swing-pivot derivation (the "swings in sensible places" fix)

**Why first:** every new swing in Task 3 depends on this. Today `BuildSwing` returns a hardcoded `(-37.5, 9, -9)` regardless of the roofs passed in ([RooftopArena.cs:366](Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs#L366)) — the root cause of past swings landing on ramps / in the void. We replace it with a derivation that provably reproduces the existing pivot for the current 22↔23 swing and generalises to any roof pair.

**Files:**
- Modify: `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` (`BuildSwing` ~364-391; add `SwingPivot` + `OverlapMidpoint` helpers)
- Test: `Assets/Tests/PlayMode/RooftopGraphTests.cs`

- [ ] **Step 1: Write the failing test** (add to `RooftopGraphTests.cs`)

```csharp
[Test]
public void SwingPivot_ReproducesHandTunedPivot_For22To23()
{
    // The old hardcoded pivot for the Con_West(22)->Con_Alley(23) swing was (-37.5, 9, -9).
    // The generic derivation must reproduce it exactly (chain length 5.5 as declared in Links[]).
    Vector3 p = RooftopArena.SwingPivot(RooftopArena.Roofs[22], RooftopArena.Roofs[23], 5.5f);
    Assert.AreEqual(-37.5f, p.x, 0.001f, "pivot.x = midpoint of the roofs' X-overlap");
    Assert.AreEqual(9f,     p.y, 0.001f, "pivot.y = max(roof heights) + chain length");
    Assert.AreEqual(-9f,    p.z, 0.001f, "pivot.z = midpoint of the N-S chasm");
}

[Test]
public void SwingPivot_SitsAboveBothRoofs_AndBetweenThem()
{
    // A synthetic E-W crossing: two 8x8 roofs at h4 and h6, 20m apart on X, same Z.
    var a = new RooftopArena.Roof("A", 0f, 0f, 4f, 8f, 8f);
    var b = new RooftopArena.Roof("B", 20f, 0f, 6f, 8f, 8f);
    Vector3 p = RooftopArena.SwingPivot(a, b, 5f);
    Assert.AreEqual(10f, p.x, 0.001f, "x = midpoint of the two facing edges (4 and 16) => 10");
    Assert.AreEqual(11f, p.y, 0.001f, "y = max(4,6) + 5");
    Assert.AreEqual(0f,  p.z, 0.001f, "z = overlap midpoint (roofs share Z) => 0");
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run (headless): `& "C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe" -batchmode -projectPath "C:\Users\jaaaa\RooftopTag" -runTests -testPlatform PlayMode -testFilter "SwingPivot_" -logFile -`
Expected: FAIL — `RooftopArena` has no public `SwingPivot` method (compile error / missing member).

- [ ] **Step 3: Add the generic derivation and rewire `BuildSwing`**

In `RooftopArena.cs`, replace the first two lines of `BuildSwing` (the hardcoded `pivot` + `exitDir`) so it calls the new helper, and add the helpers below it. Keep the rest of `BuildSwing` (the `SwingBeam` box + 60° roll) unchanged — it already reads `pivot.x/y/z`.

```csharp
private static (Vector3 pivot, float length, Vector3 exitDir) BuildSwing(Transform parent, Roof from, Roof to, float length)
{
    Vector3 pivot = SwingPivot(from, to, length);
    Vector3 exitDir = new Vector3(to.Center.x - from.Center.x, 0f, to.Center.z - from.Center.z).normalized;
    // ...existing SwingBeam creation + 60-degree roll, unchanged, using `pivot`...
    return (pivot, length, exitDir);
}

/// <summary>
/// Derives the overhead beam pivot for a swing between two roofs, generically (no hardcoded
/// coordinates). The grab point (pivot.y - length) is hung at the taller roof's surface height so a
/// runner leaping the chasm at roof height meets the chain; the pivot sits over the midpoint of the
/// gap on the crossing axis and over the midpoint of the roofs' footprint overlap on the other axis.
/// Verified to reproduce the original hand-tuned Con_West->Con_Alley pivot (-37.5, 9, -9) exactly.
/// </summary>
public static Vector3 SwingPivot(Roof from, Roof to, float length)
{
    float dx = to.Center.x - from.Center.x;
    float dz = to.Center.z - from.Center.z;
    float pivotY = Mathf.Max(from.Center.y, to.Center.y) + length;

    if (Mathf.Abs(dz) >= Mathf.Abs(dx))
    {
        // N-S crossing: pivot.z at the chasm midpoint (between the two facing Z edges),
        // pivot.x at the midpoint of the roofs' X-footprint overlap.
        float fromEdgeZ = from.Center.z + Mathf.Sign(dz) * from.SizeZ * 0.5f;
        float toEdgeZ   = to.Center.z   - Mathf.Sign(dz) * to.SizeZ   * 0.5f;
        float pz = (fromEdgeZ + toEdgeZ) * 0.5f;
        float px = OverlapMidpoint(from.Center.x, from.SizeX, to.Center.x, to.SizeX);
        return new Vector3(px, pivotY, pz);
    }

    // E-W crossing: mirror the axes.
    float fromEdgeX = from.Center.x + Mathf.Sign(dx) * from.SizeX * 0.5f;
    float toEdgeX   = to.Center.x   - Mathf.Sign(dx) * to.SizeX   * 0.5f;
    float pxEW = (fromEdgeX + toEdgeX) * 0.5f;
    float pzEW = OverlapMidpoint(from.Center.z, from.SizeZ, to.Center.z, to.SizeZ);
    return new Vector3(pxEW, pivotY, pzEW);
}

/// <summary>Midpoint of the overlap of two 1D spans [c1±s1/2] and [c2±s2/2]; if the spans don't
/// overlap, falls back to the midpoint of the two centres (keeps the pivot between the roofs).</summary>
private static float OverlapMidpoint(float c1, float s1, float c2, float s2)
{
    float lo = Mathf.Max(c1 - s1 * 0.5f, c2 - s2 * 0.5f);
    float hi = Mathf.Min(c1 + s1 * 0.5f, c2 + s2 * 0.5f);
    return lo <= hi ? (lo + hi) * 0.5f : (c1 + c2) * 0.5f;
}
```

Also update the `BuildSwing` doc-comment: replace the paragraph that justifies the hardcoded `(-37.5, 9, -9)` with a one-line pointer to `SwingPivot`.

- [ ] **Step 4: Run the test to verify it passes**

Run: same command as Step 2.
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs Assets/Tests/PlayMode/RooftopGraphTests.cs
git commit -m "Derive swing pivot from linked roofs instead of hardcoding"
```

---

## Task 2: Ramp-grade warning (the "make sure all ramps connect" guardrail)

**Why:** `BuildRamp` already clamps the ramp foot to stay ≥1m inside the lower roof, so ramps always physically connect. The remaining failure mode is a lower roof too small to host the full 22° run, which silently produces a *steeper* ramp. We surface that as a build warning so new ramps that connect but feel like walls get caught in validation instead of in play.

**Files:**
- Modify: `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` (`BuildRamp` ~434-488)

- [ ] **Step 1: Add the steepness check inside `BuildRamp`**

Immediately after the clamp block (`if (run > maxReach) { ... }`, ~line 470), before building the box, add:

```csharp
// "All ramps connect" guardrail: the clamp above keeps the foot on the lower roof, but a lower
// roof too small to host the full 22-degree run yields a steeper ramp. Warn so it's caught here,
// not in a feel-test. gradeDeg = atan(rise / run); 22deg is the design target, 34deg is the
// steepest we consider comfortably sprint-able.
float gradeDeg = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;
if (gradeDeg > 34f)
    Debug.LogWarning($"ROOFTOP_RAMP_STEEP: ramp {lower.Name} -> {upper.Name} is {gradeDeg:F0} deg " +
        $"(target 22) — lower roof too small to host the run; widen it or use a Ladder.");
```

- [ ] **Step 2: Verify current map is clean (no false positives)**

Run headless build (Task 7 command). Expected: **zero** `ROOFTOP_RAMP_STEEP` lines for the existing ramps (they were all verified at full 22° per the `BuildRamp` comment). If any appears now, the threshold is wrong — do not proceed.

- [ ] **Step 3: Commit**

```bash
git add Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs
git commit -m "Warn when a ramp is forced steeper than sprintable by a small lower roof"
```

---

## Task 3: New eastern building cluster (5 roofs, indices 26-30)

**Design:** a compact eastern zone hanging off the existing east edge (E2/N1EE at x=26), on the same 13m grid so Jump gaps are ~5m and makeable. It adds a 4-way pier hub (`East_Pier`, 26), two flanking piers (27, 28), a tall landmark reached by ramps (`East_High`, 29), and a swing-only annex (`East_Annex`, 30) that demonstrates the Task-1 pivot fix across a genuine 11m chasm. Every new roof has ≥2 in/out routes.

**Reference — existing roofs these link to:** E2(2) `(26,0,3)`, N1EE(6) `(26,13,6)`, N2EE(10) `(26,26,7)`.

**Files:**
- Modify: `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` (`Roofs[]`, `Links[]`, optionally `SpawnRoofIndices`, `CanAnchors`)

- [ ] **Step 1: Append the new roofs to the END of `Roofs[]`** (after index 25, preserving order == index)

```csharp
        // --- East pier zone (26-30): a new cluster off the E2/N1EE east edge, on the 13m grid. ---
        new("East_Pier",  39f,   0f, 4f, 8f, 8f),  // 26 — 4-way hub, 13E of E2
        new("East_PierN", 39f,  13f, 5f, 8f, 8f),  // 27 — 13N of 26, also 13E of N1EE(6)
        new("East_PierS", 39f, -13f, 3f, 8f, 8f),  // 28 — 13S of 26
        new("East_High",  52f,  -6f, 6f, 9f, 9f),  // 29 — tall landmark, ramp-reached from 26 & 28
        new("East_Annex", 39f,  32f, 5f, 8f, 8f),  // 30 — swing-only annex across an 11m N-S chasm from 27
```

- [ ] **Step 2: Append the new links to `Links[]`**

```csharp
        // --- East pier zone links (roofs 26-30) ---
        new(2, 26, LinkKind.Jump),    // E2 h3 -> East_Pier h4  (~5m gap, +1)
        new(2, 26, LinkKind.Ramp),    // walking route parallel to the jump
        new(26, 27, LinkKind.Jump),   // Pier h4 -> PierN h5    (+1)
        new(6, 27, LinkKind.Jump),    // N1EE h6 -> PierN h5     (-1, 13E)
        new(26, 28, LinkKind.Jump),   // Pier h4 -> PierS h3     (-1)
        new(26, 29, LinkKind.Ramp),   // Pier h4 -> East_High h6 (+2, ramp)
        new(28, 29, LinkKind.Ramp),   // PierS h3 -> East_High h6 (+3, ramp) — PierS's 2nd route
        new(10, 30, LinkKind.Jump),   // N2EE h7 -> East_Annex h5 (-2, ~13.6m centres) — Annex's 2nd route
        new(27, 30, LinkKind.Swing, param: 5f), // PierN h5 -> East_Annex h5 across the 11m N-S chasm
```

- [ ] **Step 3: (Optional but recommended) add a spawn point and a can anchor on the new zone**

In `SpawnRoofIndices` add `26` (spreads spawns east). In `CanAnchors` add two objective spots offset off the new roof centres, e.g.:

```csharp
        (new Vector3(39f, 4.2f, 2.5f), 1),  // East_Pier
        (new Vector3(52f, 6.2f, -8.5f), 1), // East_High
```

- [ ] **Step 4: Headless build + makeability gate** (this is the real test for hand-placed geometry)

Run the headless build (Task 7 Step 1). Expected in the log:
- `ROOFTOP_BUILD: 31 roofs, <N> links; ...` (roof count 26 → 31).
- **Zero** `ROOFTOP_LINK_SKIPPED` lines. If any Jump 2↔26 / 26↔27 / 6↔27 / 26↔28 / 10↔30 is skipped, the lip gap or rise is over threshold — nudge that roof ≤1-2m closer / level its height and rebuild until clean.
- **Zero** `ROOFTOP_RAMP_STEEP` for 2↔26 / 26↔29 / 28↔29. If 28↔29 warns (rise 3 over a 9-wide roof), widen `East_PierS` to `10f` on X or lower `East_High` to h5.
- **Zero** `ROOFTOP_LINK_REDUNDANT` for the 27→30 swing (the 11m gap must NOT be flat-jumpable). If it warns, push `East_Annex` 1-2m further north.

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs
git commit -m "Add eastern pier building cluster (roofs 26-30) with jump/ramp/swing links"
```

---

## Task 4: More void-escape pipes

**Design:** void pipes are Runner-only escape valves into open air (never bot-graph edges). Add pipes on the new zone's exposed outer faces plus one more on the existing south edge, so cornered runners in the new zone have an escape.

**Files:**
- Modify: `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` (`VoidPipes[]` ~253-265)

- [ ] **Step 1: Append to `VoidPipes[]`**

```csharp
        new(29, new Vector3( 1f, 0f,  0f), -7f), // East_High   east  face (h6) — outer edge of the new zone
        new(29, new Vector3( 0f, 0f, -1f), -7f), // East_High   south face
        new(28, new Vector3( 0f, 0f, -1f), -7f), // East_PierS  south face (h3)
        new(30, new Vector3( 0f, 0f,  1f), -7f), // East_Annex  north face (h5) — escape off the swing annex
```

- [ ] **Step 2: Headless build — confirm no ladder/ramp clip from the new pipes**

Run the headless build. Expected: no new `ROOFTOP_LADDER_RAMP_CLIP` (void pipes are added to the same ladder list `ValidateLadderRampClearance` checks). If a pipe on a face near a ramp warns, move it to a different face of the same roof.

- [ ] **Step 3: Commit**

```bash
git add Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs
git commit -m "Add void-escape pipes on the east zone outer faces"
```

---

## Task 5: More connecting ramps among existing roofs

**Design:** the user wants more walkable ramp routes. Add ramps parallel to existing jumps where the rise is small (≤2m) so `BuildRamp` lays a full 22° grade. Each turns a jump-only hop into a walk-up route.

**Files:**
- Modify: `Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs` (`Links[]`)

- [ ] **Step 1: Append to `Links[]`**

```csharp
        // More walking ramps parallel to existing jumps (small rises only, full 22-degree grade):
        new(1, 5, LinkKind.Ramp),    // E1 h4  -> N1E h5   (+1)
        new(5, 6, LinkKind.Ramp),    // N1E h5 -> N1EE h6  (+1)
        new(13, 25, LinkKind.Ramp),  // E1S h3 -> S2E h4   (+1)
        new(8, 9, LinkKind.Ramp),    // N2 h5  -> N2E h5    (level)
```

- [ ] **Step 2: Headless build — grade + clip gate**

Run the headless build. Expected: zero `ROOFTOP_RAMP_STEEP` and zero new `ROOFTOP_LADDER_RAMP_CLIP` for these four. Any steep warning → drop that ramp (its roof pair is too tight) and note it.

- [ ] **Step 3: Commit**

```bash
git add Assets/Scripts/MapGeometry/Runtime/RooftopArena.cs
git commit -m "Add four walkable ramps parallel to existing jump links"
```

---

## Task 6: Route-coverage PlayMode invariants for the new roofs

**Why:** codify "no new dead-ends" so a future edit can't quietly strand a new roof. Mirror the existing `Links_TowerHasSecondExit` pattern (counts routes off `RooftopArena.Links`, treating `Swing`/`Drop` as one-way per `IsOneWayKind`).

**Files:**
- Modify: `Assets/Tests/PlayMode/RooftopGraphTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public void Links_EveryEastZoneRoofHasTwoRoutesEachWay()
{
    // New roofs 26-30 must each have >=2 inbound and >=2 outbound routes (one-way Swing/Drop
    // counted only in their declared direction), so none is a soft dead-end.
    for (int roof = 26; roof <= 30; roof++)
    {
        int outbound = 0, inbound = 0;
        foreach (RooftopArena.Link l in RooftopArena.Links)
        {
            bool oneWay = l.Kind is RooftopArena.LinkKind.Swing or RooftopArena.LinkKind.Drop;
            if (l.From == roof) { outbound++; if (!oneWay) inbound++; }
            if (l.To == roof)   { inbound++;  if (!oneWay) outbound++; }
        }
        Assert.GreaterOrEqual(outbound, 2, $"roof {roof} ({RooftopArena.Roofs[roof].Name}) needs >=2 ways out");
        Assert.GreaterOrEqual(inbound, 2, $"roof {roof} ({RooftopArena.Roofs[roof].Name}) needs >=2 ways in");
    }
}

[Test]
public void Graph_EastAnnexSwingEdgeIsOneDirectional()
{
    // The 27->30 swing must be traversable 27->30 via a Swing edge, and 30->27 must NOT use a Swing.
    ParkourGraph g = RooftopGraphBuilder.Build(_config);
    Assert.IsTrue(HasEdgeOfType(g, 27, 30, ParkourEdgeType.Swing), "27->30 should have a Swing edge");
    Assert.IsFalse(HasEdgeOfType(g, 30, 27, ParkourEdgeType.Swing), "30->27 must not use the Swing (one-way)");
}
```

> If `HasEdgeOfType(graph, fromRoof, toRoof, type)` / `_config` helpers don't already exist in this test file, reuse the exact pattern from the existing `Graph_SwingEdgeIsOneDirectional` test (it already resolves roof nodes and scans edges) rather than inventing new helpers.

- [ ] **Step 2: Run to verify it passes** (the geometry from Tasks 1 & 3 should already satisfy it)

Run: `... -runTests -testPlatform PlayMode -testFilter "Links_EveryEastZoneRoofHasTwoRoutesEachWay|Graph_EastAnnexSwingEdgeIsOneDirectional" -logFile -`
Expected: PASS. If `Links_EveryEastZoneRoofHasTwoRoutesEachWay` fails, a new roof is under-connected — return to Task 3 and add a link.

- [ ] **Step 3: Commit**

```bash
git add Assets/Tests/PlayMode/RooftopGraphTests.cs
git commit -m "Assert east-zone roofs have >=2 routes and the annex swing is one-way"
```

---

## Task 7: Full verification loop (build → scenes → tests → self-play)

**Files:** none (verification only).

- [ ] **Step 1: Headless arena build — read every invariant log**

Run: `& "C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe" -batchmode -quit -projectPath "C:\Users\jaaaa\RooftopTag" -executeMethod PlaygroundBuilder.BuildRooftopArena -logFile -`
(Use the same `-executeMethod` the repo already uses to build/save the rooftop scene — confirm the exact method name in `PlaygroundBuilder`; it logs `ROOFTOP_ARENA_BUILD_OK`.)
Expected in the log: `ROOFTOP_BUILD: 31 roofs, <N> links`; **zero** each of `ROOFTOP_LINK_SKIPPED`, `ROOFTOP_RAMP_STEEP`, `ROOFTOP_LADDER_RAMP_CLIP`, `ROOFTOP_LINK_REDUNDANT`; `ROOFTOP_ARENA_BUILD_OK`. Any nonzero → fix the offending roof/link per Tasks 3-5 and rebuild before continuing.

- [ ] **Step 2: Regenerate BOTH saved scenes** (RooftopArena + TagArena consume the same tables)

Run the repo's scene-build entry points for both scenes (the `*_BUILD_OK` loggers). Confirm both save without errors so the saved-scene path matches the new tables.

- [ ] **Step 3: Full PlayMode suite**

Run: `& "C:\Program Files\Unity\Hub\Editor\6000.5.3f1\Editor\Unity.exe" -batchmode -projectPath "C:\Users\jaaaa\RooftopTag" -runTests -testPlatform PlayMode -logFile -`
Expected: all pass. Specifically confirm `Graph_EmitsAllNewEdgeTypes`, `Graph_HasDenseNodesPerRoof` (now `>= 31*5 = 155` nodes), the two new Task-6 tests, and — note — the previously-flaky `MovementMetricsTests.Swing_EnergyCapBoundsSwingHeight`. If that swing test fails again here, re-run it in isolation twice to confirm flakiness before treating it as a regression (it has no code path to this map change).

- [ ] **Step 4: Self-play batch (bot traversal of the new geometry)**

Run: `Tools/selfplay.sh` (10 matches; the repo's headless self-play harness).
Expected: `total_edge_usage` shows `Swing`, `Ramp`, `Drop`, `Ladder`, `Climb`, `Vault`, `Jump` all `> 0` (the new swing/ramps are actually traversed); `total_fallen` not worse than baseline (~0); `total_stuck` not materially worse than the documented baseline (~16). If bots never traverse `East_Annex`/`East_High`, or a new roof drives up `stuck`, note it — the cluster may need a friendlier route (e.g. a ladder instead of the steep ramp).

- [ ] **Step 5: Update the tuning log and commit**

Append a `TUNING_LOG.md` entry: the change (roofs 26→31, link count delta, new swing/ramps/pipes), hypothesis, and the Step-1/3/4 metric outcomes (roof/link counts, PlayMode X/Y, self-play edge usage). Then:

```bash
git add TUNING_LOG.md
git commit -m "Log rooftop arena expansion metrics (31 roofs, east pier zone)"
```

---

## Self-Review notes

- **Spec coverage:** more buildings (Task 3), more ramps (Tasks 3 & 5), more swings placed sensibly (Tasks 1 & 3), more pipes (Task 4), ramps-all-connect guardrail (Task 2), invariants stay green (Tasks 4-7). All covered.
- **Index stability:** new roofs appended at the end (26-30); no existing link's `From`/`To` shifts.
- **The swing fix is load-bearing and comes first** — Task 3's swing and any future swing depend on `SwingPivot`; without Task 1 they'd inherit the old `(-37.5, 9, -9)` and land in the wrong place.
- **Hand-placed coordinates are gated, not trusted:** every coordinate table (Tasks 3-5) is followed by a headless-build validation step whose pass condition is explicit log output, with concrete adjustment guidance if a gap/grade misses — the CLAUDE.md build→measure→improve loop.
- **Out of scope (separate follow-ups, not this plan):** loosening the `TrashCanPlacement` `canClearRadius`/`canMinSpacing` (the 16.7% `TRASHCAN_PLACE_TIGHT` rate from the bins work); confirming the flaky swing-energy test.
