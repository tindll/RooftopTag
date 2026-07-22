# Net Carry & Stow via Animation Rigging — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The pest-control tagger carries his net two-handed during locomotion and wears it diagonally on his back whenever his hands are busy (climbing, rolling, flipping), with a visible stow/redraw.

**Architecture:** A runtime-built Animation Rigging rig. A `MultiParentConstraint` blends the net between chest-mounted carry / back / throw sockets; `TwoBoneIKConstraint`s pull each hand onto grip points that are children of the net. The net is placed first and the hands follow it — the inverse of today's arms-first posing. Both mount sockets hang off the **chest, never the hand**, which is what prevents a net-follows-hand / hand-follows-net feedback loop.

**Tech Stack:** Unity 6000.5.3f1, URP 17.5, `com.unity.animation.rigging` 1.4.1 (already installed), NUnit PlayMode tests.

## Global Constraints

- Unity `6000.5.3f1`; Animation Rigging `1.4.1`.
- **Presentation only.** No gameplay timing changes. A roll, climb, or throw never waits for the net.
- Throw phase timing is **unchanged**: `ThrowWhipSeconds` 0.12, `ThrowRecoilSeconds` 0.3, `ThrowBlendInFrac` 0.3, and the whip folded into the end of the windup so the scoop connects at release.
- All Animation Rigging code lives in `Game.Movement`. `Game.Rules` never references `Unity.Animation.Rigging`.
- Everything rig-related is gated behind the existing graphics check (`SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null`), like `CharacterRagdoll`. Headless self-play must not pay for it.
- Runners (raccoons) unaffected. `NetVisual`, `BuildCharacterAnimator`, `TagAgent`, the animator controller, and all imported assets are untouched.
- Holster condition, verbatim: `MotorState ∈ { Mantling, Vaulting, Climbing, OnLadder, OnSwing, WallHook } || _diving || _flipping`. Plain airborne does **not** stow.
- `StowSeconds` = 0.2. Gesture split = 70/30. `CarryWeight` ≈ 0.9.
- Mount weights always sum to 1: `throw = throwBlend`, `back = stowBlend × (1 − throwBlend)`, `carry = (1 − stowBlend) × (1 − throwBlend)`.

## Animation Rigging API gotcha (applies to every task)

Constraint `.data` is a **struct property**. Mutating it in place silently does nothing. Always copy out, modify, assign back:

```csharp
var d = constraint.data;
d.someField = value;
constraint.data = d;      // REQUIRED — without this the change is lost
```

Same for `WeightedTransformArray`:

```csharp
var d = mount.data;
var arr = d.sourceObjects;
arr.SetWeight(0, carryW);
d.sourceObjects = arr;
mount.data = d;
```

## Spike findings (Task 1, completed — read before Task 2)

The gate passed: a `Rig` + `TwoBoneIKConstraint` built entirely in code, followed by
`RigBuilder.Build()` at runtime, pins the hand to its target at **0.0000 m** on the live rig. Two
findings from the spike change the code below:

1. **Bone local space is scaled ~167×** on the `pest_control` rig (`chest.lossyScale ≈ 167.7`, while
   the agent root is 1.0). A `localPosition` of `0.35` lands the socket **65 m** away. Socket offsets
   must therefore be authored in **agent-space metres** and assigned as a **world** position/rotation
   once at build time; Unity back-computes the local values and the socket then rides the chest
   normally. `MakeSocket` below does this — do not "simplify" it back to `localPosition`.
2. **Constraint weights are synced into the animation job by `RigBuilder`'s own per-frame update.**
   Setting `ik.weight` in `Tick()` each frame is correct and works in normal play. What does *not*
   work is changing a weight and then forcing evaluation via a manual `animator.Update()` — that
   bypasses the sync and the job keeps its previous weight. Only relevant to test harnesses, but it
   invalidates any verification written that way.

**RESOLVED in Task 2 — no action needed.** Measured live: `NetAnchor.lossyScale = 1.738` and the
carried net's `lossyScale = 1.738`, which is exactly the ~1.74 `NetVisual` is tuned for. Parenting
under the rig root happens to inherit the same scale the hand bone gave it, so no compensation is
required and `SpawnProjectile`'s `lossyScale` copy keeps working unchanged. Socket placement verified
at the same time: `anchor-to-chest = 0.320 m`, matching the authored agent-space offset exactly.
Original question, kept for context: bone `lossyScale` differs per model
(`pest_control`/`Spine01` measured 167×, while `NetThrower`'s comment records the hand bone at ~1.74×
on the Mixamo rig). `NetAnchor` parents under the rig root, not the hand, so it inherits the
CharacterModel scale rather than the bone scale — the net may render at the wrong size. Check the
net's on-screen size in Task 2 Step 7 and, if wrong, set `NetAnchor.localScale` to compensate and
record the measured value. `NetThrower.SpawnProjectile` copies `lossyScale`, so the thrown clone
follows whatever is fixed here.

## File Structure

| File | Responsibility |
|---|---|
| `Assets/Scripts/Movement/Runtime/NetCarryState.cs` | **new** — pure logic: carry state machine + mount weight math. No Unity objects, fully unit-testable. |
| `Assets/Scripts/Movement/Runtime/NetRigController.cs` | **new** — builds the rig, owns sockets, drives constraint weights each frame. |
| `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` | loses ~190 lines of hand-rolled solver; relays throw/carry calls to `NetRigController`. |
| `Assets/Scripts/Movement/Runtime/CharacterModelAttacher.cs` | builds the rig on the graphics path, mirroring the `CharacterRagdoll` block. |
| `Assets/Scripts/Movement/Game.Movement.asmdef` | gains `Unity.Animation.Rigging`. |
| `Assets/Scripts/Rules/Runtime/NetThrower.cs` | deletes the forced-rotation `LateUpdate`; parents the net to `NetAnchor`; pushes `SetNetCarried`. |
| `Assets/Tests/PlayMode/NetCarryStateTests.cs` | **new** — unit tests for the pure logic. |

**On testing, honestly:** the pure state machine and weight math get real TDD (Tasks 3). Rig construction and everything visual cannot be asserted headlessly — the rig only builds when a graphics device exists — so those are covered by the Task 1 spike and the Task 8 manual checklist. No test is written that pretends to check what it cannot see.

---

### Task 1: Runtime rig-construction spike (throwaway)

