using System.Collections.Generic;
using MorePlayersMod.Logic;
using Xunit;

public class LifeSupportTests
{
    private static readonly Vec3 Origin = new Vec3(0, 0, 0);

    [Fact]
    public void UnshieldedReactor_IsDangerousUpClose()
    {
        var blocks = new List<LifeSupportBlock> { new LifeSupportBlock(LifeSupportRole.Reactor, Origin, 3.0) };
        Assert.Equal(HazardLevel.Lethal, LifeSupportModel.RadiationLevel(LifeSupportModel.DoseRate(new Vec3(1, 0, 0), blocks)));
    }

    [Fact]
    public void ShieldedRoomAndVents_ContainTheReactor()
    {
        var blocks = new List<LifeSupportBlock> { new LifeSupportBlock(LifeSupportRole.Reactor, Origin, 3.0) };
        for (int i = 0; i < 12; i++) blocks.Add(new LifeSupportBlock(LifeSupportRole.Shield, new Vec3(2, 0, i * 0.3), 0));
        blocks.Add(new LifeSupportBlock(LifeSupportRole.Vent, new Vec3(0, 2, 0), 0));
        blocks.Add(new LifeSupportBlock(LifeSupportRole.Vent, new Vec3(0, -2, 0), 0));
        double dose = LifeSupportModel.DoseRate(new Vec3(8, 0, 0), blocks);
        Assert.Equal(HazardLevel.Safe, LifeSupportModel.RadiationLevel(dose));
    }

    [Fact]
    public void ShieldsFarFromTheReactor_DoNotCount()
    {
        var near = new List<LifeSupportBlock> { new LifeSupportBlock(LifeSupportRole.Reactor, Origin, 1) };
        var far = new List<LifeSupportBlock>(near);
        near.Add(new LifeSupportBlock(LifeSupportRole.Shield, new Vec3(1, 0, 0), 0));
        far.Add(new LifeSupportBlock(LifeSupportRole.Shield, new Vec3(30, 0, 0), 0));
        Assert.True(LifeSupportModel.Leak(Origin, near) < 1);
        Assert.Equal(1, LifeSupportModel.Leak(Origin, far), 9);
    }

    [Fact]
    public void Oxygen_DrainsWhenCrewExceedsCapacityAndRefillsOtherwise()
    {
        double reserve = LifeSupportModel.StepOxygen(100, capacity: 8, crewAboard: 12, dt: 5);
        Assert.Equal(80, reserve, 6);
        Assert.Equal(90, LifeSupportModel.StepOxygen(80, capacity: 12, crewAboard: 12, dt: 5), 6);
        Assert.Equal(0, LifeSupportModel.StepOxygen(1, 0, 24, 10));
        Assert.Equal(HazardLevel.Lethal, LifeSupportModel.OxygenLevel(0));
    }

    [Fact]
    public void Plasma_ChargesOnlyWithReactorAndCapsAtCondenserCapacity()
    {
        Assert.Equal(0, PlasmaModel.Step(0, accelerators: 2, condensers: 5, reactors: 0, dt: 10));
        Assert.Equal(100, PlasmaModel.Step(0, 2, 5, 1, 10));
        Assert.Equal(500, PlasmaModel.Step(490, 2, 5, 1, 10));
    }
}
