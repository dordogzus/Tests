using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace KingdomEightCrowns;

internal static class PlayerBodyProbe
{
	private const double SetupDelaySeconds = 2.0;

	private const double VisibleDurationSeconds = 15.0;

	private static readonly List<GameObject> CloneObjects = new List<GameObject>();

	private static readonly List<Transform> CloneTransforms = new List<Transform>();

	private static readonly List<Vector3> ClonePositions = new List<Vector3>();

	private static readonly List<List<Renderer>> CloneVisibleRenderers = new List<List<Renderer>>();

	private static readonly Stopwatch ReadyClock = new Stopwatch();

	private static readonly Stopwatch VisibleClock = new Stopwatch();

	private static PropertyInfo? _playerOne;

	private static PropertyInfo? _playerTwo;

	private static PropertyInfo? _activePlayers;

	private static PropertyInfo? _playerId;

	private static PropertyInfo? _hasLocalAuthority;

	private static MethodInfo? _instantiatePlayer;

	private static MethodInfo? _setupAsPlayer;

	private static MethodInfo? _getPlayer;

	private static object? _kingdom;

	private static object? _originalPlayerTwo;

	private static object? _originalActivePlayers;

	private static bool _completedForLevel;

	internal static void Configure(Type kingdomType, Type playerType)
	{
		_playerOne = AccessTools.Property(kingdomType, "playerOne");
		_playerTwo = AccessTools.Property(kingdomType, "playerTwo");
		_activePlayers = AccessTools.Property(kingdomType, "_activePlayers");
		_playerId = AccessTools.Property(playerType, "playerId");
		_hasLocalAuthority = AccessTools.Property(playerType, "hasLocalAuthority");
		_setupAsPlayer = AccessTools.Method(playerType, "SetupAsPlayer", new Type[1] { typeof(int) });
		_getPlayer = AccessTools.Method(kingdomType, "GetPlayer", new Type[1] { typeof(int) });
		_instantiatePlayer = typeof(UnityEngine.Object).GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "Instantiate" && method.IsGenericMethodDefinition && method.GetParameters().Length == 1)?.MakeGenericMethod(playerType);
		if ((object)_playerOne == null || (object)_playerTwo == null || (object)_activePlayers == null || (object)_playerId == null || (object)_hasLocalAuthority == null || (object)_instantiatePlayer == null || (object)_setupAsPlayer == null || (object)_getPlayer == null)
		{
			throw new MissingMemberException("The 2.1.4 Kingdom/Player registry, initialization, lookup, or typed clone members could not be resolved.");
		}
	}

	internal static void Tick(object kingdom)
	{
		if (!Plugin.PlayerBodyProbeEnabled || _completedForLevel)
		{
			return;
		}
		try
		{
			if (CloneObjects.Count != 0)
			{
				MaintainCloneVisuals();
				if (VisibleClock.Elapsed.TotalSeconds >= 15.0)
				{
					Cleanup("15-second visibility test completed");
					_completedForLevel = true;
				}
				return;
			}
			object value = _playerOne.GetValue(kingdom);
			if (value == null)
			{
				ReadyClock.Reset();
			}
			else if (!ReadyClock.IsRunning)
			{
				ReadyClock.Start();
				Plugin.LogSource.LogInfo("[Player-body probe] Player 1 detected; waiting for campaign setup to settle.");
			}
			else if (ReadyClock.Elapsed.TotalSeconds >= 2.0)
			{
				SpawnBodies(kingdom, value);
			}
		}
		catch (Exception exception)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExErrorLogInterpolatedStringHandler bepInExErrorLogInterpolatedStringHandler = new BepInExErrorLogInterpolatedStringHandler(28, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExErrorLogInterpolatedStringHandler.AppendLiteral("[Player-body probe] Failed: ");
				bepInExErrorLogInterpolatedStringHandler.AppendFormatted(Unwrap(exception));
			}
			logSource.LogError(bepInExErrorLogInterpolatedStringHandler);
			_completedForLevel = true;
			Cleanup("failure cleanup");
		}
	}

	internal static void Reset(string reason)
	{
		if (CloneObjects.Count != 0)
		{
			Cleanup(reason);
		}
		ReadyClock.Reset();
		VisibleClock.Reset();
		_completedForLevel = false;
	}

	private static void SpawnBodies(object kingdom, object playerOne)
	{
		Player obj = (playerOne as Player) ?? throw new InvalidCastException("The generated Player 1 proxy is not the expected Player type.");
		int probePlayerCount = Plugin.ProbePlayerCount;
		_kingdom = kingdom;
		_originalPlayerTwo = _playerTwo.GetValue(kingdom);
		_originalActivePlayers = _activePlayers.GetValue(kingdom);
		PlayerAppearanceRegistry.Begin(obj);
		List<object> list = new List<object>(probePlayerCount) { playerOne };
		Vector3 position = obj.transform.position;
		bool isEnabled;
		for (int i = 1; i < probePlayerCount; i++)
		{
			object obj2 = _instantiatePlayer.Invoke(null, new object[1] { playerOne }) ?? throw new InvalidOperationException($"Typed cloning returned null for Player {i + 1}.");
			if (!(obj2 is Player { gameObject: var gameObject } player))
			{
				if (obj2 is UnityEngine.Object obj3)
				{
					UnityEngine.Object.Destroy(obj3);
				}
				throw new InvalidCastException($"Typed Player {i + 1} clone returned wrapper {obj2.GetType().FullName}, not a Player.");
			}
			CloneObjects.Add(gameObject);
			gameObject.name = $"EightCrowns_Player{i + 1}_Probe";
			_hasLocalAuthority.SetValue(obj2, false);
			_playerId.SetValue(obj2, i);
			_hasLocalAuthority.SetValue(obj2, false);
			int num = (int)(_playerId.GetValue(obj2) ?? throw new InvalidOperationException($"Player {i + 1} did not expose an initialized player ID."));
			if (num != i)
			{
				throw new InvalidOperationException($"visual clone ({i}) assigned unexpected ID {num}.");
			}
			Renderer[] array = gameObject.GetComponentsInChildren<Renderer>(includeInactive: true);
			List<Renderer> list2 = new List<Renderer>();
			Renderer[] array2 = array;
			foreach (Renderer renderer in array2)
			{
				if (renderer.enabled && renderer.gameObject.activeInHierarchy)
				{
					list2.Add(renderer);
				}
			}
			float num2 = (float)((i <= 4) ? (i - 5) : (i - 4)) * 1.35f;
			Vector3 vector = position + new Vector3(num2, 0f, 0f);
			player.transform.position = vector;
			CloneTransforms.Add(player.transform);
			ClonePositions.Add(vector);
			CloneVisibleRenderers.Add(list2);
			list.Add(obj2);
			ManualLogSource logSource = Plugin.LogSource;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(140, 6, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Player-body probe] Created and initialized Player ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(i + 1);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" through visual clone (");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(i);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("), x-offset ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(num2, "0.00");
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(", ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(list2.Count);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("/");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(array.Length);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" initially visible renderer(s), and typed wrapper ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(obj2.GetType().Name);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
		}
		object value = CreatePlayerArray(_activePlayers.PropertyType, list);
		_activePlayers.SetValue(kingdom, value);
		_playerTwo.SetValue(kingdom, list[1]);
		VerifyPlayerLookup(kingdom, list);
		MaintainCloneVisuals();
		ReadyClock.Reset();
		VisibleClock.Restart();
		ManualLogSource logSource2 = Plugin.LogSource;
		BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(257, 6, out isEnabled);
		if (isEnabled)
		{
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Player-body probe] RESULT: registered ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(list.Count);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" occupied slot(s) inside the ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(Plugin.MaxPlayers);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("-player capacity, initialized IDs 0-");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(list.Count - 1);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(", and verified all ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(list.Count);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" IDs through Kingdom.GetPlayer and Player.GetAppearance. Player 2-");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(list.Count);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" are visible non-interactive clones and will be removed in ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(15.0, "0");
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" seconds.");
		}
		logSource2.LogWarning(bepInExWarningLogInterpolatedStringHandler);
	}

	private static object CreatePlayerArray(Type arrayType, IReadOnlyList<object> players)
	{
		object obj = Activator.CreateInstance(arrayType, (long)players.Count) ?? throw new InvalidOperationException("Unable to allocate the active-player array.");
		PropertyInfo propertyInfo = arrayType.GetProperty("Item") ?? throw new MissingMemberException(arrayType.FullName, "Item");
		for (int i = 0; i < players.Count; i++)
		{
			propertyInfo.SetValue(obj, players[i], new object[1] { i });
		}
		return obj;
	}

	private static void VerifyPlayerLookup(object kingdom, IReadOnlyList<object> players)
	{
		for (int i = 0; i < players.Count; i++)
		{
			bool flag = SameUnityObject(_getPlayer.Invoke(kingdom, new object[1] { i }), players[i]);
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(42, 3, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Player lookup] ID ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(i);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" resolved to Player ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(i + 1);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(": ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(flag ? "match" : "MISMATCH");
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
			if (!flag)
			{
				throw new InvalidOperationException($"Kingdom.GetPlayer({i}) did not return the registered Player {i + 1} object.");
			}
		}
		Plugin.LogSource.LogWarning($"[Player lookup] RESULT: {players.Count}/{players.Count} occupied player IDs " + "resolved to their registered Player objects.");
	}

	private static bool SameUnityObject(object? left, object right)
	{
		if (left == right)
		{
			return true;
		}
		if (left is UnityEngine.Object obj && right is UnityEngine.Object obj2)
		{
			return obj == obj2;
		}
		return false;
	}

	private static void MaintainCloneVisuals()
	{
		int num = Math.Min(CloneObjects.Count, Math.Min(CloneTransforms.Count, ClonePositions.Count));
		for (int i = 0; i < num; i++)
		{
			GameObject gameObject = CloneObjects[i];
			if (!gameObject)
			{
				continue;
			}
			if (!gameObject.activeSelf)
			{
				gameObject.SetActive(value: true);
			}
			CloneTransforms[i].position = ClonePositions[i];
			if (i >= CloneVisibleRenderers.Count)
			{
				continue;
			}
			foreach (Renderer item in CloneVisibleRenderers[i])
			{
				if (!item)
				{
					continue;
				}
				Transform transform = item.transform;
				Transform transform2 = CloneTransforms[i];
				while ((object)transform != null)
				{
					if (!transform.gameObject.activeSelf)
					{
						transform.gameObject.SetActive(value: true);
					}
					if (transform == transform2)
					{
						break;
					}
					transform = transform.parent;
				}
				item.enabled = true;
			}
		}
	}

	private static void Cleanup(string reason)
	{
		bool isEnabled;
		if (_kingdom != null)
		{
			try
			{
				_activePlayers?.SetValue(_kingdom, _originalActivePlayers);
				_playerTwo?.SetValue(_kingdom, _originalPlayerTwo);
			}
			catch (Exception exception)
			{
				ManualLogSource logSource = Plugin.LogSource;
				BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(50, 1, out isEnabled);
				if (isEnabled)
				{
					bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Player-body probe] Registry restoration warning: ");
					bepInExWarningLogInterpolatedStringHandler.AppendFormatted(Unwrap(exception));
				}
				logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
			}
		}
		foreach (GameObject cloneObject in CloneObjects)
		{
			if ((bool)cloneObject)
			{
				UnityEngine.Object.Destroy(cloneObject);
			}
		}
		if (CloneObjects.Count != 0)
		{
			ManualLogSource logSource2 = Plugin.LogSource;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(48, 2, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Player-body probe] Removed ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(CloneObjects.Count);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" temporary bodies: ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(reason);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource2.LogInfo(bepInExInfoLogInterpolatedStringHandler);
		}
		CloneObjects.Clear();
		CloneTransforms.Clear();
		ClonePositions.Clear();
		CloneVisibleRenderers.Clear();
		_kingdom = null;
		_originalPlayerTwo = null;
		_originalActivePlayers = null;
		PlayerAppearanceRegistry.Reset();
		ReadyClock.Reset();
		VisibleClock.Reset();
	}

	private static Exception Unwrap(Exception exception)
	{
		if (!(exception is TargetInvocationException) || exception.InnerException == null)
		{
			return exception;
		}
		return exception.InnerException;
	}
}
