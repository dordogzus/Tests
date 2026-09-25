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

[BepInPlugin("openai.kingdomtwocrowns.eightcrowns", "Kingdom Eight Crowns", "0.6.22-alpha")]
[BepInProcess("KingdomTwoCrowns.exe")]
public sealed class Plugin : BasePlugin
{
	public const string PluginGuid = "openai.kingdomtwocrowns.eightcrowns";

	public const string PluginName = "Kingdom Eight Crowns";

	public const string PluginVersion = "0.6.22-alpha";

	public const string SupportedGameVersion = "2.1.4";

	public const string SupportedGameAssemblySha256 = "acba335692e832a70bff06f092838dfd0cdfb0a0adbd37b4528f7cbddc89e103";

	private Harmony? _harmony;

	internal static ManualLogSource LogSource { get; private set; } = null;

	internal static int MaxPlayers { get; private set; } = 8;

	internal static bool ExtendedTopologyEnabled => MaxPlayers > 2;

	internal static bool DiagnosticsEnabled { get; private set; } = true;

	internal static bool SinglePcProbeEnabled { get; private set; } = true;

	internal static int SimulatedClients { get; private set; } = 7;

	internal static bool PlayerBodyProbeEnabled { get; private set; } = true;

	internal static int ProbePlayerCount { get; private set; } = 8;

	internal static bool DynamicBodyHooksEnabled { get; private set; } = true;

	public override void Load()
	{
		LogSource = base.Log;
		ConfigEntry<int> configEntry = base.Config.Bind("Multiplayer", "MaxPlayers", 8, "Maximum session capacity, including the host. Sessions may use any lower count. Valid range: 2-8.");
		ConfigEntry<bool> configEntry2 = base.Config.Bind("Diagnostics", "Enabled", defaultValue: true, "Log the original network router's connection and disconnection callbacks.");
		ConfigEntry<bool> configEntry3 = base.Config.Bind("SinglePcProbe", "Enabled", defaultValue: true, "When hosting, automatically connect temporary loopback clients and log the result.");
		ConfigEntry<bool> configEntry4 = base.Config.Bind("DynamicBodyHooks", "Enabled", defaultValue: true, "Keep false to silence the experimental Payable.Select and RegisterObject hooks for ESC-crash isolation; native 8-player patches stay active.");
		ConfigEntry<int> configEntry5 = base.Config.Bind("SinglePcProbe", "SimulatedClients", 7, "Number of temporary loopback clients. Valid range: 2-7.");
		ConfigEntry<bool> configEntry6 = base.Config.Bind("PlayerBodyProbe", "Enabled", defaultValue: true, "In a campaign, temporarily create and register additional visible player-body clones.");
		ConfigEntry<int> configEntry7 = base.Config.Bind("PlayerBodyProbe", "PlayerCount", 8, "Temporary occupied-slot count for the body probe. Valid range: 2-MaxPlayers.");
		MaxPlayers = Math.Clamp(configEntry.Value, 2, 8);
		DiagnosticsEnabled = configEntry2.Value;
		SinglePcProbeEnabled = configEntry3.Value;
		DynamicBodyHooksEnabled = configEntry4.Value;
		SimulatedClients = Math.Clamp(configEntry5.Value, 2, 7);
		PlayerBodyProbeEnabled = configEntry6.Value;
		ProbePlayerCount = Math.Clamp(configEntry7.Value, 2, MaxPlayers);
		LogSource.LogInfo("Kingdom Eight Crowns 0.6.22-alpha loading.");
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
			LogSource.LogWarning($"Player-body registry probe active with {ProbePlayerCount} temporary occupied slot(s) and a maximum capacity of {MaxPlayers}. Additional bodies are visual clones, receive " + "independent eight-slot PlayerModel appearances, use remote authority, and are verified through the expanded player and appearance lookups. They are removed automatically. The final session will only create slots for players who actually join.");
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
