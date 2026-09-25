using System.Collections.Generic;
using MorePlayersMod.Logic;
using Xunit;

public class PlacementAndLobbyTests
{
    private const string KeyA = "0123456789ABCDEF0123456789ABCDEF";
    private const string KeyB = "FEDCBA9876543210FEDCBA9876543210";

    [Fact]
    public void Placement_RoundTripsExactFloats()
    {
        var r = new PlacementRecord { Prefab = 123456789012345UL, Stamp = 42, PX = 287.123456f, PY = -1.5f, PZ = 1e-7f, RX = 0, RY = 0.70710677f, RZ = 0, RW = 0.70710677f };
        var parsed = PlacementFile.Parse(PlacementFile.Serialize(new Dictionary<string, PlacementRecord> { [KeyA] = r }));
        Assert.Equal(r, parsed[KeyA]);
    }

    [Fact]
    public void Placement_SkipsMalformedLines()
    {
        string text = PlacementFile.Header + "\nnot a record\n" + KeyA + " 0 1 1 2 3 0 0 0 1\n" +
                      KeyB + " 5 1 NaN 2 3 0 0 0 1\n" + "ZZ 5 1 1 2 3 0 0 0 1\n" + KeyB + " 5 1 1 2 3 0 0 0 1\n";
        var parsed = PlacementFile.Parse(text);
        Assert.Single(parsed);
        Assert.True(parsed.ContainsKey(KeyB));
    }

    [Fact]
    public void Placement_MergeReplacesAndPrunesOldest()
    {
        var store = new Dictionary<string, PlacementRecord>
        {
            [KeyA] = new PlacementRecord { Prefab = 1, Stamp = 1 },
            [KeyB] = new PlacementRecord { Prefab = 2, Stamp = 2 },
        };
        const string KeyC = "00000000000000000000000000000001";
        PlacementFile.Merge(store, new Dictionary<string, PlacementRecord> { [KeyC] = new PlacementRecord { Prefab = 3, Stamp = 3 } }, max: 2);
        Assert.False(store.ContainsKey(KeyA));
        Assert.True(store.ContainsKey(KeyB) && store.ContainsKey(KeyC));
    }

    [Theory]
    [InlineData("PlayerRow", "playerrow")]
    [InlineData("PlayerRow (3)", "playerrow")]
    [InlineData("PlayerRow(Clone)", "playerrow")]
    [InlineData("PlayerRow_12", "playerrow")]
    [InlineData("PlayerRowMPM_LobbyRow", "playerrow")]
    public void RowNames_Normalize(string input, string expected) => Assert.Equal(expected, LobbyRows.NormalizeRowName(input));

    [Fact]
    public void TextShowsName_AcceptsVanillaSuffixesOnly()
    {
        Assert.True(LobbyRows.TextShowsName("wimow (You)", "wimow"));
        Assert.True(LobbyRows.TextShowsName("<b>Anna</b>", "anna"));
        Assert.False(LobbyRows.TextShowsName("Annabel", "Anna"));
        Assert.False(LobbyRows.TextShowsName("", "Anna"));
    }

    [Fact]
    public void Overflow_ListsMembersVanillaDidNotShow()
    {
        var shown = new[] { "Host (You)", "Bea", "Cid", "Dan" };
        var members = new[] { "Host", "Bea", "Cid", "Dan", "Eve", "Fay" };
        Assert.Equal(new[] { "Eve", "Fay" }, LobbyRows.Overflow(shown, members));
    }
}