Validates the one genuinely uncertain thing before any real code: that a rig built **in code** at runtime actually drives bones on the real `pest_control` rig. If this fails, stop and report — the superseded procedural design in `docs/superpowers/specs/2026-07-22-net-carry-stow-design.md` is the fallback.

**Files:**
- Create (then delete): `Assets/Editor/NetRigSpike.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. Deleted at the end of this task. Its only output is a yes/no answer.

- [ ] **Step 1: Add the assembly reference the spike needs**

Edit `Assets/Scripts/Movement/Game.Movement.asmdef` — add `Unity.Animation.Rigging` to `references`:

```json
{
    "name": "Game.Movement",
    "rootNamespace": "Game.Movement",
    "references": [
        "Unity.InputSystem",
        "Unity.Animation.Rigging"
    ],
    "includePlatforms": [],
    "excludePlatforms": [],
    "allowUnsafeCode": false,
    "overrideReferences": false,
    "precompiledReferences": [],
    "autoReferenced": true,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": false
}
```

- [ ] **Step 2: Write the spike**

Create `Assets/Editor/NetRigSpike.cs`:

```csharp
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations.Rigging;

/// THROWAWAY. Validates that a rig built in code at runtime drives bones on the live rig.
/// Delete once answered.
public static class NetRigSpike
{
    [MenuItem("Tools/RooftopTag/SPIKE Net Rig")]
    public static void Run()
    {
        if (!Application.isPlaying) { Debug.LogError("SPIKE: enter Play mode first."); return; }

        Animator animator = null;
        foreach (var a in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
            if (a.isHuman) { animator = a; break; }
        if (animator == null) { Debug.LogError("SPIKE: no humanoid Animator in scene."); return; }

        Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                          ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        Transform upper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        Transform mid   = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        Transform tip   = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (chest == null || upper == null || mid == null || tip == null)
        {
            Debug.LogError("SPIKE: missing bones."); return;
        }

        // Target parked in front of the chest, and a hint out to the character's right.
        var target = new GameObject("SPIKE_Target").transform;
        target.SetParent(chest, false);
        target.localPosition = new Vector3(0.15f, -0.1f, 0.35f);
        var hint = new GameObject("SPIKE_Hint").transform;
        hint.SetParent(chest, false);
        hint.localPosition = new Vector3(0.5f, -0.4f, 0f);

        var rigGO = new GameObject("SPIKE_Rig");
        rigGO.transform.SetParent(animator.transform, false);
        var rig = rigGO.AddComponent<Rig>();
        rig.weight = 1f;

        var ikGO = new GameObject("SPIKE_IK");
        ikGO.transform.SetParent(rigGO.transform, false);
        var ik = ikGO.AddComponent<TwoBoneIKConstraint>();
        var d = ik.data;
        d.root = upper; d.mid = mid; d.tip = tip;
        d.target = target; d.hint = hint;
        d.targetPositionWeight = 1f; d.targetRotationWeight = 1f; d.hintWeight = 1f;
        ik.data = d;                 // struct property — must assign back
        ik.weight = 1f;

        var builder = animator.GetComponent<RigBuilder>() ?? animator.gameObject.AddComponent<RigBuilder>();
        builder.layers.Add(new RigLayer(rig, true));
        bool built = builder.Build();

        Debug.Log($"SPIKE: RigBuilder.Build() returned {built}. " +
                  "PASS = the right hand is pinned in front of the chest and follows it while moving.");
    }
}
```

- [ ] **Step 3: Run the spike**

Enter Play mode in the tag arena scene, then run menu `Tools/RooftopTag/SPIKE Net Rig`.

Expected: console logs `RigBuilder.Build() returned True`, and the character's **right hand is visibly pinned** to a point in front of his chest, tracking it as he moves. Errors mentioning `TransformStreamHandle` or `not found in stream` mean the bone is not animated by this Animator — record the exact message.

- [ ] **Step 4: Record the verdict, then delete the spike**

Report PASS or FAIL with the console output. On FAIL, **stop the plan here** and report; do not continue to Task 2.

On PASS:

```bash
rm Assets/Editor/NetRigSpike.cs Assets/Editor/NetRigSpike.cs.meta
```

- [ ] **Step 5: Commit the asmdef reference**

```bash
git add Assets/Scripts/Movement/Game.Movement.asmdef
git commit -m "build: reference Unity.Animation.Rigging from Game.Movement"
```

---

### Task 2: Net rides a chest-mounted carry socket

First visible result: the net stops floating and rides steady in front of the chest. No IK yet — hands are still empty-looking. This proves the mount and kills the forced-rotation bug.

**Files:**
- Create: `Assets/Scripts/Movement/Runtime/NetRigController.cs`
- Modify: `Assets/Scripts/Movement/Runtime/CharacterModelAttacher.cs` (the graphics-gated block at lines 75-79)
- Modify: `Assets/Scripts/Rules/Runtime/NetThrower.cs` (delete `LateUpdate` ~416-427; retarget `UpdateCarriedNet`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `NetRigController.Build(Animator animator)` — builds sockets, rig, `MultiParentConstraint`.
  - `NetRigController.NetAnchor` → `Transform?` — what the carried net parents to. Null until built.
  - `NetRigController.SetNetCarried(bool carried)` — pushed each frame by `NetThrower`.

- [ ] **Step 1: Write `NetRigController` with mount only**

Create `Assets/Scripts/Movement/Runtime/NetRigController.cs`:

```csharp
#nullable enable

using UnityEngine;
using UnityEngine.Animations.Rigging;

namespace Game.Movement;

/// <summary>
/// Runtime-built Animation Rigging rig that mounts the tagger's net and pulls his hands onto it.
/// Built by <see cref="CharacterModelAttacher"/> on the graphics path only, and rebuilt from scratch
/// on every model swap (a role conversion destroys the whole CharacterModel child, taking the rig
/// with it) — the same lifecycle as <see cref="CharacterRagdoll"/>.
///
/// Both mount sockets hang off the CHEST, never off a hand. Hanging the net off the hand while the
/// hands IK onto the net is circular — the net would read back the previous frame's IK'd hand and
/// drift. Chest-mounted, the net depends only on the animated torso and nothing depends on the hands.
/// </summary>
public sealed class NetRigController : MonoBehaviour
{
    // Local poses relative to the chest bone. Tuning knobs — expect a pass by eye.
    private static readonly Vector3 CarryLocalPos = new(0.10f, -0.05f, 0.30f);
    private static readonly Vector3 CarryLocalEuler = new(-15f, 0f, 20f);
    private static readonly Vector3 BackLocalPos = new(-0.05f, -0.05f, -0.22f);
    private static readonly Vector3 BackLocalEuler = new(0f, 0f, 55f);

