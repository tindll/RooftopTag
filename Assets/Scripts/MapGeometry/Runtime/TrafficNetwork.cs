#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Presentation-only road graph for the backdrop city's traffic, baked at editor time by
/// SceneStyler.CreateCars from the same <c>StreetSegments</c> the roads are drawn from, then serialized
/// into the scene. Holds the directed lane network (both sides of every street), which nodes are
/// signalized intersections, and the shared traffic tuning (light cycle, stop line, accel/decel). The
/// per-car <see cref="CarDrifter"/> reads this to follow lanes, stop at red lights and turn at
/// intersections.
///
/// <para>Like <see cref="CarDrifter"/>/<see cref="CarImpact"/> this component is attached ONLY by the
/// editor-time styler, never by the headless self-play harness — it is pure decor seen from the
/// rooftops far above and touches no gameplay. Lives in the runtime assembly only so the runtime
/// <see cref="CarDrifter"/> can reference it.</para>
///
/// <para>Light state is a pure function of <c>Time.time</c> (no per-frame mutation, no per-light
/// GameObjects) so any number of cars can query it for free: X-travelling lanes get green in the first
/// half of each node's cycle, Z-travelling lanes in the second half, with a per-node phase offset so
/// the whole city doesn't blink in unison.</para>
/// </summary>
public sealed class TrafficNetwork : MonoBehaviour
{
    /// <summary>A junction or street endpoint. <see cref="signalized"/> is true only where two road
    /// axes actually cross (a real intersection) — endpoints and mid-block splits stay uncontrolled so
    /// cars never brake for nothing.</summary>
    [System.Serializable]
    public struct Node
    {
        public Vector3 pos;
        public bool signalized;
        public float phaseOffset; // seconds, desyncs this junction's light cycle from its neighbours
    }

    /// <summary>One directed lane: a straight run from <see cref="from"/> to <see cref="to"/>, its
    /// centreline already offset to the driver's right so oncoming traffic separates. <see cref="axis"/>
    /// is 0 when travel is along world X, 1 along world Z — it selects which half of a junction's light
    /// cycle governs this lane.</summary>
    [System.Serializable]
    public struct Lane
    {
        public int from;
        public int to;
        public Vector3 entry; // at 'from', offset right
        public Vector3 exit;  // at 'to', offset right
        public int axis;      // 0 = X travel, 1 = Z travel
    }

    [SerializeField] private Node[] _nodes = System.Array.Empty<Node>();
    [SerializeField] private Lane[] _lanes = System.Array.Empty<Lane>();
    [SerializeField] private float _lightCycle = 9f;      // full X-then-Z cycle, seconds
    [SerializeField] private float _lightClearance = 0.7f; // all-red gap at each switch, seconds
    [SerializeField] private float _stopMargin = 3.0f;    // metres the stop line sits before a junction centre
    [SerializeField] private float _accel = 6f;           // m/s^2 pulling away
    [SerializeField] private float _decel = 16f;          // m/s^2 braking for a red

    // Adjacency (node -> outgoing lane indices), rebuilt from the serialized lanes on load. Not
    // serialized: it is pure derived data.
    private List<int>[]? _outLanes;

    public int LaneCount => _lanes.Length;
    public Lane GetLane(int i) => _lanes[i];
    public Node GetNode(int i) => _nodes[i];
    public float StopMargin => _stopMargin;
    public float Accel => _accel;
    public float Decel => _decel;

    /// <summary>Editor-time population (SceneStyler.CreateCars). Values land in the serialized fields and
    /// bake into the saved scene alongside the cars.</summary>
    public void SetData(Node[] nodes, Lane[] lanes, float lightCycle, float lightClearance,
        float stopMargin, float accel, float decel)
    {
        _nodes = nodes;
        _lanes = lanes;
        _lightCycle = lightCycle;
        _lightClearance = lightClearance;
        _stopMargin = stopMargin;
        _accel = accel;
        _decel = decel;
        BuildAdjacency();
    }

    private void Awake() => BuildAdjacency();

    private void BuildAdjacency()
    {
        _outLanes = new List<int>[_nodes.Length];
        for (int i = 0; i < _nodes.Length; i++) _outLanes[i] = new List<int>(4);
        for (int i = 0; i < _lanes.Length; i++)
        {
            int f = _lanes[i].from;
            if (f >= 0 && f < _outLanes.Length) _outLanes[f].Add(i);
        }
    }

    /// <summary>Is <paramref name="axis"/> travel green at <paramref name="nodeIndex"/> right now?
    /// Uncontrolled nodes are always green. X (axis 0) holds the first half of the cycle, Z (axis 1) the
    /// second half, each ending <see cref="_lightClearance"/> early so the box empties before the cross
    /// direction moves.</summary>
    public bool IsGreen(int nodeIndex, int axis, float time)
    {
        Node n = _nodes[nodeIndex];
        if (!n.signalized) return true;
        float half = _lightCycle * 0.5f;
        float t = Mathf.Repeat(time + n.phaseOffset, _lightCycle);
        return axis == 0 ? t < half - _lightClearance
                         : t >= half && t < _lightCycle - _lightClearance;
    }

    /// <summary>Pick a lane leaving <paramref name="node"/>, preferring anything other than an immediate
    /// U-turn back down <paramref name="currentLane"/> (only taken at a true dead end). Uniform reservoir
    /// pick over the candidates, allocation-free. Returns -1 only if the node has no outgoing lanes.</summary>
    public int NextLane(int node, int currentLane, System.Random rng)
    {
        List<int> outs = _outLanes![node];
        if (outs.Count == 0) return -1;
        int backNode = _lanes[currentLane].from; // where this car just came from
        int chosen = -1, count = 0, uTurn = -1;
        for (int k = 0; k < outs.Count; k++)
        {
            int li = outs[k];
            if (_lanes[li].to == backNode) { uTurn = li; continue; } // would double back
            count++;
            if (rng.Next(count) == 0) chosen = li; // uniform over non-U-turn lanes
        }
        return chosen >= 0 ? chosen : uTurn >= 0 ? uTurn : outs[0];
    }
}
