using System;
using System.Collections.Generic;
using System.Diagnostics;
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

	// Re-running a Player's model attach on a live body faults and corrupts it (see 0.14.17/0.14.18),
	// so every body receives at most one re-apply, and only for a model delivered over the network.
	private static readonly Dictionary<int, Player> ReappliedBodies = new Dictionary<int, Player>();

	private static readonly HashSet<int> PendingAppearance = new HashSet<int>();

	private static readonly Dictionary<int, long> BodyCreatedAt = new Dictionary<int, long>();

	private const double AppearanceSettleSeconds = 0.5;

	private static long _receivedDynamicRpcCount;

	private static Kingdom? _kingdom;

	// The game's own array that a published registry replaced, and the array this registry installed.
	private static Il2CppReferenceArray<Player>? _originalActivePlayers;

	private static Il2CppReferenceArray<Player>? _publishedActivePlayers;

	private static string? _lastPublishedLayout;

	private static Player? _nativePlayerTwo;

	private static short _nativePlayerTwoNetId = -1;

	private static int _nativePlayerTwoOriginalId = -1;

	private static bool _nativePlayerTwoOriginalAuthority;

	private static int _localOwnerId = -1;

	private static bool _dirty;

	private static bool _topologyLogged;

	private static string? _lastReconcileFailure;

	private static int _reconcileFailures;

	private static long _nextReconcileAt;

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
		MethodInfo original5 = AccessTools.Method(typeof(Player), "SetupPlayerModel") ?? throw new MissingMethodException("Player.SetupPlayerModel() was not generated.");
		harmony.Patch(original5, null, null, null, new HarmonyMethod(AccessTools.Method(typeof(DynamicPlayerRegistry), "AfterSetupPlayerModelFault")), null);
		if (Plugin.HotPathTracingEnabled)
		{
			ApplyHotPathTracing(harmony);
		}
		ApplyResiliencePatches(harmony);
	}

	private static void ApplyHotPathTracing(Harmony harmony)
	{
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
		Plugin.LogSource.LogWarning("[Dynamic bodies] Hot-path CRPC/input tracing is enabled ([Diagnostics] HotPathTracing).");
	}

	private static void ApplyResiliencePatches(Harmony harmony)
	{
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
			// A freed slot is reused by the next joiner, who must not inherit this player's ruler or fault mark.
			BodyCreatedAt.Remove(playerId);
			ReappliedBodies.Remove(playerId);
			PendingAppearance.Remove(playerId);
			LoggedPlayerModelFaults.Remove(playerId);
			PlayerAppearanceRegistry.ClearSlot(playerId);
			_dirty = true;
		}
		TryReconcile();
	}

	public static void TryReapplyAppearance(int playerId)
	{
		if (playerId < 1 || playerId >= 8)
		{
			return;
		}
		Player value;
		lock (Gate)
		{
			if (LoggedPlayerModelFaults.Contains(playerId))
			{
				PendingAppearance.Remove(playerId);
				return;
			}
			if (!Bodies.TryGetValue(playerId, out value) || value == null || !IsBodySettled(playerId))
			{
				// The body is created (or finishes its first frames) later; Tick applies the stored ruler then.
				PendingAppearance.Add(playerId);
				return;
			}
			if (value == _nativePlayerTwo || (ReappliedBodies.TryGetValue(playerId, out Player applied) && applied == value))
			{
				// The natively bound body is dressed by the stock path; a clone is only ever re-applied once.
				PendingAppearance.Remove(playerId);
				return;
			}
			if (!PlayerAppearanceRegistry.IsCommitted(playerId))
			{
				PendingAppearance.Add(playerId);
				return;
			}
			PendingAppearance.Remove(playerId);
			ReappliedBodies[playerId] = value;
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
		bool faulted;
		lock (Gate)
		{
			faulted = LoggedPlayerModelFaults.Contains(playerId);
		}
		if (faulted)
		{
			// AfterSetupPlayerModelFault swallowed the native fault; report it instead of claiming success.
			Plugin.LogSource.LogWarning($"[Dynamic bodies] Re-applying Player {playerId + 1}'s stored appearance faulted inside the game's model setup; keeping the current look and never retrying.");
			return;
		}
		Plugin.LogSource.LogWarning($"[Dynamic bodies] Re-applied Player {playerId + 1}'s stored " + "appearance to its body.");
	}

	private static bool IsBodySettled(int playerId)
	{
		if (!BodyCreatedAt.TryGetValue(playerId, out long createdAt))
		{
			return true;
		}
		return (double)(Stopwatch.GetTimestamp() - createdAt) / (double)Stopwatch.Frequency >= AppearanceSettleSeconds;
	}

	private static void ProcessPendingAppearance()
	{
		int[] pending;
		lock (Gate)
		{
			if (PendingAppearance.Count == 0)
			{
				return;
			}
			pending = PendingAppearance.Where((int id) => Bodies.TryGetValue(id, out Player body) && body != null && IsBodySettled(id) && PlayerAppearanceRegistry.IsCommitted(id)).ToArray();
		}
		foreach (int playerId in pending)
		{
			TryReapplyAppearance(playerId);
		}
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
		bool reconcileDue;
		lock (Gate)
		{
			if (_kingdom != kingdom)
			{
				ResetRuntimeBodies("new kingdom instance");
				_kingdom = kingdom;
				_originalActivePlayers = null;
				_publishedActivePlayers = null;
				_dirty = true;
				_nextReconcileAt = 0L;
			}
			PruneDestroyedBodies();
			reconcileDue = Stopwatch.GetTimestamp() >= _nextReconcileAt;
		}
		if (reconcileDue)
		{
			TryReconcile();
		}
		ProcessPendingAppearance();
	}

	// Clones parented under the level hierarchy can be destroyed by the game (level unload) without passing
	// through DeregisterClone; drop them and their dead NetID registration so they are rebuilt.
	private static void PruneDestroyedBodies()
	{
		if (Bodies.Count == 0)
		{
			return;
		}
		if ((object)_nativePlayerTwo != null && _nativePlayerTwo == null)
		{
			ResetRuntimeBodies("the native Player 2 body was destroyed");
			_dirty = DesiredNetIds.Count > 0;
			return;
		}
		int[] dead = Bodies.Where((KeyValuePair<int, Player> pair) => (object)pair.Value != null && pair.Value == null).Select((KeyValuePair<int, Player> pair) => pair.Key).ToArray();
		if (dead.Length == 0)
		{
			return;
		}
		foreach (int id in dead)
		{
			if (DesiredNetIds.TryGetValue(id, out short netId))
			{
				RemoveDeadRegistration(NetworkPostbox.Instance, netId);
			}
			Bodies.Remove(id);
			RemoveLogicalMappings(id);
			BodyCreatedAt.Remove(id);
			ReappliedBodies.Remove(id);
		}
		CloneObjects.RemoveWhere((GameObject go) => go == null);
		_dirty = true;
		Plugin.LogSource.LogWarning("[Dynamic bodies] " + dead.Length + " remote body/bodies were destroyed by the game (level change); they will be rebuilt.");
	}

	private static bool RemoveDeadRegistration(NetworkPostbox? postbox, short netId)
	{
		try
		{
			CRPCHeader header = null;
			if (postbox?.DynamicObjects == null || !postbox.DynamicObjects.TryGetValue(netId, ref header))
			{
				return false;
			}
			if (header != null && header.referencedGO != null)
			{
				return false;
			}
			postbox.DynamicObjects.Remove(netId);
			return true;
		}
		catch
		{
			return false;
		}
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
			PendingAppearance.Clear();
			PlayerAppearanceRegistry.ResetSession();
			_receivedDynamicRpcCount = 0L;
			_dirty = false;
			_reconcileFailures = 0;
			_nextReconcileAt = 0L;
			_lastReconcileFailure = null;
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
			_publishedActivePlayers = null;
			_dirty = DesiredNetIds.Count > 0;
			_nextReconcileAt = 0L;
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
			if (!_dirty || _kingdom == null)
			{
				return;
			}
			if (DesiredNetIds.Count == 0)
			{
				// Every remote player is gone: hand the stock Player 2 body and player list back to the game.
				if (Bodies.Count > 0 || _publishedActivePlayers != null)
				{
					ResetRuntimeBodies("no remote players remain");
				}
				_dirty = false;
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
					_nativePlayerTwoOriginalId = playerTwo.playerId;
					_nativePlayerTwoOriginalAuthority = playerTwo.hasLocalAuthority;
				}
				int num = ((_localOwnerId == 0) ? 1 : _localOwnerId);
				if (DesiredNetIds.TryGetValue(num, out var value))
				{
					RebindNativePlayerTwo(num, value, instance);
				}
				else if (_localOwnerId > 0)
				{
					// A client needs its own body mapping before it can place anyone else's.
					return;
				}
				// On the host an empty Player 2 slot (Player 2 left) must not freeze Players 3-8.
				string creationFailure = null;
				foreach (KeyValuePair<int, short> item in DesiredNetIds.OrderBy((KeyValuePair<int, short> pair) => pair.Key))
				{
					if (item.Key != num && ActivePlayerIds.Contains(item.Key) && (!Bodies.TryGetValue(item.Key, out Player value2) || value2 == null))
					{
						try
						{
							value2 = CreateRemoteBody(_nativePlayerTwo, item.Key, item.Value, instance);
							Bodies[item.Key] = value2;
							LogicalIds[value2] = item.Key;
							BodyCreatedAt[item.Key] = Stopwatch.GetTimestamp();
							ReappliedBodies.Remove(item.Key);
							PendingAppearance.Add(item.Key);
						}
						catch (Exception ex)
						{
							// One body that cannot be built must not block the others, removals, or publishing.
							creationFailure = creationFailure ?? ("Player " + (item.Key + 1) + ": " + ex.GetType().Name + ": " + ex.Message);
						}
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
					BodyCreatedAt.Remove(num3);
				}
				PublishActiveRegistry();
				if (creationFailure != null)
				{
					RecordReconcileFailure(creationFailure);
					return;
				}
				_dirty = false;
				_lastReconcileFailure = null;
				_reconcileFailures = 0;
				_nextReconcileAt = 0L;
				if (!_topologyLogged)
				{
					_topologyLogged = true;
					Plugin.LogSource.LogWarning($"[Dynamic bodies] Native Player 2 is bound to logical Player {num + 1}; " + $"{Bodies.Count} independently routed remote body/bodies are registered. " + "Each body now has its own collision-free dynamic CRPC identity.");
				}
			}
			catch (Exception ex)
			{
				RecordReconcileFailure(ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	private static void RecordReconcileFailure(string text)
	{
		_dirty = true;
		_reconcileFailures++;
		// Back off (0.25 s doubling to 5 s) instead of re-cloning a full Player every frame.
		double delay = Math.Min(5.0, 0.25 * Math.Pow(2.0, Math.Min(_reconcileFailures - 1, 5)));
		_nextReconcileAt = Stopwatch.GetTimestamp() + (long)(delay * (double)Stopwatch.Frequency);
		if (!string.Equals(_lastReconcileFailure, text, StringComparison.Ordinal))
		{
			_lastReconcileFailure = text;
			Plugin.LogSource.LogError($"[Dynamic bodies] Topology creation is not ready yet and will retry in {delay:0.##} s without blocking the game loop. Details: " + text);
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
			RemoveDeadRegistration(postbox, targetNetId);
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
		if (RemoveDeadRegistration(postbox, netId))
		{
			Plugin.LogSource.LogWarning($"[Dynamic bodies] Reclaimed NetID {netId} from a destroyed body before rebuilding logical Player {logicalId + 1}.");
		}
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
		Il2CppReferenceArray<Player>? current = _kingdom._activePlayers;
		if (_publishedActivePlayers == null || !SameArray(current, _publishedActivePlayers))
		{
			// Whatever the game holds now (initially, or after it replaced our array) is the stock baseline.
			_originalActivePlayers = current;
			_publishedActivePlayers = null;
		}
		Il2CppReferenceArray<Player>? stock = _originalActivePlayers;
		// Slot 0 (the host's body) stays the game's own entry; every live logical player follows in ID order
		// without gaps, so a departure below Players 3-8 no longer drops them from the player list.
		List<Player> published = new List<Player>(8);
		if (stock != null && stock.Length > 0 && stock[0] != null)
		{
			published.Add(stock[0]);
		}
		bool anyClone = false;
		foreach (KeyValuePair<int, Player> body in Bodies.OrderBy((KeyValuePair<int, Player> pair) => pair.Key))
		{
			if (body.Key >= 1 && body.Value != null && (body.Value == _nativePlayerTwo || ActivePlayerIds.Contains(body.Key)))
			{
				published.Add(body.Value);
				anyClone |= body.Value != _nativePlayerTwo;
			}
		}
		if (stock == null || !anyClone || MatchesArray(published, stock))
		{
			// No clone is live (plain two-player layout): leave the game's array untouched.
			if (_publishedActivePlayers != null && stock != null)
			{
				_kingdom._activePlayers = stock;
			}
			_publishedActivePlayers = null;
			return;
		}
		Il2CppReferenceArray<Player> il2CppReferenceArray = new Il2CppReferenceArray<Player>(published.Count);
		for (int i = 0; i < published.Count; i++)
		{
			il2CppReferenceArray[i] = published[i];
		}
		_kingdom._activePlayers = il2CppReferenceArray;
		_publishedActivePlayers = _kingdom._activePlayers;
		string layout = string.Join(",", Bodies.Where((KeyValuePair<int, Player> pair) => pair.Key >= 1 && pair.Value != null && (pair.Value == _nativePlayerTwo || ActivePlayerIds.Contains(pair.Key))).Select((KeyValuePair<int, Player> pair) => pair.Key + 1).OrderBy((int id) => id));
		if (!string.Equals(_lastPublishedLayout, layout, StringComparison.Ordinal))
		{
			_lastPublishedLayout = layout;
			Plugin.LogSource.LogWarning($"[Dynamic bodies] Published a {published.Count}-entry active-player registry (host + Players {layout}).");
		}
	}

	internal static bool TryResolvePublishedPlayer(Kingdom kingdom, int playerId, out Player? player)
	{
		player = null;
		lock (Gate)
		{
			if (_kingdom == null || kingdom == null || _kingdom != kingdom || playerId < 1 || Bodies.Count == 0)
			{
				return false;
			}
			bool published = _publishedActivePlayers != null && SameArray(kingdom._activePlayers, _publishedActivePlayers);
			if (!published && playerId < 2)
			{
				// Stock layout: Player 2 keeps the game's own lookup.
				return false;
			}
			if (Bodies.TryGetValue(playerId, out Player body) && body != null && (body == _nativePlayerTwo || ActivePlayerIds.Contains(playerId)))
			{
				player = body;
			}
			return true;
		}
	}

	private static bool SameArray(Il2CppReferenceArray<Player>? left, Il2CppReferenceArray<Player>? right)
	{
		if (left == null || right == null)
		{
			return left == null && right == null;
		}
		return left.Pointer == right.Pointer;
	}

	private static bool MatchesArray(List<Player> players, Il2CppReferenceArray<Player>? array)
	{
		if (array == null || array.Length != players.Count)
		{
			return false;
		}
		for (int i = 0; i < players.Count; i++)
		{
			if (array[i] != players[i])
			{
				return false;
			}
		}
		return true;
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
		GameObject? cloneObject = null;
		try
		{
			cloneObject = body.gameObject;
		}
		catch
		{
		}
		if (cloneObject != null)
		{
			CloneObjects.Remove(cloneObject);
			UnityEngine.Object.Destroy(cloneObject);
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
				// Only undo a rebind that actually happened; a plain two-player session keeps the stock registration.
				if (parentHeaderRef.NetID != _nativePlayerTwoNetId)
				{
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
				if (_nativePlayerTwoOriginalId >= 0 && _nativePlayerTwo.playerId != _nativePlayerTwoOriginalId)
				{
					_nativePlayerTwo.playerId = _nativePlayerTwoOriginalId;
				}
				if (_nativePlayerTwoOriginalId >= 0 && _nativePlayerTwo.hasLocalAuthority != _nativePlayerTwoOriginalAuthority)
				{
					_nativePlayerTwo.hasLocalAuthority = _nativePlayerTwoOriginalAuthority;
				}
			}
			catch
			{
			}
		}
		if (_kingdom != null && _originalActivePlayers != null && _publishedActivePlayers != null)
		{
			try
			{
				// Restore the game's array only while ours is still installed; a newer native array wins.
				if (SameArray(_kingdom._activePlayers, _publishedActivePlayers))
				{
					_kingdom._activePlayers = _originalActivePlayers;
				}
			}
			catch
			{
			}
		}
		_publishedActivePlayers = null;
		_lastPublishedLayout = null;
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
		BodyCreatedAt.Clear();
		ReappliedBodies.Clear();
		_nativePlayerTwo = null;
		_nativePlayerTwoNetId = -1;
		_nativePlayerTwoOriginalId = -1;
		_topologyLogged = false;
	}
}