    private MultiParentConstraint? _mount;

    /// <summary>Transform the carried net parents to (identity local pose). Null until built.</summary>
    public Transform? NetAnchor { get; private set; }

    /// <summary>True once <see cref="Build"/> has produced a working rig.</summary>
    public bool IsBuilt => NetAnchor != null;

    private bool _carried;

    /// <summary>Pushed each frame by NetThrower — same relay pattern as CharacterAnimatorBridge.SetEating.
    /// Keeps Game.Rules free of any Animation Rigging dependency.</summary>
    public void SetNetCarried(bool carried) => _carried = carried;

    public void Build(Animator animator)
    {
        if (!animator.isHuman) return;

        Transform? chest = animator.GetBoneTransform(HumanBodyBones.Chest)
                           ?? animator.GetBoneTransform(HumanBodyBones.Spine);
        if (chest == null) return;

        Transform agent = animator.transform.root;
        _chest = chest;
        _agent = agent;
        Transform carrySocket = MakeSocket(chest, agent, "CarrySocket", CarryLocalPos, CarryLocalEuler);
        Transform backSocket = MakeSocket(chest, agent, "BackSocket", BackLocalPos, BackLocalEuler);

        var rigGO = new GameObject("NetRig");
        rigGO.transform.SetParent(animator.transform, false);
        var rig = rigGO.AddComponent<Rig>();
        rig.weight = 1f;

        // NetAnchor lives under the rig root so it inherits the model's ~1.8/height scale, exactly as
        // the hand bone did — NetThrower.SpawnProjectile copies lossyScale off the carried net.
        var anchorGO = new GameObject("NetAnchor");
        anchorGO.transform.SetParent(rigGO.transform, false);
        NetAnchor = anchorGO.transform;

        var mountGO = new GameObject("NetMount");
        mountGO.transform.SetParent(rigGO.transform, false);
        _mount = mountGO.AddComponent<MultiParentConstraint>();
        var d = _mount.data;
        d.constrainedObject = NetAnchor;
        var sources = new WeightedTransformArray();
        sources.Add(new WeightedTransform(carrySocket, 1f));
        sources.Add(new WeightedTransform(backSocket, 0f));
        d.sourceObjects = sources;
        d.constrainedPositionXYZ = new Vector3Bool(true, true, true);
        d.constrainedRotationXYZ = new Vector3Bool(true, true, true);
        _mount.data = d;              // struct property — must assign back

        RigBuilder builder = animator.GetComponent<RigBuilder>() ?? animator.gameObject.AddComponent<RigBuilder>();
        builder.layers.Add(new RigLayer(rig, true));
        builder.Build();
    }

    // Offsets are AGENT-SPACE METRES, not bone-local: this rig's bone local space is scaled ~167x, so
    // a localPosition of 0.35 would put the socket 65m away (measured — see the plan's spike findings).
    // Assigning world pose once at build lets Unity back-compute the local values; the socket then
    // rides the chest normally from there.
    private static Transform MakeSocket(Transform parent, Transform agent, string name,
        Vector3 agentSpaceOffset, Vector3 agentSpaceEuler)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = parent.position + agent.TransformDirection(agentSpaceOffset);
        go.transform.rotation = agent.rotation * Quaternion.Euler(agentSpaceEuler);
        return go.transform;
    }
}
```

- [ ] **Step 2: Build the rig from `CharacterModelAttacher`**

In `Assets/Scripts/Movement/Runtime/CharacterModelAttacher.cs`, inside the existing graphics-gated block (currently lines 75-79), add the rig build after the ragdoll:

```csharp
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
        {
            CharacterRagdoll ragdoll = root.GetComponent<CharacterRagdoll>() ?? root.AddComponent<CharacterRagdoll>();
            ragdoll.Build(animator, motor, bridge);

            // Rebuilt per model swap like the ragdoll, but the rig's own GameObjects live under the
            // CharacterModel child, so the swap's Destroy takes them with it — only the controller
            // component on the root is reused.
            NetRigController netRig = root.GetComponent<NetRigController>() ?? root.AddComponent<NetRigController>();
            netRig.Build(animator);
            bridge.ConfigureNetRig(netRig);
        }
```

- [ ] **Step 3: Add the bridge relay**

In `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs`, add a field and setter (near `Configure`):

```csharp
    private NetRigController? _netRig;

    /// <summary>Wired by CharacterModelAttacher on the graphics path. Null headless, and null on the
    /// procedural-capsule fallback — every consumer must null-check.</summary>
    public void ConfigureNetRig(NetRigController netRig) => _netRig = netRig;

    /// <summary>The transform a carried net parents to, or null when there is no rig.</summary>
    public Transform? NetAnchor => _netRig != null ? _netRig.NetAnchor : null;

    /// <summary>Relayed to the rig; see NetRigController.SetNetCarried.</summary>
    public void SetNetCarried(bool carried) => _netRig?.SetNetCarried(carried);
```

- [ ] **Step 4: Delete the forced-rotation hack in `NetThrower`**

In `Assets/Scripts/Rules/Runtime/NetThrower.cs`, delete the entire `LateUpdate` method and its comment block (currently lines ~409-427) — the block beginning `// Parenting under the R_Hand bone with IDENTITY local rotation would leave the net's pole` and ending with the closing brace of `LateUpdate`. This is the direct cause of the handle-not-in-hand problem.

- [ ] **Step 5: Retarget `UpdateCarriedNet` onto the anchor**

In the same file, replace `ResolveHandOrShoulder` and the parenting logic in `UpdateCarriedNet`:

