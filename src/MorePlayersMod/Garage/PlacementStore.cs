using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;

namespace MorePlayersMod;

/// <summary>File access for <see cref="Logic.PlacementFile"/>; atomic replace, never throws.</summary>
internal static class PlacementStore
{
    private static string FilePath => Path.Combine(Paths.ConfigPath, "MorePlayers", "placements.txt");

    internal static Dictionary<string, Logic.PlacementRecord> Load()
    {
        try
        {
            return File.Exists(FilePath) ? Logic.PlacementFile.Parse(File.ReadAllText(FilePath)) : new Dictionary<string, Logic.PlacementRecord>();
        }
        catch { return new Dictionary<string, Logic.PlacementRecord>(); }
    }

    /// <returns>Records on disk after the merge, or -1 on failure.</returns>
    internal static int Save(IReadOnlyDictionary<string, Logic.PlacementRecord> current)
    {
        try
        {
            var store = Load();
            Logic.PlacementFile.Merge(store, current);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Logic.PlacementFile.Serialize(store));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return store.Count;
        }
        catch { return -1; }
    }
}
