using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KingdomEightCrowns.AppearanceFlow;

internal static class NativeCampaignAppearance
{
	private enum FlowState
	{
		Idle,
		WaitingForPlayerOne,
		WaitingForExtraConfirmation,
		WaitingForSelectorClose,
		WaitingToOpenSelector
	}

	private const int MinimumPlayers = 2;

	private const int MaximumPlayers = 8;

	private const double SelectorTransitionSeconds = 0.65;

	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private static readonly Dictionary<int, object> ChosenMonarchs = new Dictionary<int, object>();

	private static PropertyInfo _probePlayerCountProperty;

	private static PropertyInfo _biomeIsShownProperty;

	private static PropertyInfo _selectedBiomeProperty;

	private static FieldInfo _selectedBiomeField;

	private static FieldInfo _probeMonarchsField;

	private static MethodInfo _showNativeSkinSelect;

	private static Type _monarchType;

	private static object _biomeSelectInstance;

	private static FlowState _state;

	private static bool _campaignCreationArmed;

	private static bool _sequenceActive;

	private static bool _holdCampaignLoader;

	private static bool _confirmationCaptured;

	private static bool _probeBlockLogged;

	private static bool _loaderPauseLogged;

	private static bool _cancelReopenLogged;

	private static int _requestedPlayerCount = 8;

	private static int _currentLogicalPlayerId;

	private static int _nextLogicalPlayerId;

	private static int _selectedBiome;

	private static bool _isChallenge;

	private static double _openSelectorAt;

	internal static void Install(Harmony harmony)
	{
		Type type = RequireGameType("UI.SaveSlotsMenu");
		Type type2 = RequireGameType("Loader");
		Type type3 = RequireGameType("BiomeSelect");
		Type type4 = RequireGameType("CampaignSaveData");
		Type type5 = RequireCoreType("KingdomEightCrowns.PlayerAppearanceRegistry");
		Type type6 = RequireCoreType("KingdomEightCrowns.Plugin");
		_monarchType = RequireGameType("MonarchType");
		_probePlayerCountProperty = RequireProperty(type6, "ProbePlayerCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		_biomeIsShownProperty = RequireProperty(type3, "IsShown", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		_selectedBiomeProperty = type3.GetProperty("_selectedBiome", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		_selectedBiomeField = type3.GetField("_selectedBiome", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		_showNativeSkinSelect = FindUnique(type3, "ShowSkinSelect", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 3 && parameters[0].ParameterType == typeof(int) && parameters[1].ParameterType == typeof(int) && parameters[2].ParameterType == typeof(bool);
		});
		_probeMonarchsField = RequireField(type5, "ProbeMonarchs", BindingFlags.Static | BindingFlags.NonPublic);
		PatchPrefix(harmony, type, "StartNewCampaign", "BeforeStartNewCampaign");
		MethodInfo[] array = FindCampaignLoaderWaitPredicates(type2);
		foreach (MethodInfo original in array)
		{
			PatchPrefix(harmony, original, "BeforeCampaignLoaderWaitPredicate");
		}
		PatchPostfix(harmony, type2, "Update", "AfterLoaderUpdate");
		PatchPrefix(harmony, FindUnique(type3, "ShowBiomeSelect", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeNativeCampaignSelectorShown");
		PatchPrefix(harmony, _showNativeSkinSelect, "BeforeNativeSkinSelectorShown");
		PatchPrefix(harmony, FindUnique(type4, "SetPlayerAppearance", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 2 && parameters[0].ParameterType == _monarchType && parameters[1].ParameterType == typeof(int);
		}), "BeforeCampaignSetPlayerAppearance");
		Plugin.LogSource.LogWarning("[Appearance flow] Installed the 2.1.4 native campaign wait-predicate gate and sequential ruler flow. The original Loader iterator and its current yield object are never overridden.");
		Plugin.LogSource.LogWarning("[Appearance flow] Disabled the unsafe one-PC full-Player clone probe before object creation. This removes its incomplete-state NullReferenceException source; real network-joined Player objects are not blocked.");
	}