```csharp
    private void UpdateCarriedNet()
    {
        bool shouldCarry = _config.netCarryVisible && HasGraphics
            && _agent.Role == Role.Tagger && !_agent.IsInGrace;

        _agent.SetNetCarried(shouldCarry);

        if (!shouldCarry)
        {
            if (_carriedNet != null) { Destroy(_carriedNet); _carriedNet = null; _carryParent = null; }
            return;
        }

        Transform desiredParent = ResolveNetAnchor();
        if (_carriedNet == null || _carryParent != desiredParent)
        {
            if (_carriedNet != null) Destroy(_carriedNet);
            _carriedNet = NetVisual.BuildNet(desiredParent);
            _carryParent = desiredParent;
            if (desiredParent == transform) // no rig (headless/capsule): shoulder-ish offset, as before
                _carriedNet.transform.localPosition = new Vector3(0.28f, 1.4f, 0.18f);
        }

        _carriedNet.SetActive(_state != ThrowState.Flight);
    }

    // The rig's NetAnchor when there is one; the agent root otherwise. Re-resolved every frame because
    // TagAgent.SwapModel destroys and rebuilds the model (and with it the rig), exactly as the old
    // hand-bone lookup was.
    private Transform ResolveNetAnchor() => _agent.NetAnchor ?? transform;
```

- [ ] **Step 6: Add the `TagAgent` relays**

In `Assets/Scripts/Rules/Runtime/TagAgent.cs`, beside the existing `SetEating` relay (line ~688):

```csharp
    /// <summary>Relays to the CURRENT bridge (live _bridge field, so a model swap is picked up).</summary>
    public void SetNetCarried(bool carried) => _bridge?.SetNetCarried(carried);

    /// <summary>The rig transform a carried net parents to, or null when there is no rig.</summary>
    public Transform? NetAnchor => _bridge != null ? _bridge.NetAnchor : null;
```

- [ ] **Step 7: Verify it compiles and runs**

Run in Unity: refresh and check the console for compile errors. Then enter Play mode as a tagger.

Expected: no compile errors; the net rides **steady in front of the chest**, no longer jittering with arm swing and no longer floating detached. Hands are not yet on it — that is Task 5.

- [ ] **Step 8: Commit**

```bash
git add Assets/Scripts/Movement/Runtime/NetRigController.cs \
        Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs \
        Assets/Scripts/Movement/Runtime/CharacterModelAttacher.cs \
        Assets/Scripts/Rules/Runtime/NetThrower.cs \
        Assets/Scripts/Rules/Runtime/TagAgent.cs
git commit -m "feat(net): mount carried net on a chest socket via MultiParentConstraint"
```

---

### Task 3: Carry state machine and mount weights (pure logic, TDD)

The only part with real logic, so the only part that gets real tests. Kept free of Unity objects so it runs headless.

**Files:**
- Create: `Assets/Scripts/Movement/Runtime/NetCarryState.cs`
- Test: `Assets/Tests/PlayMode/NetCarryStateTests.cs`

**Interfaces:**
- Consumes: `MotorState` (existing enum).
- Produces:
  - `static bool NetCarryState.ShouldHolster(MotorState state, bool diving, bool flipping)`
  - `static float NetCarryState.Advance(float stowBlend, bool holster, float deltaTime, float stowSeconds)`
  - `static (float carry, float back, float throwW) NetCarryState.MountWeights(float stowBlend, float throwBlend)`
  - `static (float left, float right) NetCarryState.HandWeights(float stowBlend, float carryWeight)`

- [ ] **Step 1: Write the failing tests**

Create `Assets/Tests/PlayMode/NetCarryStateTests.cs`:

```csharp
using Game.Movement;
using NUnit.Framework;

public class NetCarryStateTests
{
    [Test]
    public void HandsBusyStatesHolster()
    {
        foreach (MotorState s in new[] { MotorState.Mantling, MotorState.Vaulting, MotorState.Climbing,
                                         MotorState.OnLadder, MotorState.OnSwing, MotorState.WallHook })
            Assert.IsTrue(NetCarryState.ShouldHolster(s, false, false), $"{s} should holster");
    }

    [Test]
    public void GroundedAndAirborneDoNotHolster()
    {
        Assert.IsFalse(NetCarryState.ShouldHolster(MotorState.Grounded, false, false));
        Assert.IsFalse(NetCarryState.ShouldHolster(MotorState.Airborne, false, false),
            "plain airborne must NOT stow — bunny-hopping would flip the net constantly");
    }

    [Test]
    public void DivingOrFlippingHolstersEvenWhenAirborne()
    {
        Assert.IsTrue(NetCarryState.ShouldHolster(MotorState.Airborne, true, false));
        Assert.IsTrue(NetCarryState.ShouldHolster(MotorState.Airborne, false, true));
    }

    [Test]
    public void AdvanceRisesToOneAndClamps()
    {
        float b = NetCarryState.Advance(0f, true, 0.1f, 0.2f);
        Assert.AreEqual(0.5f, b, 1e-4f);
        b = NetCarryState.Advance(b, true, 0.5f, 0.2f);
        Assert.AreEqual(1f, b, 1e-4f, "must clamp at 1");
    }

    [Test]
    public void AdvanceReversesWithoutPop()
    {
        float b = NetCarryState.Advance(0.6f, false, 0.1f, 0.2f);
        Assert.AreEqual(0.1f, b, 1e-4f, "reversal runs the same blend backwards");
        b = NetCarryState.Advance(b, false, 0.5f, 0.2f);
        Assert.AreEqual(0f, b, 1e-4f, "must clamp at 0");
    }

    [Test]
    public void MountWeightsAlwaysSumToOne()
    {
        foreach (float stow in new[] { 0f, 0.3f, 1f })
        foreach (float thr in new[] { 0f, 0.5f, 1f })
        {
            var (c, b, t) = NetCarryState.MountWeights(stow, thr);
            Assert.AreEqual(1f, c + b + t, 1e-4f, $"stow={stow} throw={thr}");
        }
    }

    [Test]
    public void ThrowOverridesCarryAndStow()
    {
        var (c, b, t) = NetCarryState.MountWeights(1f, 1f);
        Assert.AreEqual(1f, t, 1e-4f, "a full throw wins outright — this is what makes throwing from a stowed net work");
        Assert.AreEqual(0f, c, 1e-4f);
        Assert.AreEqual(0f, b, 1e-4f);
    }

    [Test]
    public void LeftHandReleasesImmediatelyRightHandCarriesThenReleases()
    {
        var (l0, r0) = NetCarryState.HandWeights(0f, 0.9f);
        Assert.AreEqual(0.9f, l0, 1e-4f, "both hands grip while carrying");
        Assert.AreEqual(0.9f, r0, 1e-4f);

        var (l1, r1) = NetCarryState.HandWeights(0.35f, 0.9f);
        Assert.AreEqual(0f, l1, 1e-4f, "off hand lets go as soon as the stow starts");
        Assert.AreEqual(0.9f, r1, 1e-4f, "right hand still carries the net over the shoulder");

        var (_, r2) = NetCarryState.HandWeights(0.85f, 0.9f);
        Assert.Less(r2, 0.9f, "right hand eases off over the last 30%");
        Assert.Greater(r2, 0f);

        var (_, r3) = NetCarryState.HandWeights(1f, 0.9f);
        Assert.AreEqual(0f, r3, 1e-4f, "fully stowed — arms belong to the clip again");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the PlayMode test suite in Unity (Window → General → Test Runner → PlayMode → Run All), or via the Unity MCP `run_tests` tool with `test_mode: "playmode"`.

Expected: FAIL to compile with `The name 'NetCarryState' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `Assets/Scripts/Movement/Runtime/NetCarryState.cs`:

