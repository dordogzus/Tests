using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace MorePlayersMod;

/// <summary>
/// Co-op layer on top of the vanilla fetch loop:
///
/// * SUPPLY RUNS: any vanilla Package objective completion counts as a crew
///   supply run (fetch the package from another planet/station, haul it to
///   base) and grants bonus seats + consoles for the whole crew.
/// * CREW LEVELS: any other objective (Delivery / Special - herding,
///   transports, story beats) raises the crew level and grants a smaller
///   bonus, so scouts, haulers, builders and herders all progress the crew.
/// * SUGGESTIONS: the mod points the crew at the next vanilla package
///   objective (Earth first, then outward), so there is always a "main
///   quest" for 5-24 players. Suggestions never block: ANY package delivery
///   fulfils the run.
///
/// Progress is rebuilt from the world save (completed package objectives), so
/// earned seats and Frontier tiers survive restarts, and is recomputed when the
/// crew grows so packs always match the current lobby size.
/// </summary>
internal static class Contracts
{
    private static ManualLogSource Log;
    private static int MaxPlayers;

    private static ConfigEntry<bool> CfgContracts;
    private static ConfigEntry<bool> CfgCrewLevels;
    private static ConfigEntry<float> CfgAnnounceSeconds;
    private static int _lastAnnouncedProgress = -1;

    // Host role tracking: StartHost runs only when hosting, ConnectToHost only on clients.
    // Core.Start runs everywhere (incl. single-player) - silent setup so SP gets grants too.
    private static bool _isHost = true;
    private static bool _setupDone;
    private static int _supplyRuns;
    private static int _crewLevel;
    private static int _suggestIdx;
    private static List<string> _packageTitles;
    private static readonly HashSet<uint> _sessionPackages = new HashSet<uint>();
    private static readonly List<LobbyMember> _members = new List<LobbyMember>();
    private static int _savedPackages;
    private static int _grantCrew;
    private static float _nextCrewCheck;

    internal static void Bind(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        Log = log;
        MaxPlayers = maxPlayers;
        CfgContracts = config.Bind("Contracts",
            "EnableSupplyRuns", true,
            "Vanilla package deliveries advance the matching custom CREW mission and pay its pack.");
        CfgCrewLevels = config.Bind("Contracts",
            "EnableCrewLevels", true,
            "Announce crew levels on Delivery/Special completions (flavor only, no grants).");
        CfgAnnounceSeconds = config.Bind("Contracts",
            "AnnounceSeconds", 8f,
            "How long crew announcements stay on screen.");
    }

    // REMOVED: SteamManager.LOBBY_MAX_PLAYERS setter. It is a compile-time
    // const (game code uses it as an inlined `mov edx, 4` immediate - that is
    // exactly the instruction our native patch rewrites). The interop setter
    // has no static storage behind it and throws an UNCAUGHT
    // AccessViolationException (process kill, try/catch cannot stop it).
    // The native patch is the only and correct way - nothing else needed.

