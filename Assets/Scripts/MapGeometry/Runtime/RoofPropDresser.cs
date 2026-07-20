#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Deterministic rooftop prop dressing (AC units, vents, pipe runs). AC units and vents are
/// PHYSICAL (colliders on, vault-scale) so they must exist identically in saved scenes and
/// headless self-play — the dresser is called from RooftopArena's shared build path, and every
/// physical prop position must pass the clearance rule: nothing near a parkour-graph anchor, a
/// link corridor between anchors, or a spawn point. Placement is seeded per roof name (stable
/// FNV hash), so rebuilds are identical. Pipe runs are visual-only (no colliders — snag hazard).
/// </summary>
public static class RoofPropDresser
{
    public const float DefaultClearRadius = 2.2f;
    private const float EdgeMargin = 1.2f;
    private const int PlacementAttempts = 12;

    /// <summary>XZ-plane distance from point p to segment ab (heights ignored — routes are XZ corridors).</summary>
    public static float DistanceXZ(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector2 p2 = new(p.x, p.z), a2 = new(a.x, a.z), b2 = new(b.x, b.z);
        Vector2 ab = b2 - a2;
        float len2 = ab.sqrMagnitude;
        float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p2 - a2, ab) / len2);
        return Vector2.Distance(p2, a2 + ab * t);
    }

    public static bool IsClear(Vector3 p, IReadOnlyList<(Vector3 a, Vector3 b)> segments, float radius)
    {
        for (int i = 0; i < segments.Count; i++)
            if (DistanceXZ(p, segments[i].a, segments[i].b) < radius) return false;
        return true;
    }

    /// <summary>Every corridor a bot or spawn uses: all link lines between roof walk anchors,
    /// plus the 12 spawn points as zero-length segments.</summary>
    public static List<(Vector3 a, Vector3 b)> ClearanceSegments()
    {
        var segments = new List<(Vector3 a, Vector3 b)>();
        foreach (RooftopArena.Link link in RooftopArena.Links)
            segments.Add((RooftopArena.Roofs[link.From].Walk, RooftopArena.Roofs[link.To].Walk));
        foreach (Vector3 spawn in RooftopArena.SpawnPoints(12))
            segments.Add((spawn, spawn));
        return segments;
    }

    public static void DressRoofs(Transform parent, float clearRadius = DefaultClearRadius)
    {
        List<(Vector3 a, Vector3 b)> segments = ClearanceSegments();
        for (int i = 0; i < RooftopArena.Roofs.Length; i++)
            DressRoof(RooftopArena.Roofs[i], segments, clearRadius, parent);
    }

    private static void DressRoof(RooftopArena.Roof roof, List<(Vector3 a, Vector3 b)> segments, float clearRadius, Transform parent)
    {
        var rng = new System.Random(StableHash(roof.Name));
        int propCount = 1 + rng.Next(3); // 1-3 per roof
        for (int i = 0; i < propCount; i++)
        {
            for (int attempt = 0; attempt < PlacementAttempts; attempt++)
            {
                float x = roof.Center.x + ((float)rng.NextDouble() - 0.5f) * (roof.SizeX - EdgeMargin * 2f);
                float z = roof.Center.z + ((float)rng.NextDouble() - 0.5f) * (roof.SizeZ - EdgeMargin * 2f);
                var basePos = new Vector3(x, roof.Center.y, z);
                if (!IsClear(basePos, segments, clearRadius)) continue;
                CreateProp(rng.Next(3), basePos, parent);
                break; // placed; on 12 failed attempts the prop is simply skipped
            }
        }
    }

    /// <summary>FNV-1a — stable across runs, machines and Mono/IL2CPP (string.GetHashCode is not).</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s) h = (h ^ c) * 16777619u;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static void CreateProp(int kind, Vector3 basePos, Transform parent)
    {
        switch (kind)
        {
            case 0: // AC unit — vault-scale, physical
                TagArenaMapGeometry.CreateBox("Prop_AC", parent, basePos + Vector3.up * 0.45f,
                    new Vector3(1.2f, 0.9f, 0.9f), TagArenaMapGeometry.SurfaceRole.Floor);
                break;
            case 1: // vent — knee-height, physical
                TagArenaMapGeometry.CreateBox("Prop_Vent", parent, basePos + Vector3.up * 0.25f,
                    new Vector3(0.6f, 0.5f, 0.6f), TagArenaMapGeometry.SurfaceRole.WallBody, seed: 7);
                break;
            // ponytail: no antenna prop kind — thin poles poking out of the GLB roof shells read badly.
            // AC/vent/pipe cover the roof-clutter role without the snag look.
            default: // pipe run — ankle-height, visual only (lip-stutter hazard if collidable)
            {
                GameObject pipe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                pipe.name = "Prop_Pipe";
                Object.DestroyImmediate(pipe.GetComponent<BoxCollider>());
                pipe.transform.SetParent(parent, false);
                pipe.transform.position = basePos + Vector3.up * 0.15f;
                pipe.transform.localScale = new Vector3(0.3f, 0.3f, 2.4f);
                pipe.GetComponent<Renderer>().sharedMaterial =
                    TagArenaMapGeometry.GetMaterial(TagArenaMapGeometry.SurfaceRole.Silhouette);
                break;
            }
        }
    }
}
