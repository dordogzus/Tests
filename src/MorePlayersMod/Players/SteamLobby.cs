using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace MorePlayersMod;

internal readonly struct LobbyMember
{
    internal readonly ulong Id;
    internal readonly string Name;
    internal readonly bool IsOwner;
    internal readonly bool IsLocal;
    internal LobbyMember(ulong id, string name, bool owner, bool local) { Id = id; Name = name; IsOwner = owner; IsLocal = local; }
}

/// <summary>
/// Read-only view of the game's Steam lobby via the Steamworks.NET interop
/// (resolved by name, so a missing member disables the roster, not the mod).
/// Uses Core._steam.GetLobby() / _lobbyState exactly like other working mods.
/// </summary>
internal static class SteamLobby
{
    private static ManualLogSource Log;
    private static bool _resolved, _available;
    private static Type _steamId;
    private static FieldInfo _steamIdValue;
    private static MethodInfo _numMembers, _memberByIndex, _owner, _getLimit, _setLimit, _friendName, _localId, _invite;
    private static readonly Dictionary<ulong, string> NameCache = new Dictionary<ulong, string>();

    internal static void Bind(ManualLogSource log) => Log = log;

    internal static bool Available { get { Resolve(); return _available; } }

    private static void Resolve()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            _steamId = AccessTools.TypeByName("Steamworks.CSteamID");
            var mm = AccessTools.TypeByName("Steamworks.SteamMatchmaking");
            var friends = AccessTools.TypeByName("Steamworks.SteamFriends");
            var user = AccessTools.TypeByName("Steamworks.SteamUser");
            if (_steamId == null || mm == null || friends == null || user == null) throw new Exception("Steamworks interop types not found");
            _steamIdValue = AccessTools.Field(_steamId, "m_SteamID");
            _numMembers = AccessTools.Method(mm, "GetNumLobbyMembers");
            _memberByIndex = AccessTools.Method(mm, "GetLobbyMemberByIndex");
            _owner = AccessTools.Method(mm, "GetLobbyOwner");
            _getLimit = AccessTools.Method(mm, "GetLobbyMemberLimit");
            _setLimit = AccessTools.Method(mm, "SetLobbyMemberLimit");
            _friendName = AccessTools.Method(friends, "GetFriendPersonaName");
            _invite = AccessTools.Method(friends, "ActivateGameOverlayInviteDialog");
            _localId = AccessTools.Method(user, "GetSteamID");
            _available = _steamIdValue != null && _numMembers != null && _memberByIndex != null;
            Log?.LogInfo($"Steam lobby access: {(_available ? "ready" : "unavailable (roster disabled)")}; " +
                         $"limit get/set={_getLimit != null}/{_setLimit != null}, invite={_invite != null}.");
        }
        catch (Exception e)
        {
            _available = false;
            Log?.LogWarning($"Steam lobby access unavailable, crew roster disabled: {e.Message}");
        }
    }

    private static ulong IdOf(object steamId)
    {
        if (steamId == null) return 0;
        try { return Convert.ToUInt64(_steamIdValue.GetValue(steamId)); } catch { return 0; }
    }

    private static object MakeId(ulong id)
    {
        object boxed = Activator.CreateInstance(_steamId);
        _steamIdValue.SetValue(boxed, id);
        return boxed;
    }

    /// <summary>Current lobby when the game reports Created/Connected, else null.</summary>
    private static object CurrentLobby(out bool isHost)
    {
        isHost = false;
        var core = Core.Get();
        var steam = core != null ? core._steam : null;
        if (steam == null) return null;
        try
        {
            var state = Traverse.Create(steam).Property("_lobbyState").GetValue()?.ToString();
            if (state != null && state != "Created" && state != "Connected") return null;
        }
        catch { }
        object lobby;
        try { lobby = AccessTools.Method(steam.GetType(), "GetLobby")?.Invoke(steam, null); }
        catch { return null; }
        if (IdOf(lobby) == 0) return null;
        try { isHost = steam.IsHost(); } catch { }
        return lobby;
    }

    internal static bool InLobby => Available && CurrentLobby(out _) != null;

    internal static bool TryRead(List<LobbyMember> members, out int limit, out bool isHost)
    {
        members.Clear();
        limit = 0;
        isHost = false;
        if (!Available) return false;
        try
        {
            var lobby = CurrentLobby(out isHost);
            if (lobby == null) return false;
            int count = Convert.ToInt32(_numMembers.Invoke(null, new[] { lobby }));
            ulong owner = _owner != null ? IdOf(_owner.Invoke(null, new[] { lobby })) : 0;
            ulong local = _localId != null ? IdOf(_localId.Invoke(null, null)) : 0;
            if (_getLimit != null) limit = Convert.ToInt32(_getLimit.Invoke(null, new[] { lobby }));
            for (int i = 0; i < count && i < 256; i++)
            {
                ulong id = IdOf(_memberByIndex.Invoke(null, new object[] { lobby, i }));
                if (id == 0) continue;
                members.Add(new LobbyMember(id, NameOf(id), id == owner, id == local));
            }
            return true;
        }
        catch { return false; }
    }

    private static string NameOf(ulong id)
    {
        if (NameCache.TryGetValue(id, out var cached) && !string.IsNullOrEmpty(cached) && cached != "[unknown]") return cached;
        string name = null;
        try { name = _friendName?.Invoke(null, new[] { MakeId(id) }) as string; } catch { }
        if (string.IsNullOrEmpty(name)) name = "Player " + (id % 10000).ToString("D4");
        NameCache[id] = name;
        return name;
    }

    /// <summary>Host only: raise the Steam member limit of an already created lobby.</summary>
    internal static bool EnsureMemberLimit(int wanted)
    {
        if (!Available || _getLimit == null || _setLimit == null) return false;
        try
        {
            var lobby = CurrentLobby(out bool isHost);
            if (lobby == null || !isHost) return false;
            int current = Convert.ToInt32(_getLimit.Invoke(null, new[] { lobby }));
            if (current >= wanted) return true;
            var ok = _setLimit.Invoke(null, new object[] { lobby, wanted });
            Log?.LogInfo($"Lobby member limit {current} -> {wanted} (Steam accepted={ok}).");
            return true;
        }
        catch { return false; }
    }

    internal static void OpenInviteDialog()
    {
        try
        {
            var lobby = CurrentLobby(out _);
            if (lobby != null && _invite != null) _invite.Invoke(null, new[] { lobby });
        }
        catch (Exception e) { Log?.LogWarning($"Invite dialog failed: {e.Message}"); }
    }
}
