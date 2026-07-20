#nullable enable

using System;
using System.Collections;
using UnityEngine;

namespace Game.Rules;

/// <summary>
/// The ranged catch: a THROWN bug net. Added by <see cref="TagAgent"/> to itself in Configure, it owns
/// the handheld/thrown net visuals, a windup → flight state machine, and hit/miss resolution. A HIT
/// drops a trap dome over the victim, who struggles before the normal tag flow runs via
/// <see cref="TagAgent.ExecuteTag"/>; a MISS lands the net flat and the tagger eats the whiff lockout.
/// Aimed at the LOCAL player, the flight doubles as the clutch-dodge reaction window via
/// <see cref="NetThrownAtPlayer"/>/<see cref="RoundController"/>; bots self-resolve at flight end.
/// </summary>
public sealed class NetThrower : MonoBehaviour
{
    /// <summary>Raised the instant a net is released at the LOCAL player. <see cref="RoundController"/>
    /// subscribes (OnEnable/OnDisable) and, if it takes over the reaction window, calls
    /// <see cref="MarkExternalResolution"/> synchronously so this thrower defers its own resolution.
    /// Never fired for bot/headless targets — those self-resolve.</summary>
    public static event Action<NetThrower, TagAgent>? NetThrownAtPlayer;

    private enum ThrowState { Idle, Windup, Flight }

    // Cosmetic-only constants (everything gameplay-affecting lives in TagRulesConfig).
    private const float MissLingerSeconds = 2f;   // a missed net lies on the ground this long, then destroys
    private const float ArcHeightFraction = 0.18f; // parabola apex as a fraction of throw distance
    private const float SpinDegPerSec = 720f;      // end-over-end tumble of the thrown net
    // Trap-dome VISUAL radius, deliberately decoupled from netHitRadius: the hit radius is a gameplay
    // forgiveness knob (1.1m), but a dome built that wide renders as a ~2.2m pancake swallowing the
    // rooftop. The dome only needs to read "raccoon trapped under a net", so it stays raccoon-sized.
    private const float TrapDomeVisualRadius = 0.8f;

    private static bool HasGraphics =>
        SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

    private TagAgent _agent = null!;
    private TagRulesConfig _config = null!;

    private ThrowState _state = ThrowState.Idle;
    private float _cooldownRemaining;
    private float _windupRemaining;
    private float _flightElapsed;        // scaled sim time — drives bot self-resolution and the bot projectile visual
    private float _flightVisualElapsed;  // unscaled — drives the projectile visual while the local-player QTE owns resolution
    private bool _resolutionExternal;    // true when RoundController's dodge window (not this thrower) resolves the flight
    private TagAgent? _targetAgent;      // null = a blind throw (no valid target acquired) — always a miss

    private Vector3 _launchPos;
    private Vector3 _landPos;

    private GameObject? _carriedNet;
    private Transform? _carryParent;
    private GameObject? _projectile;
    private GameObject? _trapDome;

    /// <summary>The tagger this net belongs to — RoundController reads it to route whiff/tag back.</summary>
    internal TagAgent Owner => _agent;

    internal void Initialize(TagAgent agent, TagRulesConfig config)
    {
        _agent = agent;
        _config = config;
    }

    // ---------------------------------------------------------------- Input entry point

    /// <summary>Attempt a throw (player right-click, or a bot's rate-limited AI hook). Mirrors the gates
    /// <see cref="TagAgent.TryLunge"/> checks, plus the net's own cooldown. Self-gating, so a bot may call
    /// it every tick — it no-ops unless a shot is actually available. NO BLIND THROWS: since bots call
    /// this every tick, committing without a valid target would have every tagger hurling nets at
    /// nothing and burning the cooldown right when a real target comes in range. No target = keep the
    /// net in hand.</summary>
    public void TryThrow()
    {
        if (!CanThrow()) return;

        TagAgent? target = AcquireTarget();
        if (target == null) return;

        _targetAgent = target;
        _cooldownRemaining = _config.netThrowCooldown;
        _windupRemaining = _config.netWindupSeconds;
        _state = ThrowState.Windup;
        _agent.DriveThrowWindup(_config.netWindupSeconds);
    }

    private bool CanThrow()
    {
        if (Time.timeScale == 0f) return false;             // kill cam / pause — same guard as TryLunge
        if (_agent.Role != Role.Tagger || _agent.IsInGrace) return false;
        if (_agent.Motor.IsDiving) return false;            // committed-dive lock blocks a throw
        if (_state != ThrowState.Idle) return false;        // one throw at a time
        if (_cooldownRemaining > 0f) return false;

        RoundController? round = _agent.Round;
        if (round != null && round.IsCountdownActive) return false;
        if (round != null && !round.IsPastStartGrace) return false;
        return true;
    }

