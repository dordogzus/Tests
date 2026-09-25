using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace MorePlayersMod;

/// <summary>
/// Three CUSTOM co-op missions, visible in the vanilla objectives menu (O)
/// with their own names and loot icons - built the same way vanilla builds
/// its missions (ObjectiveSetup assets), not by touching vanilla entries:
///
///   CREW: Moon Seats Run    (Moon -> HQ,        loot: Seats)
///   CREW: Earth Grid Build  (Earth -> HQ,       loot: Consoles + Power)
///   CREW: Outer Rim Frames  (outer planets,    loot: Frames + Glass)
///
/// Mechanics: vanilla text getters read baked data keyed by objective ID, so
/// our rows get their text from tiny Harmony postfixes (only our 3 IDs).
/// Completion/payouts ride the existing pack system (any package delivery),
/// which the description text states openly. Kill-switch in config.
/// </summary>
internal static class CustomMissions
{
    internal const uint IdSeats = 90001;
    internal const uint IdGrid = 90002;
    internal const uint IdFrames = 90003;

    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgEnable;
    private static bool _injected;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgEnable = config.Bind("Missions",
            "EnableCustomMissions", true,
            "Add 3 custom CREW missions after the garage/world is ready; initialization is deferred away from planet loading.");
    }

    internal static bool Enabled()
    {
        try { return CfgEnable.Value; } catch { return true; }
    }

    internal static bool IsOurs(uint id) => id == IdSeats || id == IdGrid || id == IdFrames;

    // Which vanilla package deliveries advance which custom mission.
    // Matched on the vanilla objective's ID name (Package_Moon_*, etc.).
    // NOTHING else grants packs: no random vanilla handouts, ever.
    internal static bool TryCompleteFromVanilla(ObjectiveSetup vanilla, out string missionTitle, out string grantedText)
    {
        missionTitle = null;
        grantedText = null;
        try
        {
            if (vanilla == null) return false;
            string oidName = null;
            try { oidName = vanilla._objectiveID.ToString(); } catch { return false; }
            if (string.IsNullOrEmpty(oidName) || !oidName.StartsWith("Package_")) return false;

            uint mission;
            KeyValuePair<string, int>[] packs;
            if (oidName.StartsWith("Package_Moon_"))
            {
                mission = IdSeats;
                packs = new KeyValuePair<string, int>[]
                {
                    new KeyValuePair<string, int>("Seats", 4),
                    new KeyValuePair<string, int>("Consoles", 2),
                    new KeyValuePair<string, int>("Sensors", 1),
                };
            }
            else if (oidName.StartsWith("Package_Earth_"))
            {
                mission = IdGrid;
                packs = new KeyValuePair<string, int>[]
                {
                    new KeyValuePair<string, int>("Consoles", 4),
                    new KeyValuePair<string, int>("Power", 4),
                    new KeyValuePair<string, int>("Sensors", 2),
                };
            }
            else
            {
                mission = IdFrames;
                packs = new KeyValuePair<string, int>[]
                {
                    new KeyValuePair<string, int>("Frames", 8),
                    new KeyValuePair<string, int>("Glass", 4),
                    new KeyValuePair<string, int>("Thrusters", 2),
                    new KeyValuePair<string, int>("Power", 2),
                };
            }

            var bits = new List<string>();
            foreach (var p in packs)
            {
                try
                {
                    CrewParts.AddPackBonus(p.Key, p.Value);
                    var ex = CrewParts.Examples(p.Key, 2);
                    bits.Add($"{p.Key} +{p.Value}" + (ex.Count > 0 ? $" ({string.Join(", ", ex.ToArray())})" : string.Empty));
                }
                catch { }
            }
            missionTitle = Title(mission);
            grantedText = string.Join("; ", bits.ToArray());
            Log.LogInfo($"Custom mission progress: {missionTitle} <- {oidName}: {grantedText}.");
            return true;
        }
        catch (Exception e)
        {
            Log.LogWarning($"Custom mission trigger issue: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    internal static void EnsureInjected()
    {
        if (_injected) return;
        try
        {
            if (!Enabled()) return;
            var core = Core.Get();
            if (core == null) throw new Exception("Core.Get() returned null");
            var existing = core._objectives;
            if (existing == null) throw new Exception("objectives registry missing");
            var have = new HashSet<uint>();
            foreach (var o in existing)
            {
                try { if (o != null) have.Add((uint)o._objectiveID); } catch { }
            }
            var missing = new List<uint>();
            if (!have.Contains(IdSeats)) missing.Add(IdSeats);
            if (!have.Contains(IdGrid)) missing.Add(IdGrid);
            if (!have.Contains(IdFrames)) missing.Add(IdFrames);
            if (missing.Count == 0) { _injected = true; return; }

            var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<ObjectiveSetup>(existing.Count + missing.Count);
            for (int i = 0; i < existing.Count; i++)
            {
                try { grown[i] = existing[i]; } catch { }
            }
            int idx = existing.Count;
            foreach (var id in missing)
            {
                var m = Build(id, core);
                if (m != null && idx < grown.Count) grown[idx++] = m;
            }
            core._objectives = grown;
            _injected = true;
            Log.LogInfo($"Custom missions injected: {missing.Count} added, registry now {grown.Count} objectives. Open O to see CREW rows.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Custom missions skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    private static ObjectiveSetup Build(uint id, Core core)
    {
        try
        {
            var m = UnityEngine.ScriptableObject.CreateInstance<ObjectiveSetup>();
            if (m == null) throw new Exception("CreateInstance returned null");
            m._objectiveID = (ObjectID)id;
            m._objectiveType = ObjectiveType.Package;
            m._hidden = false;
            m._canBeCompletedLocked = false;
            // All three start at HQ: the log groups rows by start planet, so this
            // keeps every CREW mission on the Earth tab from the very start.
            m._start = ObjectID.PlanetStation_Earth_Headquarters;
            m._end = ObjectID.PlanetStation_Earth_Headquarters;
            if (id == IdSeats)
            {
                m._reward = Rewards(core,
                    new KeyValuePair<string, int>("Seat", 4),
                    new KeyValuePair<string, int>("Lever Vertical", 2),
                    new KeyValuePair<string, int>("Monitor CRT", 1),
                    new KeyValuePair<string, int>("Velocity Meter", 1));
            }
            else if (id == IdGrid)
            {
                m._reward = Rewards(core,
                    new KeyValuePair<string, int>("Lever Vertical", 4),
                    new KeyValuePair<string, int>("Monitor CRT", 2),
                    new KeyValuePair<string, int>("Small Disposable Battery", 2),
                    new KeyValuePair<string, int>("Power Router", 2),
                    new KeyValuePair<string, int>("Datameter", 1),
                    new KeyValuePair<string, int>("Button", 2));
            }
            else
            {
                m._reward = Rewards(core,
                    new KeyValuePair<string, int>("Frame Full", 8),
                    new KeyValuePair<string, int>("Glass Full", 4),
                    new KeyValuePair<string, int>("Small Plasma Generator", 1),
                    new KeyValuePair<string, int>("RCS Thruster", 2),
                    new KeyValuePair<string, int>("Solar Panel", 2));
            }
            m._objectiveComponents = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Core.ComponentAmount>(0);
            m._dependencies = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ObjectID>(0);
            m._objectivesLogArrowStart = m._start;
            m._objectivesLogArrowEnd = m._end;
            Log.LogInfo($"Custom mission built: {Title(id)} with {m._reward.Count} loot rows.");
            return m;
        }
        catch (Exception e)
        {
            Log.LogWarning($"Custom mission {id} build failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Core.ComponentAmount> Rewards(
        Core core, params KeyValuePair<string, int>[] wants)
    {
        var list = new List<Core.ComponentAmount>();
        try
        {
            var catalog = core._spaceshipComponents;
            if (catalog != null)
            {
                foreach (var w in wants)
                {
                    try
                    {
                        foreach (var epc in catalog)
                        {
                            try
                            {
                                if (epc == null) continue;
                                string n = null;
                                try { n = epc.GetName(); } catch { }
                                if (!string.Equals(n, w.Key, StringComparison.OrdinalIgnoreCase)) continue;
                                var c = new Core.ComponentAmount();
                                c._sc = epc;
                                c._amount = w.Value;
                                list.Add(c);
                                break;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
        var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Core.ComponentAmount>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            try { arr[i] = list[i]; } catch { }
        }
        return arr;
    }

    internal static string Title(uint id)
    {
        if (id == IdSeats) return "CREW: Moon Seats Run";
        if (id == IdGrid) return "CREW: Earth Grid Build";
        if (id == IdFrames) return "CREW: Outer Rim Frames";
        return null;
    }

    /// <summary>Board rows: the 3 custom missions with their loot, always visible.</summary>
    internal static List<(string title, string loot)> BoardRows()
    {
        var out_ = new List<(string title, string loot)>();
        try
        {
            out_.Add(("CREW: Moon Seats Run", "Seats +4, Consoles +2, Sensors +1"));
            out_.Add(("CREW: Earth Grid Build", "Consoles +4, Power +4, Sensors +2"));
            out_.Add(("CREW: Outer Rim Frames", "Frames +8, Glass +4, Thrusters +2, Power +2"));
        }
        catch { }
        return out_;
    }

    internal static string TextFor(ObjectiveSetup o, string kind)
    {
        try
        {
            if (o == null) return null;
            uint id = 0;
            try { id = (uint)o._objectiveID; } catch { return null; }
            if (!IsOurs(id)) return null;
            if (kind == "title") return Title(id);
            if (id == IdSeats)
            {
                if (kind == "objective") return "Deliver Moon packages to Headquarters";
                if (kind == "desc") return "Haul any package found on the Moon back to Earth Headquarters. Crew reward: +4 Seats pack, straight into the garage.";
                if (kind == "hints") return "Package markers are on the map. More seats for more crew.";
            }
            else if (id == IdGrid)
            {
                if (kind == "objective") return "Finish Earth's remaining deliveries";
                if (kind == "desc") return "Deliver Earth's packages (Lost In Transit and friends) to Headquarters. Crew rewards: Consoles and Power packs - levers, monitors, batteries, routers.";
                if (kind == "hints") return "One mission left on Earth counts too. Wire up the new seats.";
            }
            else if (id == IdFrames)
            {
                if (kind == "objective") return "Deliver outer-planet packages to Headquarters";
                if (kind == "desc") return "Bring packages from Baobara, Helirion and beyond home to HQ. Crew rewards: Frames, Glass and Thruster packs for the big crew ship.";
                if (kind == "hints") return "Long hauls - bring scouts and a big hold.";
            }
        }
        catch { }
        return null;
    }

    /// <summary>Verification: log O-menu rows (proves our 3 show up or not).</summary>
    internal static void LogRows(UIObjectiveLog log)
    {
        try
        {
            if (log == null) return;
            var rows = log._rows;
            int total = -1;
            try { total = rows != null ? rows.Count : -1; } catch { }
            int ours = 0;
            if (rows != null && total > 0)
            {
                foreach (var r in rows)
                {
                    try
                    {
                        if (r == null) continue;
                        var linked = r._objectiveLinked;
                        if (linked == null) continue;
                        uint id = 0;
                        try { id = (uint)linked._objectiveID; } catch { continue; }
                        if (IsOurs(id)) ours++;
                    }
                    catch { }
                }
            }
            Log.LogInfo($"Objectives menu: {total} rows, {ours}/3 CREW missions visible.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"Objectives menu check skipped: {e.GetType().Name}: {e.Message}");
        }
    }
}
