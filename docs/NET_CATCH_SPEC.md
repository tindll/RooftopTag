# Net-Catch Spec — pest-control net throw (supersedes the "tagging = lunge only" catch presentation)

The ranged catch is a **thrown bug net** (Animal Crossing style: wooden pole, blue cord grip,
orange stitched-rim hoop, cream mesh bag). The committed-dive contact tag stays; the old instant
ranged hand-tag is replaced by the throw.

## Mechanic

- **Carry**: every Tagger visibly carries the net in their right hand (attach to the humanoid
  right-hand bone; root-offset fallback for the headless capsule).
- **Throw** (right-click / gamepad left trigger, and the bot AI's ranged attempt): windup
  (`netWindupSeconds`) → ballistic flight (`netFlightTime`) to a lead-predicted landing point →
  resolve. Self-gating like TryLunge (timescale, countdown, grace, dive-lock) plus its own
  `netThrowCooldown`.
- **Target acquisition**: nearest opposing runner within `netThrowRange`, roughly ahead
  (dot > 0.3), passing the same vertical-band + line-of-sight gate as the old ranged tag
  (`TagAgent.HasTagLineOfSight`). No target → blind throw, always a miss.
- **Hit**: trap dome (hoop + tented net) drops over the victim; victim is input-frozen and
  struggles under it for `netTrapDuration`; then the *existing* tag flow runs unchanged
  (conversion + grace, or round-end/kill-cam for the local player).
- **Miss / dodge QTE**: a net released at the LOCAL player opens the existing clutch-dodge
  slow-mo window with duration = `netFlightTime` (same ring HUD, same LMB resolve). Dodge →
  escape roll fires, net slams the empty ground (lingers ~2 s), tagger eats `taggerWhiffLockout`.
  Bots get no QTE — their dive i-frames already auto-whiff a landing net.

## Where things live (architecture contract)

| Piece | File | Assembly |
|---|---|---|
| Net + trap-dome procedural meshes | `Assets/Scripts/Rules/Runtime/NetVisual.cs` | Game.Rules |
| Throw state machine + resolution | `Assets/Scripts/Rules/Runtime/NetThrower.cs` (added by TagAgent.Configure — no bootstrap wiring) | Game.Rules |
| Tuning (all of it) | `TagRulesConfig` "Net throw" header: `netThrowRange`, `netThrowCooldown`, `netWindupSeconds`, `netFlightTime`, `netHitRadius`, `netTrapDuration`, `netCarryVisible` | Game.Rules |
| QTE routing | `RoundController` dodge region (`NetThrower.NetThrownAtPlayer` static event → `BeginDodgeWindowInternal`) | Game.Rules |
| Throw animation (procedural additive right-arm windup/whip in LateUpdate — no clip needed) | `CharacterAnimatorBridge.BeginThrow/ReleaseThrow` | Game.Movement (stays Rules-agnostic) |
| Bot usage | `ParkourBotInput` tagger block calls `agent.Net?.TryThrow()` every tick (self-gating) | Game.AI |

Sim (FixedUpdate: windup/flight/hit resolution) is separated from presentation (Update/LateUpdate:
carried net, projectile tumble, trap wiggle, arm swing) — net-throw outcomes must stay identical
headless.

## Agentic verification loops (run after ANY change to this feature)

1. **Compile/console**: recompile via MCP, zero errors, zero new warnings.
2. **Editor smoke test**: `NetVisual.BuildNet` + `BuildTrapDome` via execute_code, screenshot,
   eyeball against the AC reference (pole/grip/hoop/bag all present, nothing magenta, dome tents
   UP not through the floor).
3. **PlayMode tests**: full TagRulesTests group green (modulo known pre-existing failures logged
   in TUNING_LOG.md). Contact-tag, grace, and dodge tests must stay green — the net must never
   change committed-dive behavior.
4. **Play-mode scenario**: enter play in RooftopArena; verify (a) taggers visibly carry nets,
   (b) a bot throw produces windup → arc → trap dome or ground-slam, (c) local-player targeting
   opens the slow-mo ring and LMB dodge rolls clear, (d) console clean. Screenshot each.
5. **Self-playtest**: bot-only match; check tag distribution still lands in target bands and that
   net throws (not only dive contacts) account for a healthy share of conversions. Trap freeze
   adds ~`netTrapDuration` per catch — watch stuck-detection metrics for false positives.

## Tuning guidance

- `netFlightTime` IS the local player's reaction window — shorten it and the QTE gets harder;
  keep it ≥ the dodge-window floor (`dodgeWindowFloor`).
- `netThrowCooldown` > `netTrapDuration` or overlapping traps get weird.
- The throw replaces the hand-tag, so `tagReachStill/Moving` now only matter for the contact dive.
