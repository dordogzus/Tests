using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace MorePlayersMod;

/// <summary>
/// Entry point for everything that touches game APIs (interop + Harmony).
/// Each Harmony patch class is applied in isolation: one bad patch logs its
/// full error and never disables the rest.
/// </summary>
internal static class Features
{
    internal const int ConfigVersion = 3000;

    private static readonly Type[] PatchClasses =
    {
        typeof(Patch_CoreStart),
        typeof(Patch_StartHost),
        typeof(Patch_ConnectToHost),
        typeof(Patch_ObjectiveComplete),
        typeof(Patch_GarageDiag),
        typeof(Patch_MissionTitle),
        typeof(Patch_MissionObjective),
        typeof(Patch_MissionDescription),
        typeof(Patch_MissionHints),
        typeof(Patch_ObjectiveLogOpened),
        typeof(Patch_Tick),
        typeof(Patch_GarageBeforeStop),
        typeof(Patch_GarageStartRunning),
    };

    /// <summary>
    /// v2.x shipped SafeMode/FeatureProfile diagnostics that silently disabled most
    /// features, and 12-player / x2-garage defaults. BepInEx never overwrites saved
    /// values, so move old files to the v3 defaults once.
    /// </summary>
    internal static void MigrateCore(ConfigFile config, ConfigEntry<int> maxPlayers, ManualLogSource log)
    {
        try
        {
            var version = config.Bind("Internal", "ConfigVersion", 0, "Do not edit: marks which defaults your file already received.");
            if (version.Value >= ConfigVersion) return;
            if (maxPlayers.Value == 12 || maxPlayers.Value == 4) maxPlayers.Value = Logic.CrewProgression.DefaultMaxPlayers;
            config.Bind("General", "SafeMode", false, "").Value = false;
            config.Bind("General", "FeatureProfile", 0, "").Value = 0;
            log.LogInfo($"Config: v3 migration pending (MaxPlayers={maxPlayers.Value}).");
        }
        catch (Exception e) { log.LogWarning($"Config core migration skipped: {e.Message}"); }
    }

    /// <returns>true when game-side modules are active (overlay may be created).</returns>
    internal static bool Init(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        bool safeMode = config.Bind("General", "SafeMode", false,
            "Troubleshooting only: disable every game-side module and keep just the lobby cap patch.").Value;
        if (safeMode)
        {
            log.LogInfo("SafeMode: game-side modules disabled; only the lobby cap patch is active.");
            return false;
        }

        SteamLobby.Bind(log);
        PlayerColors.Bind(log, maxPlayers);
        CrewParts.Bind(config, log, maxPlayers);
        Contracts.Bind(config, log, maxPlayers);
        CustomMissions.Bind(config, log);
        ExtendedTransformStore.Bind(config, log);
        ModTick.Bind(config, log);
        Zone.Bind(config, log);
        Perimeter.Bind(config, log);
        FrontierBlocks.Bind(config, log);
        FrontierRuntime.Bind(config, log);
        ModOverlay.Bind(config, log, maxPlayers);

        try
        {
            var version = config.Bind("Internal", "ConfigVersion", 0, "Do not edit: marks which defaults your file already received.");
            if (version.Value < ConfigVersion)
            {
                CrewParts.Migrate();
                Contracts.Migrate();
                Zone.Migrate();
                version.Value = ConfigVersion;
                config.Save();
                log.LogInfo("Config migrated to v3.0.0 defaults.");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"Config migration skipped: {e.GetType().Name}: {e.Message}");
        }

        var harmony = new Harmony(MorePlayersPlugin.PluginGuid);
        int ok = 0, fail = 0;
        foreach (var t in PatchClasses)
        {
            try
            {
                harmony.CreateClassProcessor(t).Patch();
                ok++;
            }
            catch (Exception e)
            {
                fail++;
                log.LogError($"Harmony patch FAILED for {t.Name} (others still active): {e}");
            }
        }
        LobbyCap.Install(harmony, log, maxPlayers);
        FrontierBlocks.Install(harmony);
        log.LogInfo($"Game-side modules ready: {ok} hooks active, {fail} failed.");
        return true;
    }
}
