# Player-as-Tagger Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the player choose to play the round as the Tagger (hunt the bot runners with net + lunge), via a main-menu "Play as: Runner / Tagger" row — plus fix the two net-catch animation bugs and review the flee AI against a human hunter.

**Architecture:** Role assignment, input, win/loss, HUD role-branching, and bot flee AI are already role-generic — `TagRulesConfig.forcePlayerAsTagger` exists and `RoundController.AssignRoles` honors it. The work is: (1) expose it in the menu with a sane runner-count mapping, (2) role-branch the end-screen copy, (3) add a "YOU CAUGHT" kill cam for the player's round-winning catch, (4) fix two diagnosed presentation bugs in the net throw (stale carried-net local rotation; projectile released before the swing whip), (5) a structured playtest of the flee AI with a prepared minimal perception patch we only apply if it fails.

**Tech Stack:** Unity 6000.5.3f1, URP, OnGUI menus, Unity Test Framework (PlayMode), `Tools/selfplay.sh` for headless.

## Global Constraints

- Never hand-edit `Assets/Scenes/RooftopArena.unity` — it is regenerated via **RooftopTag → Build Rooftop Arena**. **This plan requires NO scene changes**: every touched component is attached at runtime by `TagArenaBootstrap`, and no serialized scene field changes. If any task finds it needs a scene field, stop and flag it.
- Assembly boundaries: `MainMenuOverlay`/`TagArenaBootstrap` live outside custom asmdefs (Assembly-CSharp, references everything); `RoundController`/`NetThrower`/`NetVisual`/`KillCamPlayback`/`TagAgent` are Game.Rules; `CharacterAnimatorBridge` is Game.Movement (must stay Rules-agnostic); `ParkourBotInput` is Game.AI (already references Game.Rules). **No task in this plan needs a new asmdef reference.**
- Sim/presentation separation (docs/NET_CATCH_SPEC.md): net-throw *outcomes* must stay byte-identical headless. All animation fixes here are presentation-only — `TagRulesConfig.netWindupSeconds`/`netFlightTime`/`netHitRadius`/`netTrapDuration` and the `NetThrower` FixedUpdate state machine are untouched.
- Locked decisions (do not re-litigate): role select is a pre-round menu toggle ("Play as: Runner / Tagger"); tagger win condition mirrors existing rules via `RoundController` as-is ("catch all runners before the timer" = the existing `"Taggers win! All runners tagged."` path, already role-generic at `RoundController.cs:1116`).
- Ponytail discipline: shortest correct diff, reuse existing idioms (`DrawOptionRow`, `ApplyTaggerCount`, `DrawBanner`), mark deliberate simplifications with `// ponytail:` comments.
- Verification loops after any net-feature change (from NET_CATCH_SPEC.md): recompile clean, TagRulesTests green, editor play-mode scenario, self-play tag distribution sane.

## Pre-diagnosed root causes (read-verified 2026-07-22 — re-verify line numbers before editing)

**Bug A — net visual misplaced / misaligned with hands / not following the swing.**

- **A1 (primary, confirmed by reading):** `NetThrower.LateUpdate` (`NetThrower.cs:396-407`) re-asserts the carried net's **world** rotation every frame *while the throw is Idle* (pole upright, ~25° forward, agent-space). Writing world rotation on a child of the animated `R_Hand` bone bakes an **arbitrary, frame-dependent localRotation** under the hand. The moment the throw enters Windup, that override stops (`if (_state != ThrowState.Idle …) return;` at line 399) and the net rides the entire swing with whatever stale local offset the last Idle frame happened to leave. Meanwhile `CharacterAnimatorBridge.OrientHand` (`CharacterAnimatorBridge.cs:287-295`) explicitly poses the hand assuming *"local +Y = pole axis (how NetVisual.BuildNet mounts)"* — i.e. it assumes identity local rotation, which the Idle-time writes have destroyed. Result: during the windup/whip (the only time anyone is looking at the net) it points a semi-random direction relative to the grip. **Fix: restore identity localRotation on the carried net when the throw leaves Idle.** One line.
- **A2 (secondary, confirmed by reading; magnitude to be confirmed in editor):** carried-vs-thrown **scale mismatch**. `NetVisual.BuildNet(handBone)` parents with `SetParent(parent, false)` so the carried net inherits the hand bone's lossyScale — ~1.74× per the tuning comment at `NetVisual.cs:26` (*"judged in-hand at 1.74x character-bone scale"*). The projectile is `BuildNet(null)` (`NetThrower.cs:425-429`) at world scale 1 — so the net visibly shrinks to ~57% size the instant it leaves the hand. **Fix: copy the carried net's lossyScale onto the projectile.** Diagnostic first: log the hand bone's lossyScale once in editor to confirm the 1.74 figure (Task 4 step 1).
- Residual (only if it still reads wrong after A1/A2): the net's grip-point origin sits at the hand *bone pivot* (wrist), not the palm. Do **not** pre-fix; check in the Task 4 editor pass and add a small localPosition offset only if visibly wrong.

**Bug B — swing and catch mistimed (catch registers before/after the visual connects).**

