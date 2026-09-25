using MorePlayersMod.Logic;
using Xunit;

public class ZoneMathTests
{
    [Theory]
    [InlineData(12, 12)]
    [InlineData(8.4, 8)]
    [InlineData(0, 1)]
    [InlineData(40, 12)]
    [InlineData(double.NaN, 12)]
    public void ClampMultiplier_RoundsAndCaps(double input, int expected) =>
        Assert.Equal(expected, ZoneMath.ClampMultiplier(input));

    [Fact]
    public void Expand_KeepsCenterAndScalesWidthAndDepth()
    {
        ZoneMath.Expand(-25, 10, 25, 60, 12, out var minX, out var minZ, out var maxX, out var maxZ);
        Assert.Equal(-300, minX, 6);
        Assert.Equal(300, maxX, 6);
        Assert.Equal(35 - 300, minZ, 6);
        Assert.Equal(35 + 300, maxZ, 6);
    }

    [Fact]
    public void CenterShift_MovesOriginByHalfTheGrowth()
    {
        ZoneMath.CenterShift(50, 50, 12, out var sx, out var sz);
        Assert.Equal(-275, sx, 6);
        Assert.Equal(-275, sz, 6);
    }

    [Fact]
    public void TiledMesh_UsesUInt32WhenNeededAndRejectsHugeMeshes()
    {
        Assert.True(ZoneMath.TiledMeshFits(400, 12, out var small));
        Assert.False(small); // 57,600 vertices
        Assert.True(ZoneMath.TiledMeshFits(2000, 12, out var big));
        Assert.True(big);
        Assert.False(ZoneMath.TiledMeshFits(40000, 12, out _));
    }

    [Fact]
    public void PerimeterOffset_MovesSidesAndCornersToTheNewBorder()
    {
        // Vanilla garage 50x50 centered at origin, x12.
        ZoneMath.PerimeterOffset(30, 0, 0, 0, 25, 25, 12, out var dx, out var dz);
        Assert.Equal(275, dx, 6);
        Assert.Equal(0, dz, 6);
        ZoneMath.PerimeterOffset(-40, -30, 0, 0, 25, 25, 12, out dx, out dz);
        Assert.Equal(-275, dx, 6);
        Assert.Equal(-275, dz, 6);
        ZoneMath.PerimeterOffset(10, 10, 0, 0, 25, 25, 12, out dx, out dz);
        Assert.Equal(0, dx);
        Assert.Equal(0, dz);
    }

    [Fact]
    public void SurroundingStructure_ExcludesFloorFarGeometryAndTerrain()
    {
        Assert.True(ZoneMath.IsSurroundingStructure(30, 0, 0, 0, 25, 25, 5, 5, 3));
        Assert.False(ZoneMath.IsSurroundingStructure(0, 0, 0, 0, 25, 25, 50, 50, 3));   // floor
        Assert.False(ZoneMath.IsSurroundingStructure(200, 0, 0, 0, 25, 25, 5, 5, 3));   // far away
        Assert.False(ZoneMath.IsSurroundingStructure(30, 0, 0, 0, 25, 25, 4000, 4000, 3)); // terrain
    }
}
