using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace KingdomEightCrowns;

public static class DynamicPlayerRegistry
{
	private const int MaximumPlayers = 8;

	private static readonly object Gate = new object();

	private static readonly Dictionary<int, short> DesiredNetIds = new Dictionary<int, short>();

	private static readonly Dictionary<int, Player> Bodies = new Dictionary<int, Player>();

	private static readonly Dictionary<Player, int> LogicalIds = new Dictionary<Player, int>();

	private static readonly HashSet<GameObject> CloneObjects = new HashSet<GameObject>();

	private static readonly HashSet<int> ActivePlayerIds = new HashSet<int>();

	private static readonly HashSet<short> LoggedStalePoolDespawns = new HashSet<short>();

	private static readonly HashSet<string> LoggedPoolDespawnFaults = new HashSet<string>();

	private static readonly HashSet<int> LoggedPlayerModelFaults = new HashSet<int>();

	private static readonly HashSet<short> LoggedNegativeRegistrations = new HashSet<short>();

	private static readonly HashSet<int> LoggedDynamicRpcRoutes = new HashSet<int>();

	private static readonly HashSet<short> LoggedUnroutedDynamicIds = new HashSet<short>();

	private static readonly HashSet<int> LoggedSendInputBodies = new HashSet<int>();

	private static readonly HashSet<string> LoggedPayableFaults = new HashSet<string>();

	private static readonly Dictionary<int, int> ReceivedInputCounts = new Dictionary<int, int>();

	private static long _receivedDynamicRpcCount;

	private static Kingdom? _kingdom;

	private static Il2CppReferenceArray<Player>? _originalActivePlayers;

	private static Player? _nativePlayerTwo;

	private static short _nativePlayerTwoNetId = -1;

	private static int _localOwnerId = -1;

	private static bool _dirty;

	private static bool _topologyLogged;

	private static string? _lastReconcileFailure;

	[ThreadStatic]
	private static bool _suppressCloneAutoRegistration;

	internal static bool RegistryInstalled { get; private set; }