    internal static void OnCoreStart()
    {
        try
        {
            // Silent: messenger may not exist yet; just arm grants + titles.
            // If StartHost fires later it re-runs loudly (announce).
            EnsureSetup(announce: false, reason: "local game");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts.OnCoreStart issue: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static void OnHostStart()
    {
        try
        {
            _isHost = true;
            _setupDone = false; // re-run loudly now that the world + messenger exist
            EnsureSetup(announce: true, reason: "host");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts.OnHostStart issue: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void EnsureSetup(bool announce, string reason)
    {
        if (_setupDone) return;
        _setupDone = true;
        _titlesFinalized = false; // save may not exist yet; tick retries later
        try { if (CustomMissions.Enabled()) CustomMissions.EnsureInjected(); } catch { }
        Recompute(reason);
        try { if (Zone.ExpansionEnabled && !Zone.Done) Zone.EnsureScaled(); } catch { }
        RefreshPackageTitles();
        if (announce)
            Announce($"MORE PLAYERS x{MaxPlayers} - deliver packages to earn seats for the whole crew. {SuggestNext()}");
        else
            Log.LogInfo($"Contracts: silent setup done ({reason}), garage grants armed.");
    }

    internal static void OnGarageReady()
    {
        try { Zone.ApplyRootAtRuntime(); } catch { }
        try { EnsureSetup(announce: false, reason: "garage"); } catch { }
    }

    internal static void OnClientStart()
    {
        _isHost = false;
        try { EnsureSetup(announce: false, reason: "client"); } catch { }
    }

    internal static void OnObjectiveComplete(ObjectiveSetup objective)
    {
        try
        {
            if (objective == null) return;
            ObjectiveType type;
            string title;
            try { type = objective._objectiveType; }
            catch { return; }
            try { title = objective.GetTitle(); } catch { title = "objective"; }
            if (string.IsNullOrEmpty(title)) title = "objective";
            try
            {
                string oidName = objective._objectiveID.ToString();
                uint oidNum = 0;
                try { oidNum = (uint)objective._objectiveID; } catch { }
                Log.LogInfo($"Completion seen: type={type} id={oidName} ({oidNum}) title='{title}'.");
            }
            catch { }

            if (type == ObjectiveType.Package)
            {
                if (!CfgContracts.Value) return;
                uint oid = 0;
                try { oid = (uint)objective._objectiveID; } catch { }
                if (oid != 0 && (_sessionPackages.Contains(oid) || _savedPackagesIds.Contains(oid))) return; // repeat delivery: vanilla reward only
                string missionTitle, granted;
                if (!CustomMissions.TryCompleteFromVanilla(objective, Math.Max(1, _grantCrew), out missionTitle, out granted)) return;
                if (oid != 0) _sessionPackages.Add(oid);
                _supplyRuns = _savedPackages + _sessionPackages.Count;
                if (_isHost)
                    Announce($"SUPPLY RUN #{_supplyRuns} COMPLETE ({missionTitle}): {title} delivered - {granted}, in the garage. {SuggestNext()}");
            }
            else
            {
                if (!CfgCrewLevels.Value) return;
                _crewLevel++;
                if (_isHost)
                    Announce($"CREW LEVEL {_crewLevel}: {title} done. Finish a CREW mission delivery for parts.");
            }
            _lastAnnouncedProgress = _supplyRuns * 1000 + _crewLevel;
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts.OnObjectiveComplete issue: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static int SupplyRuns => _supplyRuns;

    private static readonly HashSet<uint> _savedPackagesIds = new HashSet<uint>();

    private static int LiveCrew()
    {
        try { if (SteamLobby.TryRead(_members, out _, out _) && _members.Count > 0) return _members.Count; } catch { }
        return 1;
    }

    /// <summary>
    /// Authoritative rebuild: reset our grants, then pay one pack per completed package
    /// objective in the save plus this session's deliveries, sized for the current crew.
    /// </summary>
    internal static void Recompute(string reason)
    {
        try
        {
            int crew = Math.Max(_grantCrew, LiveCrew());
            CrewParts.ResetBonuses();
            _savedPackagesIds.Clear();
            var done = CompletedObjectiveIds();
            var names = PackageNames();
            foreach (var id in done)
            {
                if (!names.TryGetValue(id, out var idName)) continue;
                _savedPackagesIds.Add(id);
                CustomMissions.GrantSilently(idName, crew);
            }
            foreach (var id in _sessionPackages)
                if (!_savedPackagesIds.Contains(id) && names.TryGetValue(id, out var idName)) CustomMissions.GrantSilently(idName, crew);
            _savedPackages = _savedPackagesIds.Count;
            var all = new HashSet<uint>(_savedPackagesIds);
            all.UnionWith(_sessionPackages);
            _supplyRuns = all.Count;
            _grantCrew = crew;
            Log.LogInfo($"Crew progress rebuilt ({reason}): {_supplyRuns} supply runs, packs sized for {crew} crew (x{Logic.CrewProgression.CrewScale(crew)}).");
        }
        catch (Exception e) { Log.LogWarning($"Crew progress rebuild skipped: {e.GetType().Name}: {e.Message}"); }
    }

    private static Dictionary<uint, string> PackageNames()
    {
        var map = new Dictionary<uint, string>();
        try
        {
            var core = Core.Get();
            if (core == null || core._objectives == null) return map;
            foreach (var o in core._objectives)
            {
                try
                {
                    if (o == null || o._objectiveType != ObjectiveType.Package) continue;
                    uint id = (uint)o._objectiveID;
                    if (CustomMissions.IsOurs(id)) continue;
                    map[id] = o._objectiveID.ToString();
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    /// <summary>More crew joined than the packs were sized for: rebuild so seats keep up.</summary>
    private static void CheckCrewGrowth()
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextCrewCheck || !_setupDone) return;
        _nextCrewCheck = now + 10f;
        int crew = LiveCrew();
        if (Logic.CrewProgression.CrewScale(crew) <= Logic.CrewProgression.CrewScale(_grantCrew)) return;
        Recompute($"crew grew to {crew}");
        if (_isHost) Announce($"CREW OF {crew}: supply-run packs rescaled - more seats and consoles in the garage.");
    }
    internal static int CrewLevel => _crewLevel;

    /// <summary>Re-announces the mission when progress changed (garage visits stay current).</summary>
    internal static void AnnounceIfProgressed()
    {
        try
        {
            if (!_isHost) return;
            int progress = _supplyRuns * 1000 + _crewLevel;
            if (progress == _lastAnnouncedProgress) return;
            _lastAnnouncedProgress = progress;
            AnnounceCurrent();
        }
        catch { }
    }

    private static string SuggestNext()
    {
        try
        {
            if (_packageTitles == null || _packageTitles.Count == 0) return "Next: deliver any package you find.";
            string t = _packageTitles[_suggestIdx % _packageTitles.Count];
            _suggestIdx++;
            return $"Next supply run: {t}.";
        }
        catch { return "Next: deliver any package you find."; }
    }

    /// <summary>Same suggestion without consuming it (board + repeat announces).</summary>
    private static string PeekNext()
    {
        try
        {
            if (_packageTitles == null || _packageTitles.Count == 0) return "Next: deliver any package you find.";
            return $"Next supply run: {_packageTitles[_suggestIdx % _packageTitles.Count]}.";
        }
        catch { return "Next: deliver any package you find."; }
    }

    /// <summary>One-time config migration: stale values from older versions
    /// (BepInEx never overwrites them) are moved to the current design.</summary>
    internal static void Migrate()
    {
        try
        {
            CfgAnnounceSeconds.Value = 8f;
            Log.LogInfo("Contracts: config migrated (8s vanilla temporary notices).");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts migration issue: {e.GetType().Name}: {e.Message}");
        }
    }

    private static bool _titlesFinalized;

    /// <summary>Retry the mission queue once the world save exists.</summary>
    internal static void TickRefresh()
    {
        try { CheckCrewGrowth(); } catch { }
        try { CrewParts.TickRefresh(); } catch { }
        try
        {
            if (_titlesFinalized) return;
            var core = Core.Get();
            var world = core != null && core._save != null ? core._save._world : null;
            if (world == null) return;
            RefreshPackageTitles();
            Recompute("world save ready");
            _titlesFinalized = true;
        }
        catch { }
    }

    private static void RefreshPackageTitles()
    {
        try
        {
            System.Collections.Generic.IEnumerable<ObjectiveSetup> all = null;
            try
            {
                var core = Core.Get();
                if (core != null) all = core._objectives; // master registry, Earth-first order
            }
            catch { }
            if (all == null)
            {
                try { all = UnityEngine.Resources.FindObjectsOfTypeAll<ObjectiveSetup>(); }
                catch { }
            }
            if (all == null) throw new Exception("no objective source");
            var ids = new HashSet<uint>();
            var tmp = new List<(uint order, string title, uint oid)>();
            foreach (var o in all)
            {
                try
                {
                    if (o == null) continue;
                    if (o._objectiveType != ObjectiveType.Package) continue;
                    uint oid = (uint)o._objectiveID;
                    if (!ids.Add(oid)) continue;
                    string title;
                    try { title = o.GetTitle(); } catch { title = null; }
                    if (string.IsNullOrEmpty(title)) title = oid.ToString();
                    uint order;
                    try { order = (uint)o._start; } catch { order = oid; }
                    tmp.Add((order, title, oid));
                }
                catch { /* ignore single bad entry */ }
            }
            tmp.Sort((a, b) => a.order.CompareTo(b.order));
            // Drop already-completed package objectives (Earth's 3 done ones must
            // stop being suggested). Completed state lives in the world save.
            int skipped = 0;
            try
            {
                var done = CompletedObjectiveIds();
                if (done != null && done.Count > 0)
                {
                    int before = tmp.Count;
                    tmp.RemoveAll(e => done.Contains(e.oid));
                    skipped = before - tmp.Count;
                }
            }
            catch { }
            if (tmp.Count == 0)
            {
                // Everything done: fall back to the full list rather than nothing.
                Log.LogInfo("Contracts: all package objectives complete - suggesting replays.");
                RefreshPackageTitlesUnfiltered();
                return;
            }
            _packageTitles = new List<string>();
            foreach (var e in tmp) _packageTitles.Add(e.title);
            _suggestIdx = 0;
            Log.LogInfo($"Contracts: {_packageTitles.Count} incomplete package objectives queued (skipped {skipped} completed).");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts: package queue skipped: {e.GetType().Name}: {e.Message}");
            _packageTitles = null;
        }
    }

    private static void RefreshPackageTitlesUnfiltered()
    {
        // Last resort: keep every package title (used only when all are done).
        try
        {
            var core = Core.Get();
            if (core == null || core._objectives == null) return;
            var titles = new List<string>();
            foreach (var o in core._objectives)
            {
                try
                {
                    if (o == null || o._objectiveType != ObjectiveType.Package) continue;
                    string t = null;
                    try { t = o.GetTitle(); } catch { }
                    if (!string.IsNullOrEmpty(t)) titles.Add(t);
                }
                catch { }
            }
            if (titles.Count > 0) { _packageTitles = titles; _suggestIdx = 0; }
        }
        catch { }
    }

    /// <summary>Objective IDs already completed in this world save (to skip).</summary>
    private static HashSet<uint> CompletedObjectiveIds()
    {
        var out_ = new HashSet<uint>();
        try
        {
            var core = Core.Get();
            var world = core != null && core._save != null ? core._save._world : null;
            if (world == null) return out_;
            var dict = world._objectives; // SerializableDictionary<ObjectID, ObjectiveSave>
            if (dict == null) return out_;
            foreach (var kv in dict)
            {
                try
                {
                    var save = kv.Value;
                    bool done = false;
                    try { done = save.IsCompleted(); } catch { }
                    if (!done) continue;
                    try { out_.Add(unchecked((uint)kv.Key)); } catch { }
                }
                catch { }
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts completion read skipped: {e.GetType().Name}: {e.Message}");
        }
        return out_;
    }

    internal static void AnnounceCurrent()
    {
        try
        {
            _lastAnnouncedProgress = _supplyRuns * 1000 + _crewLevel;
            Announce($"MORE PLAYERS x{MaxPlayers} - crew garage grants active. {PeekNext()}");
        }
        catch { }
    }

    private static void Announce(string text)
    {
        try
        {
            var arr = UnityEngine.Resources.FindObjectsOfTypeAll<UIMessenger>();
            UIMessenger ui = null;
            foreach (var m in arr) { if (m != null) { ui = m; break; } }
            if (ui == null)
            {
                Log.LogInfo("Crew notice (no messenger yet): " + text);
                return;
            }
            float dur = CfgAnnounceSeconds.Value;
            if (dur < 2f) dur = 2f;
            if (dur > 30f) dur = 30f;
            ui.CreateNetcoreMessage(text, dur, true);
            Log.LogInfo("Crew notice: " + text);
        }
        catch (Exception e)
        {
            Log.LogWarning($"Contracts announce fallback (log only): {e.GetType().Name}: {e.Message}. Text: {text}");
        }
    }
}
