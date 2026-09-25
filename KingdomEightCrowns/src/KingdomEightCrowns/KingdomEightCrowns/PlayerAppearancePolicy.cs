namespace KingdomEightCrowns;

internal static class PlayerAppearancePolicy
{
	internal const int DefaultHorseNetId = 924;

	internal const int DefaultHorseSteedType = 9;

	internal static bool UsesStockLookup(int logicalPlayerId)
	{
		return logicalPlayerId == 0;
	}

	internal static bool UsesStockLookupUntilCommitted(int logicalPlayerId)
	{
		return logicalPlayerId == 1;
	}

	internal static bool RequiresDefaultRemoteMount(int logicalPlayerId)
	{
		if (logicalPlayerId > 0)
		{
			return logicalPlayerId < 8;
		}
		return false;
	}
}
