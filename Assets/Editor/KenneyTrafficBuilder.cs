#nullable enable

using System.Collections.Generic;
using Game.MapGeometry;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.EditorTools;

/// <summary>
/// Populates the Kenney modular street grid with real CC0 Kenney vehicles that drive their lanes, stop
/// at red lights and turn at intersections — reusing the already-working <see cref="TrafficNetwork"/> /
/// <see cref="CarDrifter"/> / <see cref="CarImpact"/> runtime (the same lane-follow + signal logic proven
/// on the old ring), just sourced from the grid's intersections/segments and dressed with vehicle models
/// instead of generated boxes. Editor-only; every component here is attached at build time, never in the
/// headless harness. Each vehicle keeps a <see cref="CarImpact"/> trigger so the street-death path (a
/// fallen agent getting launched by a car) still works.
///
/// Some placement values (vehicle scale/yaw/lift, lane offset, trigger box) are marked as consts to be
/// eyeballed against a screenshot — the Kenney car models' native size/orientation is only known once seen.
/// </summary>
public static class KenneyTrafficBuilder
{
    private const string CarsPath = "Assets/Art/Kenney/Cars/";
    // Retro Urban Kit curved traffic-signal pole (CC0), ~1 unit tall with the arm along local -Z.
    private const string SignalModelPath = "Assets/Art/Kenney/UrbanProps/detail-light-traffic.glb";
    private const float SignalScale = 4.6f; // → ~4.8m pole, arm ~2m over the roadway

    // -- placement / feel (tune against a screenshot) --------------------------
    private const float LaneOffset = 2.0f;    // metres right of a road-tile centre → keep-right lanes on 8m tiles
    private const float VehicleScale = 2.0f;  // Kenney car ≈ 1 unit long → ~2m; ~4-5m at scale 2
    private const float VehicleYaw = 0f;      // extra local yaw if the model faces the wrong way
    private const float VehicleLift = 0f;     // raise the model if its origin isn't at the wheels
    private const float SpawnChance = 0.14f;  // only ~1 lane in 7 carries a vehicle → ~40 total (busy, not gridlocked)
    private const float SpeedMin = 5f, SpeedMax = 9f;

    // -- signal / drive tuning (mirrors the theme's traffic knobs) -------------
    private const float LightCycle = 9f, LightClearance = 0.8f, StopMargin = 3.5f, Accel = 6f, Decel = 16f;

    // -- street-death impact (mirrors VisualThemeConfig car impact defaults) ---
    private const float ImpactForward = 14f, ImpactUp = 7f, TriggerMargin = 0.3f;

    // Weighted pools: mostly ordinary traffic, a rare "hero" vehicle for flavour.
    private static readonly string[] CommonVehicles =
        { "sedan", "suv", "van", "hatchback-sports", "sedan-sports", "delivery", "sedan", "suv" };
    private static readonly string[] HeroVehicles = { "taxi", "police", "ambulance" };
    private const float HeroChance = 0.13f;

    public static void BuildTraffic(Transform parent, CityGrid grid, float streetY, int seed, int dressingLayer)
    {
        Transform? existing = parent.Find("KenneyTraffic");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);
        var root = new GameObject("KenneyTraffic");
        root.transform.SetParent(parent, false);
        int ragdollLayer = LayerMask.NameToLayer("Ragdoll");

        // 1) Nodes = grid intersections (every one is a signalized 4-way crossroad).
        var nodes = new TrafficNetwork.Node[grid.Intersections.Count];
        var nodeIndex = new Dictionary<(int, int), int>();
        for (int i = 0; i < grid.Intersections.Count; i++)
        {
            Vector3 p = grid.Intersections[i];
            p.y = streetY;
            float phase = Mathf.Repeat(Mathf.Abs(p.x * 7.3f + p.z * 13.1f), LightCycle);
            nodes[i] = new TrafficNetwork.Node { pos = p, signalized = true, phaseOffset = phase };
            nodeIndex[Key(p)] = i;
        }