```csharp
#nullable enable

using UnityEngine;

namespace Game.Movement;

/// <summary>
/// Pure logic behind the tagger's net carry: when to holster, how the stow blend advances, and how
/// that blend maps onto rig constraint weights. Deliberately free of Unity objects so it is testable
/// headlessly — <see cref="NetRigController"/> owns everything that touches the rig.
/// </summary>
public static class NetCarryState
{
    /// <summary>Fraction of the stow blend spent on the visible gesture. Over the first 70% the right
    /// hand stays locked to the net and is DRAGGED over the shoulder by it; the last 30% eases the arm
    /// back to whatever the underlying clip is doing. The gesture is emergent — no arm pose is authored.</summary>
    public const float GestureFrac = 0.7f;

    /// <summary>True when the hands are busy with the world and the net belongs on the back. Plain
    /// airborne is deliberately excluded: bunny-hopping would flip the net hand-to-back constantly.</summary>
    public static bool ShouldHolster(MotorState state, bool diving, bool flipping) =>
        diving || flipping
        || state is MotorState.Mantling or MotorState.Vaulting or MotorState.Climbing
                 or MotorState.OnLadder or MotorState.OnSwing or MotorState.WallHook;

    /// <summary>Moves the 0..1 stow blend toward its target. Reversal simply runs it backwards, so a
    /// condition that flips mid-blend produces no pop.</summary>
    public static float Advance(float stowBlend, bool holster, float deltaTime, float stowSeconds)
    {
        float step = stowSeconds > 0f ? deltaTime / stowSeconds : 1f;
        return Mathf.Clamp01(stowBlend + (holster ? step : -step));
    }

    /// <summary>MultiParentConstraint source weights, always summing to 1. The throw overrides the
    /// carry/stow split rather than competing with it — which is what lets a throw start from a
    /// stowed net with no special case.</summary>
    public static (float carry, float back, float throwW) MountWeights(float stowBlend, float throwBlend)
    {
        float rest = 1f - throwBlend;
        return ((1f - stowBlend) * rest, stowBlend * rest, throwBlend);
    }

    /// <summary>Hand IK weights across the stow. The off hand lets go at once; the right hand holds
    /// full grip through the gesture and then releases.</summary>
    public static (float left, float right) HandWeights(float stowBlend, float carryWeight)
    {
        if (stowBlend <= 0f) return (carryWeight, carryWeight);
        float release = stowBlend <= GestureFrac
            ? 1f
            : 1f - (stowBlend - GestureFrac) / (1f - GestureFrac);
        return (0f, carryWeight * Mathf.Clamp01(release));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the PlayMode suite again.

Expected: all 8 `NetCarryStateTests` PASS, and no previously-passing test regresses.

- [ ] **Step 5: Commit**

```bash
git add Assets/Scripts/Movement/Runtime/NetCarryState.cs Assets/Tests/PlayMode/NetCarryStateTests.cs
git commit -m "feat(net): carry state machine and mount weight math, with tests"
```

---

### Task 4: Net stows to the back

Wires Task 3's logic into Task 2's mount. First behavioural result: climb a wall or roll, and the net moves to the back.

**Files:**
- Modify: `Assets/Scripts/Movement/Runtime/NetRigController.cs`
- Modify: `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs`

**Interfaces:**
- Consumes: `NetCarryState.ShouldHolster`, `.Advance`, `.MountWeights` (Task 3); `NetRigController.Build` (Task 2).
- Produces: `NetRigController.Tick(MotorState state, bool diving, bool flipping, float deltaTime)` — advances the blend and writes mount weights.

- [ ] **Step 1: Add the tick to `NetRigController`**

Add to `NetRigController` (fields near the top, method after `Build`):

```csharp
    private const float StowSeconds = 0.2f;

    private float _stowBlend;
    private float _throwBlend;

    /// <summary>Driven each frame by CharacterAnimatorBridge. Cosmetic only — nothing in the motor or
    /// the tag rules ever waits on this, so a roll begins on exactly the frame it always did and the
    /// stow simply plays over its opening frames.</summary>
    public void Tick(MotorState state, bool diving, bool flipping, float deltaTime)
    {
        if (_mount == null) return;

        bool holster = !_carried || NetCarryState.ShouldHolster(state, diving, flipping);
        _stowBlend = NetCarryState.Advance(_stowBlend, holster, deltaTime, StowSeconds);

        var (carry, back, _) = NetCarryState.MountWeights(_stowBlend, _throwBlend);
        var d = _mount.data;
        var arr = d.sourceObjects;
        arr.SetWeight(0, carry);
        arr.SetWeight(1, back);
        d.sourceObjects = arr;
        _mount.data = d;              // struct property — must assign back
    }
```

Note `!_carried` folds into the holster condition: an agent that is not currently carrying (runner, or a tagger in grace) parks the net on the back rather than holding it out.

- [ ] **Step 2: Drive it from the bridge**

In `CharacterAnimatorBridge.Update`, after the existing `_animator.SetBool(...)` block, add:

```csharp
        _netRig?.Tick(state, _diving, _flipping, Time.deltaTime);
```

- [ ] **Step 3: Verify in Play mode**

Enter Play mode as a tagger.

Expected: walking, the net sits in front of the chest. Grab a wall / climb a ladder / lunge-roll, and the net **slides to a diagonal across the back** over ~0.2s and returns on exit. Nothing yet holds it with a hand.

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/Movement/Runtime/NetRigController.cs Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs
git commit -m "feat(net): stow the net to a back socket while the hands are busy"
```