	internal static void InstallSafetyOnly(Harmony harmony)
	{
		Type type = RequireCoreType("KingdomEightCrowns.PlayerBodyProbe");
		Type type2 = RequireCoreType("KingdomEightCrowns.Plugin");
		PropertyInfo property = type2.GetProperty("PlayerBodyProbeEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null && property.CanWrite)
		{
			property.SetValue(null, false, null);
		}
		PropertyInfo property2 = type2.GetProperty("SinglePcProbeEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		if (property2 != null && property2.CanWrite)
		{
			property2.SetValue(null, false, null);
		}
		PatchPrefix(harmony, type, "Tick", "BlockUnsafeFullPlayerProbe");
		PatchPrefix(harmony, type, "SpawnBodies", "BlockUnsafeFullPlayerProbe");
		Plugin.LogSource.LogWarning("[Appearance flow] The temporary Player-body diagnostic is disabled at its core setting and blocked before object creation. The old multi-client loopback probe is also disabled. This safety owner remains installed even if another companion mode fails.");
	}

	private static void BeforeStartNewCampaign()
	{
		ArmNewCampaign("save-slot new-campaign command");
	}

	private static void BeforeNativeCampaignSelectorShown(object __instance, object[] __args)
	{
		if (__args == null || __args.Length != 1 || __args[0] == null)
		{
			ArmNewCampaign("full native campaign selector");
			_biomeSelectInstance = __instance;
			Plugin.LogSource.LogWarning("[Native ruler sequence] Captured the game-owned campaign selector. Confirm Player 1 normally; loading will then pause while Players 2-" + _requestedPlayerCount + " choose.");
		}
	}

	private static void BeforeNativeSkinSelectorShown(object __instance, object[] __args)
	{
		_biomeSelectInstance = __instance;
		if (_campaignCreationArmed && !_sequenceActive && _state == FlowState.WaitingForPlayerOne && ReadArgumentInt(__args, 1, -1) == 0)
		{
			_selectedBiome = ReadArgumentInt(__args, 0, 0);
			_isChallenge = ReadArgumentBool(__args, 2, fallback: false);
			Plugin.LogSource.LogWarning("[Native ruler sequence] Player 1's native ruler selector is active. Its normal confirmation will start the protected joining-player sequence.");
		}
	}

	private static bool BeforeCampaignSetPlayerAppearance(object __instance, object[] __args)
	{
		if (__args == null || __args.Length != 2)
		{
			return true;
		}
		object obj = __args[0];
		int num = ReadArgumentInt(__args, 1, -1);
		if (obj == null || num != 0)
		{
			return true;
		}
		if (_state == FlowState.WaitingForPlayerOne && _campaignCreationArmed)
		{
			_sequenceActive = true;
			_holdCampaignLoader = true;
			_currentLogicalPlayerId = 0;
			_confirmationCaptured = true;
			_selectedBiome = ReadSelectedBiome(_biomeSelectInstance, __instance);
			_state = FlowState.WaitingForSelectorClose;
			Plugin.LogSource.LogWarning("[Native ruler sequence] Player 1 confirmed " + DescribeObject(obj) + ". The Loader's native campaign wait condition is now held; the world cannot load before Players 2-" + _requestedPlayerCount + " finish.");
			return true;
		}
		if (!_sequenceActive)
		{
			return true;
		}
		if (_state == FlowState.WaitingForExtraConfirmation)
		{
			if (!_confirmationCaptured)
			{
				_confirmationCaptured = true;
				ChosenMonarchs[_currentLogicalPlayerId] = obj;
				TryWriteProbeMonarch(_currentLogicalPlayerId, obj);
				_state = FlowState.WaitingForSelectorClose;
				Plugin.LogSource.LogWarning("[Native ruler sequence] Player " + (_currentLogicalPlayerId + 1) + " confirmed " + DescribeObject(obj) + " through the native selector.");
			}
			return false;
		}
		if (_state == FlowState.WaitingForSelectorClose && _currentLogicalPlayerId > 0)
		{
			return false;
		}
		return true;
	}

	private static bool BeforeCampaignLoaderWaitPredicate(ref bool __result)
	{
		if (!_holdCampaignLoader)
		{
			return true;
		}
		if (!_loaderPauseLogged)
		{
			_loaderPauseLogged = true;
			Plugin.LogSource.LogWarning("[Native ruler sequence] The Loader's game-owned campaign wait predicate is held. Its iterator and current yield remain untouched until the final requested player confirms.");
		}
		__result = false;
		return false;
	}

	private static void AfterLoaderUpdate()
	{
		if (!_sequenceActive)
		{
			return;
		}
		try
		{
			if (!TryReadBiomeIsShown(out var isShown))
			{
				AbortAndRelease("the native selector visibility state became unavailable");
			}
			else if (_state == FlowState.WaitingForSelectorClose)
			{
				if (!isShown)
				{
					if (_currentLogicalPlayerId + 1 >= _requestedPlayerCount)
					{
						FinishNativeSequence();
					}
					else
					{
						ScheduleSelector(_currentLogicalPlayerId + 1);
					}
				}
			}
			else if (_state == FlowState.WaitingForExtraConfirmation)
			{
				if (!isShown)
				{
					if (!_cancelReopenLogged)
					{
						_cancelReopenLogged = true;
						Plugin.LogSource.LogWarning("[Native ruler sequence] Player " + (_currentLogicalPlayerId + 1) + "'s selector closed without confirmation. It will reopen; the campaign remains safely paused.");
					}
					ScheduleSelector(_currentLogicalPlayerId);
				}
			}
			else if (_state == FlowState.WaitingToOpenSelector && !(Clock.Elapsed.TotalSeconds < _openSelectorAt))
			{
				OpenScheduledSelector();
			}
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogError("[Native ruler sequence] Native selection failed. Releasing the campaign instead of leaving an infinite loading state. Details: " + Unwrap(exception));
			ReleaseCampaignLoader();
		}
	}

	private static void ScheduleSelector(int logicalPlayerId)
	{
		_nextLogicalPlayerId = logicalPlayerId;
		_openSelectorAt = Clock.Elapsed.TotalSeconds + 0.65;
		_state = FlowState.WaitingToOpenSelector;
	}

	private static void OpenScheduledSelector()
	{
		_currentLogicalPlayerId = _nextLogicalPlayerId;
		_confirmationCaptured = false;
		_cancelReopenLogged = false;
		_state = FlowState.WaitingForExtraConfirmation;
		Plugin.LogSource.LogWarning("[Native ruler sequence] Opening the game's native ruler selector for Player " + (_currentLogicalPlayerId + 1) + ". It will wait for confirmation; use the same keyboard or controller during this one-PC test.");
		_showNativeSkinSelect.Invoke(_biomeSelectInstance, new object[3] { _selectedBiome, 0, _isChallenge });
	}

	private static void FinishNativeSequence()
	{
		string text = ((ChosenMonarchs.Count == 0) ? "no joining-player choices" : string.Join(", ", from pair in ChosenMonarchs
			orderby pair.Key
			select "P" + (pair.Key + 1) + "=" + DescribeObject(pair.Value)));
		Plugin.LogSource.LogWarning("[Native ruler sequence] RESULT: " + ChosenMonarchs.Count + "/" + (_requestedPlayerCount - 1) + " joining-player rulers selected before world loading. " + text + ". Releasing the original Loader now.");
		ReleaseCampaignLoader();
	}

	private static void AbortAndRelease(string reason)
	{
		Plugin.LogSource.LogError("[Native ruler sequence] Aborted because " + reason + ". The original campaign Loader is being released safely.");
		ReleaseCampaignLoader();
	}

	private static void ReleaseCampaignLoader()
	{
		_holdCampaignLoader = false;
		_campaignCreationArmed = false;
		_sequenceActive = false;
		_confirmationCaptured = false;
		_state = FlowState.Idle;
	}

	private static bool BlockUnsafeFullPlayerProbe(MethodBase __originalMethod)
	{
		if (!_probeBlockLogged)
		{
			_probeBlockLogged = true;
			Plugin.LogSource.LogWarning("[Player-body probe] Blocked " + __originalMethod.Name + " before any full Player clone could be created. No fake bodies are expected in this build.");
		}
		return false;
	}

	private static void ArmNewCampaign(string source)
	{
		ChosenMonarchs.Clear();
		_requestedPlayerCount = ReadProbePlayerCount();
		_campaignCreationArmed = _requestedPlayerCount > 1;
		_sequenceActive = false;
		_holdCampaignLoader = false;
		_confirmationCaptured = false;
		_loaderPauseLogged = false;
		_cancelReopenLogged = false;
		_currentLogicalPlayerId = 0;
		_nextLogicalPlayerId = 1;
		_selectedBiome = 0;
		_isChallenge = false;
		_state = (_campaignCreationArmed ? FlowState.WaitingForPlayerOne : FlowState.Idle);
		Plugin.LogSource.LogWarning("[Native ruler sequence] Armed from " + source + "; requested occupied players=" + _requestedPlayerCount + ", maximum capacity=8.");
	}

	private static int ReadProbePlayerCount()
	{
		try
		{
			int val = Convert.ToInt32(_probePlayerCountProperty.GetValue(null, null));
			return Math.Max(2, Math.Min(8, val));
		}
		catch
		{
			return 8;
		}
	}

	private static int ReadSelectedBiome(object biomeSelect, object campaignSave)
	{
		try
		{
			if (biomeSelect != null && _selectedBiomeProperty != null)
			{
				int num = Convert.ToInt32(_selectedBiomeProperty.GetValue(biomeSelect, null));
				if (num >= 0)
				{
					return num;
				}
			}
			if (biomeSelect != null && _selectedBiomeField != null)
			{
				int num2 = Convert.ToInt32(_selectedBiomeField.GetValue(biomeSelect));
				if (num2 >= 0)
				{
					return num2;
				}
			}
		}
		catch
		{
		}
		try
		{
			PropertyInfo property = campaignSave.GetType().GetProperty("BiomeIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
			{
				return Math.Max(0, Convert.ToInt32(property.GetValue(campaignSave, null)));
			}
		}
		catch
		{
		}
		return 0;
	}

	private static bool TryReadBiomeIsShown(out bool isShown)
	{
		isShown = false;
		if (_biomeSelectInstance == null)
		{
			return false;
		}
		try
		{
			isShown = Convert.ToBoolean(_biomeIsShownProperty.GetValue(_biomeSelectInstance, null));
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void TryWriteProbeMonarch(int logicalId, object monarch)
	{
		try
		{
			Array array = _probeMonarchsField.GetValue(null) as Array;
			int num = logicalId - 1;
			if (array != null && num >= 0 && num < array.Length)
			{
				array.SetValue(Enum.ToObject(_monarchType, Convert.ToInt32(monarch)), num);
			}
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogWarning("[Native ruler sequence] Player " + (logicalId + 1) + "'s ruler was captured, but the optional one-PC appearance table could not be updated: " + Unwrap(exception));
		}
	}

	private static int ReadArgumentInt(object[] arguments, int index, int fallback)
	{
		if (arguments == null || index < 0 || index >= arguments.Length || arguments[index] == null)
		{
			return fallback;
		}
		try
		{
			return Convert.ToInt32(arguments[index]);
		}
		catch
		{
			return fallback;
		}
	}

	private static bool ReadArgumentBool(object[] arguments, int index, bool fallback)
	{
		if (arguments == null || index < 0 || index >= arguments.Length || arguments[index] == null)
		{
			return fallback;
		}
		try
		{
			return Convert.ToBoolean(arguments[index]);
		}
		catch
		{
			return fallback;
		}
	}

	private static MethodInfo[] FindCampaignLoaderWaitPredicates(Type loader)
	{
		MethodInfo[] array = (from method in loader.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where method.Name.IndexOf("ShowSplashScreens", StringComparison.OrdinalIgnoreCase) >= 0 && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0
			select method).ToArray();
		if (array.Length != 2)
		{
			throw new MissingMethodException("Expected the two Loader._ShowSplashScreens native wait predicates in 2.1.4, found " + array.Length + ".");
		}
		return array;
	}

	private static Type RequireGameType(string name)
	{
		return RequireType("Assembly-CSharp", name);
	}

	private static Type RequireCoreType(string name)
	{
		return RequireType("KingdomEightCrowns", name);
	}

	private static Type RequireType(string assemblyName, string typeName)
	{
		Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly candidate) => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
		if (assembly == null)
		{
			throw new TypeLoadException("Required assembly is not loaded: " + assemblyName);
		}
		Type? type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
		if (type == null)
		{
			throw new TypeLoadException("Required type was not found: " + typeName);
		}
		return type;
	}

	private static FieldInfo RequireField(Type type, string name, BindingFlags flags)
	{
		FieldInfo? field = type.GetField(name, flags);
		if (field == null)
		{
			throw new MissingFieldException(type.FullName, name);
		}
		return field;
	}

	private static PropertyInfo RequireProperty(Type type, string name, BindingFlags flags)
	{
		PropertyInfo? property = type.GetProperty(name, flags);
		if (property == null)
		{
			throw new MissingMemberException(type.FullName, name);
		}
		return property;
	}

	private static void PatchPrefix(Harmony harmony, Type type, string originalName, string patchName)
	{
		PatchPrefix(harmony, FindUnique(type, originalName), patchName);
	}

	private static void PatchPrefix(Harmony harmony, MethodInfo original, string patchName)
	{
		harmony.Patch(original, new HarmonyMethod(FindPatch(patchName)));
	}

	private static void PatchPostfix(Harmony harmony, Type type, string originalName, string patchName)
	{
		harmony.Patch(FindUnique(type, originalName), null, new HarmonyMethod(FindPatch(patchName)));
	}

	private static MethodInfo FindPatch(string patchName)
	{
		MethodInfo? method = typeof(NativeCampaignAppearance).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
		if (method == null)
		{
			throw new MissingMethodException("Appearance-flow callback was not found: " + patchName);
		}
		return method;
	}

	private static MethodInfo FindUnique(Type type, string name)
	{
		return FindUnique(type, name, (MethodInfo method) => true);
	}

	private static MethodInfo FindUnique(Type type, string name, Func<MethodInfo, bool> filter)
	{
		MethodInfo[] array = (from method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where method.Name == name && filter(method)
			select method).ToArray();
		if (array.Length != 1)
		{
			throw new MissingMethodException("Expected one " + type.Name + "." + name + " method in 2.1.4, found " + array.Length + ".");
		}
		return array[0];
	}

	private static string DescribeObject(object value)
	{
		if (value == null)
		{
			return "<null>";
		}
		try
		{
			return value.ToString();
		}
		catch
		{
			return "<" + value.GetType().Name + ">";
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
