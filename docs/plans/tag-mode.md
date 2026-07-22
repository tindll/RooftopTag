# Tag mode

A second game mode selectable from the main menu. Everything about movement, the map, the bots
and the HUD stays as it is; only the catch mechanic and the win condition change.

| | Pest Control (today) | Tag (new) |
|---|---|---|
| Catch | thrown net, 6 m, windup + flight + trap | touch tag, ~2.2 m, instant |
| On a catch | victim converts to Tagger, tagger stays Tagger (cascade) | victim becomes IT, **tagger becomes Runner** (swap) |
| Local player tagged | round ends, loss screen | player is IT, round continues |
| Models | Runner = raccoon, Tagger = pest_control | everyone pest_control |
| Trash objective | on | off |
| Win | all runners tagged / trash eaten / timer | whoever is IT when the timer expires loses |

Unchanged in both modes: the lunge is still a pure movement dash and tags nobody; the clutch dodge
(i-frames + reactive slow-mo window) still applies; the right-click in-range prompt still lights;
the cooldown rings, minimap, kill cam, countdown, best-of-5 framing and forfeit-on-R all stay.

## Why this shape

The existing hand-tag path is still fully wired and currently unreachable: `TagAgent.PerformTag`
→ `RoundController.TryBeginDodgeWindow` → shrinking per-use window → `ExecuteTag`. The net took
over as the only caller. Tag mode resurrects that path rather than adding a second catch component,
so the dodge window, the tag slow-mo, the boop, `RecordTag` and the kill-cam plumbing are all
reused verbatim.

---

## 1. `Assets/Scripts/Rules/Runtime/GameMode.cs` (new)

```csharp
namespace Game.Rules;

public enum GameMode
{
    PestControl,
    Tag,
}
```

Mirrors `Role.cs` — one enum, no namespace ceremony.

## 2. `TagRulesConfig.cs`

Add a `[Header("Mode")]` block at the top:

- `public GameMode mode = GameMode.PestControl;` — default keeps every existing scene, test and
  the headless self-play harness on today's rules with no edit.
- `public float tagTouchRange = 2.2f;` — horizontal reach for the touch tag. Deliberately well
  under `netThrowRange` (6) and above `tagReachMoving` (1.6): you are meant to touch them, but two
  0.4-radius bodies at sprint need forgiveness or every tag whiffs on latency. The existing
  `tagReachVerticalTolerance` (1.5) still gates height, via `HasTagLineOfSight`.
- `public float tagTouchCooldown = 0.6f;` — rate limiter on the touch tag, replacing
  `netThrowCooldown`'s role. Short: a missed touch should cost tempo, not a turn.

## 3. `TagAgent.cs`

**`ResourceForRole`** — in Tag mode always return `"pest_control"`, so `SetRole`'s `SwapModel`
is a no-op on every role flip and nobody ever turns into a raccoon. Single guard at the top.

**Right-click routing** — `_tagAction.performed` currently calls `Net?.TryThrow()`. Route by mode:
tag mode → `TryTouchTag()`, else the net. Reads the live `_config.mode` at invoke time, so a mode
change between rounds needs no rebind.

**New `public void TryTouchTag()`** — the touch catch. Gates mirror `NetThrower.CanThrow` exactly
(timeScale 0, role != Tagger, in grace, mid-dive, cooldown, countdown active, pre-start-grace),
plus the mode check so it is inert in pest-control mode and a bot can call it every tick. Then:

1. `_roundController.FindNearestOpposingAgent(this)`; reject if null, in grace, beyond
   `tagTouchRange` horizontally, behind us (`dot(forward, toTarget) < 0.3`, same as
   `AcquireTarget`), or failing `HasTagLineOfSight`.
2. Arm `_lungeCooldownRemaining`-style touch cooldown (its own field, so the lunge ring and the
   touch ring stay independent).
3. `_bridge?.TriggerTagSwipe()` for the animation.
4. `PerformTag(target)` — the existing method. It already routes a local-player victim through the
   dodge window (i-frames, then `TryBeginDodgeWindow`) and everyone else straight to `ExecuteTag`.

**New `public bool HasTouchTarget`** — the same gates without committing, for the HUD prompt
(mirrors `NetThrower.HasThrowTarget`).

**New `public float TouchCooldownProgress`** — for the cooldown ring, mirrors
`NetThrower.CooldownProgress`.

