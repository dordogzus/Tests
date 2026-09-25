using System.Linq;
using MorePlayersMod.Logic;
using Xunit;

public class CrewProgressionTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(12, 3)]
    [InlineData(24, 6)]
    public void CrewScale_GrowsPerFourPlayers(int crew, int scale) => Assert.Equal(scale, CrewProgression.CrewScale(crew));

    [Fact]
    public void MoonRun_PaysFourSeatsPerFourCrew()
    {
        int Seats(int crew) => CrewProgression.Packs(SupplyRoute.Moon, crew).Single(p => p.Key == "Seats").Value;
        Assert.Equal(4, Seats(3));
        Assert.Equal(12, Seats(12));
        Assert.Equal(24, Seats(24));
    }

    [Fact]
    public void Routes_AreMatchedByObjectiveId()
    {
        Assert.Equal(SupplyRoute.Moon, CrewProgression.RouteFor("Package_Moon_Crater"));
        Assert.Equal(SupplyRoute.Earth, CrewProgression.RouteFor("Package_Earth_LostInTransit"));
        Assert.Equal(SupplyRoute.OuterRim, CrewProgression.RouteFor("Package_Baobara_Relay"));
    }

    [Fact]
    public void EveryRoute_PaysSomethingAndNeverNegative()
    {
        foreach (SupplyRoute route in System.Enum.GetValues(typeof(SupplyRoute)))
            foreach (int crew in new[] { 1, 4, 9, 24, 64 })
            {
                var packs = CrewProgression.Packs(route, crew);
                Assert.NotEmpty(packs);
                Assert.All(packs, p => Assert.True(p.Value > 0));
            }
    }

    [Fact]
    public void TwentyFourCrew_NeedsOnlyOneMoonRunForSeats() =>
        Assert.Equal(1, CrewProgression.RunsUntilEverySeat(24, 4));

    [Theory]
    [InlineData(1, 2)]
    [InlineData(24, 24)]
    [InlineData(500, 64)]
    public void MaxPlayers_IsClamped(int input, int expected) => Assert.Equal(expected, CrewProgression.ClampMaxPlayers(input));
}
