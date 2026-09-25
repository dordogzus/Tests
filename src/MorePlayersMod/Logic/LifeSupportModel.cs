using System;
using System.Collections.Generic;

namespace MorePlayersMod.Logic;

public readonly struct Vec3
{
    public readonly double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static double DistanceSq(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}

public enum LifeSupportRole { None, Reactor, Shield, Vent, OxygenGenerator, Scrubber }

public readonly struct LifeSupportBlock
{
    public readonly LifeSupportRole Role;
    public readonly Vec3 Position;
    /// <summary>Reactor: radiation strength. Oxygen generator / scrubber: crew supported.</summary>
    public readonly double Value;
    public LifeSupportBlock(LifeSupportRole role, Vec3 position, double value) { Role = role; Position = position; Value = value; }
}

public enum HazardLevel { Safe, Elevated, Dangerous, Lethal }

/// <summary>
/// Radiation + oxygen simulation for Frontier ships (pure, unit tested).
///
/// Radiation: every reactor leaks radiation. Shield walls within the containment
/// radius each absorb a share (a fully walled reactor room ≈ 95% contained) and
/// radiation vents pipe a share of what remains outside the hull. Dose at a point
/// falls off with distance.
///
/// Oxygen: generators and scrubbers each support a number of crew; the ship's
/// reserve drains when the crew on board exceeds supply and refills otherwise.
/// Ships without any Frontier life-support block are never simulated.
/// </summary>
public static class LifeSupportModel
{
    public const double ContainmentRadius = 6.0;
    public const double ShieldAbsorb = 0.22;
    public const double VentExhaust = 0.35;
    public const double FalloffScale = 16.0;

    public static double Leak(Vec3 reactor, IReadOnlyList<LifeSupportBlock> blocks)
    {
        int shields = 0, vents = 0;
        double r2 = ContainmentRadius * ContainmentRadius;
        foreach (var b in blocks)
        {
            if (Vec3.DistanceSq(b.Position, reactor) > r2) continue;
            if (b.Role == LifeSupportRole.Shield) shields++;
            else if (b.Role == LifeSupportRole.Vent) vents++;
        }
        return Math.Pow(1 - ShieldAbsorb, Math.Min(shields, 24)) * Math.Pow(1 - VentExhaust, Math.Min(vents, 8));
    }

    /// <summary>Dose rate (arbitrary "rad/s" units) at <paramref name="point"/>.</summary>
    public static double DoseRate(Vec3 point, IReadOnlyList<LifeSupportBlock> blocks)
    {
        double total = 0;
        foreach (var b in blocks)
        {
            if (b.Role != LifeSupportRole.Reactor || b.Value <= 0) continue;
            double falloff = 1.0 / (1.0 + Vec3.DistanceSq(point, b.Position) / FalloffScale);
            total += b.Value * Leak(b.Position, blocks) * falloff;
        }
        return total;
    }

    public static HazardLevel RadiationLevel(double doseRate)
    {
        if (doseRate < 0.05) return HazardLevel.Safe;
        if (doseRate < 0.5) return HazardLevel.Elevated;
        if (doseRate < 2.0) return HazardLevel.Dangerous;
        return HazardLevel.Lethal;
    }

    public static bool HasLifeSupport(IReadOnlyList<LifeSupportBlock> blocks)
    {
        foreach (var b in blocks) if (b.Role != LifeSupportRole.None) return true;
        return false;
    }

    /// <summary>Crew the ship's oxygen equipment can support.</summary>
    public static double OxygenCapacity(IReadOnlyList<LifeSupportBlock> blocks)
    {
        double capacity = 0;
        foreach (var b in blocks)
            if (b.Role == LifeSupportRole.OxygenGenerator || b.Role == LifeSupportRole.Scrubber) capacity += Math.Max(0, b.Value);
        return capacity;
    }

    /// <summary>Next oxygen reserve (0..100%). Drains 1%/s per unsupported crew member, refills 2%/s when supplied.</summary>
    public static double StepOxygen(double reserve, double capacity, int crewAboard, double dt)
    {
        if (dt <= 0) return Clamp01x100(reserve);
        double deficit = crewAboard - capacity;
        double rate = deficit > 0 ? -deficit * 1.0 : 2.0;
        return Clamp01x100(reserve + rate * dt);
    }

    public static HazardLevel OxygenLevel(double reserve)
    {
        if (reserve > 50) return HazardLevel.Safe;
        if (reserve > 25) return HazardLevel.Elevated;
        if (reserve > 5) return HazardLevel.Dangerous;
        return HazardLevel.Lethal;
    }

    private static double Clamp01x100(double v) => v < 0 ? 0 : v > 100 ? 100 : v;
}
