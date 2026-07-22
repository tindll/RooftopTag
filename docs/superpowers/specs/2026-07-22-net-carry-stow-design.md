# Net carry & stow presentation — design

Date: 2026-07-22
Status: **SUPERSEDED** by `2026-07-22-net-carry-stow-rigging-design.md`

> Superseded before implementation. This version hand-rolls what Unity's first-party
> `com.unity.animation.rigging` package provides (`MultiParentConstraint` for the holster,
> `TwoBoneIKConstraint` for the grips). The package was installed and the design reworked onto it.
> Kept for the reasoning trail — the requirements, keypose intent, and the stiffness risk it
> identifies all carry forward unchanged.

## Problem

The tagger always carries a bug net, but the animation never accounts for it:

- **Idle / walk / run** — the net floats near the right hand; the pole handle is not actually gripped. The Mixamo locomotion clips pose the hands as if empty, and `NetThrower.LateUpdate` forces the net's *world* rotation every frame regardless of where the hand ended up, so hand and handle drift apart.
- **Wall climb / ladder / swing / wall-hook / mantle / vault** — the net stays mounted on the hand that is supposedly gripping the wall, so it hangs in front of the character.
- **Dive roll / catch roll / front flip** — same problem, plus there is no beat where the tagger puts the net away before committing to the roll.

## Goals

1. Grounded locomotion reads as **two hands on the pole** (pest-control "ready carry").
2. Hands-busy states carry the net **on the back**, diagonally, and it stays glued to the torso even while the body tumbles.
3. A visible ~0.2s **stow gesture** (right arm sweeps the net over the shoulder) and its mirror on redraw.
4. Presentation only. No gameplay timing changes: a roll, climb, or throw never waits for the net.

## Non-goals

- No new animation clips, no Mixamo downloads, no `CharacterAnimator.controller` rebuild.
- Runners (raccoons) are untouched — they never carry a net.
- The thrown-net projectile, trap dome, and net gameplay rules are untouched.

## Approach

Extend the existing procedural upper-body system in
`Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` rather than adding an animator layer.
The bridge already owns exactly the machinery this needs: a humanoid bone cache, per-frame
`LateUpdate` posing over the Animator's output, and the `AimSegment` / `StabilizeBendPlane` /
`OrientHand` helpers that were written for the net-throw swing. A humanoid Animator ignores generic
transform curves on mapped human bones, which is why the throw is procedural in the first place;
the same constraint applies to a carry pose, so the same solution applies.

### 1. One pose axis

Today `ApplySwingPose(arc, authority)` interpolates three keyposes along `arc ∈ [0..2]`
(READY → LOAD → SCOOP). Extend that axis backwards with two new keyposes:

| arc | keypose | meaning |
|-----|---------|---------|
| -2  | STOW    | right arm swept up over the right shoulder, left arm released |
| -1  | CARRY   | both hands on the pole across the body, pole up-forward |
|  0  | READY   | existing throw-ready pose |
|  1  | LOAD    | existing over-the-shoulder load |
|  2  | SCOOP   | existing whip-down |

One scalar drives everything. Carry sits at `arc = -1`; the throw runs `-1 → 0 → 1 → 2` and recoils
back to `-1` instead of fading authority to zero, so carry → windup → carry is continuous with no
pop and no special-case blending between two independent systems.

`CARRY`'s direction vectors start as a copy of `READY`'s and are then tuned independently (pole more
upright, hands nearer hip height, less forward lean) — `READY` was authored as a transition pose and
will read wrong as a permanent stance.

### 2. Carry authority

Carry pose applies at `CarryAuthority` (a tuning constant, starting around 0.85, **not** 1.0) so some
of the underlying locomotion clip's shoulder motion bleeds through and the upper body does not read
as frozen while running. The throw keeps full authority — it already ramps in over
`ThrowBlendInFrac`.

The arm must keep authority *through* the stow sweep and only hand the arms back to the climb/roll
clip once the net has arrived on the back. So authority does not track `stowBlend` linearly:

```
poseAuthority = CarryAuthority × releaseCurve(stowBlend)
releaseCurve(b) = 1                        for b ≤ GestureFrac (0.7)
                = 1 − (b − 0.7) / 0.3      for b > GestureFrac
```

The sweep plays at full carry authority over the first 70% of the blend; the last 30% eases the arms
back to whatever the underlying clip is doing. Drawing runs the same curve in reverse.

Left-hand grip weight = `clamp01(arc + 2)` — zero at STOW (hand released), full from CARRY onward.
This is what makes the stow gesture read as one arm putting the net away.

### 3. Carry state machine

New in the bridge:

```
enum NetCarry { InHands, Stowing, OnBack, Drawing }
```

Holster condition:

```
MotorState ∈ { Mantling, Vaulting, Climbing, OnLadder, OnSwing, WallHook }  ||  _diving  ||  _flipping
```

`_diving` already covers the lunge roll, the tagger's finishing catch, and the cosmetic hard-landing
roll; `_flipping` covers the double-jump front flip. Plain airborne (jump/fall) deliberately does
**not** stow — bunny-hopping would flip the net between hand and back constantly.

Transitions: condition true → `Stowing` over `StowSeconds` (0.2) → `OnBack`; condition false →
`Drawing` over the same duration → `InHands`. The blend is a single 0→1 scalar `stowBlend` that runs
backwards on reversal, so a condition that flips mid-blend produces no pop.

`arc` while not throwing = `Mathf.Lerp(-1f, -2f, Mathf.Clamp01(stowBlend / GestureFrac))` — the arm
sweeps from CARRY to STOW over the first 70% of the blend, reaching the over-the-shoulder pose exactly
as the net arrives on the back, and is then released by the authority curve above rather than being
animated back down.

