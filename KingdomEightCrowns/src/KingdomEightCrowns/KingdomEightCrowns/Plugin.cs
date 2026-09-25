using System;
using System.IO;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace KingdomEightCrowns;

[BepInPlugin("openai.kingdomtwocrowns.eightcrowns", "Kingdom Eight Crowns", "0.6.23-alpha")]
[BepInProcess("KingdomTwoCrowns.exe")]
public sealed class Plugin : BasePlugin
{
	public const string PluginGuid = "openai.kingdomtwocrowns.eightcrowns";

	public const string PluginName = "Kingdom Eight Crowns";

	public const string PluginVersion = "0.6.23-alpha";

	public const string SupportedGameVersion = "2.1.4";

	public const string SupportedGameAssemblySha256 = "acba335692e832a70bff06f092838dfd0cdfb0a0adbd37b4528f7cbddc89e103";

	private Harmony? _harmony;

	internal static ManualLogSource LogSource { get; private set; } = null;

	internal static int MaxPlayers { get; private set; } = 8;

	internal static bool ExtendedTopologyEnabled => MaxPlayers > 2;

	internal static bool DiagnosticsEnabled { get; private set; } = true;

	// Per-RPC / per-input tracing. Off by default: it runs on every CRPC the game dispatches.
	internal static bool HotPathTracingEnabled { get; private set; }

	internal static bool SinglePcProbeEnabled { get; private set; }

	internal static int SimulatedClients { get; private set; } = 7;

	internal static bool PlayerBodyProbeEnabled { get; private set; }

	internal static int ProbePlayerCount { get; private set; } = 8;

	internal static bool DynamicBodyHooksEnabled { get; private set; } = true;

	public override void Load()
	{
		LogSource = base.Log;
		ConfigEntry<int> configEntry = base.Config.Bind("Multiplayer", "MaxPlayers", 8, "Maximum session capacity, including the host. Sessions may use any lower count. Valid range: 2-8.");
		ConfigEntry<bool> configEntry2 = base.Config.Bind("Diagnostics", "Enabled", defaultValue: true, "Log the original network router's connection and disconnection callbacks.");
		ConfigEntry<bool> configEntry8 = base.Config.Bind("Diagnostics", "HotPathTracing", defaultValue: false, "Trace every CRPC dispatch and player input (first-use lines and volume samples). Costs frame time with many players; enable only when collecting logs for a bug report.");
		ConfigEntry<bool> configEntry3 = base.Config.Bind("SinglePcProbe", "Enabled", defaultValue: false, "Developer diagnostic. Keep false for real sessions.");
		ConfigEntry<bool> configEntry4 = base.Config.Bind("DynamicBodyHooks", "Enabled", defaultValue: true, "Keep false to silence the experimental Payable.Select and RegisterObject hooks for ESC-crash isolation; native 8-player patches stay active.");
		ConfigEntry<int> configEntry5 = base.Config.Bind("SinglePcProbe", "SimulatedClients", 7, "Number of temporary loopback clients. Valid range: 2-7.");
		ConfigEntry<bool> configEntry6 = base.Config.Bind("PlayerBodyProbe", "Enabled", defaultValue: false, "Developer diagnostic that swaps in fake player bodies for 15 seconds. Keep false for real sessions.");
		ConfigEntry<int> configEntry7 = base.Config.Bind("PlayerBodyProbe", "PlayerCount", 8, "Temporary occupied-slot count for the body probe. Valid range: 2-MaxPlayers.");
		MaxPlayers = Math.Clamp(configEntry.Value, 2, 8);
		DiagnosticsEnabled = configEntry2.Value;
		HotPathTracingEnabled = configEntry8.Value;
		SinglePcProbeEnabled = configEntry3.Value;
		DynamicBodyHooksEnabled = configEntry4.Value;
		SimulatedClients = Math.Clamp(configEntry5.Value, 2, 7);
		PlayerBodyProbeEnabled = configEntry6.Value;
		ProbePlayerCount = Math.Clamp(configEntry7.Value, 2, MaxPlayers);
		LogSource.LogInfo("Kingdom Eight Crowns " + PluginVersion + " loading.");
		LogSource.LogInfo("Detected game version: " + Application.version);
		if (!VerifyBuild())
		{
			LogSource.LogError("This build is not the supplied Kingdom Two Crowns 2.1.4 (r22452) executable. No native patch was applied.");
			return;
		}
		try
		{
			NativePatch.ApplyConnectionLimit(MaxPlayers);
			NativePatch.ApplyPlayerIdLimit(MaxPlayers - 1);
			_harmony = new Harmony("openai.kingdomtwocrowns.eightcrowns");
			PlayerAppearanceLookup.Apply(_harmony);
			KingdomPlayerLookup.Apply(_harmony);
			NetworkDiagnostics.Apply(_harmony);
			LogSource.LogInfo($"Eight-player core ready: maximum capacity {MaxPlayers}, hot-path tracing {(HotPathTracingEnabled ? "on" : "off")}.");
			if (PlayerBodyProbeEnabled)
			{
				LogSource.LogWarning($"[Player-body probe] Developer probe enabled with {ProbePlayerCount} temporary slot(s); disable [PlayerBodyProbe] Enabled for real sessions.");
			}
		}
		catch (Exception arg)
		{
			_harmony?.UnpatchSelf();
			_harmony = null;
			LogSource.LogError("The eight-player development patches could not be enabled. " + $"The game will continue without them. Details: {arg}");
		}
	}

	private bool VerifyBuild()
	{
		try
		{
			string text = Path.Combine(Paths.GameRootPath, "GameAssembly.dll");
			if (!File.Exists(text))
			{
				LogSource.LogError("GameAssembly.dll was not found at " + text);
				return false;
			}
			using FileStream inputStream = File.OpenRead(text);
			using SHA256 sHA = SHA256.Create();
			string text2 = BitConverter.ToString(sHA.ComputeHash(inputStream)).Replace("-", string.Empty).ToLowerInvariant();
			LogSource.LogInfo("GameAssembly SHA-256: " + text2);
			return string.Equals(text2, "acba335692e832a70bff06f092838dfd0cdfb0a0adbd37b4528f7cbddc89e103", StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception t)
		{
			ManualLogSource logSource = LogSource;
			bool isEnabled;
			BepInExErrorLogInterpolatedStringHandler bepInExErrorLogInterpolatedStringHandler = new BepInExErrorLogInterpolatedStringHandler(27, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExErrorLogInterpolatedStringHandler.AppendLiteral("Build verification failed: ");
				bepInExErrorLogInterpolatedStringHandler.AppendFormatted(t);
			}
			logSource.LogError(bepInExErrorLogInterpolatedStringHandler);
			return false;
		}
	}
}
