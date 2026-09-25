using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace KingdomEightCrowns.AppearanceFlow;

internal static class AppearanceFlowSettings
{
	private static readonly Dictionary<string, bool> SettingCache = new Dictionary<string, bool>();

	private static readonly Dictionary<string, float> FloatCache = new Dictionary<string, float>();

	internal static bool OnePcNativeSequence => ReadBoolean("OnePcNativeSequence", fallback: false);

	internal static bool SafeLoopbackDiagnostic => ReadBoolean("SafeLoopbackDiagnostic", fallback: false);

	internal static bool OverlayDisableAll => ReadBoolean("OverlayDisableAll", fallback: false);

	internal static bool OverlayBadge => ReadBoolean("OverlayBadge", fallback: true);

	internal static bool OverlayScrollbar => ReadBoolean("OverlayScrollbar", fallback: true);

	internal static bool OverlayFontSwaps => ReadBoolean("OverlayFontSwaps", fallback: true);

	internal static bool OverlayCrossfadeMotion => ReadBoolean("OverlayCrossfadeMotion", fallback: true);

	internal static bool DynamicSessionDisableOpenFriends => ReadBoolean("DynamicSessionDisableOpenFriends", fallback: false);

	internal static bool DynamicSessionDisablePeerWatchdog => ReadBoolean("DynamicSessionDisablePeerWatchdog", fallback: false);

	internal static float BadgeScale => ReadFloat("BadgeScale", 1f);

	internal static float BadgeFontSize => ReadFloat("BadgeFontSize", 8f);

	internal static bool CounterForceColor => ReadBoolean("CounterForceColor", fallback: true);

	internal static float CounterColorR => ReadFloat("CounterColorR", 255f);

	internal static float CounterColorG => ReadFloat("CounterColorG", 255f);

	internal static float CounterColorB => ReadFloat("CounterColorB", 255f);

	internal static float BadgeXRatio => ReadFloat("BadgeXRatio", 0.7563f);

	internal static float BadgeYMultiplier => ReadFloat("BadgeYMultiplier", 2.45f);

	internal static float BadgeLineGapRatio => ReadFloat("BadgeLineGapRatio", 0.15f);

	internal static float CounterScale => ReadFloat("CounterScale", 1.25f);

	internal static float NicknameScale => ReadFloat("NicknameScale", 1.375f);

	internal static float ScrollbarTrackR => ReadFloat("ScrollbarTrackR", 133f);

	internal static float ScrollbarTrackG => ReadFloat("ScrollbarTrackG", 110f);

	internal static float ScrollbarTrackB => ReadFloat("ScrollbarTrackB", 38f);

	internal static float ScrollbarTrackOutlineR => ReadFloat("ScrollbarTrackOutlineR", 45f);

	internal static float ScrollbarTrackOutlineG => ReadFloat("ScrollbarTrackOutlineG", 40f);

	internal static float ScrollbarTrackOutlineB => ReadFloat("ScrollbarTrackOutlineB", 36f);

	internal static float ScrollbarThumbR => ReadFloat("ScrollbarThumbR", 150f);

	internal static float ScrollbarThumbG => ReadFloat("ScrollbarThumbG", 150f);

	internal static float ScrollbarThumbB => ReadFloat("ScrollbarThumbB", 158f);

	internal static float ScrollbarPixelScale => ReadFloat("ScrollbarPixelScale", 2f);

	internal static float ScrollbarBottomExtendRatio => ReadFloat("ScrollbarBottomExtendRatio", 0.1f);

	internal static bool CounterPixelOverlay => ReadBoolean("CounterPixelOverlay", fallback: false);

	private static bool ReadBoolean(string settingName, bool fallback)
	{
		lock (SettingCache)
		{
			if (SettingCache.TryGetValue(settingName, out var value))
			{
				return value;
			}
		}
		string text = Path.Combine(AppContext.BaseDirectory, "BepInEx", "config", "openai.kingdomtwocrowns.eightcrowns.appearanceflow.cfg");
		if (!File.Exists(text))
		{
			CacheSetting(settingName, fallback);
			return fallback;
		}
		try
		{
			string[] array = File.ReadAllLines(text);
			for (int i = 0; i < array.Length; i++)
			{
				string text2 = array[i].Trim();
				if (text2.Length == 0 || text2.StartsWith("#", StringComparison.Ordinal) || text2.StartsWith(";", StringComparison.Ordinal))
				{
					continue;
				}
				int num = text2.IndexOf('=');
				if (num >= 0 && string.Equals(text2.Substring(0, num).Trim(), settingName, StringComparison.OrdinalIgnoreCase))
				{
					if (!bool.TryParse(text2.Substring(num + 1).Trim(), out var result))
					{
						result = fallback;
					}
					CacheSetting(settingName, result);
					return result;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Appearance flow] Could not read setting " + settingName + " from " + text + "; using " + fallback + ". Details: " + ex.Message);
		}
		CacheSetting(settingName, fallback);
		return fallback;
	}

	private static void CacheSetting(string settingName, bool value)
	{
		lock (SettingCache)
		{
			SettingCache[settingName] = value;
		}
	}

	internal static float ReadFloat(string settingName, float fallback)
	{
		lock (FloatCache)
		{
			if (FloatCache.TryGetValue(settingName, out var value))
			{
				return value;
			}
		}
		string path = Path.Combine(AppContext.BaseDirectory, "BepInEx", "config", "openai.kingdomtwocrowns.eightcrowns.appearanceflow.cfg");
		if (!File.Exists(path))
		{
			CacheFloat(settingName, fallback);
			return fallback;
		}
		try
		{
			string[] array = File.ReadAllLines(path);
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i].Trim();
				if (text.Length == 0 || text.StartsWith("#", StringComparison.Ordinal) || text.StartsWith(";", StringComparison.Ordinal))
				{
					continue;
				}
				int num = text.IndexOf('=');
				if (num >= 0 && string.Equals(text.Substring(0, num).Trim(), settingName, StringComparison.OrdinalIgnoreCase))
				{
					if (!float.TryParse(text.Substring(num + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
					{
						result = fallback;
					}
					CacheFloat(settingName, result);
					return result;
				}
			}
		}
		catch
		{
		}
		CacheFloat(settingName, fallback);
		return fallback;
	}

	private static void CacheFloat(string settingName, float value)
	{
		lock (FloatCache)
		{
			FloatCache[settingName] = value;
		}
	}
}