    /// <summary>Nearest valid opposing runner within <see cref="TagRulesConfig.netThrowRange"/>, roughly
    /// ahead (dot &gt; 0.3), and passing the same vertical-band + line-of-sight checks the old ranged
    /// tag used (reused via <see cref="TagAgent.HasTagLineOfSight"/>).</summary>
    private TagAgent? AcquireTarget()
    {
        RoundController? round = _agent.Round;
        if (round == null) return null;

        TagAgent? nearest = round.FindNearestOpposingAgent(_agent);
        if (nearest == null || nearest.IsInGrace) return null;

        Vector3 delta = nearest.transform.position - transform.position;
        if (Vector3.ProjectOnPlane(delta, Vector3.up).magnitude > _config.netThrowRange) return null;
        if (Vector3.Dot(transform.forward, delta.normalized) < 0.3f) return null;
        if (!_agent.HasTagLineOfSight(nearest)) return null;
        return nearest;
    }

    // ---------------------------------------------------------------- Simulation (FixedUpdate)

    private void FixedUpdate()
    {
        if (_agent == null) return; // added-but-not-yet-Initialized safety (only ever created by TagAgent.Configure)

        // Abandon any in-flight throw the instant the round ends, so it can't resolve against stale /
        // respawned state or stick in Flight across a restart (the local-player QTE path never gets its
        // callback once time freezes to 0). Update still runs at timeScale 0, but keeping this in the
        // sim tick alongside the rest of the state machine reads cleaner.
        if (_state != ThrowState.Idle && _agent.Round is { IsRoundOver: true })
        {
            AbortThrow();
            return;
        }

        float dt = Time.fixedDeltaTime;
        if (_cooldownRemaining > 0f) _cooldownRemaining -= dt;

        switch (_state)
        {
            case ThrowState.Windup:
                _windupRemaining -= dt;
                if (_windupRemaining <= 0f) Release();
                break;

            case ThrowState.Flight:
                _flightElapsed += dt;
                // Bots self-resolve at flight end; the local-player QTE (external) waits for a callback.
                if (!_resolutionExternal && _flightElapsed >= _config.netFlightTime)
                    ResolveSelf();
                break;
        }
    }

    private void Release()
    {
        _state = ThrowState.Flight;
        _flightElapsed = 0f;
        _flightVisualElapsed = 0f;
        _resolutionExternal = false;
        _agent.DriveThrowRelease();

        _launchPos = HandWorldPos();
        _landPos = PredictLandPos();

        if (HasGraphics) SpawnProjectile();

        // Aimed at the local player: hand the flight to the clutch-dodge window as its reaction test.
        // RoundController calls MarkExternalResolution() during this synchronous invoke if it takes over.
        if (_targetAgent != null && _targetAgent.IsLocalPlayer)
            NetThrownAtPlayer?.Invoke(this, _targetAgent);
    }

    // Lead the throw: aim at where the runner will be when the net lands, not where they are now.
    private Vector3 PredictLandPos()
    {
        if (_targetAgent == null)
            return transform.position + transform.forward * _config.netThrowRange;

        // CLOSE-RANGE WHIFF FIX: a full-velocity lead overshoots a NEAR target — point-blank against a
        // 7 m/s sprint the land point sits ~3m past the runner, outside netHitRadius, so taggers right
        // next to their target whiffed. Scale the lead by how far the target is (0 at the thrower, 1 at
        // netThrowRange): close throws aim near the target's CURRENT position, only max-range throws keep
        // the full predictive lead.
        Vector3 toTarget = _targetAgent.transform.position - transform.position;
        float horizontalDist = Vector3.ProjectOnPlane(toTarget, Vector3.up).magnitude;
        float leadScale = Mathf.Clamp01(horizontalDist / _config.netThrowRange);
        Vector3 land = _targetAgent.transform.position
            + _targetAgent.Motor.HorizontalVelocity * (_config.netFlightTime * leadScale);

        // LEAD CLAMP: even scaled, a fast runner near max range could lead the land point well past
        // netThrowRange. Clamp the land point's HORIZONTAL offset from the thrower back to
        // netThrowRange; keep its height so the arc/land-raycast handle vertical.
        Vector3 flatOffset = Vector3.ProjectOnPlane(land - transform.position, Vector3.up);
        if (flatOffset.magnitude > _config.netThrowRange)
        {
            Vector3 clamped = transform.position + flatOffset.normalized * _config.netThrowRange;
            land = new Vector3(clamped.x, land.y, clamped.z);
        }
        return land;
    }

    private Vector3 HandWorldPos() => _carriedNet != null
        ? _carriedNet.transform.position
        : transform.position + Vector3.up * 1.4f + transform.forward * 0.3f;

