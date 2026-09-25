using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace KingdomEightCrowns;

internal static class KingdomPlayerLookup
{
	internal static void Apply(Harmony harmony)
	{
		harmony.Patch(AccessTools.Method(typeof(Kingdom), "GetPlayer", new Type[1] { typeof(int) }) ?? throw new MissingMethodException("Unable to resolve Kingdom.GetPlayer(int) for the expanded lookup."), new HarmonyMethod(AccessTools.Method(typeof(KingdomPlayerLookup), "BeforeGetPlayer")));
		Plugin.LogSource.LogInfo("Installed expanded Kingdom.GetPlayer lookup for active registries above two players.");
	}

	private static bool BeforeGetPlayer(Kingdom __instance, int playerId, ref Player __result)
	{
		Il2CppReferenceArray<Player> activePlayers = __instance._activePlayers;
		if (activePlayers == null || activePlayers.Length <= 2)
		{
			return true;
		}
		if (playerId < 0 || playerId >= activePlayers.Length)
		{
			__result = null;
			return false;
		}
		__result = activePlayers[playerId];
		return false;
	}
}
