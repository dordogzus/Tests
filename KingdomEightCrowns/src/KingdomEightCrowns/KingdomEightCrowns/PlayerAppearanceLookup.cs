using System;
using HarmonyLib;

namespace KingdomEightCrowns;

internal static class PlayerAppearanceLookup
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(Player), "GetAppearance", Type.EmptyTypes) ?? throw new MissingMethodException("Unable to resolve Player.GetAppearance() for the expanded lookup."), new HarmonyMethod(AccessTools.Method(typeof(PlayerAppearanceLookup), "BeforeGetAppearance")), null, null, new HarmonyMethod(AccessTools.Method(typeof(PlayerAppearanceLookup), "AfterGetAppearanceFault")), null);
		Plugin.LogSource.LogInfo("Installed expanded Player.GetAppearance lookup for player IDs 0-7.");
	}

	private static bool BeforeGetAppearance(Player __instance, ref PlayerModel __result)
	{
		try
		{
			int num = DynamicPlayerRegistry.ResolveLogicalPlayerId(__instance);
			if (PlayerAppearancePolicy.UsesStockLookup(num))
			{
				return true;
			}
			if (PlayerAppearanceRegistry.TryGet(num, out PlayerModel appearance) && appearance != null)
			{
				__result = appearance;
				return false;
			}
			if (PlayerAppearancePolicy.UsesStockLookupUntilCommitted(num))
			{
				return true;
			}
			if (!PlayerAppearanceRegistry.TryGetOrCreateFallback(num, __instance, out appearance) || appearance == null)
			{
				return true;
			}
			__result = appearance;
			return false;
		}
		catch
		{
			return true;
		}
	}

	private static Exception? AfterGetAppearanceFault(Exception? __exception, Player __instance, ref PlayerModel __result)
	{
		if (__exception == null)
		{
			return null;
		}
		try
		{
			int num = DynamicPlayerRegistry.ResolveLogicalPlayerId(__instance);
			if (PlayerAppearanceRegistry.TryGetOrCreateFallback(num, __instance, out PlayerModel appearance) && appearance != null)
			{
				__result = appearance;
				Plugin.LogSource.LogWarning($"[Appearance lookup] Recovered Player {num + 1} " + "from a native GetAppearance setup gap without aborting the join.");
				return null;
			}
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogError("[Appearance lookup] Safe GetAppearance recovery also failed; preserving the original native exception. " + ex);
		}
		return __exception;
	}
}