    // ---------------------------------------------------------------- Resolution

    // Bot / headless path: hit if the target is still under the landing point and not evading.
    private void ResolveSelf()
    {
        TagAgent? victim = _targetAgent;
        bool hit = victim != null
            && !victim.IsInGrace
            && !victim.IsDodgingViaIFrames()
            && WithinHitRadius(victim);

        if (hit)
        {
            ResolveHit(victim!);
        }
        else
        {
            if (victim != null) _agent.WhiffLunge(); // a real target evaded → whiff lockout
            ResolveMiss();
        }
        _state = ThrowState.Idle;
        _targetAgent = null;
    }

    /// <summary>Local-player QTE resolved as a DODGE: the net slams the empty ground where it was thrown.
    /// The roll + whiff lockout are already applied by RoundController's dodge resolution.</summary>
    internal void OnDodged()
    {
        ResolveMiss();
        _state = ThrowState.Idle;
        _targetAgent = null;
        _resolutionExternal = false;
    }

    /// <summary>Local-player QTE elapsed with NO dodge: the net lands. Honors the same i-frame / grace
    /// auto-whiff the ranged tag did, then drops the trap dome and (after the trap) lands the tag.</summary>
    internal void OnHitConfirmed()
    {
        TagAgent? victim = _targetAgent;
        // The QTE only tests reaction TIME (did the player dodge?), so WHERE the net landed must be
        // gated separately or a player who simply outran the landing point would still get caught from
        // metres away. Gate on WithinHitRadius exactly like the bot path (ResolveSelf): outside the radius = miss.
        if (victim != null && !victim.IsInGrace && !victim.IsDodgingViaIFrames() && WithinHitRadius(victim))
        {
            ResolveHit(victim);
        }
        else
        {
            if (victim != null) _agent.WhiffLunge();
            ResolveMiss();
        }
        _state = ThrowState.Idle;
        _targetAgent = null;
        _resolutionExternal = false;
    }

    /// <summary>Called by RoundController during the synchronous <see cref="NetThrownAtPlayer"/> invoke
    /// when it takes ownership of this flight's resolution (the local-player dodge window).</summary>
    internal void MarkExternalResolution() => _resolutionExternal = true;

    private bool WithinHitRadius(TagAgent victim) =>
        Vector3.ProjectOnPlane(victim.transform.position - _landPos, Vector3.up).magnitude <= _config.netHitRadius;

    // HIT: trap dome over the victim, freeze + struggle for netTrapDuration, THEN the normal tag flow.
    private void ResolveHit(TagAgent victim)
    {
        DestroyProjectile();

        GameObject? dome = null;
        if (HasGraphics)
        {
            dome = NetVisual.BuildTrapDome(null, TrapDomeVisualRadius);
            dome.transform.position = victim.transform.position; // hoop centre at the victim's ground
        }
        _trapDome = dome; // most-recent, for AbortThrow's best-effort cleanup

        victim.BeginNetTrap(_config.netTrapDuration); // freeze control + struggle wiggle (presentation on the victim)
        StartCoroutine(TrapThenTag(victim, dome)); // dome passed in so overlapping traps can't cross-destroy
    }

    private IEnumerator TrapThenTag(TagAgent victim, GameObject? dome)
    {
        yield return new WaitForSeconds(_config.netTrapDuration);
        if (dome != null)
        {
            if (_trapDome == dome) _trapDome = null;
            Destroy(dome);
        }
        // Identical downstream flow to the old ranged tag: role convert / WasTagged → PlayerCaught /
        // kill-cam data. ExecuteTag re-checks IsRoundOver, so a round that ended during the trap no-ops.
        _agent.ExecuteTag(victim);
    }

    // MISS: the net lies flat on the ground where it was thrown, lingers, then destroys.
    private void ResolveMiss()
    {
        if (_projectile == null) return;

        // _landPos is a PREDICTED point, routinely mid-air (target led off a roof edge or over a street
        // gap), so it can't be used directly or the net would hang in the sky for the whole linger.
        // Raycast straight down to find the real surface under the predicted point and drop the net flat
        // on it; if there's nothing below (an open gap) destroy the net immediately rather than float it.
        if (Physics.Raycast(_landPos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 40f))
        {
            _projectile.transform.position = hit.point;
            _projectile.transform.rotation = Quaternion.Euler(90f, _projectile.transform.eulerAngles.y, 0f); // hoop flat down
            Destroy(_projectile, MissLingerSeconds);
        }
        else
        {
            Destroy(_projectile);
        }
        _projectile = null;
    }

    private void AbortThrow()
    {
        DestroyProjectile();
        if (_trapDome != null) { Destroy(_trapDome); _trapDome = null; }
        _state = ThrowState.Idle;
        _targetAgent = null;
        _resolutionExternal = false;
    }

