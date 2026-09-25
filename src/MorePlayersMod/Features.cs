using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace MorePlayersMod;

/// <summary>
/// Entry point for everything that touches game APIs (interop + Harmony).
/// Called from Plugin.Load inside try/catch. Each Harmony patch class is
/// applied in isolation: one bad patch logs its FULL error and can never
/// silently disable the rest (v2.3 lesson: a single PatchAll failure killed
/// every later hook with just "IL Compile Error").
/// </summary>
internal static class Features
{
    private static readonly Type[] PatchClasses = new Type[]
    {
        typeof(Patch_CoreStart),
        typeof(Patch_StartHost),
        typeof(Patch_ConnectToHost),
        typeof(Patch_ObjectiveComplete),
        typeof(Patch_MaxAvailable),
        typeof(Patch_GarageDiag),
        typeof(Patch_MissionTitle),
        typeof(Patch_MissionObjective),
        typeof(Patch_MissionDescription),
        typeof(Patch_MissionHints),
        typeof(Patch_ObjectiveLogOpened),
        typeof(Patch_Tick),
        typeof(Patch_WorldSaveWatch),
        typeof(Patch_GameSaveWatch),
        typeof(Patch_GarageBeforeStop),
        typeof(Patch_GarageStartRunning),
    };

    internal static void Init(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        bool safeMode = config.Bind("General", "SafeMode", true,
            "Disables all game-side hooks and modules while keeping the lobby cap patch; used to isolate planet-load crashes.").Value;
        if (safeMode)
        {
            log.LogInfo("Game-side features are disabled by SafeMode; only the lobby cap patch is active.");
            return;
        }
        int profile = config.Bind("General", "FeatureProfile", 0,
            "Diagnostic isolation profile: 0 = all features, 1 = manager tick only.").Value;
        if (profile == 1)
        {
            ModTick.Bind(config, log);
            var profileHarmony = new Harmony(MorePlayersPlugin.PluginGuid);
            try
            {
                profileHarmony.CreateClassProcessor(typeof(Patch_Tick)).Patch();
                log.LogInfo("Diagnostic FeatureProfile 1 active: manager tick only.");
            }
            catch (Exception e) { log.LogError($"Diagnostic FeatureProfile 1 patch failed: {e}"); }
            return;
        }
        CrewParts.Bind(config, log, maxPlayers);
        Contracts.Bind(config, log, maxPlayers);
        ModBoard.Bind(config, log);
        CustomMissions.Bind(config, log);
        CustomItems.Bind(config, log);
        ShipWatch.Bind(config, log);
        ExtendedTransformStore.Bind(config, log);
        SaveWatch.Bind(config, log);
        ModTick.Bind(config, log);
        Zone.Bind(config, log);

        // One-time migration: BepInEx never overwrites existing cfg values,
        // so stale handouts/rotation/zone would otherwise stick forever.
        try
        {
            int ver = config.Bind("Internal", "ConfigVersion", 0,
                "Do not edit: marks which defaults your file already received.").Value;
            if (ver < 2206)
            {
                CrewParts.Migrate();
                Contracts.Migrate();
                Zone.Migrate();
                CustomItems.Migrate();
                config.Bind("Internal", "ConfigVersion", 0, "Do not edit.").Value = 2206;
                config.Save();
                log.LogInfo("Config migrated to v2.20.6 defaults (deferred full feature profile).");
            }
        }
        catch (Exception e)
        {
            log.LogWarning($"Config migration skipped: {e.GetType().Name}: {e.Message}");
        }

        Type[] activePatchClasses = PatchClasses;
        if (profile == 2)
        {
            activePatchClasses = new Type[]
            {
                typeof(Patch_ObjectiveComplete),
                typeof(Patch_GarageDiag),
                typeof(Patch_MissionTitle),
                typeof(Patch_MissionObjective),
                typeof(Patch_MissionDescription),
                typeof(Patch_MissionHints),
                typeof(Patch_ObjectiveLogOpened),
                typeof(Patch_Tick),
                typeof(Patch_WorldSaveWatch),
                typeof(Patch_GameSaveWatch),
                typeof(Patch_GarageBeforeStop),
                typeof(Patch_GarageStartRunning),
            };
        }
        var harmony = new Harmony(MorePlayersPlugin.PluginGuid);
        int ok = 0, fail = 0;
        foreach (var t in activePatchClasses)
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
        log.LogInfo($"Bonus modules ready: {ok} hooks active, {fail} failed (see errors above).");
    }
}
