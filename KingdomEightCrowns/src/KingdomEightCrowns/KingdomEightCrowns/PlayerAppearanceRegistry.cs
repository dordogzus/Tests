using System;
using System.Collections.Generic;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using UnityEngine;

namespace KingdomEightCrowns;

internal static class PlayerAppearanceRegistry
{
	private static readonly MonarchType[] ProbeMonarchs = new MonarchType[7]
	{
		(MonarchType)1,
		(MonarchType)2,
		(MonarchType)5,
		(MonarchType)10,
		(MonarchType)14,
		(MonarchType)16,
		(MonarchType)23
	};

	private static readonly Color[] PrimaryColours = new Color[7]
	{
		new Color(0.86f, 0.17f, 0.16f, 1f),
		new Color(0.12f, 0.42f, 0.88f, 1f),
		new Color(0.18f, 0.7f, 0.32f, 1f),
		new Color(0.62f, 0.22f, 0.78f, 1f),
		new Color(0.92f, 0.58f, 0.08f, 1f),
		new Color(0.08f, 0.72f, 0.74f, 1f),
		new Color(0.82f, 0.24f, 0.52f, 1f)
	};

	private static readonly PlayerModel?[] Slots = new PlayerModel[8];

	private static readonly HashSet<int> LoggedFallbackSlots = new HashSet<int>();

	private static PlayerModel? _template;

