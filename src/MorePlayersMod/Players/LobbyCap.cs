using BepInEx.Logging;
using HarmonyLib;
using System;

namespace MorePlayersMod;

/// <summary>
/// Second, independent layer for the lobby size (the first is the native byte
/// patch in Plugin.cs): forces cMaxMembers on Steamworks CreateLobby and, while
/// hosting, raises the member limit of a lobby that already exists.
/// The server-side "ServerIsFull" check can only be lifted by the native patch.
/// </summary>
internal static class LobbyCap
{
    private static ManualLogSource Log;
    private static int _max;
    private static float _nextCheck;

    internal static void Install(Harmony harmony, ManualLogSource log, int maxPlayers)
    {
        Log = log;
        _max = maxPlayers;
        try
        {
            var mm = AccessTools.TypeByName("Steamworks.SteamMatchmaking");
            var create = mm != null ? AccessTools.Method(mm, "CreateLobby") : null;
            if (create == null) { log.LogWarning("LobbyCap: SteamMatchmaking.CreateLobby not found; relying on native patch."); return; }
            harmony.Patch(create, prefix: new HarmonyMethod(typeof(LobbyCap), nameof(CreateLobbyPrefix)));
            log.LogInfo($"LobbyCap: Steam CreateLobby forced to {maxPlayers} members.");
        }
        catch (Exception e) { log.LogWarning($"LobbyCap patch failed (native patch still active): {e.Message}"); }
    }

    private static void CreateLobbyPrefix(ref int cMaxMembers)
    {
        if (cMaxMembers < _max) cMaxMembers = _max;
    }

    internal static void Tick()
    {
        if (_max <= 0) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextCheck) return;
        _nextCheck = now + 5f;
        try { SteamLobby.EnsureMemberLimit(_max); } catch { }
    }
}
