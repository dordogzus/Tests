using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace MorePlayersMod;

/// <summary>
/// Grants MORE OF THE ORIGINAL parts (same vanilla Seat, frames, thrusters,
/// etc., nothing custom) by writing bonuses into the prefabs' _availableAmount
/// (the number the garage rows display) plus a hook on the game's own budget
/// query. Classes cover the whole catalog so missions can reward everything,
/// not just seats. Vanilla progression stays intact; counts just go higher.
/// Works in single-player too, and on every modded client consistently.
/// </summary>
internal static class CrewParts
{
    // Pack classes in match-priority order (first match wins).
    internal static readonly string[] ClassOrder = new string[]
        { "Seats", "Consoles", "Power", "Frames", "Glass", "Thrusters", "Cables", "Tools", "Sensors" };

    private static readonly Dictionary<string, string[]> DefaultFilters = new Dictionary<string, string[]>
    {
        { "Seats", new string[] { "Seat", "Chair", "Cockpit" } },
        { "Consoles", new string[] { "Console", "Lever", "Button", "Knob", "Valve", "Joystick", "Control", "Switch", "Panel", "Monitor", "Radar" } },
        { "Power", new string[] { "Battery", "Batteries", "Router", "Power", "Dynamo", "Accumulator", "Generator", "Fuse" } },
        { "Frames", new string[] { "Frame", "Decoupler", "Damper" } },
        { "Glass", new string[] { "Glass", "Window", "Porthole" } },
        { "Thrusters", new string[] { "Thruster", "Fan", "RCS", "Turbopump" } },
        { "Cables", new string[] { "Cable", "Pipe", "Hub", "Transmitter", "Redirector" } },
        { "Tools", new string[] { "Whiteboard", "Label", "Light", "Camera", "Drum", "Beeper", "Welder" } },
        { "Sensors", new string[] { "Meter", "Scan", "Gyro", "Magnetometer", "Thermometer", "Accelerometer", "Altimeter", "Inclinometer", "Atmometer", "Aerometer", "Anemometer", "Barometer", "Speedometer", "Compass" } },
    };

    private static ManualLogSource Log;
    private static int MaxPlayers;

    private static ConfigEntry<bool> CfgBoostAtStart;
    private static ConfigEntry<int> CfgBaseSeatBonus;
    private static ConfigEntry<int> CfgBaseConsoleBonus;
    private static ConfigEntry<string> CfgSeatFilter;
    private static ConfigEntry<string> CfgConsoleFilter;
    private static ConfigEntry<bool> CfgLogPartNames;
    private static ConfigEntry<bool> CfgLogCatalog;

    // Bonus counters per class + exact deltas WE wrote (for revert).
    private static Dictionary<string, int> s_bonus;
    private static Dictionary<SCPrefab, int> s_applied;
    private static Dictionary<SCPrefab, string> s_classOf;
    private static Dictionary<string, int> s_lastValues;

    private static bool _catalogNamesLogged;

    internal static void Bind(ConfigFile config, ManualLogSource log, int maxPlayers)
    {
        Log = log;
        MaxPlayers = maxPlayers;
        s_bonus = new Dictionary<string, int>();
        foreach (var c in ClassOrder) s_bonus[c] = 0;
        CfgBoostAtStart = config.Bind("CrewParts",
            "BoostAtHostStart", true,
            "Grant base seats/consoles automatically (scaled by MaxPlayers). Everything else is mission-earned.");
        CfgBaseSeatBonus = config.Bind("CrewParts",
            "BaseSeatBonus", 0,
            "Free original seats at start. Default 0: everything is mission-earned (vanilla starter seats stay).");
        CfgBaseConsoleBonus = config.Bind("CrewParts",
            "BaseConsoleBonus", 0,
            "Free original consoles/controls at start. Default 0: mission-earned.");
        CfgSeatFilter = config.Bind("CrewParts",
            "SeatNameFilter", "Seat,Chair,Cockpit",
            "Override substrings for the Seats class (empty = default).");
        CfgConsoleFilter = config.Bind("CrewParts",
            "ConsoleNameFilter", "Console,Lever,Button,Knob,Valve,Joystick,Control,Switch,Panel,Monitor,Radar",
            "Override substrings for the Consoles class (empty = default).");
        CfgLogPartNames = config.Bind("CrewParts",
            "LogAllPartNames", false,
            "Log every catalog part name with its class once.");
        CfgLogCatalog = config.Bind("CrewParts",
            "LogGarageCatalog", true,
            "Log garage row count on build-menu refresh (once with full names + amounts).");
    }

