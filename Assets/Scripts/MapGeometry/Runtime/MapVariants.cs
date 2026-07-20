#nullable enable

using UnityEngine;

namespace Game.MapGeometry;

/// <summary>Seeded map layout variants for match variety. Each seed generates a unique street grid,
/// building placement, and construction lot layout while keeping the playable rooftop cluster
/// (RooftopArena) identical — the core arena never changes, only the surrounding city reads
/// different. Variants rotate per round in a best-of-5 to feel like escalation without the
/// player leaving the map.</summary>
public static class MapVariants
{
    // Vetted seeds: playtested to be playable (no bots stuck, no unreachable areas, fair bot spread).
    // Each generates a unique city layout. Rotate through them per round for variety.
    private static readonly int[] VerifiedSeeds = { 90210, 42069, 77777, 99999, 12345 };

    /// <summary>Get the seed for round N (0-indexed) in a best-of-5 match.</summary>
    public static int GetSeedForRound(int roundIndex)
    {
        roundIndex = Mathf.Clamp(roundIndex, 0, 99); // fallback for overflow
        return VerifiedSeeds[roundIndex % VerifiedSeeds.Length];
    }

    /// <summary>Seed used for the procedural city grid, building placement, and construction lots.</summary>
    public const int KenneyCityDefaultSeed = 90210;
}
