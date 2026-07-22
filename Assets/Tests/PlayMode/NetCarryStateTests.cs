using Game.Movement;
using NUnit.Framework;

public class NetCarryStateTests
{
    [Test]
    public void HandsBusyStatesHolster()
    {
        foreach (MotorState s in new[] { MotorState.Mantling, MotorState.Vaulting, MotorState.Climbing,
                                         MotorState.OnLadder, MotorState.OnSwing, MotorState.WallHook })
            Assert.IsTrue(NetCarryState.ShouldHolster(s, false, false), $"{s} should holster");
    }

    [Test]
    public void GroundedAndAirborneDoNotHolster()
    {
        Assert.IsFalse(NetCarryState.ShouldHolster(MotorState.Grounded, false, false));
        Assert.IsFalse(NetCarryState.ShouldHolster(MotorState.Airborne, false, false),
            "plain airborne must NOT stow — bunny-hopping would flip the net constantly");
    }

    [Test]
    public void DivingOrFlippingHolstersEvenWhenAirborne()
    {
        Assert.IsTrue(NetCarryState.ShouldHolster(MotorState.Airborne, true, false));
        Assert.IsTrue(NetCarryState.ShouldHolster(MotorState.Airborne, false, true));
    }

    [Test]
    public void AdvanceRisesToOneAndClamps()
    {
        float b = NetCarryState.Advance(0f, true, 0.1f, 0.2f);
        Assert.AreEqual(0.5f, b, 1e-4f);
        b = NetCarryState.Advance(b, true, 0.5f, 0.2f);
        Assert.AreEqual(1f, b, 1e-4f, "must clamp at 1");
    }

    [Test]
    public void AdvanceReversesWithoutPop()
    {
        float b = NetCarryState.Advance(0.6f, false, 0.1f, 0.2f);
        Assert.AreEqual(0.1f, b, 1e-4f, "reversal runs the same blend backwards");
        b = NetCarryState.Advance(b, false, 0.5f, 0.2f);
        Assert.AreEqual(0f, b, 1e-4f, "must clamp at 0");
    }

    [Test]
    public void MountWeightsAlwaysSumToOne()
    {
        foreach (float stow in new[] { 0f, 0.3f, 1f })
        foreach (float thr in new[] { 0f, 0.5f, 1f })
        {
            var (c, b, t) = NetCarryState.MountWeights(stow, thr);
            Assert.AreEqual(1f, c + b + t, 1e-4f, $"stow={stow} throw={thr}");
        }
    }

    [Test]
    public void ThrowOverridesCarryAndStow()
    {
        var (c, b, t) = NetCarryState.MountWeights(1f, 1f);
        Assert.AreEqual(1f, t, 1e-4f, "a full throw wins outright — this is what makes throwing from a stowed net work");
        Assert.AreEqual(0f, c, 1e-4f);
        Assert.AreEqual(0f, b, 1e-4f);
    }

    [Test]
    public void LeftHandReleasesImmediatelyRightHandCarriesThenReleases()
    {
        var (l0, r0) = NetCarryState.HandWeights(0f, 0.9f);
        Assert.AreEqual(0.9f, l0, 1e-4f, "both hands grip while carrying");
        Assert.AreEqual(0.9f, r0, 1e-4f);

        var (l1, r1) = NetCarryState.HandWeights(0.35f, 0.9f);
        Assert.AreEqual(0f, l1, 1e-4f, "off hand lets go as soon as the stow starts");
        Assert.AreEqual(0.9f, r1, 1e-4f, "right hand still carries the net over the shoulder");

        var (_, r2) = NetCarryState.HandWeights(0.85f, 0.9f);
        Assert.Less(r2, 0.9f, "right hand eases off over the last 30%");
        Assert.Greater(r2, 0f);

        var (_, r3) = NetCarryState.HandWeights(1f, 0.9f);
        Assert.AreEqual(0f, r3, 1e-4f, "fully stowed — arms belong to the clip again");
    }
}
