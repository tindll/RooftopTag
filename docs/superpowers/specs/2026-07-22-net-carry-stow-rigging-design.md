# Net carry & stow via Animation Rigging — design

Date: 2026-07-22
Status: awaiting review
Supersedes: `2026-07-22-net-carry-stow-design.md` (hand-rolled procedural version)

## Problem

The tagger always carries a bug net, but no animation accounts for it:

- **Idle / walk / run** — the net floats near the right hand; the pole handle is not gripped. The
  Mixamo locomotion clips pose the hands as if empty, and `NetThrower.LateUpdate` forces the net's
  *world* rotation every frame regardless of where the hand ended up, so hand and handle drift apart.
- **Wall climb / ladder / swing / wall-hook / mantle / vault** — the net stays on the hand that is
  supposedly gripping the wall, so it hangs in front of the character.
- **Dive roll / catch roll / front flip** — same, plus no beat where the tagger puts the net away
  before committing to the roll.

## Goals

1. Grounded locomotion reads as **two hands on the pole** (pest-control "ready carry").
2. Hands-busy states carry the net **diagonally on the back**, glued to the torso even while the body
   tumbles through a roll.
3. A visible ~0.2s **stow gesture** and its mirror on redraw.
4. Presentation only. No gameplay timing changes: a roll, climb, or throw never waits for the net.

## Non-goals

- No new animation clips, no Mixamo downloads, no `CharacterAnimator.controller` rebuild.
- Runners (raccoons) are untouched — they never carry a net.
- The thrown-net projectile, trap dome, and net gameplay rules are untouched.

## Why Animation Rigging

`com.unity.animation.rigging` **1.4.1 is now installed** (added via Package Manager; resolved clean,
no console errors). It is Unity's first-party constraint system and provides, as stock components,
the two things this feature is made of:

- **`MultiParentConstraint`** — blends a transform between multiple parent sources by weight. This is
  the canonical holster/unholster solution and *is* the stow, in one component.
- **`TwoBoneIKConstraint`** — drives a two-bone limb onto a target with a hint for the bend plane.
  This is the canonical "put the hand on the prop", one per arm.

Constraints evaluate inside the Animator's playable graph (Burst-compiled animation jobs), so they
**compose with** the animation instead of overwriting it in `LateUpdate`. Three consequences:

- The script-execution-order hazard between `CharacterAnimatorBridge.LateUpdate` and
  `NetThrower.LateUpdate` stops existing rather than being papered over with
  `[DefaultExecutionOrder]`.
- Constraint evaluation order within a rig is explicit and deterministic, which this design depends on
  (torso before net before hands).
- Roughly 100 lines of hand-written CCD solver (`AimSegment`, `StabilizeBendPlane`, `OrientHand`,
  `ApplySwingPose`) are deleted rather than extended.

## Architecture

### The dependency direction inverts, and that is the point

Today the arms are posed and the net follows the hand. Under this design the **net is placed first
and the hands are IK'd onto it**. That is how a studio rigs a prop, and it makes "handle not in the
hand" structurally impossible: the hand's IK target *is* a point on the net.

### Breaking the circular dependency (load-bearing)

The obvious mounting — hang the net off a socket on the right hand, then IK the hands onto the net —
is **circular**: the net follows the hand, the hand follows the net. Within a frame, constraint
ordering hides it (the net is placed from the *pre-IK* hand, then the hand moves), but next frame the
net reads the *post-IK* hand. That feedback loop drifts and oscillates.

The fix determines the whole layout: **both sockets hang off the torso, never off the hand.** The
net's position depends only on the animated chest; the hands depend on the net; nothing depends on
the hands. One-directional, stable.

This is also better-looking. Someone carrying a pole two-handed holds it steady relative to their
chest — it does not flap around with arm swing, which is what a hand-parented net would do.

### Hierarchy, built at runtime

```
CharacterModel/<rig root, has Animator>          ← RigBuilder goes HERE, not on the agent root
├── RigBuilder
└── NetRig  (Rig, weight 1)
    ├── TorsoOverride   MultiRotationConstraint  → Chest        (throw lean/twist only)
    ├── HeadCounter     MultiRotationConstraint  → Neck         (throw counter-pitch only)
    ├── NetMount        MultiParentConstraint    → NetAnchor
    ├── RightHandIK     TwoBoneIKConstraint      → RightHand
    └── LeftHandIK      TwoBoneIKConstraint      → LeftHand
```

Constraint order is significant and is the order above: torso leans, *then* the net is placed,
*then* the hands solve onto it — so the IK corrects for the torso rather than fighting it.

Transforms created alongside the rig:

| Transform | Parent | Purpose |
|---|---|---|
| `CarrySocket` | Chest bone | Net pose for the two-hand ready carry |
| `BackSocket`  | Chest bone | Net pose lying diagonally across the back |
| `ThrowSocket` | Chest bone | Driven through the throw keyposes each frame |
| `NetAnchor`   | Rig root   | Constrained by `NetMount`; the net parents to this |
| `GripLower`   | `NetAnchor` | Right-hand IK target (at the pole grip) |
| `GripUpper`   | `NetAnchor` | Left-hand IK target, `ThrowGripSeparation` up the pole |
| `ElbowHintL/R` | Chest bone | Bend-plane hints, replacing `StabilizeBendPlane` |

`NetMount` sources are `CarrySocket`, `BackSocket`, and `ThrowSocket`. Two scalars drive all three
weights, and they must always sum to 1:

```
throw  = throwBlend
back   = stowBlend × (1 − throwBlend)
carry  = (1 − stowBlend) × (1 − throwBlend)
```

`throwBlend` therefore overrides the carry/stow split rather than competing with it, which is what
makes "throw from a stowed net" work with no special case.

`GripLower`/`GripUpper` are children of `NetAnchor`, so the grips travel with the net for free — no
per-frame grip math anywhere.

### Carry, stow, and the emergent gesture

State machine (unchanged in intent from the superseded design):

```
enum NetCarry { InHands, Stowing, OnBack, Drawing }
```

Holster condition:

```
MotorState ∈ { Mantling, Vaulting, Climbing, OnLadder, OnSwing, WallHook }  ||  _diving  ||  _flipping
```

`_diving` already covers the lunge roll, the tagger's finishing catch, and the cosmetic hard-landing
roll; `_flipping` covers the double-jump front flip. Plain airborne deliberately does **not** stow —
bunny-hopping would flip the net between hand and back constantly.

`stowBlend` runs 0→1 over `StowSeconds` (0.2) and backwards on reversal, so a condition that flips
mid-blend produces no pop. It drives `NetMount`'s source weights directly: `CarrySocket` gets
`1 − stowBlend`, `BackSocket` gets `stowBlend`.

**The stow gesture is emergent, not authored.** During the stow:

- `LeftHandIK.weight` drops to 0 immediately — the off hand lets go first.
- `RightHandIK.weight` holds at full for the first 70% of the blend, so the right arm is *dragged by
  the net* over the shoulder to the back socket, then eases to 0 over the last 30% and hands the arm
  back to the underlying clip.

The arm carries the net away because it is IK-locked to a net that is moving to the back. No arm
keypose is authored at all. This is strictly simpler than the superseded design's hand-written STOW
keypose, and it stays correct automatically if the back socket is re-tuned.

Carry IK weight caps at `CarryWeight` (a tuning constant starting around 0.9, not 1.0) so a little of
the clip's shoulder motion survives.

### The throw, ported

The throw moves onto the same rig rather than being left as a second posing system — two systems
fighting over the same bones is worse than either alone.

`ThrowSocket` is driven each frame through the existing keypose arc (READY → LOAD → SCOOP) as a
**pose of the net**, and `NetMount` blends its weight in over the windup and out on the recoil. The
hands follow via the same IK that already holds the carry. `TorsoOverride` supplies the existing
pitch/twist values (`ThrowArchBackDeg`, `ThrowPitchFwdDeg`, `ThrowTwistLoadDeg`); `HeadCounter`
supplies the counter-pitch that keeps the eyes on the target. Both sit at weight 0 outside a throw —
the carry does not lean the torso.

The keyposes change representation: today they are *limb direction vectors* in agent space; they
become *net poses* (position + rotation) relative to the chest. This is a re-derivation, not a
translation — the existing vectors are the reference for what the pose should look like, and the new
values are tuned by eye against them. Net poses are considerably easier to author and inspect than
limb directions, because a socket transform can be positioned and viewed directly in the scene.

The phase timing (`ThrowWhipSeconds`, `ThrowRecoilSeconds`, `ThrowBlendInFrac`, and the whip folded
into the end of the windup so the scoop connects at release) is **unchanged** — it is tuned and
correct, and none of it depends on how the pose is produced.

Throwing from a stowed net (legal — `CanThrow` permits a throw while climbing) needs no special case:
the mount blends from `BackSocket` to `ThrowSocket` like any other transition.

### Prop mounting

In `Assets/Scripts/Rules/Runtime/NetThrower.cs`:

- **Delete** the `LateUpdate` world-rotation re-assert (lines ~416–427) — the direct cause of the
  handle-not-in-hand problem.
- The carried net parents to `NetAnchor` with identity local pose, and is never re-parented again.
  All motion comes from the rig.
- `SpawnProjectile` still copies `lossyScale` for the thrown clone; `NetAnchor` must therefore
  inherit the same ~1.74× rig scale the hand bone had, or the constant is re-tuned once.