---

### Task 5: Hands grip the pole

Adds the IK. This is the task that fixes the original complaint.

**Files:**
- Modify: `Assets/Scripts/Movement/Runtime/NetRigController.cs`

**Interfaces:**
- Consumes: `NetRigController.Build`, `.Tick`.
- Produces: `NetRigController` privately owns `_leftHandIK` / `_rightHandIK`; no new public surface.

- [ ] **Step 1: Add grip targets and IK constraints to `Build`**

Add constants and fields:

```csharp
    // Grip points as children of NetAnchor, so they travel with the net for free — this is what
    // removes all per-frame grip math. Local +Y is the pole axis (how NetVisual.BuildNet mounts).
    private const float GripLowerY = 0f;
    private const float GripUpperY = 0.38f;   // was ThrowGripSeparation — left hand grips above the right
    private static readonly Vector3 ElbowHintLLocal = new(-0.45f, -0.35f, 0.10f);
    private static readonly Vector3 ElbowHintRLocal = new(0.45f, -0.35f, 0.10f);

    private TwoBoneIKConstraint? _leftHandIK;
    private TwoBoneIKConstraint? _rightHandIK;
```

In `Build`, after the mount is created and before `builder.Build()`:

```csharp
        // Grips ride the net, so they are authored along the ANCHOR's own local +Y (the pole axis) —
        // NetAnchor is not a scaled bone, so plain localPosition is correct here.
        var gripLower = new GameObject("GripLower").transform;
        gripLower.SetParent(NetAnchor, false);
        gripLower.localPosition = new Vector3(0f, GripLowerY, 0f);
        var gripUpper = new GameObject("GripUpper").transform;
        gripUpper.SetParent(NetAnchor, false);
        gripUpper.localPosition = new Vector3(0f, GripUpperY, 0f);

        Transform hintL = MakeSocket(chest, agent, "ElbowHintL", ElbowHintLLocal, Vector3.zero);
        Transform hintR = MakeSocket(chest, agent, "ElbowHintR", ElbowHintRLocal, Vector3.zero);

        _rightHandIK = MakeHandIK(rigGO.transform, "RightHandIK",
            animator.GetBoneTransform(HumanBodyBones.RightUpperArm),
            animator.GetBoneTransform(HumanBodyBones.RightLowerArm),
            animator.GetBoneTransform(HumanBodyBones.RightHand), gripLower, hintR);
        _leftHandIK = MakeHandIK(rigGO.transform, "LeftHandIK",
            animator.GetBoneTransform(HumanBodyBones.LeftUpperArm),
            animator.GetBoneTransform(HumanBodyBones.LeftLowerArm),
            animator.GetBoneTransform(HumanBodyBones.LeftHand), gripUpper, hintL);
```

Add the factory:

```csharp
    private static TwoBoneIKConstraint? MakeHandIK(Transform rigRoot, string name,
        Transform? root, Transform? mid, Transform? tip, Transform target, Transform hint)
    {
        if (root == null || mid == null || tip == null) return null;

        var go = new GameObject(name);
        go.transform.SetParent(rigRoot, false);
        var ik = go.AddComponent<TwoBoneIKConstraint>();
        var d = ik.data;
        d.root = root; d.mid = mid; d.tip = tip;
        d.target = target; d.hint = hint;
        d.targetPositionWeight = 1f;
        d.targetRotationWeight = 1f;
        d.hintWeight = 1f;
        ik.data = d;                  // struct property — must assign back
        ik.weight = 0f;               // driven by Tick
        return ik;
    }
```

- [ ] **Step 2: Drive the IK weights in `Tick`**

Add a constant and extend `Tick`'s tail:

```csharp
    // Below 1.0 so a little of the clip's own shoulder motion survives and the upper body doesn't
    // read as a frozen mannequin. Raise toward 1 for a tighter grip, lower for more life.
    private const float CarryWeight = 0.9f;
```

```csharp
        var (leftW, rightW) = NetCarryState.HandWeights(_stowBlend, CarryWeight);
        if (_leftHandIK != null) _leftHandIK.weight = leftW;
        if (_rightHandIK != null) _rightHandIK.weight = rightW;
```

- [ ] **Step 3: Verify in Play mode**

Enter Play mode as a tagger.

Expected: **both hands are on the pole** while idle/walking/running, the handle inside the right fist and the left hand gripping above it. Stow still works. Note by eye whether the run reads stiff — that judgement feeds Task 8.

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/Movement/Runtime/NetRigController.cs
git commit -m "feat(net): IK both hands onto the carried net's grip points"
```

---

### Task 6: Port the throw onto the rig

Removes the second posing system. Lands last among the code tasks so it is never in flight alongside the carry work.

**Files:**
- Modify: `Assets/Scripts/Movement/Runtime/NetRigController.cs`
- Modify: `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` (delete lines ~111-305: the whole procedural throw section)

**Interfaces:**
- Consumes: everything above.
- Produces: `NetRigController.BeginThrow(float windupSeconds)`, `.ReleaseThrow()` — same names and semantics the bridge exposes today, so `TagAgent.DriveThrowWindup` / `DriveThrowRelease` are unchanged.

**Note on a spec refinement:** the spec named `MultiRotationConstraint` for the torso. Use **`OverrideTransform` in `Pose` space** instead — it applies a rotation *additively on top of the animated pose*, which is exactly what a throw lean is. `MultiRotationConstraint` would need a target holding the chest's full desired world rotation, which means either overriding the clip's spine motion entirely or reading the chest back post-graph (circular). `OverrideTransform` avoids both.

- [ ] **Step 1: Add the throw socket and torso constraints to `Build`**

Add fields and keypose constants:

```csharp
    // Throw keyposes as NET poses relative to the chest — the same READY → LOAD → SCOOP arc the old
    // procedural swing described with limb-direction vectors, re-expressed as where the net goes.
    // Hands follow via the existing IK, so no arm pose is authored.
    private static readonly Vector3 ReadyPos = new(0.15f, -0.05f, 0.35f);
    private static readonly Vector3 ReadyEuler = new(-20f, 0f, 15f);
    private static readonly Vector3 LoadPos = new(0.30f, 0.30f, -0.15f);
    private static readonly Vector3 LoadEuler = new(35f, 0f, 35f);
    private static readonly Vector3 ScoopPos = new(-0.05f, -0.30f, 0.55f);
    private static readonly Vector3 ScoopEuler = new(-70f, 0f, -10f);

    // Torso angles per keypose — carried over unchanged from the old procedural swing.
    private const float ThrowArchBackDeg = 14f;
    private const float ThrowPitchFwdDeg = 22f;
    private const float ThrowTwistLoadDeg = 15f;

    private Transform? _throwSocket;
    private OverrideTransform? _torsoLean;
    private OverrideTransform? _headCounter;

    // Cached at Build — the throw socket is driven in world space each frame against these.
    private Transform? _chest;
    private Transform? _agent;