        // 2) Lanes = two directed, right-offset lanes per road segment.
        var lanes = new List<TrafficNetwork.Lane>();
        foreach ((Vector3 a0, Vector3 b0) in grid.RoadSegments)
        {
            Vector3 a = new(a0.x, streetY, a0.z);
            Vector3 b = new(b0.x, streetY, b0.z);
            if (!nodeIndex.TryGetValue(Key(a), out int ia)) continue;
            if (!nodeIndex.TryGetValue(Key(b), out int ib)) continue;
            Vector3 d = b - a;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f) continue;
            d.Normalize();
            int axis = Mathf.Abs(d.x) >= Mathf.Abs(d.z) ? 0 : 1;
            Vector3 off = Vector3.Cross(Vector3.up, d) * LaneOffset;
            lanes.Add(new TrafficNetwork.Lane { from = ia, to = ib, entry = a + off, exit = b + off, axis = axis });
            lanes.Add(new TrafficNetwork.Lane { from = ib, to = ia, entry = b - off, exit = a - off, axis = axis });
        }

        var net = root.AddComponent<TrafficNetwork>();
        net.SetData(nodes, lanes.ToArray(), LightCycle, LightClearance, StopMargin, Accel, Decel);

        // Cycling signal posts (green/yellow/red) at every intersection, driven off the SAME network the
        // cars obey. The post visual is the CC0 Kenney Retro Urban Kit's curved traffic-signal pole
        // (user round 3: "make them a little nicer looking than just BIG square on stick"); the small
        // emissive Bulb cube sits at the signal head hanging over the road, and TrafficLightPost cycles
        // its color. One shared bulb material is enough: each post's Awake clones a per-post instance
        // via renderer.material (see TrafficLightPost's load-safety notes).
        Shader? lit = Shader.Find("Universal Render Pipeline/Lit");
        var bulbMat = new Material(lit != null ? lit : Shader.Find("Standard")) { color = new Color(0.94f, 0.16f, 0.13f) };
        bulbMat.EnableKeyword("_EMISSION");
        bulbMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        var green = new Color(0.21f, 0.88f, 0.33f);
        var yellow = new Color(1.0f, 0.75f, 0.20f);
        var red = new Color(0.94f, 0.16f, 0.13f);
        GameObject? signalModel = AssetDatabase.LoadAssetAtPath<GameObject>(SignalModelPath);
        if (signalModel == null) Debug.LogWarning($"KENNEY_TRAFFIC: missing signal model {SignalModelPath} — posts will have no pole visual.");
        // ONE shared night-tinted pole material for all 81 posts: the retro kit's metal.png renders
        // near-white under the night rig, so rebuild as URP/Lit with a dark slate tint (same treatment
        // every other Kenney model gets).
        var signalPoleMat = new Material(lit != null ? lit : Shader.Find("Standard"));
        signalPoleMat.SetColor("_BaseColor", new Color(0.20f, 0.21f, 0.27f));
        signalPoleMat.color = new Color(0.20f, 0.21f, 0.27f);
        signalPoleMat.SetFloat("_Metallic", 0f);
        signalPoleMat.SetFloat("_Smoothness", 0.15f);
        for (int ni = 0; ni < nodes.Length; ni++)
        {
            var post = new GameObject($"Signal_{ni}");
            if (dressingLayer >= 0) post.layer = dressingLayer;
            post.transform.SetParent(root.transform, false);
            // Kerb corner of the 8m intersection tile ITSELF (±4 from node) — 4.6 put posts on the block
            // plinth where building plots can reach and clip them. The curved arm (local -Z on the model)
            // swings out over the roadway toward the intersection centre.
            post.transform.position = nodes[ni].pos + new Vector3(3.55f, 0f, 3.55f);
            post.transform.rotation = Quaternion.LookRotation(new Vector3(1f, 0f, 1f));

            if (signalModel != null)
            {
                var pole = (GameObject?)PrefabUtility.InstantiatePrefab(signalModel, post.transform);
                if (pole == null) pole = Object.Instantiate(signalModel, post.transform);
                pole!.name = "Pole";
                pole.transform.localPosition = Vector3.zero;
                pole.transform.localRotation = Quaternion.identity;
                pole.transform.localScale = Vector3.one * SignalScale;
                SetLayerNoShadow(pole, dressingLayer);
                foreach (Renderer pr in pole.GetComponentsInChildren<Renderer>(true))
                    pr.sharedMaterial = signalPoleMat;
                foreach (Collider c in pole.GetComponentsInChildren<Collider>(true))
                    Object.DestroyImmediate(c); // decor only — nothing here may collide
            }

            GameObject bulb = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.DestroyImmediate(bulb.GetComponent<Collider>());
            bulb.name = "Bulb"; // TrafficLightPost.Awake finds it by this name
            if (dressingLayer >= 0) bulb.layer = dressingLayer;
            bulb.transform.SetParent(post.transform, false);
            // Hang just BELOW the arm end (model-space head ~(0.04, 0.93, -0.42)): inside the head box
            // the bulb was invisible; under it, it reads as the hanging signal lamp from street level.
            bulb.transform.localPosition = new Vector3(0.04f, 0.80f, -0.45f) * SignalScale;
            bulb.transform.localScale = Vector3.one * 0.5f;
            var br = bulb.GetComponent<Renderer>();
            br.sharedMaterial = bulbMat;
            br.shadowCastingMode = ShadowCastingMode.Off;

            post.AddComponent<TrafficLightPost>().Configure(ni, 0, green, yellow, red, 2.4f);
        }

        // 3) Spawn vehicles strung along the lanes.
        var rng = new System.Random(seed);
        var cache = new Dictionary<string, GameObject?>();
        int carIndex = 0;
        for (int li = 0; li < lanes.Count; li++)
        {
            if (rng.NextDouble() > SpawnChance) continue; // keep the streets busy but not gridlocked
            TrafficNetwork.Lane lane = lanes[li];
            float len = Vector3.Distance(lane.entry, lane.exit);
            const int perLane = 1;
            for (int j = 0; j < perLane; j++, carIndex++)
            {
                string model = rng.NextDouble() < HeroChance
                    ? HeroVehicles[rng.Next(HeroVehicles.Length)]
                    : CommonVehicles[rng.Next(CommonVehicles.Length)];

                var car = new GameObject($"KCar_{carIndex}");
                if (dressingLayer >= 0) car.layer = dressingLayer;
                car.transform.SetParent(root.transform, false);

                GameObject? src = Load(model, cache);
                if (src != null)
                {
                    var vis = (GameObject?)PrefabUtility.InstantiatePrefab(src, car.transform);
                    if (vis == null) vis = Object.Instantiate(src, car.transform);
                    vis!.transform.localPosition = new Vector3(0f, VehicleLift, 0f);
                    vis.transform.localRotation = Quaternion.Euler(0f, VehicleYaw, 0f);
                    vis.transform.localScale = Vector3.one * VehicleScale;
                    SetLayerNoShadow(vis, dressingLayer);
                }

                float speed = Mathf.Lerp(SpeedMin, SpeedMax, (float)rng.NextDouble());
                float startDist = (j + (float)rng.NextDouble()) / perLane * len;
                car.AddComponent<CarDrifter>().Configure(net, li, speed, startDist, 20240718 + carIndex);

                // Street-death trigger (Ragdoll layer, kinematic mover) — same pattern as the old cars.
                var impact = new GameObject("Impact");
                if (ragdollLayer >= 0) impact.layer = ragdollLayer;
                impact.transform.SetParent(car.transform, false);
                var trigger = impact.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = new Vector3(2.2f, 1.6f, 4.6f) + Vector3.one * (TriggerMargin * 2f);
                trigger.center = new Vector3(0f, 0.8f, 0f);
                var body = impact.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                impact.AddComponent<CarImpact>().Configure(ImpactForward, ImpactUp);
            }
        }

        Debug.Log($"KENNEY_TRAFFIC: {nodes.Length} nodes, {lanes.Count} lanes, {carIndex} vehicles, {nodes.Length} signal posts.");
    }

    private static (int, int) Key(Vector3 p) => (Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.z * 10f));

    private static GameObject? Load(string name, Dictionary<string, GameObject?> cache)
    {
        if (cache.TryGetValue(name, out GameObject? cached)) return cached;
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(CarsPath + name + ".glb");
        if (asset == null) Debug.LogWarning($"KENNEY_TRAFFIC: missing vehicle {CarsPath}{name}.glb");
        cache[name] = asset;
        return asset;
    }

    private static void SetLayerNoShadow(GameObject go, int layer)
    {
        if (layer >= 0) go.layer = layer;
        foreach (Transform t in go.transform) SetLayerNoShadow(t.gameObject, layer);
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
    }
}
