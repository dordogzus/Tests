using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MorePlayersMod.Logic;

/// <summary>Exact garage placement of one ship part (station-garage space).</summary>
public struct PlacementRecord
{
    public ulong Prefab;
    public float PX, PY, PZ;
    public float RX, RY, RZ, RW;
    /// <summary>Unix seconds of the save that last wrote this record (used for pruning).</summary>
    public long Stamp;
}

/// <summary>
/// Side-car store for exact placements. The game saves garage transforms through a
/// compact codec sized for the vanilla garage, so parts far out in the 12x area come
/// back shifted after a restart; this file keeps the exact values, keyed by the
/// part's 128-bit SCGuid (unique across worlds, so no world id is needed).
/// Plain text: one record per line, invariant culture (pure, unit tested).
/// </summary>
public static class PlacementFile
{
    public const string Header = "# MorePlayers exact placements v1";
    public const int MaxRecords = 50000;

    public static string Serialize(IReadOnlyDictionary<string, PlacementRecord> records)
    {
        var sb = new StringBuilder(records.Count * 96 + 64);
        sb.Append(Header).Append('\n');
        var keys = new List<string>(records.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var r = records[key];
            sb.Append(key).Append(' ').Append(r.Prefab.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(r.Stamp.ToString(CultureInfo.InvariantCulture));
            foreach (var f in new[] { r.PX, r.PY, r.PZ, r.RX, r.RY, r.RZ, r.RW })
                sb.Append(' ').Append(f.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Parses a file; malformed or non-finite lines are skipped, never thrown.</summary>
    public static Dictionary<string, PlacementRecord> Parse(string text)
    {
        var result = new Dictionary<string, PlacementRecord>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ');
            if (parts.Length != 10 || !IsKey(parts[0])) continue;
            if (!ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefab) || prefab == 0) continue;
            if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stamp)) continue;
            var f = new float[7];
            bool ok = true;
            for (int i = 0; i < 7 && ok; i++)
                ok = float.TryParse(parts[3 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i]) && float.IsFinite(f[i]);
            if (!ok) continue;
            result[parts[0]] = new PlacementRecord { Prefab = prefab, Stamp = stamp, PX = f[0], PY = f[1], PZ = f[2], RX = f[3], RY = f[4], RZ = f[5], RW = f[6] };
        }
        return result;
    }

    /// <summary>Current parts replace their stored records; others are kept, oldest pruned past the cap.</summary>
    public static void Merge(Dictionary<string, PlacementRecord> store, IReadOnlyDictionary<string, PlacementRecord> current, int max = MaxRecords)
    {
        foreach (var kv in current) store[kv.Key] = kv.Value;
        if (store.Count <= max) return;
        var ordered = new List<KeyValuePair<string, PlacementRecord>>(store);
        ordered.Sort((a, b) => a.Value.Stamp.CompareTo(b.Value.Stamp));
        for (int i = 0; i < ordered.Count - max; i++) store.Remove(ordered[i].Key);
    }

    public static bool IsKey(string key)
    {
        if (key == null || key.Length != 32) return false;
        foreach (char c in key) if (!Uri.IsHexDigit(c)) return false;
        return key != new string('0', 32);
    }
}