- **B1 (primary, confirmed by reading):** the projectile is released at the **start** of the swing whip, not at its contact point. Timeline as-built: `NetThrower.FixedUpdate` counts down `netWindupSeconds` (0.45s) during which the bridge poses READY→LOAD (arm cocked over the right shoulder, `CharacterAnimatorBridge.BeginThrow`). At zero, `Release()` (`NetThrower.cs:156-173`) captures `_launchPos = HandWorldPos()` (the over-the-shoulder LOAD position), spawns the projectile, starts the 0.45s flight, **and only then** calls `DriveThrowRelease` → `ReleaseThrow`, which *begins* the LOAD→SCOOP whip taking `ThrowWhipSeconds = 0.12s` (`CharacterAnimatorBridge.cs:125`). So for the first ~27% of the flight the arm is still whipping: the net departs from behind the head while the visible "throw" connects 0.12s later. **Fix (presentation-only, inside the bridge): fold the whip into the END of the windup** — READY→LOAD over `windupSeconds - ThrowWhipSeconds`, then LOAD→SCOOP over the final `ThrowWhipSeconds`, holding at the scoop; `ReleaseThrow` becomes recoil-only. The scoop then lands exactly when `Release()` fires, and `_launchPos` automatically captures the hand at the forward-low scoop point, fixing the launch position too. Zero API change, zero sim change.
- **B2 (secondary, needs an editor look — root cause known, severity unknown):** the logical hit tests the victim against the *predicted* `_landPos` within `netHitRadius = 1.1m` (`NetThrower.cs:268-269`), but on a hit the projectile is destroyed and the trap dome spawns at the **victim's actual position** (`NetThrower.cs:279-281`) — up to 1.1m of projectile→dome teleport at the resolve instant. **Diagnostic (Task 4 step 6): watch one bot-vs-bot hit and one player-thrown hit at `Time.timeScale = 0.25`; optionally `Debug.DrawLine(_landPos, victim.transform.position, Color.red, 2f)` in `ResolveHit`.** Recommendation: **accept it** (`// ponytail:` — the hit radius is a gameplay forgiveness knob; the 0.8m dome covers the victim and mostly swallows the gap). Only if it clearly reads as a teleport in the editor pass, apply the minimal cosmetic drift given in Task 4 step 7. Do not "fix" this speculatively.

---

### Task 1: Pin the existing behavior — PlayMode tests for forced-tagger roles and a thrown-net catch

These are characterization tests of code that already exists — they should pass immediately, and they are the smallest set that fails if this feature's foundation breaks. Written first so every later task runs against a guarded core.

**Files:**
- Modify: `Assets/Tests/PlayMode/TagRulesTests.cs` (extend `CreateTagAgent` helper ~line 713; add two tests)

**Interfaces:**
- Consumes: `TagRulesConfig.forcePlayerAsTagger/forcePlayerAsRunner/taggerCount/roundStartGraceDuration`, `RoundController.Configure/RegisterAgent`, `TagAgent.SetRole/Net/Role/IsInGrace`, `NetThrower.TryThrow`.
- Produces: nothing later tasks call — safety net only.

- [ ] **Step 1: Extend the agent helper to accept a per-test config**

The fixture's shared `_tagConfig` (created in `[OneTimeSetUp]`) must not be mutated per-test. Change the helper signature (keep the default so all existing call sites compile unchanged):

```csharp
private (GameObject go, CharacterMotor motor, TagAgent agent, ScriptedCharacterInput input) CreateTagAgent(
    Vector3 position, TagRulesConfig? config = null)
```

