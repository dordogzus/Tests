using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MorePlayersMod;

/// <summary>
/// Automatic teleport detector: on every garage open, snapshots all placed
/// ship-part world positions (SCGuid-carrying entities) and compares with the
/// previous snapshot by nearest neighbor. Reports uniform shifts (whole ship
/// re-docked by the game = vanilla behavior) vs scattered moves (individual
/// parts relocated = bounds rollback). Read-only, throttled by garage opens.
/// </summary>
internal static class ShipWatch
{
    private static ManualLogSource Log;
    private static List<float3> s_prev;
    private static bool s_failLogged;
    private static bool s_stableLogged;
    private const int Cap = 1500;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
    }

    internal static void Snapshot()
    {
        List<float3> now = null;
        try
        {
            var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            if (world == null) return;
            bool created = false;
            try { created = world.IsCreated; } catch { return; }
            if (!created) return;
            var em = world.EntityManager;
            Unity.Entities.EntityManager emCopy = em;
            var ct1 = Unity.Entities.ComponentType.ReadOnly(
                Il2CppSystem.Type.GetType("SCGuid, Assembly-CSharp"));
            var ct2 = Unity.Entities.ComponentType.ReadOnly(
                Il2CppSystem.Type.GetType("Unity.Transforms.LocalToWorld, Unity.Transforms"));
            var query = emCopy.CreateEntityQuery(new Unity.Entities.ComponentType[] { ct1, ct2 });
            int count = 0;
            try { count = query.CalculateEntityCount(); } catch { return; }
            if (count <= 0 || count > 100000) return;
            var arr = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                now = new List<float3>(Math.Min(arr.Length, Cap));
                int n = Math.Min(arr.Length, Cap);
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var ltw = emCopy.GetComponentData<Unity.Transforms.LocalToWorld>(arr[i]);
                        now.Add(ltw.Value.c3.xyz);
                    }
                    catch { }
                }
            }
            finally
            {
                try { arr.Dispose(); } catch { }
            }
        }
        catch (Exception e)
        {
            if (!s_failLogged)
            {
                s_failLogged = true;
                try { Log.LogWarning($"ShipWatch unavailable: {e.GetType().Name}: {e.Message}"); } catch { }
            }
            return;
        }

        try
        {
            if (now == null || now.Count == 0) return;
            var prev = s_prev;
            s_prev = now;
            if (prev == null || prev.Count == 0)
            {
                Log.LogInfo($"ShipWatch: baseline recorded ({now.Count} placed parts).");
                return;
            }
            // Formation analysis (flight-proof): pairwise distances are invariant
            // under ship motion/rotation/docking. Only real relocations (or
            // added/removed parts) change the shape signature.
            int k = Math.Min(now.Count, 80);
            int stride = Math.Max(1, now.Count / 80);
            var pts = new List<float3>(k);
            for (int i = 0; i < now.Count && pts.Count < k; i += stride)
            {
                try { pts.Add(now[i]); } catch { }
            }
            var pk = new List<float3>(k);
            int strideP = Math.Max(1, prev.Count / 80);
            for (int i = 0; i < prev.Count && pk.Count < k; i += strideP)
            {
                try { pk.Add(prev[i]); } catch { }
            }
            double kept = ShapeKept(pts, pk);
            int appeared = Math.Max(0, now.Count - prev.Count);
            int vanished = Math.Max(0, prev.Count - now.Count);
            // World switch guard: wildly different counts = different scene,
            // not movement. Re-baseline silently.
            int lo = Math.Min(now.Count, prev.Count);
            int hi = Math.Max(now.Count, prev.Count);
            if (hi > 0 && (double)lo / hi < 0.3) return;
            if (kept >= 0.95 && appeared == 0 && vanished == 0)
            {
                if (!s_stableLogged)
                {
                    s_stableLogged = true;
                    Log.LogInfo($"ShipWatch: {now.Count} parts tracked, formation kept.");
                }
                return;
            }
            // Strict teleport signature: SAME parts, CHANGED formation.
            // Construction (appeared/vanished) is reported, never accused.
            if (appeared == 0 && vanished == 0 && kept < 0.8)
            {
                Log.LogInfo($"ShipWatch: {now.Count} parts, formation kept {kept * 100:F0}% " +
                    $"= PARTS RELOCATED relative to each other (teleport confirmed)");
                return;
            }
            Log.LogInfo($"ShipWatch: {now.Count} parts (was {prev.Count}), formation kept {kept * 100:F0}% " +
                $"(+{appeared}/-{vanished}) = construction or ship motion, not teleport.");
        }
        catch { }
    }

    /// <summary>Fraction of sampled pairwise distances present in both shapes.</summary>
    private static double ShapeKept(List<float3> a, List<float3> b)
    {
        try
        {
            if (a.Count < 4 || b.Count < 4) return 1.0;
            var ha = ShapeHist(a);
            var hb = ShapeHist(b);
            if (ha.Count == 0 || hb.Count == 0) return 1.0;
            double inter = 0, total = 0;
            foreach (var kv in ha)
            {
                int cb = 0;
                hb.TryGetValue(kv.Key, out cb);
                inter += Math.Min(kv.Value, cb);
                total += kv.Value;
            }
            if (total <= 0) return 1.0;
            return inter / total;
        }
        catch { return 1.0; }
    }

    private static Dictionary<int, int> ShapeHist(List<float3> pts)
    {
        var h = new Dictionary<int, int>();
        try
        {
            int n = pts.Count;
            int jim = Math.Max(1, n / 25);
            for (int i = 0; i < n; i += jim)
            {
                for (int j = i + jim; j < n; j += jim)
                {
                    float3 p = pts[i], q = pts[j];
                    double dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
                    double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (d > 400.0) continue;
                    int bin = (int)(d * 2.0);
                    int c = 0;
                    h.TryGetValue(bin, out c);
                    h[bin] = c + 1;
                    if (h.Count > 4000) return h;
                }
            }
        }
        catch { }
        return h;
    }
}