    internal static void ResetBonuses()
    {
        try
        {
            EnsureSets();
            RevertApplied();
            foreach (var c in ClassOrder) s_bonus[c] = 0;
            if (CfgBoostAtStart.Value)
            {
                // Day-one starter is intentionally empty by default: the crew
                // earns everything through missions (vanilla starters stay).
                s_bonus["Seats"] = Math.Max(Math.Max(0, CfgBaseSeatBonus.Value), 0);
                s_bonus["Consoles"] = Math.Max(0, CfgBaseConsoleBonus.Value);
            }
            ApplyBudgetDelta("Seats", s_bonus["Seats"]);
            ApplyBudgetDelta("Consoles", s_bonus["Consoles"]);
            Log.LogInfo($"CrewParts: base grants reset -> +{s_bonus["Seats"]} seats, +{s_bonus["Consoles"]} consoles (rest is mission-earned).");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts.ResetBonuses issue: {e.GetType().Name}: {e.Message}");
        }
    }

    internal static void AddPackBonus(string className, int amount, bool quiet = false)
    {
        try
        {
            if (string.IsNullOrEmpty(className) || !s_bonus.ContainsKey(className)) return;
            amount = Math.Max(0, amount);
            if (amount <= 0) return;
            EnsureSets();
            s_bonus[className] += amount;
            ApplyBudgetDelta(className, amount);
            if (!quiet) Log.LogInfo($"CrewParts: {className} pack -> class total +{s_bonus[className]}.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts.AddPackBonus issue: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>One-time config migration for the earned-only design.</summary>
    internal static void Migrate()
    {
        try
        {
            CfgBaseSeatBonus.Value = 0;
            CfgBaseConsoleBonus.Value = 0;
            Log.LogInfo("CrewParts: config migrated (no starting handouts, everything mission-earned).");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts migration issue: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Copy of per-class bonus totals, for the mission board.</summary>
    internal static Dictionary<string, int> Totals()
    {
        var out_ = new Dictionary<string, int>();
        try
        {
            if (s_bonus != null)
                foreach (var kv in s_bonus) out_[kv.Key] = kv.Value;
        }
        catch { }
        return out_;
    }

    /// <summary>Two example part names of a class, for mission messages.</summary>
    internal static List<string> Examples(string className, int count)
    {
        var out_ = new List<string>();
        try
        {
            if (s_classOf == null) return out_;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var core = Core.Get();
            if (core == null || core._spaceshipComponents == null) return out_;
            foreach (var epc in core._spaceshipComponents)
            {
                try
                {
                    if (epc == null) continue;
                    var key = new SCPrefab(epc);
                    string cls;
                    if (!s_classOf.TryGetValue(key, out cls) || cls != className) continue;
                    string n = null;
                    try { n = epc.GetName(); } catch { }
                    if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                    out_.Add(n);
                    if (out_.Count >= count) break;
                }
                catch { }
            }
        }
        catch { }
        return out_;
    }

    /// <summary>
    /// Hot path: runs on every garage budget query. Must be tiny and
    /// exception-proof. Returns vanilla + our bonus for matched classes.
    /// </summary>
    internal static int ApplyBonus(SCPrefab prefab, int vanilla)
    {
        try
        {
            var map = s_classOf;
            if (map == null) return vanilla;
            if (vanilla >= 100000) return vanilla; // leave infinite markers alone
            string cls;
            if (!map.TryGetValue(prefab, out cls)) return vanilla;
            int b = 0;
            if (!s_bonus.TryGetValue(cls, out b) || b <= 0) return vanilla;
            int v = vanilla + b;
            return v > 999 ? 999 : v;
        }
        catch { return vanilla; }
    }

    /// <summary>
    /// Writes OUR delta for one class straight into the original prefabs'
    /// _availableAmount (the number the garage rows display). Only touches
    /// sane finite budgets (0..899): x0 locked rows open up, infinities stay.
    /// </summary>
    private static void ApplyBudgetDelta(string className, int amount)
    {
        try
        {
            if (amount <= 0) { Refresh(); return; }
            var core = Core.Get();
            if (core == null) throw new Exception("Core.Get() returned null");
            var catalog = core._spaceshipComponents;
            if (catalog == null) throw new Exception("spaceshipComponents catalog missing");
            string[] filters = FiltersFor(className);
            if (s_applied == null) s_applied = new Dictionary<SCPrefab, int>();
            int touched = 0;
            foreach (var epc in catalog)
            {
                try
                {
                    if (epc == null) continue;
                    string name = null;
                    try { name = epc.GetName(); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    // Direct single-class match (Classify over a one-entry map
                    // can never hit other classes - that bug starved Consoles+).
                    if (!Match(name, filters)) continue;
                    int cur = epc._availableAmount;
                    if (cur < 0 || cur >= 900) continue;
                    epc._availableAmount = Math.Min(999, cur + amount);
                    var key = new SCPrefab(epc);
                    int prev = 0;
                    s_applied.TryGetValue(key, out prev);
                    s_applied[key] = prev + amount;
                    touched++;
                }
                catch { /* ignore single bad entry */ }
            }
            Log.LogInfo($"CrewParts: wrote +{amount} {className} budgets into {touched} original prefabs.");
            Refresh();
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts.ApplyBudgetDelta issue: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void RevertApplied()
    {
        try
        {
            if (s_applied == null || s_applied.Count == 0) return;
            var core = Core.Get();
            if (core == null) { s_applied.Clear(); return; }
            var catalog = core._spaceshipComponents;
            if (catalog == null) { s_applied.Clear(); return; }
            var map = new Dictionary<SCPrefab, EPC_SpaceshipComponent>();
            foreach (var epc in catalog)
            {
                try { if (epc != null) map[new SCPrefab(epc)] = epc; } catch { }
            }
            int reverted = 0;
            foreach (var kv in s_applied)
            {
                try
                {
                    EPC_SpaceshipComponent epc;
                    if (!map.TryGetValue(kv.Key, out epc) || epc == null) continue;
                    epc._availableAmount = Math.Max(0, epc._availableAmount - kv.Value);
                    reverted++;
                }
                catch { }
            }
            s_applied.Clear();
            Log.LogInfo($"CrewParts: reverted previous session deltas on {reverted} prefabs.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts.RevertApplied issue: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void EnsureSets()
    {
        if (s_classOf != null) return;
        var map = new Dictionary<SCPrefab, string>();
        try
        {
            var core = Core.Get();
            if (core == null) throw new Exception("Core.Get() returned null");
            var catalog = core._spaceshipComponents;
            if (catalog == null) throw new Exception("spaceshipComponents catalog missing");
            var filters = new Dictionary<string, string[]>();
            foreach (var c in ClassOrder) filters[c] = FiltersFor(c);
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>();
            foreach (var c in ClassOrder) counts[c] = 0;
            foreach (var epc in catalog)
            {
                try
                {
                    if (epc == null || FrontierBlocks.IsFrontier(epc)) continue; // Frontier stock is tier-gated separately
                    string name = null;
                    try { name = epc.GetName(); } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    string cls = Classify(name, filters);
                    if (cls == null) continue;
                    map[new SCPrefab(epc)] = cls;
                    counts[cls]++;
                    if (CfgLogPartNames.Value) names.Add($"{name} [{cls}]");
                }
                catch { /* ignore single bad entry */ }
            }
            foreach (var c in ClassOrder) Log.LogInfo($"CrewParts class {c}: {counts[c]} parts.");
            if (CfgLogPartNames.Value)
                foreach (var n in names) Log.LogInfo($"CrewParts part: {n}");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CrewParts: catalog scan failed: {e.GetType().Name}: {e.Message}");
        }
        s_classOf = map;
    }

    private static string[] FiltersFor(string className)
    {
        try
        {
            if (className == "Seats")
            {
                string csv = CfgSeatFilter.Value;
                if (!string.IsNullOrWhiteSpace(csv)) return SplitFilter(csv);
            }
            if (className == "Consoles")
            {
                string csv = CfgConsoleFilter.Value;
                if (!string.IsNullOrWhiteSpace(csv)) return SplitFilter(csv);
            }
        }
        catch { }
        string[] d;
        if (DefaultFilters.TryGetValue(className, out d)) return d;
        return new string[0];
    }

    private static string Classify(string name, Dictionary<string, string[]> filters)
    {
        try
        {
            if (filters == null)
            {
                filters = new Dictionary<string, string[]>();
                foreach (var c in ClassOrder) filters[c] = FiltersFor(c);
            }
            foreach (var c in ClassOrder)
            {
                string[] f = filters[c];
                if (f == null) continue;
                foreach (var t in f)
                    if (name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return c;
            }
        }
        catch { }
        return null;
    }

    private static bool Match(string name, string[] filters)
    {
        foreach (var f in filters)
            if (name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string[] SplitFilter(string csv)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(csv)) return list.ToArray();
        foreach (var f in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = f.Trim();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }

    private static bool _refreshPending;
    private static float _nextRefreshAt;

    internal static void RequestRefresh() => _refreshPending = true;

    /// <summary>
    /// The garage validates placement against availability maps that the game rebuilds in
    /// Core.RefreshSharedAvailableComponents; ask for one rebuild after our budgets change,
    /// only in build mode (the call is unsafe during early startup).
    /// </summary>
    internal static void TickRefresh()
    {
        if (!_refreshPending || !ExtendedTransformStore.GarageBuildMode) return;
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (now < _nextRefreshAt) return;
        _nextRefreshAt = now + 2f;
        _refreshPending = false;
        try
        {
            var core = Core.Get();
            var m = core != null ? HarmonyLib.AccessTools.Method(core.GetType(), "RefreshSharedAvailableComponents") : null;
            if (m != null && m.GetParameters().Length == 0) m.Invoke(core, null);
        }
        catch (Exception e) { Log.LogDebug($"Availability refresh skipped: {e.Message}"); }
    }

    private static void Refresh()
    {
        _refreshPending = true;
        // v2.17: do not force Core.RefreshStandaloneAvailableComponents here.
        // During early startup the game object graph is incomplete and this
        // native call throws NullReferenceException. GetMaxAvailableComponents
        // is already Harmony-patched, so the next normal inventory refresh sees
        // the correct crew bonus without forcing an unsafe refresh.
    }

    /// <summary>
    /// Garage diagnostic: log-only snapshot of what the build menu sees.
    /// First call also announces the current contract (messenger exists by
    /// now, so single-player sees the mission too). Re-announces whenever
    /// crew progress changed since the last announce.
    /// </summary>
    private static bool _announced;
    internal static void LogGarage(UIInventory inv)
    {
        try
        {
            if (inv == null) return;
            try { Contracts.AnnounceIfProgressed(); } catch { }
            if (!_announced)
            {
                _announced = true;
                try { Contracts.AnnounceCurrent(); } catch { }
            }
            try { if (!Zone.Done) Zone.EnsureScaled(); } catch { }
            try { Zone.LogYardState(); } catch { }
            var items = inv._allListItems;
            int count = -1;
            try { count = items != null ? items.Count : -1; } catch { }
            int boosted = 0;
            List<string> names = null;
            bool wantNames = false;
            try { wantNames = CfgLogCatalog.Value && !_catalogNamesLogged; } catch { }
            if (wantNames) names = new List<string>();
            if (items != null && count > 0)
            {
                try
                {
                    foreach (var it in items)
                    {
                        try
                        {
                            if (it == null) continue;
                            var prefab = it._prefab;
                            if (prefab == null) continue;
                            string n = null;
                            try { n = prefab.GetName(); } catch { }
                            if (string.IsNullOrEmpty(n)) continue;
                            if (names != null)
                            {
                                int amt = -999;
                                try { amt = prefab._availableAmount; } catch { }
                                names.Add($"{n} x{amt}");
                            }
                            if (s_classOf != null)
                            {
                                try { if (s_classOf.ContainsKey(new SCPrefab(prefab))) boosted++; } catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Log.LogInfo($"Garage catalog: {count} rows, {boosted} boosted (crew classes).");
            if (names != null)
            {
                _catalogNamesLogged = true;
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var n in names) Log.LogInfo($"Garage row: {n}");
            }
        }
        catch (Exception e)
        {
            Log.LogWarning($"Garage diag skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Tick-visible counts: master array size + garage rows, no menu needed.
    /// Proves (or kills) catalog registration for custom items.
    /// </summary>
    private static string _lastCountsSig;
    internal static void LogCountsTick()
    {
        try
        {
            int arr = -1, rows = -1;
            try
            {
                var core = Core.Get();
                var cat = core != null ? core._spaceshipComponents : null;
                if (cat != null) arr = cat.Count;
            }
            catch { }
            try
            {
                var uis = UnityEngine.Resources.FindObjectsOfTypeAll<UIInventory>();
                foreach (var ui in uis)
                {
                    try
                    {
                        if (ui == null) continue;
                        var items = ui._allListItems;
                        if (items != null) { rows = items.Count; break; }
                    }
                    catch { }
                }
            }
            catch { }
            string sig = arr + "/" + rows;
            if (sig == _lastCountsSig) return;
            _lastCountsSig = sig;
            Log.LogInfo($"Counts: catalog array={arr} garage rows={rows} (309 rows = custom items possible).");
        }
        catch { }
    }

    /// <summary>
    /// Display-source proof: logs what number the game itself pushes into
    /// each crew-class row (only when the value is new or changed).
    /// </summary>
    internal static void LogRowValue(UIInventoryListItem item, int value)
    {
        try
        {
            if (item == null) return;
            var prefab = item._prefab;
            if (prefab == null) return;
            string n = null;
            try { n = prefab.GetName(); } catch { }
            if (string.IsNullOrEmpty(n)) return;
            bool interesting = false;
            try
            {
                if (s_classOf != null) interesting = s_classOf.ContainsKey(new SCPrefab(prefab));
                else interesting = n.IndexOf("Seat", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { }
            if (!interesting) return;
            if (s_lastValues == null) s_lastValues = new Dictionary<string, int>();
            int prev;
            if (s_lastValues.TryGetValue(n, out prev) && prev == value) return;
            s_lastValues[n] = value;
            int field = -999;
            try { field = prefab._availableAmount; } catch { }
            Log.LogInfo($"Garage push: '{n}' row <- {value} (prefab field x{field}).");
        }
        catch { }
    }
}
