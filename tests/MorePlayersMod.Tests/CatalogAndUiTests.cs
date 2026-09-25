using System.Collections.Generic;
using System.Linq;
using MorePlayersMod.Logic;
using Xunit;

public class CatalogAndUiTests
{
    [Fact]
    public void Catalog_IsValid() => Assert.Empty(FrontierCatalog.Validate(FrontierCatalog.All));

    [Fact]
    public void Catalog_DetectsDuplicatesAndBadData()
    {
        var bad = new List<FrontierBlock>
        {
            new FrontierBlock { Id = "MPM_A", Name = "A", Tier = 1, Donors = new[] { "Frame" } },
            new FrontierBlock { Id = "MPM_A", Name = "", Tier = 7, Donors = new string[0], MassScale = 0 },
        };
        var errors = FrontierCatalog.Validate(bad);
        Assert.Contains(errors, e => e.Contains("duplicate"));
        Assert.Contains(errors, e => e.Contains("tier"));
        Assert.Contains(errors, e => e.Contains("donors"));
    }

    [Fact]
    public void PickDonor_PrefersExactThenShortestMatch()
    {
        var block = FrontierCatalog.All.First(b => b.Id == "MPM_PlasmaDrive");
        var names = new[] { "Small Electric Thruster", "Large Electric Thruster Mk2", "Large Electric Thruster" };
        Assert.Equal("Large Electric Thruster", FrontierCatalog.PickDonor(block, names));
        Assert.Null(FrontierCatalog.PickDonor(block, new[] { "Seat", "Frame" }));
    }

    [Fact]
    public void ScaleFor_TunesPowerButNeverMassOrTemperature()
    {
        var reactor = FrontierCatalog.All.First(b => b.Id == "MPM_FusionReactor");
        Assert.Equal(12f, FrontierCatalog.ScaleFor(reactor, "_maxPower"));
        Assert.Equal(12f, FrontierCatalog.ScaleFor(reactor, "_capacity"));
        Assert.Equal(1f, FrontierCatalog.ScaleFor(reactor, "_mass"));
        Assert.Equal(1f, FrontierCatalog.ScaleFor(reactor, "_maxTemperature"));
    }

    [Fact]
    public void Tiers_UnlockByDeliveries()
    {
        var t1 = FrontierCatalog.All.First(b => b.Tier == 1);
        var t3 = FrontierCatalog.All.First(b => b.Tier == 3);
        Assert.False(FrontierCatalog.Unlocked(t1, 0, false));
        Assert.True(FrontierCatalog.Unlocked(t1, 1, false));
        Assert.False(FrontierCatalog.Unlocked(t3, 5, false));
        Assert.True(FrontierCatalog.Unlocked(t3, 0, true));
    }

    [Theory]
    [InlineData(4, 1, 4)]
    [InlineData(12, 2, 6)]
    [InlineData(24, 3, 8)]
    public void Roster_GridFitsEveryPlayer(int capacity, int columns, int rows)
    {
        Assert.Equal(columns, RosterLayout.Columns(capacity));
        Assert.Equal(rows, RosterLayout.Rows(capacity));
        Assert.True(RosterLayout.Columns(capacity) * RosterLayout.Rows(capacity) >= capacity);
    }

    [Fact]
    public void Palette_GivesTwentyDistinctExtraColours()
    {
        var colours = PlayerPalette.Generate(4, 24);
        Assert.Equal(20, colours.Count);
        for (int i = 0; i < colours.Count; i++)
            for (int j = i + 1; j < colours.Count; j++)
            {
                var a = colours[i]; var b = colours[j];
                double d = System.Math.Abs(a.r - b.r) + System.Math.Abs(a.g - b.g) + System.Math.Abs(a.b - b.b);
                Assert.True(d > 0.08, $"colours {i + 4} and {j + 4} are too similar");
            }
    }
}