The state machine is purely cosmetic: nothing in `CharacterMotor` or `TagAgent` consults it. The roll
starts on the same frame it does today; the stow plays over the roll's opening frames.

### 4. Back socket from a torso frame

The back mount cannot use bone-local axes — this auto-rig's bone axes are arbitrary (the existing
swing code notes this and works entirely in the agent's frame for that reason). But the agent
transform does not tumble during a dive roll (root motion is off; the motor owns the transform), so a
socket defined in agent space would stay upright while the body rolls.

Instead, derive an orthonormal **torso frame** from bone *positions*:

```
up      = normalize(chest.position - hips.position)
right   = normalize(rightUpperArm.position - leftUpperArm.position)   // orthogonalized against up
forward = cross(right, up)
```

The socket pose is `chest.position + torsoFrame * BackSocketOffset` with rotation
`torsoFrame * BackPoleOrientation`, tuned so the pole lies diagonally across the back — grip near the
left hip, hoop up behind the right shoulder. This frame rolls, pitches, and yaws with the actual
skinned torso.

The bridge exposes:

```csharp
public bool TryGetNetMount(out Vector3 position, out Quaternion rotation, out float stowBlend);
public void SetNetCarried(bool carried);
```

`SetNetCarried` is pushed each frame by `NetThrower` (same pattern as the existing `SetEating`), so
`Game.Movement` stays free of any `Game.Rules` dependency. When no net is carried, the carry pose is
skipped entirely and the arms are left to the locomotion clips — this is what keeps runners and
un-netted taggers unaffected.

### 5. Prop mounting

In `Assets/Scripts/Rules/Runtime/NetThrower.cs`:

- **Delete** the current `LateUpdate` world-rotation re-assert (lines ~416–427). It is the direct
  cause of the handle-not-in-hand problem: it overwrites whatever the hand did with an agent-space
  orientation. With the carry pose orienting the *hand* so its local +Y is the pole axis (the
  contract `OrientHand` and `NetVisual.BuildNet` already share), an identity local rotation under the
  hand bone is correct by construction.
- The net stays parented to the right hand bone in every state. Parenting is never churned; it also
  preserves the ~1.74× hand-bone `lossyScale` that `SpawnProjectile` copies.
- New `LateUpdate` behaviour:
  - `stowBlend == 0` → leave local position/rotation at identity. No per-frame work.
  - `stowBlend > 0` → write the net's **world** pose as
    `Lerp(handPose, socketPose, stowBlend)` (position lerp, rotation slerp).
- `NetThrower` re-resolves the bridge lazily via `GetComponent<CharacterAnimatorBridge>()`, because
  `TagAgent.SwapModel` destroys and recreates the bridge — the same reason the hand bone is already
  re-resolved lazily in `UpdateCarriedNet`.
- The existing no-hand-bone fallback (`_carryParent == transform`) keeps its current shoulder offset
  and skips all of the above; without bones there is no pose to drive.

### 6. Script execution order

The blend in `NetThrower.LateUpdate` reads the hand bone *after* the bridge has posed it. Ordering
between two `MonoBehaviour`s on the same GameObject is undefined, so annotate `NetThrower` with
`[DefaultExecutionOrder(100)]` to force it after the bridge. This also fixes the same latent ordering
hazard in today's code.

### 7. Throw from a stowed net

`CanThrow` permits a throw while climbing or mid-air, so the net may be on the back when
`BeginThrow` fires. On `BeginThrow`, force the carry state to `Drawing` with a shortened duration
(~0.1s) folded into the start of the windup, so the net is in hand well before the load pose. No
gameplay timing changes — the windup length is unchanged.

## Files touched

| File | Change |
|------|--------|
| `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` | carry state machine, CARRY/STOW keyposes, extended `arc` axis, torso frame + `TryGetNetMount`, `SetNetCarried` |
| `Assets/Scripts/Rules/Runtime/NetThrower.cs` | delete forced-rotation `LateUpdate`, add blended mount, push `SetNetCarried`, `[DefaultExecutionOrder(100)]` |

No changes to `NetVisual`, `BuildCharacterAnimator`, `TagAgent`, the animator controller, or any
imported asset.

## Verification

No automated test covers procedural pose output, and none is added — this is presentation whose
correctness is visual. Verification is a play-mode pass in the Unity editor, as a tagger:

1. **Idle / walk / run** — both hands are on the pole, the handle is inside the right fist, the hoop
   does not clip the body, and the upper body still shows some motion (carry authority < 1).
2. **Wall climb, ladder, swing, wall hook, mantle, vault** — the net is diagonal across the back and
   nothing floats in front; the stow gesture is visible on entry and the redraw on exit.
3. **Lunge / dive roll** — the net stows as the roll begins, stays glued to the tumbling torso
   through the roll, and redraws on recovery. The roll's timing is unchanged.
4. **Double-jump front flip** — same as the roll.
5. **Throw** — carry → windup → scoop → carry runs continuously with no pop at either seam; a throw
   started while climbing draws the net before the load pose.
6. **Runner role and role swap** — raccoons show no pose change; swapping a tagger's model mid-round
   leaves the net correctly mounted on the new rig.

Existing PlayMode tests must still pass; all new code sits behind the presentation paths already
gated by `HasGraphics`, so headless runs are unaffected.

## Tuning knobs

`CarryAuthority`, `StowSeconds`, `BackSocketOffset`, `BackPoleOrientation`, the CARRY and STOW
keypose direction vectors, and the draw-during-windup duration are all constants intended to be
adjusted by eye after the first play-mode pass. The design is expected to need a tuning round; that
is normal for procedural posing on an auto-rig, not a sign the approach is wrong.
