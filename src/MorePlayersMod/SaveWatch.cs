using BepInEx.Logging;
using System;

namespace MorePlayersMod;

/// <summary>
/// Persistence lifecycle forensics (read-only): logs every world/game save
/// with a counter. If placements never survive fly->build, the first question
/// is whether anything ever WRITES them. Caps output; zero gameplay impact.
/// </summary>
internal static class SaveWatch
{
    private static ManualLogSource Log;
    private static int s_worldSaves;
    private static int s_gameSaves;
    private const int Cap = 20;

    internal static void Bind(BepInEx.Configuration.ConfigFile config, ManualLogSource log)
    {
        Log = log;
    }

    internal static void OnWorldSave()
    {
        try
        {
            s_worldSaves++;
            if (s_worldSaves <= Cap)
                Log.LogInfo($"SaveWatch: world save written #{s_worldSaves}.");
            else if (s_worldSaves == Cap + 1)
                Log.LogInfo("SaveWatch: further world saves muted (still counting).");
        }
        catch { }
    }

    internal static void OnGameSave()
    {
        try
        {
            s_gameSaves++;
            if (s_gameSaves <= Cap)
                Log.LogInfo($"SaveWatch: game data save written #{s_gameSaves}.");
            else if (s_gameSaves == Cap + 1)
                Log.LogInfo("SaveWatch: further game saves muted (still counting).");
        }
        catch { }
    }
}
