using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MorePlayersMod;

/// <summary>
/// Extended-yard transform round-trip fix + diagnostics.
///
/// Approximately Up compresses GarageTransform values to a uint for save/reset
/// reconstruction. The vanilla codec was authored for the vanilla garage. Once
/// the legal build volume is enlarged, far-away transforms can be encoded to a
/// value that later decodes back inside the old box. That is the visible
/// "teleport back" after fly -> build.
///
/// We let the vanilla codec keep producing its uint (network/save compatibility),
/// but remember the exact GarageTransform for positions beyond the vanilla range.
/// When the same prefab+uint is decoded later in this process, the exact original
/// transform replaces the lossy vanilla decode result. No on-disk format change,
/// no custom network packet, and vanilla-range parts stay untouched.
/// </summary>
internal static class TeleportWatch
{
    private static ManualLogSource Log;
    private static ConfigEntry<bool> CfgRoundTripFix;
    private static ConfigEntry<float> CfgVanillaHalfExtent;

    private static Dictionary<SCPrefab, string> s_names;
    private static readonly Dictionary<SCPrefab, Dictionary<uint, Queue<GarageTransform>>> s_exact
        = new Dictionary<SCPrefab, Dictionary<uint, Queue<GarageTransform>>>();

    private static int s_cached;
    private static int s_restored;
    private static int s_encLogged;
    private static int s_decLogged;
    private static int s_batch;
    private static float s_lastEncodeTime = -1000f;
    private const int LogCap = 100;
    private const int CacheCap = 8192;
    private const float NewBatchGapSeconds = 1.0f;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
        CfgRoundTripFix = config.Bind("Garage",
            "ExtendedTransformRoundTripFix", true,
            "Preserve exact out-of-vanilla GarageTransform values across fly/build reconstruction. " +
            "Disable only for troubleshooting.");
        CfgVanillaHalfExtent = config.Bind("Garage",
            "VanillaCodecHalfExtent", 25f,
            "Approximate vanilla garage-local half extent used only to decide which transforms need " +
            "the exact round-trip cache. 25 is correct for the current game build.");
    }

    private static string NameOf(SCPrefab prefab)
    {
        try
        {
            if (s_names == null)
            {
                s_names = new Dictionary<SCPrefab, string>();
                var core = Core.Get();
                var catalog = core != null ? core._spaceshipComponents : null;
                if (catalog != null)
                {
                    foreach (var epc in catalog)
                    {
                        try
                        {
                            if (epc == null) continue;
                            string n = null;
                            try { n = epc.GetName(); } catch { }
                            if (!string.IsNullOrEmpty(n)) s_names[new SCPrefab(epc)] = n;
                        }
                        catch { }
                    }
                }
            }
            string name;
            if (s_names.TryGetValue(prefab, out name)) return name;
        }
        catch { }
        return "part";
    }

    private static bool OutsideVanilla(float3 p)
    {
        try
        {
            float h = 25f;
            try { h = Math.Max(1f, CfgVanillaHalfExtent.Value); } catch { }
            return Math.Abs(p.x) > h || Math.Abs(p.y) > h || Math.Abs(p.z) > h;
        }
        catch { return false; }
    }

    private static float Distance(float3 a, float3 b)
    {
        try
        {
            float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
        catch { return 0f; }
    }

    private static void StartNewBatchIfNeeded()
    {
        try
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - s_lastEncodeTime > NewBatchGapSeconds)
            {
                // A ship is normally encoded as one tight burst. Keep only the
                // latest burst so an old save/blueprint encode cannot poison a
                // future decode sequence.
                s_exact.Clear();
                s_cached = 0;
                unchecked { s_batch++; }
            }
            s_lastEncodeTime = now;
        }
        catch { }
    }

    internal static void OnEncode(GarageTransform t, SCPrefab prefab, uint data)
    {
        try
        {
            float3 p = new float3(0, 0, 0);
            try { p = t.Position(); } catch { }
            bool outside = OutsideVanilla(p);

            if (CfgRoundTripFix != null && CfgRoundTripFix.Value && outside)
            {
                StartNewBatchIfNeeded();
                if (s_cached < CacheCap)
                {
                    Dictionary<uint, Queue<GarageTransform>> byData;
                    if (!s_exact.TryGetValue(prefab, out byData))
                    {
                        byData = new Dictionary<uint, Queue<GarageTransform>>();
                        s_exact[prefab] = byData;
                    }
                    Queue<GarageTransform> q;
                    if (!byData.TryGetValue(data, out q))
                    {
                        q = new Queue<GarageTransform>();
                        byData[data] = q;
                    }
                    q.Enqueue(t);
                    s_cached++;
                }
            }

            if (s_encLogged < LogCap && (outside || s_encLogged < 10))
            {
                s_encLogged++;
                Log.LogInfo($"TeleportWatch encode #{s_encLogged}: '{NameOf(prefab)}' data={data} at garage-local {p}" +
                    (outside ? $" (EXTENDED; cached batch {s_batch})" : string.Empty));
            }
        }
        catch { }
    }

    internal static void OnDecode(SCPrefab prefab, uint data, ref GarageTransform result)
    {
        try
        {
            float3 vanilla = new float3(0, 0, 0);
            try { vanilla = result.Position(); } catch { }

            bool fixedOne = false;
            float3 exactPos = vanilla;
            if (CfgRoundTripFix != null && CfgRoundTripFix.Value)
            {
                Dictionary<uint, Queue<GarageTransform>> byData;
                Queue<GarageTransform> q;
                if (s_exact.TryGetValue(prefab, out byData) &&
                    byData.TryGetValue(data, out q) && q != null && q.Count > 0)
                {
                    GarageTransform exact = q.Dequeue();
                    try { exactPos = exact.Position(); } catch { }
                    result = exact;
                    fixedOne = true;
                    s_restored++;
                    if (s_cached > 0) s_cached--;

                    if (q.Count == 0) byData.Remove(data);
                    if (byData.Count == 0) s_exact.Remove(prefab);
                }
            }

            bool outside = OutsideVanilla(exactPos);
            float moved = fixedOne ? Distance(vanilla, exactPos) : 0f;
            if (s_decLogged < LogCap && (fixedOne || outside || s_decLogged < 10))
            {
                s_decLogged++;
                if (fixedOne)
                {
                    Log.LogInfo($"TeleportWatch decode #{s_decLogged}: '{NameOf(prefab)}' data={data} " +
                        $"vanilla={vanilla} -> exact={exactPos}, corrected {moved:F3}m " +
                        $"(restored total {s_restored}).");
                }
                else
                {
                    Log.LogInfo($"TeleportWatch decode #{s_decLogged}: '{NameOf(prefab)}' data={data} -> {vanilla}" +
                        (outside ? " (OUTSIDE vanilla box, no matching cache entry)" : string.Empty));
                }
            }
        }
        catch { }
    }
}
