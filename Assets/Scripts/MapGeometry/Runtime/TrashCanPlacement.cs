#nullable enable

using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGeometry;

/// <summary>
/// Per-round random trash-can placement: samples fresh spots across the rooftops each round instead
/// of reusing RooftopArena's fixed CanAnchors, so active bins don't camp the same handful of positions
/// every match. Reuses RoofPropDresser's clearance rule (link corridors, graph anchors and spawn
/// points — see RoofPropDresser.ClearanceSegments) so a bin never lands on a jump/ramp/swing/ladder
/// line or a spawn point, and additionally rejects candidates too close to an already-placed bin this
/// round (minSpacing).
/// </summary>
public static class TrashCanPlacement
{
    private const float CanClearRadius = 2.5f;
    private const float EdgeInset = 1.5f;
    private const int MaxAttemptsPerSpot = 40;

    /// <summary>Samples <paramref name="count"/> world positions on the rooftops, each at least
    /// <paramref name="minSpacing"/> XZ-metres from every other returned spot and at least
    /// <see cref="CanClearRadius"/> from every link corridor/spawn point. Always returns exactly
    /// <paramref name="count"/> spots (barring the pathological "no roofs at all" case): if a spot
    /// can't find a fully clean candidate within the attempt budget, the best candidate seen (the one
    /// maximizing the minimum clearance to corridors and already-accepted spots) is kept and a warning
    /// is logged.</summary>
    public static List<Vector3> SampleSpots(int count, float minSpacing)
    {
        var spots = new List<Vector3>(count);
        List<(Vector3 a, Vector3 b)> segments = RoofPropDresser.ClearanceSegments();

        var eligibleRoofs = new List<RooftopArena.Roof>();
        foreach (RooftopArena.Roof roof in RooftopArena.Roofs)
        {
            if (roof.SizeX >= 2f * EdgeInset + 1f && roof.SizeZ >= 2f * EdgeInset + 1f)
                eligibleRoofs.Add(roof);
        }

        if (eligibleRoofs.Count == 0)
        {
            // No roof is big enough to inset by EdgeInset — degrade to roof centres rather than
            // looping forever or throwing. Extremely unlikely (would require a hand-authored map with
            // only tiny roofs) but must never crash round start.
            Debug.LogWarning($"TRASHCAN_PLACE_NO_ROOFS: no roof is large enough to inset by " +
                $"{EdgeInset:F1}m on both axes; falling back to roof centres for all {count} spots.");
            if (RooftopArena.Roofs.Length == 0)
            {
                Debug.LogWarning("TRASHCAN_PLACE_EMPTY_MAP: RooftopArena.Roofs is empty; returning 0 spots.");
                return spots;
            }
            for (int i = 0; i < count; i++)
                spots.Add(RooftopArena.Roofs[i % RooftopArena.Roofs.Length].Center);
            return spots;
        }

        for (int i = 0; i < count; i++)
        {
            Vector3 bestCandidate = default;
            float bestScore = float.MinValue;
            bool found = false;

            for (int attempt = 0; attempt < MaxAttemptsPerSpot; attempt++)
            {
                RooftopArena.Roof roof = eligibleRoofs[Random.Range(0, eligibleRoofs.Count)];
                float halfX = roof.SizeX * 0.5f - EdgeInset;
                float halfZ = roof.SizeZ * 0.5f - EdgeInset;
                float x = roof.Center.x + Random.Range(-halfX, halfX);
                float z = roof.Center.z + Random.Range(-halfZ, halfZ);
                var candidate = new Vector3(x, roof.Center.y + 0.15f, z);

                float corridorDist = MinDistanceToSegments(candidate, segments);
                float spotDist = MinDistanceToSpots(candidate, spots);
                float score = Mathf.Min(corridorDist, spotDist);

                if (RoofPropDresser.IsClear(candidate, segments, CanClearRadius) && spotDist >= minSpacing)
                {
                    spots.Add(candidate);
                    found = true;
                    break;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }
            }

            if (!found)
            {
                Debug.LogWarning($"TRASHCAN_PLACE_TIGHT: spot {i} kept a candidate at " +
                    $"({bestCandidate.x:F1}, {bestCandidate.z:F1}) after {MaxAttemptsPerSpot} attempts " +
                    $"— best clearance {bestScore:F2}m (wanted corridor>={CanClearRadius:F1}m, spot-spacing>={minSpacing:F1}m).");
                spots.Add(bestCandidate);
            }
        }

        Debug.Log($"TRASHCAN_PLACED: {count} spots, minSpacing={minSpacing:F1}");
        return spots;
    }

    private static float MinDistanceToSegments(Vector3 p, List<(Vector3 a, Vector3 b)> segments)
    {
        if (segments.Count == 0) return float.MaxValue;
        float min = float.MaxValue;
        foreach ((Vector3 a, Vector3 b) in segments)
        {
            float d = RoofPropDresser.DistanceXZ(p, a, b);
            if (d < min) min = d;
        }
        return min;
    }

    private static float MinDistanceToSpots(Vector3 p, List<Vector3> spots)
    {
        if (spots.Count == 0) return float.MaxValue;
        float min = float.MaxValue;
        foreach (Vector3 s in spots)
        {
            float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(s.x, s.z));
            if (d < min) min = d;
        }
        return min;
    }
}