```

In `Build`, add the throw socket as a third mount source and the two torso constraints. The socket is created **before** the mount so it can be added to `sources`:

```csharp
        _throwSocket = MakeSocket(chest, agent, "ThrowSocket", ReadyPos, ReadyEuler);
```

and extend the source list:

```csharp
        sources.Add(new WeightedTransform(_throwSocket, 0f));
```

Then, after the mount and **before** the hand IK (constraint order is torso → net → hands, so the IK corrects for the lean rather than fighting it):

```csharp
        _torsoLean = MakeOverride(rigGO.transform, "TorsoLean", chest);
        _headCounter = MakeOverride(rigGO.transform, "HeadCounter",
            animator.GetBoneTransform(HumanBodyBones.Neck));
```

Add the factory:

```csharp
    // Pose space = the rotation is applied ON TOP OF the animated pose, which is what an additive
    // lean needs. World/Local space would replace the clip's spine motion outright.
    private static OverrideTransform? MakeOverride(Transform rigRoot, string name, Transform? constrained)
    {
        if (constrained == null) return null;

        var go = new GameObject(name);
        go.transform.SetParent(rigRoot, false);
        var ov = go.AddComponent<OverrideTransform>();
        var d = ov.data;
        d.constrainedObject = constrained;
        d.sourceObject = null;
        d.space = OverrideTransformData.Space.Pose;
        d.positionWeight = 0f;        // rotation only
        d.rotationWeight = 1f;
        ov.data = d;                  // struct property — must assign back
        ov.weight = 0f;               // driven by the throw
        return ov;
    }
```

- [ ] **Step 2: Move the throw phase machine into `NetRigController`**

Add, replacing what the bridge used to own. The phase timing constants are copied **verbatim** — they are tuned and must not drift:

```csharp
    private enum ThrowPhase { None, Windup, Hold, Release }

    private const float ThrowWhipSeconds = 0.12f;
    private const float ThrowRecoilSeconds = 0.3f;
    private const float ThrowBlendInFrac = 0.3f;

    private ThrowPhase _throwPhase = ThrowPhase.None;
    private float _throwWindup = 0.45f;
    private float _throwTimer;

    /// <summary>Begin the wind-up: the net travels up over the right shoulder across
    /// <paramref name="windupSeconds"/>, then holds loaded until <see cref="ReleaseThrow"/>.</summary>
    public void BeginThrow(float windupSeconds)
    {
        _throwPhase = ThrowPhase.Windup;
        _throwWindup = Mathf.Max(0.01f, windupSeconds);
        _throwTimer = 0f;
    }

    /// <summary>Release: whip through the scoop, then recoil back into the carry.</summary>
    public void ReleaseThrow()
    {
        if (_throwPhase == ThrowPhase.None) return;
        _throwPhase = ThrowPhase.Release;
        _throwTimer = 0f;
    }

    // Returns arc in [0..2] (ready → load → scoop) and the throw's authority over the carry.
    // The LOAD→SCOOP whip is folded into the END of the windup, NOT the release: NetThrower.Release()
    // fires the instant the windup expires, so starting the whip at release would have the net leave
    // the hand 0.12s before the swing visually threw it.
    private (float arc, float blend) AdvanceThrow(float deltaTime)
    {
        if (_throwPhase == ThrowPhase.None) return (0f, 0f);
        _throwTimer += deltaTime;

        switch (_throwPhase)
        {
            case ThrowPhase.Windup:
            {
                float loadSeconds = Mathf.Max(0.01f, _throwWindup - ThrowWhipSeconds);
                if (_throwTimer <= loadSeconds)
                {
                    float t = Mathf.Clamp01(_throwTimer / loadSeconds);
                    return (1f - (1f - t) * (1f - t), Mathf.Clamp01(t / ThrowBlendInFrac));
                }
                float u = Mathf.Clamp01((_throwTimer - loadSeconds) / ThrowWhipSeconds);
                if (u >= 1f) _throwPhase = ThrowPhase.Hold;
                return (1f + u * u, 1f);
            }
            case ThrowPhase.Hold:
                return (2f, 1f);
            case ThrowPhase.Release when _throwTimer <= ThrowRecoilSeconds:
            {
                float v = _throwTimer / ThrowRecoilSeconds;
                return (2f, 1f - (1f - (1f - v) * (1f - v)));
            }
            default:
                _throwPhase = ThrowPhase.None;
                return (0f, 0f);
        }
    }
```

- [ ] **Step 3: Drive the throw socket and torso from `Tick`**

Insert at the top of `Tick`, before the stow blend is advanced:

```csharp
        var (arc, throwBlend) = AdvanceThrow(deltaTime);
        _throwBlend = throwBlend;

        if (_throwSocket != null && _chest != null && _agent != null)
        {
            float u = Mathf.Clamp01(arc);          // ready → load
            float v = Mathf.Clamp01(arc - 1f);     // load → scoop
            Vector3 offset = Vector3.Lerp(Vector3.Lerp(ReadyPos, LoadPos, u), ScoopPos, v);
            Quaternion rot = Quaternion.Slerp(
                Quaternion.Slerp(Quaternion.Euler(ReadyEuler), Quaternion.Euler(LoadEuler), u),
                Quaternion.Euler(ScoopEuler), v);
            // WORLD-space, for the same reason MakeSocket is: the parent bone is ~167x scaled, so
            // writing agent-space metres into localPosition would throw the socket metres off.
            _throwSocket.position = _chest.position + _agent.TransformDirection(offset);
            _throwSocket.rotation = _agent.rotation * rot;

            float pitch = Mathf.Lerp(Mathf.Lerp(4f, -ThrowArchBackDeg, u), ThrowPitchFwdDeg, v);
            float twist = Mathf.Lerp(Mathf.Lerp(0f, ThrowTwistLoadDeg, u), -4f, v);
            SetOverrideRotation(_torsoLean, new Vector3(pitch, twist, 0f), throwBlend);
            SetOverrideRotation(_headCounter, new Vector3(-pitch * 0.7f, 0f, 0f), throwBlend);
        }
