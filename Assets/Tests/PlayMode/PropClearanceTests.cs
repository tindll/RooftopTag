#nullable enable

using Game.MapGeometry;
using NUnit.Framework;
using UnityEngine;

namespace RooftopTag.Tests.PlayMode;

public class PropClearanceTests
{
    [Test]
    public void DistanceXZ_PointOnSegment_IsZero()
    {
        Vector3 a = new(0f, 3f, 0f), b = new(10f, 5f, 0f); // height differences must be ignored
        Assert.That(RoofPropDresser.DistanceXZ(new Vector3(5f, 99f, 0f), a, b), Is.LessThan(1e-4f));
    }

    [Test]
    public void DistanceXZ_PointBesideSegment_IsPerpendicularDistance()
    {
        Vector3 a = new(0f, 0f, 0f), b = new(10f, 0f, 0f);
        Assert.That(RoofPropDresser.DistanceXZ(new Vector3(5f, 0f, 3f), a, b), Is.EqualTo(3f).Within(1e-4f));
    }

    [Test]
    public void DistanceXZ_ZeroLengthSegment_IsPointDistance()
    {
        Vector3 p = new(3f, 0f, 4f), s = Vector3.zero;
        Assert.That(RoofPropDresser.DistanceXZ(p, s, s), Is.EqualTo(5f).Within(1e-4f));
    }

    [Test]
    public void IsClear_RejectsPointOnFirstLinkLine()
    {
        var segments = RoofPropDresser.ClearanceSegments();
        // Midpoint of link 0 (Roof_Spawn -> Roof_E1) is squarely on a bot route.
        Vector3 mid = (RooftopArena.Roofs[RooftopArena.Links[0].From].Walk
                     + RooftopArena.Roofs[RooftopArena.Links[0].To].Walk) * 0.5f;
        Assert.That(RoofPropDresser.IsClear(mid, segments, 2.2f), Is.False);
    }

    [Test]
    public void IsClear_AcceptsFarAwayPoint()
    {
        var segments = RoofPropDresser.ClearanceSegments();
        Assert.That(RoofPropDresser.IsClear(new Vector3(1000f, 0f, 1000f), segments, 2.2f), Is.True);
    }

    [Test]
    public void ClearanceSegments_IncludeAllLinksAndSpawns()
    {
        var segments = RoofPropDresser.ClearanceSegments();
        Assert.That(segments.Count, Is.EqualTo(RooftopArena.Links.Length + 12));
    }
}
