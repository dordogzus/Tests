using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using UnityEngine;

namespace MorePlayersMod;

/// <summary>
/// The game hands every player a colour from Core._availablePlayerColors, which
/// only has 4 entries. With more players the extra ones would share or miss a
/// colour, so the array is grown (vanilla colours kept, new distinct ones appended).
/// </summary>
internal static class PlayerColors
{
    private static ManualLogSource Log;
    private static int _max;
    private static bool _done;
    private static int _attempts;
    private static Color[] _cache = Array.Empty<Color>();

    internal static void Bind(ManualLogSource log, int maxPlayers) { Log = log; _max = maxPlayers; }

    internal static void ResetSession() { _done = false; _attempts = 0; }

    internal static Color ColorFor(int index)
    {
        if (index >= 0 && index < _cache.Length) return _cache[index];
        var c = Logic.PlayerPalette.Generate(index, index + 1)[0];
        return new Color(c.r, c.g, c.b, 1f);
    }

    internal static void EnsureExtended()
    {
        if (_done || _attempts > 40) return;
        var core = Core.Get();
        if (core == null) return;
        _attempts++;
        try
        {
            var prop = Traverse.Create(core).Property("_availablePlayerColors");
            if (!prop.PropertyExists()) { _done = true; Log.LogInfo("Player colours: field not present in this build; skipped."); return; }
            var current = prop.GetValue<Il2CppStructArray<Color>>();
            if (current == null) return;
            int len = current.Length;
            if (len >= _max) { Cache(current); _done = true; return; }
            var grown = new Il2CppStructArray<Color>(_max);
            for (int i = 0; i < len; i++) grown[i] = current[i];
            var extra = Logic.PlayerPalette.Generate(len, _max);
            for (int i = 0; i < extra.Count; i++) grown[len + i] = new Color(extra[i].r, extra[i].g, extra[i].b, 1f);
            prop.SetValue(grown);
            Cache(grown);
            _done = true;
            Log.LogInfo($"Player colours: {len} -> {_max} (vanilla colours kept).");
        }
        catch (Exception e)
        {
            if (_attempts == 1) Log.LogWarning($"Player colours not extended yet: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void Cache(Il2CppStructArray<Color> colors)
    {
        var copy = new Color[colors.Length];
        for (int i = 0; i < copy.Length; i++) copy[i] = colors[i];
        _cache = copy;
    }
}