**`ExecuteTag`** — mode branch on the conversion:

- Pest control: unchanged (`!other._isLocalPlayer` guard, victim → Tagger, tagger unchanged).
- Tag: `other.SetRole(Role.Tagger, startGrace: true)` **for everyone including the local player**,
  and `this.SetRole(Role.Runner, startGrace: false)` — the swap.

  The new IT's `conversionGrace` (2.5 s) is the no-tag-backs rule and the fresh runner's head
  start, for free: `IsInGrace` blocks their own catch and `FindNearestOpposingAgent`'s callers
  already skip in-grace targets. The ex-tagger gets no grace — with exactly one IT and that IT
  frozen by grace, nothing can reach them anyway, and grace would make them untargetable later.

`WasTagged` still fires, so the "YOU'RE IT" flash and the slow-mo dip both keep working.

## 4. `NetThrower.cs`

One line in `CanThrow`: return false in Tag mode. The component still gets created (no bootstrap
change), but it never throws, `HasThrowTarget` stays false so no prompt draws, `CooldownProgress`
stays 1 so the outer ring self-hides, and `UpdateCarriedNet` drops the handheld net because
`shouldCarry` is gated on the same throw path. No net in anyone's hand, no branch anywhere else.

## 5. `RoundController.cs`

**`AssignRoles`** — Tag mode needs the player inside the kept roster whichever role they picked:

- Player pinned Runner in Tag mode: insert them at index `taggerCount` (the first Runner slot)
  instead of appending at the back. `isTagger = i < taggerCount` then still resolves them to
  Runner, and they sit inside the bench cut.
- Bench rule in Tag mode: `bench = i >= taggerCount + runnerCount` for every non-player agent —
  the same rule the `forceTagger` branch already uses, just applied regardless of the pinned role.
  So exactly `taggerCount + runnerCount` agents are in play.

**`SetupTrashCans`** — early-return in Tag mode after clearing. `_activeCans` stays empty, so the
eat loop, the bin HUD row, `ShouldShowBinRow` and the trash win check all no-op with no further
edits.

**`PlayerCaught`** — return immediately in Tag mode. Being tagged is not a loss; it makes you IT.
This also skips the per-tag kill cam, which must not fire (a replay on every tag would freeze the
round a dozen times).

**Fall consequence** — in Tag mode a fall respawns you at your spawn with your role unchanged, for
bots and the local player alike. Today a fallen Runner converts to Tagger ("the map tagged you"),
which in Tag mode would mint a second IT out of nowhere, and a fallen player loses the round. Both
guards: the `_playerLost = true` at the fall-start branch in `Update` and the local-player branch
in `ApplyFallConsequence`.

**Win check** — in Tag mode:

- Skip the `runnersRemaining == 0` branch (can't happen with a swap, but a taggerCount equal to the
  roster would trip it).
- Timer expiry → `EndRound("Runners win! Time's up.")`. `EndRound`'s existing verdict computation
  (`runnersWon = message.StartsWith("Runners")`, `localWon = runnersWon == (localRole == Runner)`)
  then reads a player who is IT as a loss and a player who is not as a win, with no new plumbing.
  The session tally, the best-of-5 pips and the end screen all follow from that.

**HUD — role chip.** A small always-on capsule under the timer: `YOU'RE IT` in `taggerColor` when
the local player is a Tagger, `RUNNER` in `runnerColor` otherwise. Tag mode only — pest control
already telegraphs the role through the model. Reuses `GameUIStyle.Panel`/`Label`.

**HUD — IT marker.** A screen-projected caret above every Tagger's head, so the one IT is always
findable. `Camera.main.WorldToScreenPoint(agent.position + up * 2.2f)`, skip when `z <= 0`
(behind the camera), draw the existing triangle texture tinted `taggerColor`, fading with distance.
Skipped for the local player (the role chip covers them) and headless. This is the piece the
minimap can't do — the minimap already colours icons by role, but a 1-IT game needs an on-screen
answer to "where is it".

**`DrawThrowPrompt`** — light on `HasTouchTarget` in Tag mode instead of `Net.HasThrowTarget`.
Same RMB glyph, same fade, same anchor.

**`DrawActionCooldownRings`** — the outer ring reads `TouchCooldownProgress` in Tag mode.

## 6. `ParkourBotInput.cs`

