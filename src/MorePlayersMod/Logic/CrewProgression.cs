using System;
using System.Collections.Generic;

namespace MorePlayersMod.Logic;

public enum SupplyRoute { Moon, Earth, OuterRim }

/// <summary>
/// How extra crew capacity is earned. Nothing is handed out at session start:
/// every vanilla package delivery pays a pack sized for the current crew, and
/// Frontier tech tiers unlock after a number of completed supply runs.
/// </summary>
public static class CrewProgression
{
    public const int VanillaPlayers = 4;
    public const int DefaultMaxPlayers = 24;
    public const int AbsoluteMaxPlayers = 64;

    /// <summary>1 for up to 4 players, 2 for 5-8, ..., 6 for 21-24.</summary>
    public static int CrewScale(int crewSize)
    {
        if (crewSize <= VanillaPlayers) return 1;
        return (int)Math.Ceiling(crewSize / (double)VanillaPlayers);
    }

    public static SupplyRoute RouteFor(string objectiveIdName)
    {
        if (objectiveIdName != null && objectiveIdName.StartsWith("Package_Moon_", StringComparison.Ordinal)) return SupplyRoute.Moon;
        if (objectiveIdName != null && objectiveIdName.StartsWith("Package_Earth_", StringComparison.Ordinal)) return SupplyRoute.Earth;
        return SupplyRoute.OuterRim;
    }

    /// <summary>Part packs for one delivery. Crew-facing classes scale with crew size; structure scales at half rate.</summary>
    public static List<KeyValuePair<string, int>> Packs(SupplyRoute route, int crewSize)
    {
        int s = CrewScale(Math.Max(1, crewSize));
        int half = (s + 1) / 2;
        var list = new List<KeyValuePair<string, int>>();
        void Add(string cls, int n) { if (n > 0) list.Add(new KeyValuePair<string, int>(cls, n)); }
        switch (route)
        {
            case SupplyRoute.Moon:
                Add("Seats", 4 * s); Add("Consoles", 2 * s); Add("Sensors", 1 * half);
                break;
            case SupplyRoute.Earth:
                Add("Consoles", 4 * s); Add("Power", 4 * half); Add("Sensors", 2 * half);
                break;
            default:
                Add("Frames", 8 * half); Add("Glass", 4 * half); Add("Thrusters", 2 * half); Add("Power", 2 * half); Add("Seats", 1 * s);
                break;
        }
        return list;
    }

    /// <summary>Supply runs needed before a seat exists for every crew member (vanilla ships start with ~2-4).</summary>
    public static int RunsUntilEverySeat(int crewSize, int vanillaSeats)
    {
        int missing = Math.Max(0, crewSize - vanillaSeats);
        if (missing == 0) return 0;
        int perMoonRun = 4 * CrewScale(crewSize);
        return (int)Math.Ceiling(missing / (double)perMoonRun);
    }

    public static int ClampMaxPlayers(int value) => Math.Max(2, Math.Min(AbsoluteMaxPlayers, value));
}
