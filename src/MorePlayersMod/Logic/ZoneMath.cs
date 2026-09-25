using System;

namespace MorePlayersMod.Logic;

/// <summary>
/// Pure geometry for the enlarged garage (no game types, unit tested).
/// All coordinates are station-local; width (X) and depth (Z) scale, height (Y) stays vanilla.
/// </summary>
public static class ZoneMath
{
    public const int MaxMultiplier = 12;
    public const int DefaultMultiplier = 12;

    /// <summary>Hard cap on generated floor vertices (CPU + GPU memory guard).</summary>
    public const int MaxTiledVertices = 4_000_000;

    public static int ClampMultiplier(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return DefaultMultiplier;
        int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return Math.Max(1, Math.Min(MaxMultiplier, rounded));
    }

    /// <summary>Centered expansion of [min,max] on X and Z by <paramref name="multiplier"/>.</summary>
    public static void Expand(double minX, double minZ, double maxX, double maxZ, int multiplier,
        out double newMinX, out double newMinZ, out double newMaxX, out double newMaxZ)
    {
        double cx = (minX + maxX) * 0.5, cz = (minZ + maxZ) * 0.5;
        double hx = (maxX - minX) * 0.5 * multiplier, hz = (maxZ - minZ) * 0.5 * multiplier;
        newMinX = cx - hx; newMaxX = cx + hx;
        newMinZ = cz - hz; newMaxZ = cz + hz;
    }

    /// <summary>Root-matrix translation (in root axes) that keeps the enlarged box centered on the vanilla one.</summary>
    public static void CenterShift(double baseX, double baseZ, int multiplier, out double shiftX, out double shiftZ)
    {
        shiftX = -(baseX * multiplier - baseX) * 0.5;
        shiftZ = -(baseZ * multiplier - baseZ) * 0.5;
    }

    public static bool TiledMeshFits(int sourceVertexCount, int tiles, out bool needsUInt32)
    {
        needsUInt32 = false;
        if (sourceVertexCount <= 0 || tiles <= 0) return false;
        long total = (long)sourceVertexCount * tiles * tiles;
        if (total > MaxTiledVertices) return false;
        needsUInt32 = total > 65535;
        return true;
    }

    /// <summary>
    /// Outward offset that moves a surrounding station structure from the vanilla
    /// border to the enlarged border. Structures whose center lies inside the vanilla
    /// footprint (the floor itself, pads, markers) are not moved. Each axis is handled
    /// independently, so corner structures move diagonally and keep their relative layout.
    /// </summary>
    public static void PerimeterOffset(double localX, double localZ, double centerX, double centerZ,
        double halfX, double halfZ, int multiplier, out double dx, out double dz)
    {
        dx = 0; dz = 0;
        if (multiplier <= 1 || halfX <= 0 || halfZ <= 0) return;
        double rx = localX - centerX, rz = localZ - centerZ;
        if (Math.Abs(rx) >= halfX) dx = Math.Sign(rx) * halfX * (multiplier - 1);
        if (Math.Abs(rz) >= halfZ) dz = Math.Sign(rz) * halfZ * (multiplier - 1);
    }

    /// <summary>
    /// Whether a structure belongs to the garage surroundings: outside the vanilla footprint
    /// but within <paramref name="ringFactor"/> vanilla half-extents, and not planet-sized.
    /// </summary>
    public static bool IsSurroundingStructure(double localX, double localZ, double centerX, double centerZ,
        double halfX, double halfZ, double sizeX, double sizeZ, double ringFactor)
    {
        if (halfX <= 0 || halfZ <= 0) return false;
        double rx = Math.Abs(localX - centerX), rz = Math.Abs(localZ - centerZ);
        bool outside = rx >= halfX || rz >= halfZ;
        bool nearby = rx <= halfX * ringFactor && rz <= halfZ * ringFactor;
        bool modest = sizeX <= halfX * 2 * ringFactor && sizeZ <= halfZ * 2 * ringFactor;
        return outside && nearby && modest;
    }
}