	internal static void Apply(Harmony harmony)
	{
		RegistryInstalled = true;
		MethodInfo methodInfo = AccessTools.Method(typeof(CRPCAutoRegister), "Awake");
		if (methodInfo != null)
		{
			harmony.Patch(methodInfo, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforeCloneAutoRegister")));
		}
		MethodInfo methodInfo2 = AccessTools.Method(typeof(CRPCStamp), "ReregisterNow");
		if (methodInfo2 != null)
		{
			harmony.Patch(methodInfo2, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforeCloneAutoRegister")));
		}
		MethodInfo original = AccessTools.Method(typeof(Game), "IsPlayerControllable", new Type[1] { typeof(Player) }) ?? throw new MissingMethodException("Game.IsPlayerControllable(Player) was not generated.");
		harmony.Patch(original, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforeIsPlayerControllable")));
		MethodInfo original2 = AccessTools.Method(typeof(NetworkBigBoss), "HandleRecvPoolDespawn", new Type[1] { typeof(PoolDespawn) }) ?? throw new MissingMethodException("NetworkBigBoss.HandleRecvPoolDespawn(PoolDespawn) was not generated.");
		harmony.Patch(original2, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforePoolDespawn")), null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterPoolDespawnFault")), null);
		MethodInfo original3 = AccessTools.Method(typeof(NetworkPostbox), "CallCRPC", new Type[3]
		{
			typeof(CRPCType),
			typeof(short),
			typeof(byte)
		}) ?? throw new MissingMethodException("NetworkPostbox.CallCRPC(CRPCType, short, byte) was not generated.");
		harmony.Patch(original3, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforeCallCRPC")));
		MethodInfo original4 = AccessTools.Method(typeof(Player), "RecvInput") ?? throw new MissingMethodException("Player.RecvInput() was not generated.");
		harmony.Patch(original4, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterRecvInput")));
		MethodInfo methodInfo3 = AccessTools.Method(typeof(Player), "SendInput");
		if (methodInfo3 != null)
		{
			harmony.Patch(methodInfo3, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterSendInput")));
		}
		else
		{
			Plugin.LogSource.LogWarning("[Dynamic bodies] Player.SendInput was not generated; outbound input instrumentation is unavailable.");
		}
		MethodInfo original5 = AccessTools.Method(typeof(Player), "SetupPlayerModel") ?? throw new MissingMethodException("Player.SetupPlayerModel() was not generated.");
		harmony.Patch(original5, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterSetupPlayerModelFault")), null);
		try
		{
			MethodInfo methodInfo4 = AccessTools.Method(typeof(Payable), "AddCurrencyIndicators", new Type[1] { typeof(Player) });
			if (methodInfo4 != null)
			{
				harmony.Patch(methodInfo4, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterPayableIndicatorFault")), null);
			}
			MethodInfo methodInfo5 = AccessTools.Method(typeof(LockIndicator), "Init", new Type[3]
			{
				typeof(Payable),
				typeof(Player),
				typeof(bool)
			});
			if (methodInfo5 != null)
			{
				harmony.Patch(methodInfo5, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterPayableIndicatorFault")), null);
			}
			MethodInfo methodInfo6 = AccessTools.Method(typeof(PayableUpgrade), "OnSelect", new Type[1] { typeof(Player) });
			if (methodInfo6 != null)
			{
				harmony.Patch(methodInfo6, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterPayableIndicatorFault")), null);
			}
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Dynamic bodies] AddCurrencyIndicators resilience patch failed: " + ex.Message);
		}
		try
		{
			MethodInfo methodInfo7 = AccessTools.Method(typeof(Payable), "Select", new Type[1] { typeof(Player) });
			if (methodInfo7 != null && Plugin.DynamicBodyHooksEnabled)
			{
				harmony.Patch(methodInfo7, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterPayableSelectFault")), null);
			}
			else if (!Plugin.DynamicBodyHooksEnabled)
			{
				Plugin.LogSource.LogWarning("[Dynamic bodies] Payable.Select fault finalizer skipped by DynamicBodyHooks.Enabled=false (crash isolation).");
			}
		}
		catch (Exception ex2)
		{
			Plugin.LogSource.LogWarning("[Dynamic bodies] Payable.Select resilience patch failed: " + ex2.Message);
		}
		try
		{
			if (!Plugin.DynamicBodyHooksEnabled)
			{
				Plugin.LogSource.LogWarning("[Dynamic bodies] Stale-save clone purge skipped by DynamicBodyHooks.Enabled=false (crash isolation).");
				return;
			}
			MethodInfo methodInfo8 = AccessTools.Method(typeof(NetworkPostbox), "RegisterObject", new Type[3]
			{
				typeof(GameObject),
				typeof(short),
				typeof(CRPCType)
			});
			if (methodInfo8 != null)
			{
				harmony.Patch(methodInfo8, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "BeforeRegisterObject")));
				Plugin.LogSource.LogWarning("[Dynamic bodies] Installed the stale-save clone purge on NetworkPostbox.RegisterObject(GameObject, Int16, CRPCType).");
			}
			else
			{
				Plugin.LogSource.LogWarning("[Dynamic bodies] NetworkPostbox.RegisterObject(GameObject, Int16, CRPCType) was not generated; stale-save clone purge is unavailable.");
			}
		}
		catch (Exception ex3)
		{
			Plugin.LogSource.LogWarning("[Dynamic bodies] RegisterObject purge patch failed: " + ex3.Message);
		}
	}

	private static bool IsStaleBodyPartCloneName(string name, short netId)
	{
		if (!name.EndsWith("(Clone)", StringComparison.Ordinal))
		{
			return false;
		}
		if (netId >= 900 && netId < 1000)
		{
			return true;
		}
		int num = name.Length - "(Clone)".Length - 1;
		bool flag = false;
		while (num >= 0 && name[num] >= '0' && name[num] <= '9')
		{
			flag = true;
			num--;
		}
		if (!flag || num < 1 || name[num] != 'P')
		{
			return false;
		}
		num--;
		if (num >= 0)
		{
			return name[num] == ' ';
		}
		return false;
	}

	private static bool BeforeRegisterObject(object[] __args)
	{
		try
		{
			if (__args == null || __args.Length < 1 || __args[0] == null)
			{
				return true;
			}
			GameObject gameObject;
			try
			{
				gameObject = (GameObject)__args[0];
			}
			catch
			{
				return true;
			}
			if (gameObject == null)
			{
				return true;
			}
			if (CloneObjects.Contains(gameObject))
			{
				return true;
			}
			string text;
			try
			{
				text = gameObject.name ?? string.Empty;
			}
			catch
			{
				return true;
			}
			short num = (short)((__args.Length > 1 && __args[1] is short num2) ? num2 : (-1));
			if (num < 0)
			{
				bool flag;
				lock (Gate)
				{
					flag = LoggedNegativeRegistrations.Add(num);
				}
				if (flag)
				{
					Plugin.LogSource.LogWarning("[Dynamic bodies] Skipped a registration carrying NetID " + num + " ('" + text + "'); negative IDs are not addressable and would collide in the postbox table.");
				}
				return false;
			}
			if (!text.StartsWith("EightCrowns_Player", StringComparison.Ordinal))
			{
				return true;
			}
			Plugin.LogSource.LogWarning("[Dynamic bodies] Destroyed stale clone '" + text + "' (NetID " + num + ") deserialized from save data before it could collide with a live identity.");
			try
			{
				gameObject.SetActive(value: false);
			}
			catch
			{
			}
			UnityEngine.Object.Destroy(gameObject);
			return false;
		}
		catch
		{
			return true;
		}
	}

	public static int GetNativePlayerTwoNetId()
	{
		return NetworkPostbox.Player2ID;
	}

	public static int ReserveBodyNetId()
	{
		NetworkPostbox obj = NetworkPostbox.Instance ?? throw new InvalidOperationException("NetworkPostbox is not initialized.");
		short num = obj.ReserveNextNetId((CRPCType)1);
		if (num < 1000 || num >= NetworkPostbox.ClientIDOffset)
		{
			throw new InvalidOperationException($"Reserved body NetID {num} is outside the host dynamic range.");
		}
		if (obj.DynamicObjects.ContainsKey(num))
		{
			throw new InvalidOperationException($"Reserved dynamic body NetID {num} is already occupied.");
		}
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(61, 1, out isEnabled);
		if (isEnabled)
		{
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Reserved collision-free dynamic body NetID ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(".");
		}
		logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		return num;
	}

	public static void SetLocalOwner(int playerId)
	{
		if (playerId < 0 || playerId >= 8)
		{
			throw new ArgumentOutOfRangeException("playerId");
		}
		lock (Gate)
		{
			if (_localOwnerId != playerId)
			{
				_localOwnerId = playerId;
				_dirty = true;
			}
		}
		TryReconcile();
	}

	public static void AssignPlayer(int playerId, int netId)
	{
		if (playerId < 1 || playerId >= 8)
		{
			throw new ArgumentOutOfRangeException("playerId");
		}
		if (netId < 0 || netId > 32767)
		{
			throw new ArgumentOutOfRangeException("netId");
		}
		lock (Gate)
		{
			short num = checked((short)netId);
			if (!DesiredNetIds.TryGetValue(playerId, out var value) || value != num)
			{
				DesiredNetIds[playerId] = num;
				_dirty = true;
			}
		}
		TryReconcile();
	}

	public static void ActivatePlayer(int playerId)
	{
		if (playerId < 1 || playerId >= 8)
		{
			throw new ArgumentOutOfRangeException("playerId");
		}
		lock (Gate)
		{
			if (ActivePlayerIds.Add(playerId))
			{
				_dirty = true;
			}
		}
		TryReconcile();
	}

	public static void RemovePlayer(int playerId)
	{
		if (playerId < 1 || playerId >= 8)
		{
			return;
		}
		lock (Gate)
		{
			DesiredNetIds.Remove(playerId);
			ActivePlayerIds.Remove(playerId);
			if (Bodies.TryGetValue(playerId, out Player value) && value != null && value != _nativePlayerTwo)
			{
				DeregisterClone(value);
			}
			Bodies.Remove(playerId);
			RemoveLogicalMappings(playerId);
			_dirty = true;
		}
		TryReconcile();
	}

	public static void TryReapplyAppearance(int playerId)
	{
		if (playerId < 1)
		{
			return;
		}
		Player value;
		lock (Gate)
		{
			if (LoggedPlayerModelFaults.Contains(playerId) || !Bodies.TryGetValue(playerId, out value) || value == null)
			{
				return;
			}
		}
		try
		{
			value.SetupPlayerModel();
		}
		catch (Exception ex)
		{
			lock (Gate)
			{
				LoggedPlayerModelFaults.Add(playerId);
			}
			Plugin.LogSource.LogWarning($"[Dynamic bodies] Re-applying Player {playerId + 1}'s stored " + "appearance faulted (" + ex.GetType().Name + "); keeping the current look and never retrying.");
			return;
		}
		Plugin.LogSource.LogWarning($"[Dynamic bodies] Re-applied Player {playerId + 1}'s stored " + "appearance to its body.");
	}

	public static int ResolveLogicalPlayerId(Player player)
	{
		if (player == null)
		{
			return -1;
		}
		lock (Gate)
		{
			int value;
			return LogicalIds.TryGetValue(player, out value) ? value : player.playerId;
		}
	}

	internal static bool IsManagedBody(Player player)
	{
		if (player == null)
		{
			return false;
		}
		lock (Gate)
		{
			return LogicalIds.ContainsKey(player);
		}
	}

	internal static void Tick(Kingdom kingdom)
	{
		if (!RegistryInstalled || kingdom == null)
		{
			return;
		}
		lock (Gate)
		{
			if (_kingdom != kingdom)
			{
				ResetRuntimeBodies("new kingdom instance");
				_kingdom = kingdom;
				_originalActivePlayers = kingdom._activePlayers;
				_dirty = true;
			}
		}
		TryReconcile();
	}

	public static void ResetSession(string reason)
	{
		if (!RegistryInstalled)
		{
			return;
		}
		lock (Gate)
		{
			ResetRuntimeBodies(reason);
			DesiredNetIds.Clear();
			ActivePlayerIds.Clear();
			_localOwnerId = -1;
			_kingdom = null;
			_originalActivePlayers = null;
			LoggedStalePoolDespawns.Clear();
			LoggedPoolDespawnFaults.Clear();
			LoggedPlayerModelFaults.Clear();
			LoggedDynamicRpcRoutes.Clear();
			LoggedUnroutedDynamicIds.Clear();
			LoggedSendInputBodies.Clear();
			ReceivedInputCounts.Clear();
			_receivedDynamicRpcCount = 0L;
			_dirty = false;
		}
	}

	public static void ResetLevel(string reason)
	{
		if (!RegistryInstalled)
		{
			return;
		}
		lock (Gate)
		{
			ResetRuntimeBodies(reason);
			_kingdom = null;
			_originalActivePlayers = null;
			_dirty = DesiredNetIds.Count > 0;
		}
	}

	private static bool BeforeCloneAutoRegister()
	{
		return !_suppressCloneAutoRegistration;
	}

	private static bool BeforeIsPlayerControllable(Player player, ref bool __result)
	{
		if (player == null || ResolveLogicalPlayerId(player) <= 1)
		{
			return true;
		}
		__result = player.hasLocalAuthority;
		return false;
	}

	private static bool BeforePoolDespawn(PoolDespawn despawnInfo)
	{
		try
		{
			NetworkPostbox instance = NetworkPostbox.Instance;
			CRPCHeader value = default(CRPCHeader);
			if (instance?.DynamicObjects != null && instance.DynamicObjects.TryGetValue(despawnInfo.dynSyncID, ref value) && value != null && value.referencedGO != null)
			{
				return true;
			}
			bool flag;
			lock (Gate)
			{
				flag = LoggedStalePoolDespawns.Add(despawnInfo.dynSyncID);
			}
			if (flag)
			{
				Plugin.LogSource.LogWarning($"[Network resilience] Ignored stale pool despawn {despawnInfo.dynSyncID}; " + "the remaining messages in its Steam receive group will still run.");
			}
			return false;
		}
		catch (Exception exception)
		{
			LogPoolDespawnFault(despawnInfo.dynSyncID, exception, "validation");
			return true;
		}
	}

	private static Exception? AfterPoolDespawnFault(Exception? __exception, PoolDespawn despawnInfo)
	{
		if (__exception == null)
		{
			return null;
		}
		LogPoolDespawnFault(despawnInfo.dynSyncID, __exception, "native handler");
		return null;
	}

	private static void LogPoolDespawnFault(short dynSyncId, Exception exception, string phase)
	{
		string item = dynSyncId + ":" + phase + ":" + exception.GetType().FullName;
		bool flag;
		lock (Gate)
		{
			flag = LoggedPoolDespawnFaults.Add(item);
		}
		if (flag)
		{
			Plugin.LogSource.LogWarning($"[Network resilience] Suppressed pool despawn {dynSyncId} {phase} " + "fault so later messages can continue: " + exception.GetType().Name + ": " + exception.Message);
		}
	}

	private static void BeforeCallCRPC(object[] __args)
	{
		if (__args == null || __args.Length < 3)
		{
			return;
		}
		int num = Convert.ToInt32(__args[0]);
		short num2 = Convert.ToInt16(__args[1]);
		byte b = Convert.ToByte(__args[2]);
		int num3 = -1;
		bool flag = false;
		bool flag2 = false;
		long receivedDynamicRpcCount;
		lock (Gate)
		{
			_receivedDynamicRpcCount++;
			receivedDynamicRpcCount = _receivedDynamicRpcCount;
			foreach (KeyValuePair<int, short> desiredNetId in DesiredNetIds)
			{
				if (desiredNetId.Value == num2)
				{
					num3 = desiredNetId.Key;
					flag = LoggedDynamicRpcRoutes.Add((num2 << 8) | b);
					break;
				}
			}
			if (num3 < 0 && LoggedUnroutedDynamicIds.Count < 20)
			{
				bool flag3 = false;
				NetworkPostbox instance = NetworkPostbox.Instance;
				if (instance?.DynamicObjects != null)
				{
					flag3 = instance.DynamicObjects.ContainsKey(num2);
				}
				if (!flag3)
				{
					flag2 = LoggedUnroutedDynamicIds.Add(num2);
				}
			}
		}
		if (flag)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(93, 4, out isEnabled);
			if (isEnabled)
			{
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Dispatched first CRPC type=");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" ");
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("function ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(b);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" for logical Player ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num3 + 1);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" ");
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("on dynamic NetID ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num2);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		}
		if (receivedDynamicRpcCount == 1 || receivedDynamicRpcCount % 5000 == 0L)
		{
			Plugin.LogSource.LogWarning($"[Dynamic bodies] CRPC volume sample: {receivedDynamicRpcCount} " + "dispatched on this machine.");
		}
		if (flag2)
		{
			Plugin.LogSource.LogError($"[Dynamic bodies] CRPC type={num} function {b} " + $"arrived for NetID {num2}, which no logical player owns " + "on this machine; this indicates a body-mapping desync.");
		}
	}

	private static void AfterSendInput(Player __instance)
	{
		if (__instance == null)
		{
			return;
		}
		int num = ResolveLogicalPlayerId(__instance);
		if (num >= 0)
		{
			bool flag;
			lock (Gate)
			{
				flag = LoggedSendInputBodies.Add(num);
			}
			if (flag)
			{
				Plugin.LogSource.LogWarning($"[Dynamic bodies] Player {num + 1} transmitted its first " + "input RPC on NetID " + $"{__instance.parentHeaderRef?.NetID ?? (-1)}.");
			}
		}
	}

	private static void AfterRecvInput(Player __instance)
	{
		if (__instance == null)
		{
			return;
		}
		int num = ResolveLogicalPlayerId(__instance);
		if (num < 1 || !IsManagedBody(__instance))
		{
			return;
		}
		int value;
		lock (Gate)
		{
			ReceivedInputCounts.TryGetValue(num, out value);
			value++;
			ReceivedInputCounts[num] = value;
		}
		if (value == 1 || value % 300 == 0)
		{
			short t = __instance.parentHeaderRef?.NetID ?? (-1);
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(99, 5, out isEnabled);
			if (isEnabled)
			{
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Player ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num + 1);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" body processed RecvInput ");
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("#");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(value);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" on NetID ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(t);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("; localAuthority=");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(__instance.hasLocalAuthority);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(", ");
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("controllerPresent=");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(__instance.CurrentUnitController != null);
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		}
	}

	private static Exception? AfterSetupPlayerModelFault(Exception? __exception, Player __instance)
	{
		if (__exception == null)
		{
			return null;
		}
		if (__instance == null || !IsManagedBody(__instance))
		{
			return __exception;
		}
		int num = ResolveLogicalPlayerId(__instance);
		bool flag;
		lock (Gate)
		{
			flag = LoggedPlayerModelFaults.Add(num);
		}
		if (flag)
		{
			Plugin.LogSource.LogWarning($"[Network resilience] Player {num + 1}'s cloned model setup " + "was incomplete (" + __exception.GetType().Name + ": " + __exception.Message + "). Its temporary appearance remains active and later messages in the same Steam receive group will still run.");
		}
		return null;
	}

	private static Exception? AfterPayableIndicatorFault(Exception? __exception, object __instance)
	{
		return SwallowPayableCapacityFault(__exception, __instance, "AddCurrencyIndicators");
	}

	private static Exception? AfterPayableSelectFault(Exception? __exception, object __instance)
	{
		return SwallowPayableCapacityFault(__exception, __instance, "Select");
	}

	private static Exception? SwallowPayableCapacityFault(Exception? exception, object instance, string operation)
	{
		if (exception == null)
		{
			return null;
		}
		if (!IsTwoPlayerCapacityFault(exception))
		{
			return exception;
		}
		string item = operation + ":" + (instance?.GetType().Name ?? "null");
		bool flag;
		lock (Gate)
		{
			flag = LoggedPayableFaults.Add(item);
		}
		if (flag)
		{
			Plugin.LogSource.LogWarning("[Network resilience] " + instance?.GetType().Name + "." + operation + " exceeded the stock two-player indicator capacity (" + DescribeCapacityFault(exception) + "); suppressed so interaction prompts keep working for expanded rosters.");
		}
		return null;
	}

	private static bool IsTwoPlayerCapacityFault(Exception exception)
	{
		if (exception is IndexOutOfRangeException || exception is ArgumentOutOfRangeException)
		{
			return true;
		}
		if (exception.GetType().Name == "Il2CppException")
		{
			if (!exception.Message.StartsWith("System.IndexOutOfRangeException", StringComparison.Ordinal))
			{
				return exception.Message.StartsWith("System.ArgumentOutOfRangeException", StringComparison.Ordinal);
			}
			return true;
		}
		return false;
	}

	private static string DescribeCapacityFault(Exception exception)
	{
		string text = exception.Message ?? string.Empty;
		int num = text.IndexOf(':');
		if (num <= 0)
		{
			return exception.GetType().Name;
		}
		return text.Substring(0, num);
	}

	private static void TryReconcile()
	{
		lock (Gate)
		{
			if (!_dirty || _kingdom == null || DesiredNetIds.Count == 0)
			{
				return;
			}
			if (_localOwnerId < 0)
			{
				if (!NetworkBigBoss.HasWorldAuth)
				{
					return;
				}
				_localOwnerId = 0;
			}
			if (_localOwnerId > 0 && !ActivePlayerIds.Contains(_localOwnerId))
			{
				return;
			}
			try
			{
				Player playerTwo = _kingdom.playerTwo;
				if (playerTwo == null || playerTwo.parentHeaderRef == null)
				{
					return;
				}
				NetworkPostbox instance = NetworkPostbox.Instance;
				if ((object)instance == null || instance.DynamicObjects == null || instance.MasterDynCRPCHLookup == null)
				{
					return;
				}
				if (_nativePlayerTwo == null)
				{
					_nativePlayerTwo = playerTwo;
					_nativePlayerTwoNetId = playerTwo.parentHeaderRef.NetID;
				}
				int num = ((_localOwnerId == 0) ? 1 : _localOwnerId);
				if (!DesiredNetIds.TryGetValue(num, out var value))
				{
					return;
				}
				RebindNativePlayerTwo(num, value, instance);
				foreach (KeyValuePair<int, short> item in DesiredNetIds.OrderBy((KeyValuePair<int, short> pair) => pair.Key))
				{
					if (item.Key != num && ActivePlayerIds.Contains(item.Key) && (!Bodies.TryGetValue(item.Key, out Player value2) || value2 == null))
					{
						value2 = CreateRemoteBody(_nativePlayerTwo, item.Key, item.Value, instance);
						Bodies[item.Key] = value2;
						LogicalIds[value2] = item.Key;
					}
				}
				int[] array = Bodies.Keys.Where((int id) => !DesiredNetIds.ContainsKey(id)).ToArray();
				foreach (int num3 in array)
				{
					Player player = Bodies[num3];
					if (player != null && player != _nativePlayerTwo)
					{
						DeregisterClone(player);
					}
					Bodies.Remove(num3);
					RemoveLogicalMappings(num3);
				}
				PublishActiveRegistry();
				_dirty = false;
				_lastReconcileFailure = null;
				if (!_topologyLogged)
				{
					_topologyLogged = true;
					Plugin.LogSource.LogWarning($"[Dynamic bodies] Native Player 2 is bound to logical Player {num + 1}; " + $"{Bodies.Count} independently routed remote body/bodies are registered. " + "Each body now has its own collision-free dynamic CRPC identity.");
				}
			}
			catch (Exception ex)
			{
				_dirty = true;
				string text = ex.GetType().Name + ": " + ex.Message;
				if (!string.Equals(_lastReconcileFailure, text, StringComparison.Ordinal))
				{
					_lastReconcileFailure = text;
					Plugin.LogSource.LogError("[Dynamic bodies] Topology creation is not ready yet and will retry without blocking the game loop. Details: " + text);
				}
			}
		}
	}

	private static void RebindNativePlayerTwo(int logicalId, short targetNetId, NetworkPostbox postbox)
	{
		Player nativePlayerTwo = _nativePlayerTwo;
		CRPCHeader parentHeaderRef = nativePlayerTwo.parentHeaderRef;
		short netID = parentHeaderRef.NetID;
		if (parentHeaderRef.HeaderType != (CRPCType)1)
		{
			throw new InvalidOperationException($"The native Player 2 header uses unexpected CRPC type {parentHeaderRef.HeaderType}.");
		}
		if (logicalId > 1)
		{
			PlayerAppearanceRegistry.EnsureFallback(logicalId, nativePlayerTwo);
		}
		if (netID != targetNetId)
		{
			if (postbox.DynamicObjects.ContainsKey(targetNetId) && postbox.DynamicObjects[targetNetId] != parentHeaderRef)
			{
				throw new InvalidOperationException($"Cannot bind local body to NetID {targetNetId}; it is already occupied.");
			}
			postbox.DynamicObjects.Remove(netID);
			parentHeaderRef.ReceiveNetIDOverride(targetNetId);
			postbox.DynamicObjects[targetNetId] = parentHeaderRef;
			CRPCStamp component = nativePlayerTwo.gameObject.GetComponent<CRPCStamp>();
			if (component != null)
			{
				component.semiStatic = false;
				component.Setup(parentHeaderRef, semiStatic: false);
			}
			Plugin.LogSource.LogWarning("[Dynamic bodies] Rebound the locally controlled native Player 2 CRPC " + $"identity from {netID} to {targetNetId} for logical Player {logicalId + 1}. " + "The stock Player 2 control path remains intact.");
		}
		nativePlayerTwo.playerId = logicalId;
		nativePlayerTwo.hasLocalAuthority = _localOwnerId > 0;
		Bodies[logicalId] = nativePlayerTwo;
		LogicalIds[nativePlayerTwo] = logicalId;
	}

	private static Player CreateRemoteBody(Player source, int logicalId, short netId, NetworkPostbox postbox)
	{
		if (postbox.DynamicObjects.ContainsKey(netId))
		{
			throw new InvalidOperationException($"Cannot create logical Player {logicalId + 1}; NetID {netId} is occupied.");
		}
		PlayerAppearanceRegistry.EnsureFallback(logicalId, source);
		Transform transform = source.transform.parent;
		if (transform == null && _kingdom != null)
		{
			transform = _kingdom.transform;
		}
		if (transform == null)
		{
			throw new InvalidOperationException($"Cannot create logical Player {logicalId + 1}; the native Player 2 " + "body has no scene hierarchy owner.");
		}
		_suppressCloneAutoRegistration = true;
		Player player;
		try
		{
			player = UnityEngine.Object.Instantiate(source, transform, worldPositionStays: true);
		}
		finally
		{
			_suppressCloneAutoRegistration = false;
		}
		if (player == null)
		{
			throw new InvalidOperationException($"Unity failed to clone Player {logicalId + 1}.");
		}
		GameObject gameObject = player.gameObject;
		gameObject.name = $"EightCrowns_Player{logicalId + 1}_Live";
		CloneObjects.Add(gameObject);
		player.hasLocalAuthority = false;
		player.playerId = logicalId;
		short sourceNetId = source.parentHeaderRef?.NetID ?? (-1);
		CRPCHeader cRPCHeader;
		int num;
		try
		{
			num = ResetRpcRegistrationState(gameObject);
			cRPCHeader = postbox.RegisterObject(gameObject, netId, (CRPCType)1);
		}
		catch
		{
			DestroyFailedClone(gameObject, postbox, netId);
			throw;
		}
		if (cRPCHeader == null)
		{
			DestroyFailedClone(gameObject, postbox, netId);
			throw new InvalidOperationException($"Player {logicalId + 1}'s cloned RPC components refused registration.");
		}
		try
		{
			num += InvalidateCopiedDescendantStamps(gameObject, cRPCHeader, sourceNetId);
		}
		catch
		{
			DestroyFailedClone(gameObject, postbox, netId);
			throw;
		}
		int num2 = source.parentHeaderRef?.RemoteMethodList?.Count ?? (-1);
		int num3 = cRPCHeader.RemoteMethodList?.Count ?? (-1);
		if (num2 >= 0 && num3 != num2)
		{
			DeregisterClone(player);
			throw new InvalidOperationException($"Player {logicalId + 1} registered {num3} RPC methods; " + $"the native Player 2 body has {num2}.");
		}
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(184, 4, out isEnabled);
		if (isEnabled)
		{
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Created logical Player ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(logicalId + 1);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" with independent ");
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("dynamic NetID ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(netId);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(", remote authority, ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num3);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" RPC methods, ");
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("and ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" copied pseudodyn stamp(s) invalidated ");
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("inside the native Player hierarchy.");
		}
		logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		return player;
	}

	private static void DestroyFailedClone(GameObject cloneObject, NetworkPostbox postbox, short requestedNetId)
	{
		try
		{
			postbox.DynamicObjects?.Remove(requestedNetId);
			postbox.MasterDynCRPCHLookup?.Remove(cloneObject);
		}
		catch
		{
		}
		CloneObjects.Remove(cloneObject);
		if (cloneObject != null)
		{
			UnityEngine.Object.Destroy(cloneObject);
		}
	}

	private static int ResetRpcRegistrationState(GameObject cloneObject)
	{
		foreach (MonoBehaviour componentsInChild in cloneObject.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
		{
			if (componentsInChild == null)
			{
				continue;
			}
			PropertyInfo[] properties = componentsInChild.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (PropertyInfo propertyInfo in properties)
			{
				if (!propertyInfo.CanWrite || propertyInfo.GetIndexParameters().Length != 0)
				{
					continue;
				}
				try
				{
					if (propertyInfo.PropertyType == typeof(CRPCHeader))
					{
						propertyInfo.SetValue(componentsInChild, null);
					}
					else if (propertyInfo.PropertyType == typeof(int) && LooksLikeRpcIndex(propertyInfo.Name))
					{
						propertyInfo.SetValue(componentsInChild, -1);
					}
				}
				catch
				{
				}
			}
		}
		Player obj2 = cloneObject.GetComponent<Player>() ?? throw new InvalidOperationException("The cloned ruler has no Player component.");
		obj2.parentHeaderRef = null;
		obj2._staminaRPCIndex = -1;
		obj2._eatRPCIndex = -1;
		obj2._sparkleRPCIndex = -1;
		obj2._walletCountIndex = -1;
		obj2._setCrownStateIndex = -1;
		obj2._currencyDroppedFromDamageIndex = -1;
		obj2._rearRPCIndex = -1;
		obj2._sendInput = -1;
		obj2._sendTunneling = -1;
		int num = 0;
		foreach (CRPCStamp componentsInChild2 in cloneObject.GetComponentsInChildren<CRPCStamp>(includeInactive: true))
		{
			if (!(componentsInChild2 == null))
			{
				componentsInChild2.parentHeaderRef = null;
				componentsInChild2.NetIDCacheHack = -1;
				componentsInChild2.semiStatic = false;
				num++;
			}
		}
		return num;
	}

	private static int InvalidateCopiedDescendantStamps(GameObject cloneObject, CRPCHeader cloneHeader, short sourceNetId)
	{
		if (sourceNetId < 0)
		{
			return 0;
		}
		int num = 0;
		foreach (CRPCStamp componentsInChild in cloneObject.GetComponentsInChildren<CRPCStamp>(includeInactive: true))
		{
			if (!(componentsInChild == null) && !(componentsInChild == cloneHeader.stampRef) && componentsInChild.NetIDCacheHack == sourceNetId)
			{
				componentsInChild.parentHeaderRef = null;
				componentsInChild.NetIDCacheHack = -1;
				componentsInChild.semiStatic = false;
				num++;
			}
		}
		return num;
	}

	private static bool LooksLikeRpcIndex(string name)
	{
		if (name.IndexOf("rpc", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return name.StartsWith("_send", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static void PublishActiveRegistry()
	{
		if (_kingdom == null)
		{
			return;
		}
		int num = _originalActivePlayers?.Length ?? 0;
		int num2 = 0;
		Player value;
		for (int i = 1; i < 8 && Bodies.TryGetValue(i, out value); i++)
		{
			if (value == null)
			{
				break;
			}
			if (!(value == _nativePlayerTwo) && !ActivePlayerIds.Contains(i))
			{
				break;
			}
			num2 = i + 1;
		}
		if (num2 <= Math.Max(2, num))
		{
			if (_originalActivePlayers != null && _kingdom._activePlayers != _originalActivePlayers)
			{
				_kingdom._activePlayers = _originalActivePlayers;
			}
			return;
		}
		int num3 = Math.Max(2, Math.Max(num, num2));
		Il2CppReferenceArray<Player> il2CppReferenceArray = new Il2CppReferenceArray<Player>(num3);
		if (_originalActivePlayers != null)
		{
			int num4 = Math.Min(_originalActivePlayers.Length, num3);
			for (int j = 0; j < num4; j++)
			{
				il2CppReferenceArray[j] = _originalActivePlayers[j];
			}
		}
		foreach (KeyValuePair<int, Player> body in Bodies)
		{
			if (body.Key >= 0 && body.Key < num3 && body.Value != null && (body.Value == _nativePlayerTwo || ActivePlayerIds.Contains(body.Key)))
			{
				il2CppReferenceArray[body.Key] = body.Value;
			}
		}
		_kingdom._activePlayers = il2CppReferenceArray;
		Plugin.LogSource.LogWarning($"[Dynamic bodies] Published a contiguous {num3}-slot active-player " + "registry with no empty padding entries.");
	}

	private static void DeregisterClone(Player body)
	{
		if (body == null)
		{
			return;
		}
		try
		{
			NetworkPostbox instance = NetworkPostbox.Instance;
			CRPCHeader parentHeaderRef = body.parentHeaderRef;
			if ((object)instance != null && parentHeaderRef != null)
			{
				instance.DynamicObjects?.Remove(parentHeaderRef.NetID);
				instance.MasterDynCRPCHLookup?.Remove(body.gameObject);
			}
		}
		catch (Exception ex)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(47, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Clone deregistration warning: ");
				bepInExWarningLogInterpolatedStringHandler.AppendFormatted(ex.Message);
			}
			logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		}
		LogicalIds.Remove(body);
		CloneObjects.Remove(body.gameObject);
		if (body.gameObject != null)
		{
			UnityEngine.Object.Destroy(body.gameObject);
		}
	}

	private static void RemoveLogicalMappings(int logicalId)
	{
		Player[] array = (from pair in LogicalIds
			where pair.Value == logicalId
			select pair.Key).ToArray();
		foreach (Player key in array)
		{
			LogicalIds.Remove(key);
		}
	}

	private static void ResetRuntimeBodies(string reason)
	{
		Player[] array = Bodies.Values.Distinct().ToArray();
		foreach (Player player in array)
		{
			if (player != null && player != _nativePlayerTwo)
			{
				DeregisterClone(player);
			}
		}
		if (_nativePlayerTwo != null && _nativePlayerTwo.parentHeaderRef != null && _nativePlayerTwoNetId >= 0)
		{
			try
			{
				NetworkPostbox instance = NetworkPostbox.Instance;
				CRPCHeader parentHeaderRef = _nativePlayerTwo.parentHeaderRef;
				instance?.DynamicObjects?.Remove(parentHeaderRef.NetID);
				parentHeaderRef.ReceiveNetIDOverride(_nativePlayerTwoNetId);
				if (instance?.DynamicObjects != null)
				{
					instance.DynamicObjects[_nativePlayerTwoNetId] = parentHeaderRef;
				}
				CRPCStamp component = _nativePlayerTwo.gameObject.GetComponent<CRPCStamp>();
				if (component != null)
				{
					component.semiStatic = false;
					component.Setup(parentHeaderRef, semiStatic: false);
				}
			}
			catch
			{
			}
		}
		if (_kingdom != null && _originalActivePlayers != null)
		{
			try
			{
				_kingdom._activePlayers = _originalActivePlayers;
			}
			catch
			{
			}
		}
		if (CloneObjects.Count > 0)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(42, 2, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Dynamic bodies] Removed ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(CloneObjects.Count);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" live clone(s): ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(reason);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
		}
		CloneObjects.Clear();
		Bodies.Clear();
		LogicalIds.Clear();
		_nativePlayerTwo = null;
		_nativePlayerTwoNetId = -1;
		_topologyLogged = false;
	}
}