```

and change the mount write to include the throw weight:

```csharp
        var (carry, back, throwW) = NetCarryState.MountWeights(_stowBlend, _throwBlend);
        var d = _mount.data;
        var arr = d.sourceObjects;
        arr.SetWeight(0, carry);
        arr.SetWeight(1, back);
        arr.SetWeight(2, throwW);
        d.sourceObjects = arr;
        _mount.data = d;
```

Both hands must grip through the throw regardless of stow state, so extend the hand-weight line:

```csharp
        var (leftW, rightW) = NetCarryState.HandWeights(_stowBlend, CarryWeight);
        leftW = Mathf.Max(leftW, _throwBlend * CarryWeight);
        rightW = Mathf.Max(rightW, _throwBlend * CarryWeight);
        if (_leftHandIK != null) _leftHandIK.weight = leftW;
        if (_rightHandIK != null) _rightHandIK.weight = rightW;
```

Add the helper:

```csharp
    private static void SetOverrideRotation(OverrideTransform? ov, Vector3 euler, float weight)
    {
        if (ov == null) return;
        var d = ov.data;
        d.rotation = euler;
        ov.data = d;                  // struct property — must assign back
        ov.weight = weight;
    }
```

- [ ] **Step 4: Delete the procedural throw from the bridge**

In `CharacterAnimatorBridge.cs`, delete the entire section from the comment
`// ---------------------------------------------------------------- Net throw (procedural upper-body)`
through the end of the `Rotate` helper — that is the `ThrowPhase` enum, all throw fields and constants, `LateUpdate`, `ApplySwingPose`, `AimSegment`, `StabilizeBendPlane`, `OrientHand`, and `Rotate` (currently lines ~111-305).

Replace `BeginThrow` / `ReleaseThrow` with relays that keep the public signature `TagAgent` already calls:

```csharp
    /// <summary>Begin the net-throw wind-up. Relayed to the rig; see NetRigController.BeginThrow.</summary>
    public void BeginThrow(float windupSeconds) => _netRig?.BeginThrow(windupSeconds);

    /// <summary>Release the net throw. Relayed to the rig; see NetRigController.ReleaseThrow.</summary>
    public void ReleaseThrow() => _netRig?.ReleaseThrow();
```

- [ ] **Step 5: Drop the now-dead rotation reset in `NetThrower.TryThrow`**

In `NetThrower.TryThrow`, delete the comment block and line that reset the carried net's local rotation (currently ~lines 93-97, `if (_carriedNet != null) _carriedNet.transform.localRotation = Quaternion.identity;`). It existed only to undo the deleted `LateUpdate` hack; the net now sits at identity under `NetAnchor` permanently.

- [ ] **Step 6: Verify**

Run the PlayMode suite — expected: all tests still pass, including `NetCarryStateTests`.

Then Play mode as a tagger: right-click to throw. Expected: the net rises over the right shoulder during the windup and whips down through the scoop, the hands stay on the pole throughout, the scoop **connects at release** (the net leaves the hand at the bottom of the swing, not before), and the pose settles back into the carry with no pop.

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/Movement/Runtime/NetRigController.cs \
        Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs \
        Assets/Scripts/Rules/Runtime/NetThrower.cs
git commit -m "refactor(net): port the throw onto the rig, delete the hand-rolled solver"
```

---

### Task 7: Full verification pass and tuning

**Files:**
- Modify (tuning constants only): `Assets/Scripts/Movement/Runtime/NetRigController.cs`

**Interfaces:**
- Consumes: everything. Produces: no new API.

- [ ] **Step 1: Run the automated suite**

Run the full PlayMode suite. Expected: every test passes, with no regression in `TagRulesTests`, `DiveSheetTests`, `DoubleJumpTests`, `SelfPlayTests`.

- [ ] **Step 2: Work the manual checklist**

Play mode as a tagger. Record pass/fail per line — do not summarise as "looks fine":

1. **Idle / walk / run** — both hands on the pole, handle inside the fists, no body clipping, upper body still shows motion.
2. **Climb, ladder, swing, wall hook, mantle, vault** — net diagonal across the back, nothing in front; stow visible on entry, redraw on exit.
3. **Lunge / dive roll** — stows as the roll begins, stays glued to the tumbling torso, redraws on recovery, roll timing unchanged.
4. **Double-jump front flip** — as the roll.
5. **Throw** — carry → windup → scoop → carry, no pop at either seam, scoop connects at release, a throw begun while climbing draws the net first.
6. **Runner role and role swap** — raccoons unchanged; swapping a tagger's model mid-round rebuilds the rig with the net mounted.
7. **Headless self-play** — no errors, no measurable cost.

- [ ] **Step 3: Tune**

Adjust by eye and re-check. Knobs, in the order most likely to be wrong: `CarryLocalPos`/`CarryLocalEuler`, `BackLocalPos`/`BackLocalEuler`, the `Ready`/`Load`/`Scoop` poses, `ElbowHintL/RLocal`, `GripUpperY`, `CarryWeight`, `StowSeconds`.

**If the run reads stiff** (the risk flagged in the spec — no constraint system fixes a pose-authoring problem): lower `CarryWeight` first; then add a small bob/sway on `CarrySocket` keyed to the locomotion cycle, which is cheap here because moving the socket moves the hands for free; then, if still wrong, fall back to a one-hand carry by leaving `_leftHandIK.weight` at 0 during locomotion so an arm swings naturally. Report which was needed.

- [ ] **Step 4: Commit tuning**

```bash
git add Assets/Scripts/Movement/Runtime/NetRigController.cs
git commit -m "tune(net): carry, stow and throw pose constants"
```

---

## Rollback

The carry work (Tasks 2, 4, 5) and the throw port (Task 6) are separate commits. If the throw port regresses and cannot be tuned out, revert that single commit — the carry survives and the old procedural throw returns intact. If the Task 1 spike fails outright, no production code has been written and the superseded procedural spec is the fallback design.