    // ---------------------------------------------------------------- Presentation (Update)

    private void Update()
    {
        if (_agent == null) return;

        UpdateCarriedNet();

        if (_state != ThrowState.Flight || _projectile == null) return;

        float t;
        if (_resolutionExternal)
        {
            // Under the QTE's slow-mo the world crawls but the net keeps coming at real speed — it lands
            // exactly when the unscaled dodge window resolves, which is the reaction the player is racing.
            _flightVisualElapsed += Time.unscaledDeltaTime;
            t = _config.netFlightTime > 0f ? _flightVisualElapsed / _config.netFlightTime : 1f;
        }
        else
        {
            t = _config.netFlightTime > 0f ? _flightElapsed / _config.netFlightTime : 1f;
        }
        t = Mathf.Clamp01(t);

        Vector3 pos = Vector3.Lerp(_launchPos, _landPos, t);
        pos.y += Mathf.Sin(t * Mathf.PI) * Vector3.Distance(_launchPos, _landPos) * ArcHeightFraction;
        _projectile.transform.position = pos;
        _projectile.transform.Rotate(Vector3.right, SpinDegPerSec * Time.deltaTime, Space.Self);
    }

    // Lazily (re)attach the carried net to the right hand — rebuilds automatically after a role/model
    // swap destroys the old hand bone (Unity nulls the destroyed child reference). Hidden mid-flight
    // (the thrown clone stands in) and whenever the agent isn't an out-of-grace Tagger.
    private void UpdateCarriedNet()
    {
        bool shouldCarry = _config.netCarryVisible && HasGraphics
            && _agent.Role == Role.Tagger && !_agent.IsInGrace;

        if (!shouldCarry)
        {
            if (_carriedNet != null) { Destroy(_carriedNet); _carriedNet = null; _carryParent = null; }
            return;
        }

        Transform desiredParent = ResolveHandOrShoulder();
        if (_carriedNet == null || _carryParent != desiredParent)
        {
            if (_carriedNet != null) Destroy(_carriedNet);
            _carriedNet = NetVisual.BuildNet(desiredParent);
            _carryParent = desiredParent;
            if (desiredParent == transform) // root fallback (no hand bone): sit it at a shoulder-ish offset
                _carriedNet.transform.localPosition = new Vector3(0.28f, 1.4f, 0.18f);
        }

        _carriedNet.SetActive(_state != ThrowState.Flight);
    }

    // Parenting under the R_Hand bone with IDENTITY local rotation would leave the net's pole (local +Y)
    // following the Tripo rig's arbitrary hand axes, and a one-time corrective rotation at build would
    // drift as the run cycle swings the arm — so every frame AFTER the Animator has posed the hand
    // (LateUpdate), re-assert an AGENT-space orientation — pole up and ~25° forward, hoop opening facing
    // the agent's forward — while the throw is Idle. During Windup the hand's own swing takes over (the
    // additive throw pose in CharacterAnimatorBridge reads better with the net following the arm), and
    // in Flight the carried net is hidden anyway.
    private void LateUpdate()
    {
        if (_carriedNet == null || !_carriedNet.activeSelf) return;
        if (_state != ThrowState.Idle || _carryParent == transform) return;
        // Pole direction is the axis that must be exact (it sells "held upright"), so build the rotation
        // with the pole as primary: LookRotation's forward argument gets the agent-forward re-projected
        // perpendicular to the pole, and its up argument gets the pole itself (which LookRotation then
        // honors exactly, since the two are orthogonal).
        Vector3 poleDir = Vector3.Slerp(transform.up, transform.forward, 25f / 90f).normalized; // +Y up, ~25° fwd
        Vector3 hoopFwd = Vector3.ProjectOnPlane(transform.forward, poleDir).normalized;
        _carriedNet.transform.rotation = Quaternion.LookRotation(hoopFwd, poleDir);
    }

    private Transform ResolveHandOrShoulder()
    {
        Animator animator = GetComponentInChildren<Animator>();
        if (animator != null && animator.isHuman)
        {
            Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
            if (hand != null) return hand;
        }
        return transform; // fallback: agent root, offset in UpdateCarriedNet
    }

    private void DestroyProjectile()
    {
        if (_projectile != null) { Destroy(_projectile); _projectile = null; }
    }

    private void SpawnProjectile()
    {
        _projectile = NetVisual.BuildNet(null);
        _projectile.transform.position = _launchPos;
    }

    private void OnDestroy()
    {
        if (_carriedNet != null) Destroy(_carriedNet);
        if (_projectile != null) Destroy(_projectile);
        if (_trapDome != null) Destroy(_trapDome);
    }
}