- `NetThrower` pushes `SetNetCarried(bool)` each frame (same pattern as the existing `SetEating`) and
  otherwise knows nothing about the rig.

### Assembly boundary

All Animation Rigging code lives in **`Game.Movement`**; only
`Assets/Scripts/Movement/Game.Movement.asmdef` gains the `Unity.Animation.Rigging` reference.
`Game.Rules` stays free of the dependency and talks to the rig only through the bridge, matching the
existing `SetEating` / `DriveThrowWindup` pattern.

### Lifecycle

Rig construction mirrors `CharacterRagdoll.Build`: a `NetRigController` component in `Game.Movement`,
built from `CharacterModelAttacher.Attach` once the Animator exists. Because every rig transform is a
child of the model, `TagAgent.SwapModel` destroying and rebuilding the model disposes the rig with it
and the re-entry rebuilds it — no separate teardown path.

Guarded by the existing `graphicsDeviceType != Null` check, like the ragdoll, so headless self-play
does not pay for it.

## Files touched

| File | Change |
|---|---|
| `Assets/Scripts/Movement/Runtime/NetRigController.cs` | **new** — builds the rig, owns the carry state machine, drives weights and `ThrowSocket` |
| `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` | delete `ApplySwingPose` + solver helpers + throw `LateUpdate`; relay throw/carry calls to `NetRigController` |
| `Assets/Scripts/Movement/Runtime/CharacterModelAttacher.cs` | build the rig on the graphics path |
| `Assets/Scripts/Movement/Game.Movement.asmdef` | add `Unity.Animation.Rigging` reference |
| `Assets/Scripts/Rules/Runtime/NetThrower.cs` | delete forced-rotation `LateUpdate`, parent to `NetAnchor`, push `SetNetCarried` |
| `Packages/manifest.json` | already done — `com.unity.animation.rigging@1.4.1` |

`NetVisual`, `BuildCharacterAnimator`, `TagAgent`, the animator controller, and every imported asset
are unchanged.

## Risks

**Runtime rig construction is the main technical risk.** Animation Rigging is normally authored in
the editor; building constraints in code and calling `RigBuilder.Build()` is supported but is the
part most likely to misbehave (constraint data must be fully populated before `Build()`, and the
build must happen after the Animator is live). **This gets a throwaway spike before the real work** —
one constraint, one socket, confirm the hand tracks it on the actual `pest_control` rig. If the spike
fails, the superseded procedural design is a working fallback for the carry, and the throw stays as-is.

**Stiffness is unchanged by any of this.** Both hands locked to a pole over a Mixamo run cycle, with
no torso counter-rotation, risks reading as a mannequin gliding. This is a pose-authoring problem,
not a solver problem, and no package fixes it. Mitigations, in order of cost: lower `CarryWeight`;
add a small procedural bob/sway on `CarrySocket` keyed to the locomotion cycle (cheap here, because
animating the socket moves the hands for free); fall back to a one-hand carry so an arm swings
naturally. Evaluate on the first play-mode pass.

**Deleting a working throw** to re-derive its keyposes in a new representation is real regression
risk. Mitigation: the throw port lands as its own step, after the carry is working and verified, so
the two are never in flight together.

## Verification

No automated test covers procedural pose output and none is added — this is presentation whose
correctness is visual. Existing PlayMode tests must still pass; all new code sits behind the
`HasGraphics` / `graphicsDeviceType` gates already used, so headless runs are unaffected.

Play-mode pass in the editor, as a tagger:

1. **Idle / walk / run** — both hands on the pole, handle inside the fists, no body clipping, upper
   body still shows some motion.
2. **Wall climb, ladder, swing, wall hook, mantle, vault** — net diagonal across the back, nothing in
   front; stow gesture visible on entry, redraw on exit.
3. **Lunge / dive roll** — net stows as the roll begins, stays glued to the tumbling torso, redraws on
   recovery, roll timing unchanged.
4. **Double-jump front flip** — as the roll.
5. **Throw** — carry → windup → scoop → carry with no pop at either seam; the scoop still connects at
   release; a throw started while climbing draws the net first.
6. **Runner role and role swap** — raccoons show no change; swapping a tagger's model mid-round
   rebuilds the rig with the net correctly mounted.
7. **Headless self-play** — runs with no errors and no measurable cost.

## Tuning knobs

`CarryWeight`, `StowSeconds`, the 70/30 gesture split, the `CarrySocket` / `BackSocket` local poses,
the `ThrowSocket` keyposes, and the elbow hint positions are all expected to need a tuning round by
eye after the first play-mode pass. That is normal for prop rigging on an auto-rig, not a sign the
approach is wrong.