In the `Role.Tagger` branch, alongside the existing `_agent.Net?.TryThrow()`, add
`_agent.TryTouchTag()`. Both self-gate on mode and range, so no `if` is needed and the bot's
behaviour in pest-control mode is byte-identical. The lunge still closes the last few metres; the
dive overshoots the touch range and the bot turns back, which is the same close-in behaviour it
already has against the net's 6 m.

## 7. `MainMenuOverlay.cs`

New first row: `Mode` ← `Pest Control` / `Tag`.

In Tag mode the rows become `Mode / Play as / Difficulty / Opponents / Round time`:

- `Play as` relabels its values to `IT` / `Runner`.
- `Opponents` is a new `OpponentCounts = { 1, 3, 5, 7, 11 }` — bots in play.
- `Play()` in Tag mode sets `taggerCount = 1` and `runnerCount = opponents` (total in play =
  `opponents + 1`), matching `AssignRoles`' cap. Starting ITs stays fixed at 1 with no row —
  add one if multi-IT tag is ever wanted.
- Card height gains/loses its row exactly as `_playAsTagger` already does.

## 8. `TagArenaBootstrap.cs`

`public GameMode Mode => _tagConfig.mode;` and `public void ApplyMode(GameMode m) => _tagConfig.mode = m;`
— the same read/apply pair as `ApplyTaggerCount`/`ApplyUnlimitedTime`. No model change here: the
player is still built as `raccon_testing`, and `AssignRoles` → `SetRole` → `SwapModel` moves them
to `pest_control` on the round's first frame in Tag mode.

## 9. Animation — `tagging_animation.fbx`

The FBX has no `.meta` yet, so Unity has not imported it. On import
`CharacterImportPostprocessor` already forces Humanoid and leaves it one-shot (it is not in
`LoopClips`), so no postprocessor edit is needed.

**`CharacterAnimatorBridge.cs`** — a `Tagging` bool held for `TagSwipeHoldSeconds` after
`TriggerTagSwipe()`, exactly the shape `Diving`/`TriggerDiveRoll` already uses, so the locomotion
AnyState can't snatch the swipe mid-play.

**`BuildCharacterAnimator.cs`** — a `Tagging` bool parameter, a `TagSwipe` state on
`Clip("tagging_animation")`, and an AnyState transition `If Tagging` with a short duration and
`canTransitionToSelf = false`. Add `IfNot Tagging` to the grounded/airborne/`Any()` transitions so
the swipe survives the same way the dive does.

Playback speed is left at 1 with no trim: the clip's frame ranges are unmeasured, and every other
trim in this project (`SlideFirst/Last`, `DiveFirst/Last`, `CatchFirst/Last`) was derived from a
real per-frame `CharacterPreviewShot` measurement. Trimming blind would be a guess. If the swipe
reads slow or opens on a dead standing windup, measure it the same way and add the constants then.
`TagSwipeHoldSeconds` is the knob in the meantime.

## 10. Test — `Assets/Tests/PlayMode/TagRulesTests.cs`

One test, following the file's existing `CreateTagAgent` + ground harness:

`TouchTag_InTagMode_SwapsRoles` — two agents 1.5 m apart facing each other, `mode = Tag`,
one Tagger one Runner, wait out the spawn swallow, `taggerAgent.TryTouchTag()`, then assert the
victim is `Tagger` and in grace **and the original tagger is now `Runner`**. That is the one piece
of logic that is genuinely new and would silently half-work (victim converts, tagger doesn't
release) if it broke.

The existing tests all construct a default `TagRulesConfig`, which is `PestControl`, so none of
them change behaviour.

## Risks / open

- **Touch range feel.** 2.2 m is a starting number, not a measured one. It is a config field, so
  tune it in play. Too low and sprint-vs-sprint tags never land; too high and it reads as the net
  without the net.
- **Bot dive overshoot.** Bots lunge from `lungeRange` 4.5 m and the dive carries ~7.2 m, so a bot
  diving at a 2.2 m touch target flies past. Behaviour is unchanged from today (the net has the
  same geometry), but it will be more visible when the catch needs contact. If it reads badly, the
  fix is a mode-aware `lungeRange`, deliberately not done up front.
- **Clip quality unknown.** Not imported yet, so the swipe's read is unverified until Unity picks
  it up and the animator is rebuilt (`Tools/RooftopTag/Build Character Animator`).
