#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only: drives one Kenney vehicle along the street grid's lane graph
/// (<see cref="TrafficNetwork"/>), stopping for red lights on its travel axis and turning lanes at
/// clear intersections. Flavour dressing only — no collider; the ragdoll trigger is a separate child
/// (<see cref="CarImpact"/>). Attached only by <see cref="Game.EditorTools.KenneyTrafficBuilder"/>, never
/// the headless self-play harness. <c>transform.forward</c> tracks the lane each frame so CarImpact's
/// launch direction keeps working through turns.
/// </summary>
public sealed class CarDrifter : MonoBehaviour
{
    // Config is SERIALIZED: Configure() runs at editor scene-build time, and these values must survive
    // the scene being saved, reloaded and domain-reloaded into a real play session — a private
    // non-serialized field here would come back null/default in that session and stop Update() from
    // ever moving the car.
    [SerializeField] private TrafficNetwork? _net;
    [SerializeField] private int _startLane;
    [SerializeField] private float _cruise = 4f; // this car's preferred speed
    [SerializeField] private float _startDist;   // metres along the start lane at spawn
    [SerializeField] private int _seed;

    private System.Random _rng = new(0);
    private float _v;           // current speed, eased toward the target for smooth stop/go
    private float _dist;        // metres travelled along the current lane from its entry

    // Cached geometry of the current lane (refreshed on every lane change).
    private int _lane;
    private int _toNode;
    private int _axis;
    private Vector3 _entry;
    private Vector3 _dir;
    private float _len;

    /// <summary><paramref name="startDist"/> seeds how far along the lane this car begins (metres) so a
    /// street's cars string out instead of all leaving the same end together. <paramref name="seed"/>
    /// makes each car's turn choices independent yet reproducible across rebuilds.</summary>
    public void Configure(TrafficNetwork net, int startLane, float cruiseSpeed, float startDist, int seed)
    {
        _net = net;
        _startLane = startLane;
        _cruise = cruiseSpeed;
        _startDist = startDist;
        _seed = seed;
        Initialise();
    }

    /// <summary>Rebuilds all runtime state from the serialized config — called by Configure at editor
    /// build time (so the saved scene shows cars in position) and again in Awake for a real play session
    /// (where only the serialized fields survived).</summary>
    private void Initialise()
    {
        // The serialized _net reference is written correctly into the scene (verified: every car carries
        // the network's fileID) but comes back NULL on scene load — this project's documented
        // custom-asmdef quirk where typed scene references to asmdef components silently fail to bind
        // (the same reason InteractableMarker/the bootstraps exist). Value-type fields survive fine, so
        // recover the reference structurally: every car is a child of the root that OWNS the network.
        if (_net == null) _net = GetComponentInParent<TrafficNetwork>();
        if (_net == null) return;
        _rng = new System.Random(_seed);
        _v = _cruise;
        SetLane(_startLane);
        _dist = _len > 0f ? Mathf.Repeat(_startDist, _len) : 0f;
        Apply();
    }

    private void Awake() => Initialise();

    private void SetLane(int lane)
    {
        _lane = lane;
        TrafficNetwork.Lane l = _net!.GetLane(lane);
        _entry = l.entry;
        _axis = l.axis;
        _toNode = l.to;
        Vector3 d = l.exit - l.entry;
        d.y = 0f;
        _len = d.magnitude;
        _dir = _len > 0.001f ? d / _len : Vector3.forward;
    }

    private void Update()
    {
        if (_net == null) return;
        float dt = Time.deltaTime;

        // Target speed: full cruise unless the junction ahead is red and we still have to reach it, in
        // which case ease down so we arrive at the stop line at ~0. Braking begins brakeDist out.
        float stopLine = Mathf.Max(0f, _len - _net.StopMargin);
        bool red = _net.GetNode(_toNode).signalized && !_net.IsGreen(_toNode, _axis, Time.time);
        float target = _cruise;
        if (red)
        {
            const float brakeDist = 9f;
            float remain = stopLine - _dist;
            target = remain <= 0.05f ? 0f : Mathf.Min(_cruise, _cruise * (remain / brakeDist));
        }

        float rate = target < _v ? _net.Decel : _net.Accel;
        _v = Mathf.MoveTowards(_v, target, rate * dt);
        _dist += _v * dt;

        if (red && _dist > stopLine)
        {
            // Hold exactly at the line while red — never roll into the box.
            _dist = stopLine;
            _v = 0f;
        }
        else if (_dist >= _len)
        {
            // Cleared the intersection: turn onto the next lane, carrying the overshoot so speed reads
            // continuously through the corner.
            float carry = _dist - _len;
            int next = _net.NextLane(_toNode, _lane, _rng);
            if (next >= 0)
            {
                SetLane(next);
                _dist = _len > 0f ? Mathf.Min(carry, _len) : 0f;
            }
            else
            {
                _dist = _len; // isolated node with no exits: park (should not happen on a connected grid)
            }
        }

        Apply();
    }

    private void Apply()
    {
        Vector3 p = _entry + _dir * _dist;
        transform.position = p;
        if (_dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(_dir, Vector3.up);
    }
}