	internal static void Begin(Player hostPlayer)
	{
		Reset();
		_template = hostPlayer.GetAppearance() ?? throw new InvalidOperationException("Player 1 did not provide the game's PlayerModel appearance payload.");
		Slots[0] = CopyModel(_template, 0, (MonarchType)_template.monarchType);
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(56, 1, out isEnabled);
		if (isEnabled)
		{
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Appearance probe] Registered host appearance in ID 0: ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted((MonarchType)_template.monarchType);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
		}
		logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
	}

	internal static bool TryGet(int playerId, out PlayerModel? appearance)
	{
		appearance = ((playerId >= 0 && playerId < Slots.Length) ? Slots[playerId] : null);
		return appearance != null;
	}

	internal static void EnsureFallback(int playerId, Player? sourcePlayer = null)
	{
		if (TryGetOrCreateFallback(playerId, sourcePlayer, out PlayerModel _))
		{
			return;
		}
		throw new InvalidOperationException($"Appearance slot {playerId} cannot be initialized until the native host appearance is available.");
	}

	internal static bool TryGetOrCreateFallback(int playerId, out PlayerModel? appearance)
	{
		return TryGetOrCreateFallback(playerId, null, out appearance);
	}

	internal static bool TryGetOrCreateFallback(int playerId, Player? sourcePlayer, out PlayerModel? appearance)
	{
		if (TryGet(playerId, out appearance))
		{
			return true;
		}
		if (playerId < 0 || playerId >= Slots.Length)
		{
			return false;
		}
		PlayerModel playerModel = ResolveNativeModel(playerId);
		if (playerModel == null)
		{
			playerModel = Slots[0] ?? _template;
		}
		if (playerModel == null && sourcePlayer != null)
		{
			int playerId2 = sourcePlayer.playerId;
			if (playerId2 >= 0 && playerId2 < Slots.Length)
			{
				playerModel = Slots[playerId2];
			}
			if (playerModel == null)
			{
				playerModel = CreateEmergencyModel(sourcePlayer, playerId);
			}
		}
		if (playerModel == null)
		{
			return false;
		}
		appearance = CopyModel(playerModel, playerId, (MonarchType)playerModel.monarchType);
		if (PlayerAppearancePolicy.RequiresDefaultRemoteMount(playerId))
		{
			appearance.steedNetID = 924;
			appearance.steedType = 9;
		}
		Slots[playerId] = appearance;
		if (LoggedFallbackSlots.Add(playerId))
		{
			Plugin.LogSource.LogWarning($"[Appearance lookup] Installed a safe temporary PlayerModel for Player {playerId + 1}; " + "the client's native ruler selection will replace it.");
		}
		return true;
	}

	private static PlayerModel? ResolveNativeModel(int playerId)
	{
		try
		{
			NetworkBigBoss instance = NetworkBigBoss.Instance;
			if (instance == null)
			{
				return null;
			}
			if (playerId == 0 && instance.p1Model != null)
			{
				return instance.p1Model;
			}
			if (playerId > 0 && instance.p2Model != null)
			{
				return instance.p2Model;
			}
			return instance.p1Model ?? instance.p2Model;
		}
		catch
		{
			return null;
		}
	}

	private static PlayerModel CreateEmergencyModel(Player sourcePlayer, int playerId)
	{
		Color skinColor = sourcePlayer._skinColor;
		Color primaryColour = new Color(0.82f, 0.67f, 0.28f, 1f);
		Color secondaryColour = new Color(0.22f, 0.36f, 0.62f, 1f);
		Color emblemColour = new Color(0.92f, 0.92f, 0.82f, 1f);
		return new PlayerModel(skinColor, primaryColour, secondaryColour, emblemColour, sourcePlayer._model, playerId, 924, (SteedType)9, sourcePlayer._crownType, (ItemOfPower.ItemType)0);
	}

	internal static void ApplyProbeAppearance(Player player, int playerId)
	{
		if (_template == null)
		{
			throw new InvalidOperationException("The appearance registry was not initialized from Player 1.");
		}
		if (playerId <= 0 || playerId >= Slots.Length)
		{
			throw new ArgumentOutOfRangeException("playerId", playerId, "Extended player appearance IDs must be between 1 and 7.");
		}
		MonarchType monarchType = ProbeMonarchs[playerId - 1];
		Color primaryColour = PrimaryColours[playerId - 1];
		PlayerModel playerModel = new PlayerModel(secondaryColour: new Color(1f - primaryColour.r * 0.55f, 1f - primaryColour.g * 0.55f, 1f - primaryColour.b * 0.55f, 1f), emblemColour: new Color(primaryColour.b, primaryColour.r, primaryColour.g, 1f), skinColour: _template.skinColour, primaryColour: primaryColour, monarchType: monarchType, playerId: playerId, steedNetID: _template.steedNetID, steedType: (SteedType)_template.steedType, crownType: (RiderCrown.CrownType)_template.crownType, rulerItemType: (ItemOfPower.ItemType)_template.rulerItemType);
		Slots[playerId] = playerModel;
		player.hasLocalAuthority = false;
		if (player.playerId != playerId)
		{
			throw new InvalidOperationException($"Player {playerId + 1} did not retain appearance ownership for ID {playerId}.");
		}
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(91, 3, out isEnabled);
		if (isEnabled)
		{
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Appearance probe] Player ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(playerId + 1);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(", ID ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(playerId);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(": applied independent ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(monarchType);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" PlayerModel with a unique colour set.");
		}
		logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
	}

	internal static void Verify(IReadOnlyList<object> players)
	{
		for (int i = 0; i < players.Count; i++)
		{
			PlayerModel playerModel = Slots[i] ?? throw new InvalidOperationException($"Appearance slot {i} was not registered.");
			if (playerModel.playerId != i)
			{
				throw new InvalidOperationException($"Appearance slot {i} contains payload ID {playerModel.playerId}.");
			}
			for (int j = 0; j < i; j++)
			{
				if (playerModel == Slots[j])
				{
					throw new InvalidOperationException($"Appearance IDs {j} and {i} share one PlayerModel object.");
				}
			}
			bool flag = ((players[i] as Player) ?? throw new InvalidCastException($"Registered Player {i + 1} is not the generated Player type.")).GetAppearance() == playerModel;
			ManualLogSource logSource = Plugin.LogSource;
			bool isEnabled;
			BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(66, 2, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Appearance lookup] ID ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(i);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" resolved to its independent PlayerModel: ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(flag ? "match" : "MISMATCH");
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
			if (!flag)
			{
				throw new InvalidOperationException($"Player.GetAppearance() did not return the registered payload for ID {i}.");
			}
		}
		Plugin.LogSource.LogWarning($"[Appearance probe] RESULT: {players.Count}/{players.Count} occupied IDs have independent " + "PlayerModel payloads and resolve them through the eight-slot appearance lookup.");
	}

	internal static void Reset()
	{
		Array.Clear(Slots, 0, Slots.Length);
		LoggedFallbackSlots.Clear();
		_template = null;
	}

	private static PlayerModel CopyModel(PlayerModel source, int playerId, MonarchType monarch)
	{
		return new PlayerModel(source.skinColour, source.primaryColour, source.secondaryColour, source.emblemColour, monarch, playerId, source.steedNetID, (SteedType)source.steedType, (RiderCrown.CrownType)source.crownType, (ItemOfPower.ItemType)source.rulerItemType);
	}
}
