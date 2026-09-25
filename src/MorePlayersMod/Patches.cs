using HarmonyLib;

namespace MorePlayersMod;

/// <summary>
/// Event-driven hooks only (no per-frame polling):
/// setup on game/host/client start, crew credit on vanilla objective
/// completion, part grants on the game's own budget query, garage snapshot
/// for the log. Every postfix is independently try/caught.
/// </summary>
[HarmonyPatch(typeof(Core), nameof(Core.Start))]
internal static class Patch_CoreStart
{
    static void Prefix()
    {
        try { ExtendedTransformStore.ResetSession(); } catch { }
        try { Zone.ResetSession(); } catch { }
    }

    static void Postfix()
    {
    }
}

[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.StartHost))]
internal static class Patch_StartHost
{
    static void Postfix()
    {
        try { Contracts.OnHostStart(); } catch { }
    }
}

[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.ConnectToHost))]
internal static class Patch_ConnectToHost
{
    static void Postfix()
    {
        try { Contracts.OnClientStart(); } catch { }
    }
}

[HarmonyPatch(typeof(ObjectiveSystem), nameof(ObjectiveSystem.ShowObjectiveCompletionUISuccess))]
internal static class Patch_ObjectiveComplete
{
    static void Postfix(ObjectiveSetup objective)
    {
        try { Contracts.OnObjectiveComplete(objective); } catch { }
    }
}

[HarmonyPatch(typeof(Core.Singleton), nameof(Core.Singleton.GetMaxAvailableComponents))]
internal static class Patch_MaxAvailable
{
    static void Postfix(SCPrefab scPrefab, ref int __result)
    {
        try { __result = CrewParts.ApplyBonus(scPrefab, __result); } catch { }
    }
}

[HarmonyPatch(typeof(UIInventory), nameof(UIInventory.RefreshItems))]
internal static class Patch_GarageDiag
{
    static void Postfix(UIInventory __instance)
    {
        try { Contracts.OnGarageReady(); } catch { }
        try { CrewParts.LogGarage(__instance); } catch { }
    }
}
// REMOVED v2.5: Patch_RowValueDiag (UIInventoryListItem.SetAvailableComponents)
// fails IL codegen on this IL2CPP build ("IL Compile Error", confirmed in log);
// it took down every later hook with it. Row numbers are already proven by the
// garage snapshot + the user's own screen.

[HarmonyPatch(typeof(ObjectiveSetup), nameof(ObjectiveSetup.GetTitle))]
internal static class Patch_MissionTitle
{
    static void Postfix(ObjectiveSetup __instance, ref string __result)
    {
        try
        {
            var t = CustomMissions.TextFor(__instance, "title");
            if (t != null) __result = t;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(ObjectiveSetup), nameof(ObjectiveSetup.GetObjective))]
internal static class Patch_MissionObjective
{
    static void Postfix(ObjectiveSetup __instance, ref string __result)
    {
        try
        {
            var t = CustomMissions.TextFor(__instance, "objective");
            if (t != null) __result = t;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(ObjectiveSetup), nameof(ObjectiveSetup.GetDescription))]
internal static class Patch_MissionDescription
{
    static void Postfix(ObjectiveSetup __instance, ref string __result)
    {
        try
        {
            var t = CustomMissions.TextFor(__instance, "desc");
            if (t != null) __result = t;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(ObjectiveSetup), nameof(ObjectiveSetup.GetHints))]
internal static class Patch_MissionHints
{
    static void Postfix(ObjectiveSetup __instance, ref string __result)
    {
        try
        {
            var t = CustomMissions.TextFor(__instance, "hints");
            if (t != null) __result = t;
        }
        catch { }
    }
}

[HarmonyPatch(typeof(UIObjectiveLog), nameof(UIObjectiveLog.OnEnable))]
internal static class Patch_ObjectiveLogOpened
{
    static void Postfix(UIObjectiveLog __instance)
    {
        try { CustomMissions.LogRows(__instance); } catch { }
    }
}

[HarmonyPatch(typeof(SteamManager), nameof(SteamManager.UpdateManager))]
internal static class Patch_Tick
{
    static void Postfix()
    {
        try { ModTick.OnTick(); } catch { }
    }
}

[HarmonyPatch(typeof(Core.DiskWorldSave), nameof(Core.DiskWorldSave.SaveToFile))]
internal static class Patch_WorldSaveWatch
{
    static void Postfix()
    {
        try { SaveWatch.OnWorldSave(); } catch { }
    }
}

[HarmonyPatch(typeof(Core.GameData), nameof(Core.GameData.SaveToFile))]
internal static class Patch_GameSaveWatch
{
    static void Postfix()
    {
        try { SaveWatch.OnGameSave(); } catch { }
    }
}

/// <summary>
/// Authoritative live-garage expansion. We modify the arguments before the
/// game's own SetRootMatrix so its inverse/rotation/bounds users all see one
/// coherent centered coordinate system.
/// </summary>
[HarmonyPatch(typeof(GarageGrabberSingleton), nameof(GarageGrabberSingleton.SetRootMatrix))]
internal static class Patch_GarageRootMatrix
{
    static void Prefix(ref Unity.Mathematics.double4x4 matrix, ref Unity.Mathematics.float3 boundsSize)
    {
        try { Zone.AdjustRootMatrix(ref matrix, ref boundsSize); } catch { }
    }
}

[HarmonyPatch(typeof(GarageGrabber), nameof(GarageGrabber.DoBeforeStop))]
internal static class Patch_GarageBeforeStop
{
    static void Prefix()
    {
        try { ExtendedTransformStore.CaptureBeforeGarageStops(); } catch { }
    }
}

[HarmonyPatch(typeof(GarageGrabber), nameof(GarageGrabber.OnStartRunning))]
internal static class Patch_GarageStartRunning
{
    static void Postfix()
    {
        try { ExtendedTransformStore.BeginRestore(); } catch { }
    }
}
