using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace KingdomEightCrowns.AppearanceFlow;

[BepInPlugin("openai.kingdomtwocrowns.eightcrowns.appearanceflow", "Kingdom Eight Crowns - Appearance Flow", "0.14.20-alpha")]
[BepInDependency("openai.kingdomtwocrowns.eightcrowns", BepInDependency.DependencyFlags.HardDependency)]
[BepInProcess("KingdomTwoCrowns.exe")]
public sealed class Plugin : BasePlugin
{
	public const string PluginGuid = "openai.kingdomtwocrowns.eightcrowns.appearanceflow";

	public const string PluginName = "Kingdom Eight Crowns - Appearance Flow";

	public const string PluginVersion = "0.14.20-alpha";

	private Harmony _safetyHarmony;

	private Harmony _modeHarmony;

	private Harmony _loopbackHarmony;

	internal static ManualLogSource LogSource { get; private set; }

	public override void Load()
	{
		LogSource = base.Log;
		LogSource.LogInfo("Kingdom Eight Crowns - Appearance Flow 0.14.20-alpha loading.");
		_safetyHarmony = new Harmony("openai.kingdomtwocrowns.eightcrowns.appearanceflow.safety");
		try
		{
			NativeCampaignAppearance.InstallSafetyOnly(_safetyHarmony);
		}
		catch (Exception exception)
		{
			_safetyHarmony.UnpatchSelf();
			_safetyHarmony = null;
			LogSource.LogError("[Appearance flow] The permanent safety layer could not be installed, so the companion stopped before enabling any experimental mode. Details: " + Unwrap(exception));
			return;
		}
		_modeHarmony = new Harmony("openai.kingdomtwocrowns.eightcrowns.appearanceflow.mode");
		try
		{
			if (AppearanceFlowSettings.OnePcNativeSequence)
			{
				NativeCampaignAppearance.Install(_modeHarmony);
				LogSource.LogWarning("[Appearance flow] OnePcNativeSequence=true. The verified eight-screen native selector diagnostic is enabled.");
				return;
			}
			RealSessionNetwork.Install(_modeHarmony);
			LogSource.LogWarning("[Appearance flow] Dynamic real-session mode is active. A campaign host selects only the host ruler; each actual joining client uses its own game-native ruler selector.");
			LogSource.LogWarning("[Appearance flow] Effective isolation switches: OverlayDisableAll=" + AppearanceFlowSettings.OverlayDisableAll + ", OverlayBadge=" + AppearanceFlowSettings.OverlayBadge + ", OverlayScrollbar=" + AppearanceFlowSettings.OverlayScrollbar + ", OverlayFontSwaps=" + AppearanceFlowSettings.OverlayFontSwaps + ", OverlayCrossfadeMotion=" + AppearanceFlowSettings.OverlayCrossfadeMotion + ", DisableOpenFriends=" + AppearanceFlowSettings.DynamicSessionDisableOpenFriends + ", DisablePeerWatchdog=" + AppearanceFlowSettings.DynamicSessionDisablePeerWatchdog + ".");
			if (!AppearanceFlowSettings.SafeLoopbackDiagnostic)
			{
				return;
			}
			_loopbackHarmony = new Harmony("openai.kingdomtwocrowns.eightcrowns.appearanceflow.loopback");
			try
			{
				SafeLoopbackDiagnostic.Install(_loopbackHarmony);
			}
			catch (Exception exception2)
			{
				_loopbackHarmony.UnpatchSelf();
				_loopbackHarmony = null;
				LogSource.LogError("[Safe loopback] The optional one-PC diagnostic could not be installed. Its partial patches were removed; dynamic real-session mode and the permanent body safety layer remain active. Details: " + Unwrap(exception2));
			}
		}
		catch (Exception exception3)
		{
			_modeHarmony.UnpatchSelf();
			_modeHarmony = null;
			LogSource.LogError("[Appearance flow] The selected appearance/network mode could not be installed. Its partial patches were removed, while the independent safety layer remains active and the stable eight-slot core remains unchanged. Details: " + Unwrap(exception3));
		}
	}

	private static Exception Unwrap(Exception exception)
	{
		if (!(exception is TargetInvocationException { InnerException: not null } ex))
		{
			return exception;
		}
		return ex.InnerException;
	}
}
