using System;
using System.Linq;
using System.Reflection;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using HarmonyLib;

namespace KingdomEightCrowns;

internal static class NetworkDiagnostics
{
	private static int _serverConnectCallbacks;

	private static int _serverDisconnectCallbacks;

	internal static void Apply(Harmony harmony)
	{
		Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly candidate) => string.Equals(candidate.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal));
		Type type = assembly?.GetType("UNetRouter", throwOnError: false);
		Type type2 = assembly?.GetType("CustomNetworkConnection", throwOnError: false);
		Type type3 = assembly?.GetType("Player", throwOnError: false);
		Type type4 = assembly?.GetType("Kingdom", throwOnError: false);
		if ((object)type == null || (object)type2 == null || (object)type3 == null || (object)type4 == null)
		{
			throw new TypeLoadException("The 2.1.4 IL2CPP interop networking/player types were not generated.");
		}
		PatchPostfix(harmony, AccessTools.Method(type, "StartHostingCore"), "AfterStartHostingCore");
		PatchPrefix(harmony, AccessTools.Method(type, "Server_HandleOnConnect"), "BeforeServerConnect");
		PatchPrefix(harmony, AccessTools.Method(type, "Server_HandleOnDisconnect"), "BeforeServerDisconnect");
		PatchPrefix(harmony, AccessTools.Method(type, "Client_HandleOnConnect"), "BeforeClientConnect");
		PatchPrefix(harmony, AccessTools.Method(type, "Client_HandleOnDisconnect"), "BeforeClientDisconnect");
		PatchPrefix(harmony, AccessTools.Method(type2, "PollAsHost"), "BeforeHostPoll");
		PatchPostfix(harmony, AccessTools.Method(type4, "Update"), "AfterKingdomUpdate");
		PatchPrefix(harmony, AccessTools.Method(type4, "OnLevelLoaded"), "BeforeKingdomLevelLoaded");
		PatchPostfix(harmony, AccessTools.Method(type4, "OnDestroy"), "AfterKingdomDestroyed");
		SinglePcSelfTest.Configure(type2);
		PlayerBodyProbe.Configure(type4, type3);
		if (Plugin.ExtendedTopologyEnabled)
		{
			DynamicPlayerRegistry.Apply(harmony);
		}
		else
		{
			Plugin.LogSource.LogWarning("Two-player parity mode is active (MaxPlayers=2): the dynamic body registry is disabled and all networking stays game-native.");
		}
		Plugin.LogSource.LogInfo("Installed 2.1.4 network and campaign diagnostics.");
	}

	private static void PatchPrefix(Harmony harmony, MethodBase? original, string patchName)
	{
		if ((object)original == null)
		{
			throw new MissingMethodException("Unable to find method required by diagnostic patch " + patchName + ".");
		}
		harmony.Patch(original, new HarmonyMethod(AccessTools.Method(typeof(NetworkDiagnostics), patchName)));
	}

	private static void PatchPostfix(Harmony harmony, MethodBase? original, string patchName)
	{
		if ((object)original == null)
		{
			throw new MissingMethodException("Unable to find method required by diagnostic patch " + patchName + ".");
		}
		harmony.Patch(original, null, new HarmonyMethod(AccessTools.Method(typeof(NetworkDiagnostics), patchName)));
	}

	private static void AfterStartHostingCore(object __instance, bool __result)
	{
		if (Plugin.DiagnosticsEnabled)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(57, 2, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("UNetRouter.StartHostingCore returned ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(__result);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("; maximum capacity=");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(Plugin.MaxPlayers);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
		}
	}

	private static bool BeforeServerConnect()
	{
		int t = ++_serverConnectCallbacks;
		if (Plugin.DiagnosticsEnabled)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(38, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("Server connection callback #");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(t);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" received.");
			}
			logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		}
		if (!SinglePcSelfTest.Active)
		{
			return true;
		}
		Plugin.LogSource.LogInfo("[Single-PC probe] Intercepted the loopback server-connect callback; gameplay/player creation was intentionally skipped.");
		SinglePcSelfTest.RecordServerConnect();
		return false;
	}

	private static bool BeforeServerDisconnect()
	{
		int t = ++_serverDisconnectCallbacks;
		if (Plugin.DiagnosticsEnabled)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(41, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("Server disconnection callback #");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(t);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" received.");
			}
			logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		}
		if (!SinglePcSelfTest.Active)
		{
			return true;
		}
		Plugin.LogSource.LogInfo("[Single-PC probe] Intercepted the loopback server-disconnect callback; gameplay/player state was intentionally left unchanged.");
		return false;
	}

	private static bool BeforeClientConnect()
	{
		if (Plugin.DiagnosticsEnabled)
		{
			Plugin.LogSource.LogInfo("Client connection callback received.");
		}
		if (!SinglePcSelfTest.Active)
		{
			return true;
		}
		Plugin.LogSource.LogInfo("[Single-PC probe] Intercepted the loopback client-connect callback; client gameplay setup was intentionally skipped.");
		return false;
	}

	private static bool BeforeClientDisconnect()
	{
		if (Plugin.DiagnosticsEnabled)
		{
			Plugin.LogSource.LogInfo("Client disconnection callback received.");
		}
		if (!SinglePcSelfTest.Active)
		{
			DynamicPlayerRegistry.ResetSession("client disconnected");
			return true;
		}
		Plugin.LogSource.LogInfo("[Single-PC probe] Intercepted the loopback client-disconnect callback; client gameplay state was intentionally left unchanged.");
		return false;
	}

	private static void BeforeHostPoll()
	{
	}

	private static void AfterKingdomUpdate(object __instance)
	{
		PlayerBodyProbe.Tick(__instance);
		DynamicPlayerRegistry.Tick((Kingdom)__instance);
	}

	private static void BeforeKingdomLevelLoaded()
	{
		PlayerBodyProbe.Reset("level loading");
		DynamicPlayerRegistry.ResetLevel("level loading");
	}

	private static void AfterKingdomDestroyed()
	{
		PlayerBodyProbe.Reset("kingdom destroyed");
		DynamicPlayerRegistry.ResetSession("kingdom destroyed");
	}
}
