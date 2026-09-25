using BepInEx.Configuration;
using BepInEx.Logging;
using System;

namespace MorePlayersMod;

/// <summary>
/// Decisive custom-items experiment (v2.10): the garage catalog is baked
/// against runtime clones (v2.1 proved: 0 MPM clones visible), BUT nobody has
/// tested whether the catalog reads the LIVE Core._spaceshipComponents array.
/// This appends a DUPLICATE Seat reference (same object, zero weird data) to
/// a grown array. Garage row count 308 -> 309 means custom parts are possible
/// (v2.11 builds the real pipeline); still 308 means the list is baked
/// elsewhere and custom parts are definitively dead. Harmless either way.
/// </summary>
internal static class CustomItems
{
    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgTest;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgTest = config.Bind("CustomItems",
            "CatalogDupTest", false,
            "One-time test: duplicate the Seat entry in the live parts catalog to see if the garage picks it up (row count 308 -> 309).");
    }

    internal static void Migrate()
    {
        try { if (CfgTest != null) CfgTest.Value = false; } catch { }
    }

    internal static void EnsureTested()
    {
        // RE-ARMED v2.15: v2.10 ran this exact append for a whole session with
        // zero crashes, so the duplicate-key fear was unfounded. If garage rows
        // go 308 -> 309, custom parts are possible and v2.16 builds the real
        // pipeline (autopilot sensor blocks). Verified via tick, no menu needed.
        if (_done) return;
        try
        {
            if (!CfgTest.Value) return;
            var core = Core.Get();
            if (core == null) throw new Exception("Core.Get() returned null");
            var catalog = core._spaceshipComponents;
            if (catalog == null) throw new Exception("spaceshipComponents catalog missing");
            int n = 0;
            try { n = catalog.Count; } catch { }
            if (n <= 0) throw new Exception("catalog empty");
            if (n > 400) { _done = true; return; } // already appended (or unexpected); don't pile on
            EPC_SpaceshipComponent seat = null;
            foreach (var epc in catalog)
            {
                try
                {
                    if (epc == null) continue;
                    string nm = null;
                    try { nm = epc.GetName(); } catch { }
                    if (string.Equals(nm, "Seat", StringComparison.OrdinalIgnoreCase)) { seat = epc; break; }
                }
                catch { }
            }
            if (seat == null) throw new Exception("Seat prefab not found");
            var grown = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<EPC_SpaceshipComponent>(n + 1);
            for (int i = 0; i < n; i++)
            {
                try { grown[i] = catalog[i]; } catch { }
            }
            grown[n] = seat;
            core._spaceshipComponents = grown;
            _done = true;
            Log.LogInfo($"CustomItems test: catalog {n} -> {grown.Count} (dup Seat). Garage rows will prove registration.");
        }
        catch (Exception e)
        {
            Log.LogWarning($"CustomItems test skipped: {e.GetType().Name}: {e.Message}");
        }
    }
    private static bool _done;
}