and inside the helper use `config ?? _tagConfig` wherever `_tagConfig` was used. Also add `Time.timeScale = 1f;` to `[TearDown] Cleanup()` if not already present (Task 1's local-player registration can arm freeze paths).

- [ ] **Step 2: Write the forced-tagger role-assignment test**

```csharp
[UnityTest]
public IEnumerator AssignRoles_ForcePlayerAsTagger_PlayerIsTaggerAndBotsAreRunners()
{
    _sceneRoot = new GameObject("TestScene");
    CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

    var config = ScriptableObject.CreateInstance<TagRulesConfig>();
    config.forcePlayerAsRunner = false;
    config.forcePlayerAsTagger = true;
    config.taggerCount = 1; // player is the SOLE tagger; both bots must come out Runners

    (_, _, TagAgent player, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f), config);
    (_, _, TagAgent botA, _) = CreateTagAgent(new Vector3(3f, 1.1f, 0f), config);
    (_, _, TagAgent botB, _) = CreateTagAgent(new Vector3(6f, 1.1f, 0f), config);

    var controllerGo = new GameObject("RoundController");
    controllerGo.transform.SetParent(_sceneRoot.transform);
    RoundController controller = controllerGo.AddComponent<RoundController>();
    controller.Configure(config);
    player.SetRoundController(controller);
    botA.SetRoundController(controller);
    botB.SetRoundController(controller);
    controller.RegisterAgent(player, isLocalPlayer: true);
    controller.RegisterAgent(botA, isLocalPlayer: false);
    controller.RegisterAgent(botB, isLocalPlayer: false);

    yield return null; // RoundController.Start() -> StartRound() -> AssignRoles()

    Assert.AreEqual(Role.Tagger, player.Role, "forcePlayerAsTagger must pin the local player to Tagger.");
    Assert.AreEqual(Role.Runner, botA.Role, "With taggerCount=1 every bot must be a Runner (no benching outside chase-me).");
    Assert.AreEqual(Role.Runner, botB.Role, "With taggerCount=1 every bot must be a Runner (no benching outside chase-me).");
    Assert.IsTrue(botA.gameObject.activeSelf && botB.gameObject.activeSelf,
        "forcePlayerAsTagger must never bench bots — benching is chase-me (forcePlayerAsRunner) only.");
}
```

- [ ] **Step 3: Write the thrown-net-catches-a-bot test**

The human tagger's throw path is byte-identical to a bot's (`TagAgent` input just calls `Net?.TryThrow()`), so exercising `TryThrow` against a bot victim covers the human-thrown case. Timeline: windup 0.45 + flight 0.45 + trap 1.2 = 2.1s.

```csharp
[UnityTest]
public IEnumerator NetThrow_LandsOnBotRunner_ConvertsAfterTrap()
{
    _sceneRoot = new GameObject("TestScene");
    CreateGround(_sceneRoot.transform, new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f));

    var config = ScriptableObject.CreateInstance<TagRulesConfig>();
    config.roundStartGraceDuration = 0f; // NetThrower.CanThrow gates on IsPastStartGrace

    // Runner 4m ahead of the tagger (+Z), inside netThrowRange (6m), stationary -> lead ~0,
    // lands within netHitRadius.
    (_, _, TagAgent tagger, _) = CreateTagAgent(new Vector3(0f, 1.1f, 0f), config);
    (_, _, TagAgent runner, _) = CreateTagAgent(new Vector3(0f, 1.1f, 4f), config);

    var controllerGo = new GameObject("RoundController");
    controllerGo.transform.SetParent(_sceneRoot.transform);
    RoundController controller = controllerGo.AddComponent<RoundController>();
    controller.Configure(config);
    tagger.SetRoundController(controller);
    runner.SetRoundController(controller);
    controller.RegisterAgent(tagger, isLocalPlayer: false); // bot resolve path == human-thrown-at-bot path
    controller.RegisterAgent(runner, isLocalPlayer: false);

    yield return null; // Start() -> StartRound() (AssignRoles will shuffle roles; re-pin below)

    tagger.SetRole(Role.Tagger, startGrace: false);
    runner.SetRole(Role.Runner, startGrace: false);
    tagger.Net!.TryThrow();

    // windup 0.45 + flight 0.45 + trap 1.2 = 2.1s; pad for fixed-step slack.
    yield return new WaitForSeconds(2.6f);

    Assert.AreEqual(Role.Tagger, runner.Role, "A landed net must convert the runner after netTrapDuration.");
    Assert.IsTrue(runner.IsInGrace, "Net-trap conversion must start the normal conversion grace.");
}
```

- [ ] **Step 4: Run both tests, expect PASS (characterization)**

Run in Unity Test Runner (PlayMode), filter `AssignRoles_ForcePlayerAsTagger` and `NetThrow_LandsOnBot`. Expected: both PASS against current code. If either FAILS, stop — the plan's premise ("role plumbing already works") is wrong and the failure is the real first work item. Also confirm the whole TagRulesTests group still passes (helper signature change).

- [ ] **Step 5: Commit**

```bash
git add Assets/Tests/PlayMode/TagRulesTests.cs
git commit -m "test: pin forcePlayerAsTagger roles and thrown-net bot catch"
```

---

### Task 2: Menu role-select row + bootstrap plumbing (game playable as tagger)

**Files:**
- Modify: `Assets/Scripts/TagArenaBootstrap.cs` (~line 63, next to `ApplyTaggerCount`)
- Modify: `Assets/Scripts/MainMenuOverlay.cs` (rows ~lines 22, 57-60, 130-142, 181-199, 240-251)

**Interfaces:**
- Consumes: `TagRulesConfig.forcePlayerAsTagger/forcePlayerAsRunner`, existing `DrawOptionRow` idiom, existing `ApplyTaggerCount` pattern.
- Produces: `TagArenaBootstrap.ApplyPlayerRole(bool asTagger)`, `TagArenaBootstrap.PlayerIsTagger { get; }`, `TagArenaBootstrap.RosterSize { get; }` — consumed by MainMenuOverlay only.

**Design decisions (resolve the Chasers interaction explicitly):**

- The row is a two-value toggle: **"Play as: Runner / Tagger"**. No "Random" — the config supports it (`both flags false`) but nobody asked; adding it later is a third state in one toggle lambda. `// ponytail:` comment marks it.
- **Chasers row interaction:** `taggerCount` semantics differ by mode. In chase-me (forceRunner) it means "bot chasers hunting you" and surplus bots are *benched* (`AssignRoles` `bench` branch); Chasers = 0 is free-roam. With forceTagger, the player is inserted at index 0 so `taggerCount` **includes the player**, and nothing benches — every non-tagger bot becomes a runner. Exposing raw "Chasers" while playing tagger is therefore both off-by-one and prey-starving (Chasers 10 → 1 runner). **Resolution: when Tagger is selected, the row relabels to "Runners" with values {1, 3, 5, 10}, and Play() maps it to `taggerCount = RosterSize - runners`** (extra taggers beyond the player are bot co-hunters). Runners ≥ 1 by construction, so the tagger-mode `taggerCount` is always ≥ 1 — the "Chasers = 0 as tagger" edge (which would silently degrade forceTagger via the `taggerCount > 0` gate at `RoundController.cs:704` into an all-runner free-roam) **cannot be selected**. Free-roam remains reachable exactly as today: Runner + Chasers 0.
- Default tagger-mode selection: Runners = 10 → `taggerCount = 1` → solo hunt vs 10, the mirror of the default chase-me.
- The bootstrap's serialized `forcePlayerAsRunner` (TagArenaBootstrap.cs:26-29) stays as the pre-menu initial value; the menu overwrites both config flags on every PLAY. No scene change.

- [ ] **Step 1: Add the bootstrap API**

In `TagArenaBootstrap.cs`, next to `ApplyTaggerCount`:

```csharp
/// <summary>Whether the local player will be pinned Tagger next round — the main menu reads it to
/// show the live value when re-showing (same pattern as TaggerCount/UnlimitedTime).</summary>
public bool PlayerIsTagger => _tagConfig.forcePlayerAsTagger;

/// <summary>Total live agents (player + spawned bots) — the menu's Runners row maps through this.</summary>
public int RosterSize => _bots.Count + 1;

/// <summary>Pins the local player's role for the next StartRound. Runner keeps chase-me semantics
/// (surplus bots benched, Chasers row = bot hunters); Tagger disables benching, so every
/// non-tagger bot plays as a Runner. ponytail: two-state only — "genuinely random" (both flags
/// false) exists in config but has no menu surface until someone asks.</summary>
public void ApplyPlayerRole(bool asTagger)
{
    _tagConfig.forcePlayerAsTagger = asTagger;
    _tagConfig.forcePlayerAsRunner = !asTagger;
}
```

- [ ] **Step 2: Add the menu row + mode-dependent Chasers/Runners row**

In `MainMenuOverlay.cs`:

```csharp
// next to ChaserCounts (~line 23). No 0: a hunt with no prey is meaningless — free-roam is
// Runner + 0 chasers, exactly as before.
private static readonly int[] RunnerCounts = { 1, 3, 5, 10 };
```

```csharp
// next to _chaserIndex (~line 58)
private bool _playAsTagger;
private int _runnerIndex = 3; // RunnerCounts[3] = 10 -> solo hunt, mirror of the chase-me default
```

In `Configure(...)` (after the `_chaserIndex` init):

```csharp
_playAsTagger = bootstrap.PlayerIsTagger;
```

New row method (both arrows toggle, same idiom as `DrawTimeRow`):

```csharp
private float DrawRoleRow(float x, float y, float width) =>
    DrawOptionRow(x, y, width, "Play as", _playAsTagger ? "Tagger" : "Runner",
        () => _playAsTagger = !_playAsTagger,
        () => _playAsTagger = !_playAsTagger);
```

Replace `DrawChaserRow` with the mode-dependent version (keep both index fields so toggling role loses no state):

```csharp
private float DrawChaserRow(float x, float y, float width) => _playAsTagger
    ? DrawOptionRow(x, y, width, "Runners", RunnerCounts[_runnerIndex].ToString(),
        () => _runnerIndex = (_runnerIndex - 1 + RunnerCounts.Length) % RunnerCounts.Length,
        () => _runnerIndex = (_runnerIndex + 1) % RunnerCounts.Length)
    : DrawOptionRow(x, y, width, "Chasers", ChaserCounts[_chaserIndex].ToString(),
        () => _chaserIndex = (_chaserIndex - 1 + ChaserCounts.Length) % ChaserCounts.Length,
        () => _chaserIndex = (_chaserIndex + 1) % ChaserCounts.Length);
```

In `DrawCard`, insert the role row first in the row stack:

```csharp
y = DrawRoleRow(contentX, y, contentWidth);
y = DrawDifficultyRow(contentX, y, contentWidth);
y = DrawChaserRow(contentX, y, contentWidth);
y = DrawTimeRow(contentX, y, contentWidth);
```

and grow the card by one row: `CardBaseHeight` from `384f` to `440f` (row = 46 + 10 gap).

- [ ] **Step 3: Wire Play()**

Replace the `ApplyTaggerCount` line in `Play()`:

```csharp
_bootstrap.ApplyPlayerRole(_playAsTagger);
// Tagger mode: the row is prey count; taggerCount includes the player (AssignRoles inserts them
// at index 0), so taggers = roster - runners. Max guard covers hypothetical smaller scenes.
_bootstrap.ApplyTaggerCount(_playAsTagger
    ? Mathf.Max(1, _bootstrap.RosterSize - RunnerCounts[_runnerIndex])
    : ChaserCounts[_chaserIndex]);
```

- [ ] **Step 4: Verify in editor (this is the "playable as tagger" gate)**

Enter play in RooftopArena. Expected, in order:
1. Menu shows "Play as  < Runner >" above Difficulty; card not clipped.
2. Toggle to Tagger — Chasers row relabels to "Runners  < 10 >".
3. PLAY → countdown runs, "GO!" tinted tagger red, player model is pest_control **carrying the net**, 10 raccoons scatter (all bots active, none benched).
4. Right-click near a fleeing raccoon → windup → throw → trap dome → raccoon converts (turns tagger-colored, joins the hunt); left-click lunge-tag also works.
5. Catch all runners → end screen appears with the round counted as a **win** (verdict copy still runner-flavored — fixed in Task 3).
6. Back out to menu (Quit), select Runner, Chasers 10, PLAY → chase-me works exactly as before.
7. Runner + Chasers 0 + Unlimited → free-roam still works.

- [ ] **Step 5: Run TagRulesTests (both new tests + full group), expect PASS**

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/TagArenaBootstrap.cs Assets/Scripts/MainMenuOverlay.cs
git commit -m "feat(menu): Play as Runner/Tagger role select with runner-count mapping"
```

---

### Task 3: Role-appropriate end-screen and copy audit

**Files:**
- Modify: `Assets/Scripts/Rules/Runtime/RoundController.cs` (`DrawEndScreen` ~lines 1520-1556; audit `DrawRoundStartBanner` and `DrawCountdown`)

**Interfaces:**
- Consumes: `_localPlayerAgent.Role`, `localWon`, `_playerLost`, `_caughtByName` — all already computed in `DrawEndScreen`.
- Produces: nothing.

**Copy matrix (the target):**

| Situation | Verdict | Subline |
|---|---|---|
| Runner, timer/trash win | `YOU ESCAPED` (unchanged) | `survived the timer` (unchanged) |
| Runner, tagged | `CAUGHT` (unchanged) | `caught by <NAME>` (unchanged) |
| Any role, street fall | `YOU DIED` (unchanged) | `the street broke your fall` (unchanged) |
| **Tagger, all runners caught** | **`ALL CAUGHT`** | **`every runner in the net`** |
| **Tagger, timer expired** | **`THEY ESCAPED`** | **`the runners outlasted the clock`** |

- [ ] **Step 1: Branch the verdict**

Replace the round-level verdict expression at ~line 1520-1522. Key ordering point: a tagger timer-loss has `_playerLost == false` and empty `_caughtByName`, which the current code would mislabel `YOU DIED` — branch on `_playerLost` first:

```csharp
bool localIsRunner = _localPlayerAgent == null || _localPlayerAgent.Role == Role.Runner;
string verdict = matchEnd
    ? (playerWonMatch ? $"MATCH WON {_matchPlayerWins}-{_matchBotWins}" : $"MATCH LOST {_matchPlayerWins}-{_matchBotWins}")
    : localWon
        ? (localIsRunner ? "YOU ESCAPED" : "ALL CAUGHT")
        : _playerLost
            ? (string.IsNullOrEmpty(_caughtByName) ? "YOU DIED" : "CAUGHT")
            : "THEY ESCAPED"; // a Tagger who ran out the clock; a Runner can't reach here (timer expiry IS their win)
```

- [ ] **Step 2: Branch the subline**

Replace the subline expression at ~line 1550-1552:

```csharp
string subline = localWon
    ? (localIsRunner ? "survived the timer" : "every runner in the net")
    : _playerLost
        ? (!string.IsNullOrEmpty(_caughtByName) ? $"caught by {_caughtByName}" : "the street broke your fall")
        : "the runners outlasted the clock";
```

- [ ] **Step 3: Audit the remaining copy sites (read, change only what's wrong)**

- `DrawRoundStartBanner` — read it; if its text assumes the runner role (e.g. "RUN"), branch on `_localPlayerAgent.Role` with a tagger variant ("HUNT"). If it's role-neutral, leave it.
- `DrawCountdown` "GO!" — already role-colored; leave it.
- `ShouldShowRunnerBinRow` (~1343) and `DrawTrashObjective` (~1419) — **already role-branched, do not touch** (a player-tagger correctly gets the "THE RACCOON IS EATING" warning banner and no bin row).
- "YOU'RE IT" flash — armed only by the local player's `WasTagged`, which never fires for a player-tagger. Leave it.
- End-screen stats grid labels (below the subline) — read once; keep unless a label is flatly wrong for a tagger (e.g. "Best survival" is session-wide and stays).

- [ ] **Step 4: Verify in editor**

Play as Tagger: (a) catch all runners → "ALL CAUGHT / every runner in the net"; (b) let the timer expire (set Chasers/Runners low, hide) → "THEY ESCAPED / the runners outlasted the clock"; (c) jump off the roof → "YOU DIED". Play as Runner once → "YOU ESCAPED" unchanged.

- [ ] **Step 5: Run TagRulesTests group, expect PASS (copy is untested, guard against accidental logic edits)**

- [ ] **Step 6: Commit**

```bash
git add Assets/Scripts/Rules/Runtime/RoundController.cs
git commit -m "feat(ui): role-branched end-screen verdict and subline for player-tagger"
```

---

### Task 4: Fix the two catch-animation bugs (diagnosis pass + fixes)

Root causes pre-diagnosed at the top of this plan (A1/A2/B1 confirmed by reading; B2 severity unknown). Steps 1 and 6 are the mandated diagnosis/confirmation passes — run them, don't skip to the edits.

**Files:**
- Modify: `Assets/Scripts/Rules/Runtime/NetThrower.cs` (`TryThrow` ~line 75, `SpawnProjectile` ~line 425)
- Modify: `Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs` (`LateUpdate` throw-phase switch ~lines 153-194)

**Interfaces:**
- Consumes: existing `BeginThrow(windupSeconds)` / `ReleaseThrow()` API — **unchanged**, so `TagAgent.DriveThrowWindup/DriveThrowRelease` and the procedural-capsule fallback need no edits.
- Produces: nothing new.

- [ ] **Step 1: Confirm A2's magnitude (30-second diagnostic)**

In editor play as Tagger, pause, and in the hierarchy select the carried `NetVisual` under the hand bone; read its lossyScale in the inspector (or one-shot `Debug.Log(_carriedNet.transform.lossyScale)` — delete after). Expected ~1.74 per `NetVisual.cs:26`. Record the actual value in the commit message. If it's ~1.0, A2 is not real on this rig — skip step 3.

- [ ] **Step 2: Fix A1 — stale local rotation entering the swing**

In `NetThrower.TryThrow()`, after `_state = ThrowState.Windup;`:

```csharp
// The Idle-time LateUpdate below re-asserts the carried net's WORLD rotation every frame, which
// bakes an arbitrary localRotation under the animated hand bone. The swing pose
// (CharacterAnimatorBridge.OrientHand) mounts the pole on the hand's local +Y and assumes the
// identity local rotation BuildNet started with — restore that contract as the hand takes over.
if (_carriedNet != null) _carriedNet.transform.localRotation = Quaternion.identity;
```

- [ ] **Step 3: Fix A2 — projectile matches carried size**

Replace `SpawnProjectile`:

```csharp
private void SpawnProjectile()
{
    _projectile = NetVisual.BuildNet(null);
    _projectile.transform.position = _launchPos;
    // The carried net inherits the hand bone's lossyScale (~1.74x on this rig — see
    // NetVisual.NetModelScale's tuning note); the unparented projectile doesn't. Copy it so the
    // net doesn't visibly shrink the instant it leaves the hand.
    if (_carriedNet != null) _projectile.transform.localScale = _carriedNet.transform.lossyScale;
}
```

- [ ] **Step 4: Fix B1 — fold the whip into the windup so the scoop connects at release**

In `CharacterAnimatorBridge.LateUpdate`, replace the `ThrowPhase` switch cases (`Windup`/`Hold`/both `Release` cases). `ReleaseThrow()` itself is unchanged (still sets `_throwPhase = Release; _throwTimer = 0;`) — it now only drives the recoil:

```csharp
switch (_throwPhase)
{
    case ThrowPhase.Windup:
    {
        // The scoop must CONNECT at release: NetThrower.Release() fires the instant its windup
        // expires, so the LOAD->SCOOP whip is folded into the END of the windup instead of
        // starting at release. Previously the net left the hand 0.12s (ThrowWhipSeconds) before
        // the swing visually threw it — a quarter of the whole flight.
        float loadSeconds = Mathf.Max(0.01f, _throwWindup - ThrowWhipSeconds);
        if (_throwTimer <= loadSeconds)
        {
            float t = Mathf.Clamp01(_throwTimer / loadSeconds);
            arc = 1f - (1f - t) * (1f - t); // EaseOut into the load
            authority = Mathf.Clamp01(t / ThrowBlendInFrac);
        }
        else
        {
            float u = Mathf.Clamp01((_throwTimer - loadSeconds) / ThrowWhipSeconds);
            arc = 1f + u * u; // EaseIn whip LOAD -> SCOOP
            if (u >= 1f) _throwPhase = ThrowPhase.Hold;
        }
        break;
    }
    case ThrowPhase.Hold:
        arc = 2f; // parked at the scoop (a fixed-step of skew, at most) until ReleaseThrow's recoil
        break;
    case ThrowPhase.Release when _throwTimer <= ThrowRecoilSeconds:
    {
        float v = _throwTimer / ThrowRecoilSeconds;
        float settle = 1f - (1f - v) * (1f - v);
        arc = 2f;
        authority = 1f - settle; // hand the pose back to locomotion
        break;
    }
    default:
        _throwPhase = ThrowPhase.None;
        return;
}
```

Sanity notes for the implementer: `netWindupSeconds` (0.45) > `ThrowWhipSeconds` (0.12), so `loadSeconds` is a real 0.33s and the telegraph still reads; the bridge counts `Time.deltaTime` while NetThrower counts fixed steps, so the Hold case absorbs the ≤1-tick skew between "scoop reached" and "Release() fired". No NetThrower timing changes — sim identical, headless identical.

- [ ] **Step 5: Verify A1/A2/B1 in editor**

Play as Tagger, `Time.timeScale = 0.25` via a temporary console call or the pause menu if it has one (or just watch at full speed several times). Expected:
1. Carried net stays gripped along the hand through READY→LOAD→SCOOP (no sideways/backwards pole during windup) — A1.
2. The thrown net is the same size as the carried one — A2.
3. The net leaves the hand at the *bottom of the forward scoop*, from in front of the body, and the arm follows through into recoil — not launching from behind the head — B1.
4. Bot throws look right too (same code path).
5. Residual check: if the net still reads as growing out of the wrist rather than the palm, add a small `localPosition` offset on the carried net — otherwise skip (`// ponytail:` if added).

- [ ] **Step 6: B2 diagnosis — watch the resolve instant**

Temporarily add to `ResolveHit` (delete before commit, or keep behind `Debug.isDebugBuild` only if it earns it):

```csharp
Debug.DrawLine(_landPos, victim.transform.position, Color.red, 2f);
```

Watch 3-4 hits (player-thrown and bot-thrown) at slow timescale. Question to answer: does the projectile→trap-dome swap read as a teleport when the victim is near the 1.1m edge of `netHitRadius`?

- [ ] **Step 7: B2 decision**

- If it reads fine (expected — the 0.8m dome swallows most of the gap): add the marker comment in `ResolveHit` and stop:

```csharp
// ponytail: the projectile lands at the PREDICTED _landPos while the dome spawns on the victim
// (<= netHitRadius apart). Verified in-editor: the dome swallows the gap. If it ever reads as a
// teleport, drift the flight visual toward the victim over the last ~30% of t in Update.
```

- Only if it clearly reads badly: apply exactly that drift in `Update`'s flight block (after `pos` is computed, before assignment) — cosmetic only, misses unaffected because a dodged/outrun victim just pulls the drift target with them marginally for the last frames:

```csharp
if (_targetAgent != null && t > 0.7f)
    pos = Vector3.Lerp(pos, _targetAgent.transform.position + Vector3.up * 0.4f, (t - 0.7f) / 0.3f);
```

- [ ] **Step 8: Run the full TagRulesTests group + NetThrow test, expect PASS; self-play sanity**

Run `Tools/selfplay.sh` (headless) once and eyeball the tag-distribution metrics against the previous run — the fixes are presentation-only, so any shift means a sim leak. Expected: unchanged.

- [ ] **Step 9: Commit**

```bash
git add Assets/Scripts/Rules/Runtime/NetThrower.cs Assets/Scripts/Movement/Runtime/CharacterAnimatorBridge.cs
git commit -m "fix(anim): net rides the swing correctly and releases at the scoop, not before it"
```

---

### Task 5: Kill cam for the player-tagger's round-winning catch

**Decision (recommended, not a menu of options): the player-tagger gets the cinematic on the FINAL catch only** — the round-winning one — with caption "YOU CAUGHT <NAME>". Rationale: a tagger lands up to 10 catches per round; a ~4s world-freeze replay per catch would wreck the hunt's pacing, and the victim-side kill cam is also round-ending-only. Mid-round catches already get juice for free (`ExecuteTag` fires `TriggerTagSlowMo` whenever the local player is involved — verified at `TagAgent.cs:817-820`). If a *bot co-tagger* lands the final catch, no cinematic — it's the player's moment or nothing.

**Files:**
- Modify: `Assets/Scripts/Rules/Runtime/RoundController.cs` (`RecordTag` ~line 443; the `runnersRemaining == 0` win check ~line 1070; `PlayerCaught` call site ~line 1188)
- Modify: `Assets/Scripts/Rules/Runtime/KillCamPlayback.cs` (`Play` signature ~line 120, `_caughtLabel` assignment ~line 153)
- Modify: `Assets/Scripts/Rules/Runtime/TagAgent.cs` (`ExecuteTag`'s `RecordTag` call ~line 811)

**Interfaces:**
- Consumes: `KillCamPlayback.Play(tagger, victim, label, onComplete)`, `TagAgent.DisplayName`.
- Produces: `RoundController.RecordTag(TagAgent tagger, TagAgent victim)` (breaking change to the old 1-arg signature — grep confirms `TagAgent.ExecuteTag` is the only caller, verify with `Grep "RecordTag("` before editing); `KillCamPlayback.Play`'s third param becomes the full caption string `caughtLabel` (callers: `PlayerCaught` only — verify with `Grep "\.Play("`).

- [ ] **Step 1: Caption-generalize KillCamPlayback**

`Play(TagAgent tagger, TagAgent victim, string taggerName, ...)` → `Play(TagAgent tagger, TagAgent victim, string caughtLabel, ...)`; inside, `_caughtLabel = $"CAUGHT BY {taggerName}";` → `_caughtLabel = caughtLabel;`. Update the doc comment. Note the replay still frames the **tagger's** third-person view — for a player catch that is the player's own view replayed, which is exactly right.

- [ ] **Step 2: Update PlayerCaught's call site**

```csharp
_killCamPlayback.Play(tagger, player, $"CAUGHT BY {tagger.DisplayName}", () => EndRound("You were tagged!"));
```

- [ ] **Step 3: Track the last tag**

`RecordTag` gains the victim and stores the pair:

```csharp
// Last landed tag (tagger, victim) — read by the win check to decide whether the round-winning
// catch was the local player's and deserves the "YOU CAUGHT" kill cam.
private TagAgent? _lastTagTagger;
private TagAgent? _lastTagVictim;

/// <summary>Increments the tagger's tag count for the summary screen and remembers the pair.
/// Called from TagAgent.ExecuteTag for every landed tag.</summary>
public void RecordTag(TagAgent tagger, TagAgent victim)
{
    _tagCounts.TryGetValue(tagger, out int count);
    _tagCounts[tagger] = count + 1;
    _lastTagTagger = tagger;
    _lastTagVictim = victim;
}
```

In `TagAgent.ExecuteTag`: `_roundController?.RecordTag(this);` → `_roundController?.RecordTag(this, other);`. Clear both fields in `StartRound` (next to the other per-round resets) so a stale pair can't leak across rounds.

- [ ] **Step 4: Wire the win check**

Replace the `runnersRemaining == 0` block (~line 1070) — same defer-EndRound-into-the-callback shape as `PlayerCaught`; `Update` already early-returns while `_killCamPlayback.IsPlaying` (set synchronously inside `Play`), so this can't re-trigger next frame:

```csharp
if (runnersRemaining == 0)
{
    // Player-tagger's round-winning catch gets the victim-side cinematic treatment, final catch
    // ONLY (a replay per catch would freeze the hunt up to 10 times a round). A bot co-tagger
    // landing the last catch gets no replay — it's the player's moment or nothing.
    if (_killCamPlayback != null && _localPlayerAgent != null
        && _localPlayerAgent.Role == Role.Tagger
        && _lastTagTagger == _localPlayerAgent && _lastTagVictim != null)
    {
        _killCamPlayback.Play(_lastTagTagger, _lastTagVictim,
            $"YOU CAUGHT {_lastTagVictim.DisplayName}", () => EndRound("Taggers win! All runners tagged."));
        return;
    }
    EndRound("Taggers win! All runners tagged.");
    return;
}
```

- [ ] **Step 5: Verify in editor**

1. Play as Tagger, Runners 1 (Runners row value 1): catch the sole runner → world freezes, replay of your own approach + catch with the red bands and "YOU CAUGHT <NAME>", then "ALL CAUGHT" end screen. Round tallies as a win exactly once.
2. Runners 3: first two catches → slow-mo juice only, no replay; final catch → replay.
3. Play as Runner, get caught → victim-side kill cam unchanged ("CAUGHT BY <NAME>").
4. Street-fall as tagger → no kill cam, straight to "YOU DIED".

- [ ] **Step 6: Run TagRulesTests + KillCamRecorderTests + SelfPlayTests headless (`Tools/selfplay.sh`), expect PASS**

Kill cam is `-nographics`-gated (`Play` bails and invokes `onComplete` immediately at `KillCamPlayback.cs:124-128`), so headless self-play must be byte-identical. The `RecordTag` signature change compiles into SelfPlayTests' path — run it.

- [ ] **Step 7: Commit**

```bash
git add Assets/Scripts/Rules/Runtime/RoundController.cs Assets/Scripts/Rules/Runtime/KillCamPlayback.cs Assets/Scripts/Rules/Runtime/TagAgent.cs
git commit -m "feat(killcam): YOU CAUGHT replay on the player-tagger's round-winning catch"
```

---

### Task 6: Runner-AI review vs a human hunter (playtest + decision gate)

The flee AI is real and role-symmetric (`ParkourBotInput.FleeGoalNode` ~697-730: escape-lead scoring over the parkour graph, dead-end penalty, `FleeGoalSpread` anti-funneling; `UpdateJuke` ~783-828 on close approach; trash-eating under low threat; runner-only double jump). Perception is **omniscient flat distance** for both roles (`RoundController.FindNearestOpposingAgent` ~449-507, no LOS/cone) — bots react to a human tagger perfectly even through walls.

**Recommendation (opinionated): ship WITHOUT a perception model.** Three reasons: (1) this game has no stealth verbs — the tagger sprints, dives, and throws; there is no crouch/hide to make omniscience *observable* as unfairness in normal play; (2) the flee code is shared with bot taggers, and any perception gate shifts the self-play balance bands the tuning is built on; (3) difficulty already has a menu knob. The playtest below is the evidence gate — apply the prepared patch only if it fails.

**Files:**
- Possibly modify (only on playtest failure): `Assets/Scripts/AI/Runtime/ParkourBotInput.cs` (`FleeGoalNode` and its threat scan)

- [ ] **Step 1: Structured playtest (15 minutes, Tagger mode, Skilled difficulty, Runners 10 then Runners 3)**

Score each YES/NO, note timestamps:
1. Can you close distance on a fleeing bot with sprint + dive within ~20s of picking one? (NO = flee too strong.)
2. Do bots turn to flee *before you could plausibly be seen* (around corners, from behind cover) in a way you actually notice? (YES = omniscience observable.)
3. Do several runners funnel onto the same escape route so one net catches the group's tail repeatedly? (`FleeGoalSpread` should prevent this.)
4. Does the juke on close approach create real 50/50s, or does it read as random twitching?
5. Do bots still commit to trash cans while you hunt, creating ambush opportunities? (This is the intended risk/reward — it should happen.)
6. Round length as solo tagger vs 10: does a full clear feel achievable inside the 120s timer at Skilled?

- [ ] **Step 2: Decision gate**

- All acceptable → write one line in the commit message ("flee AI playtested vs human tagger: shipped as-is") and add the marker comment at the top of `FleeGoalNode`:

```csharp
// ponytail: perception is omniscient flat distance (RoundController.FindNearestOpposingAgent) for
// both roles — playtested vs a human tagger and it reads fine because there are no stealth verbs.
// If hiding ever becomes a verb, gate threat scoring on the awareness check below (radius or LOS).
```

- Only if item 2 (observable omniscience) fails: apply the minimal awareness gate inside `ParkourBotInput` — a tagger contributes to flee scoring only when close or visible. No memory, no cone, no new asmdef refs (`Game.AI` already references `Game.Rules`):

```csharp
private const float AwareRadius = 25f; // ponytail: binary awareness, no vision cone / memory —
                                       // upgrade to a perception state only if hiding becomes a verb.

private bool AwareOf(TagAgent tagger) =>
    (tagger.transform.position - _agent.transform.position).sqrMagnitude <= AwareRadius * AwareRadius
    || _agent.HasTagLineOfSight(tagger);
```

used to filter the tagger set in `FleeGoalNode`'s escape-lead min() and the threat check that suppresses trash-eating. Keep `UpdateJuke` unconditional (it only fires on close approach, which always passes the radius).

- [ ] **Step 3: If (and only if) the patch was applied — re-verify balance**

Run `Tools/selfplay.sh` and compare tag-distribution / round-length metrics to the pre-patch run; the awareness gate also blinds bots to *bot* taggers beyond 25m, so the bands may shift. If they leave the target bands, tune `AwareRadius` up before touching anything else. Then re-run the Step 1 playtest items 1-2.

- [ ] **Step 4: Commit**

```bash
git add Assets/Scripts/AI/Runtime/ParkourBotInput.cs
git commit -m "docs(ai): flee-AI playtest verdict vs human tagger (+ awareness gate if applied)"
```

---

## Test summary (smallest set that fails if this breaks)

| Test | File | Guards |
|---|---|---|
| `AssignRoles_ForcePlayerAsTagger_PlayerIsTaggerAndBotsAreRunners` | TagRulesTests.cs (new, Task 1) | forced-tagger role pinning, no benching in tagger mode |
| `NetThrow_LandsOnBotRunner_ConvertsAfterTrap` | TagRulesTests.cs (new, Task 1) | the whole human-tagger catch loop: TryThrow → windup → flight → trap → ExecuteTag conversion |
| Existing TagRulesTests group | TagRulesTests.cs | contact tag / grace / dodge invariants the net must never change |
| Existing SelfPlayTests via `Tools/selfplay.sh` | SelfPlayTests.cs | headless sim identity after the presentation-only animation fixes and the RecordTag signature change |

Menu rows, end-screen copy, and kill-cam wiring are OnGUI/presentation — verified by the explicit editor steps in Tasks 2/3/5, not unit tests (`// ponytail:` — an OnGUI harness would cost more than the feature).

## Execution order rationale

Task 1 (guard rails) → Task 2 (**playable as tagger** — the user can hunt from this point on) → Task 3 (correct copy) → Task 4 (animation fixes, biggest quality win) → Task 5 (kill-cam polish) → Task 6 (AI review last, because it needs Tasks 2+4 in place to playtest the real experience). Every task compiles, passes tests, and is independently verifiable in the editor.
