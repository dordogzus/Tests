using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace MorePlayersMod;

internal static class ModTick
{
    private static ManualLogSource Log;
    private static uint s_frames;
    private static int s_lastFrame = -1;
    private static bool s_running;
    private static bool s_aliveLogged;

    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        Log = log;
    }

    internal static void OnTick()
    {
        if (s_running) return;
        s_running = true;
        try
        {
            int frame = Time.frameCount;
            if (frame == s_lastFrame) return;
            s_lastFrame = frame;
            if (!s_aliveLogged)
            {
                s_aliveLogged = true;
                try { Log?.LogInfo("Manager tick alive: garage expansion, placement restore, lobby cap, colours and Frontier simulation running."); } catch { }
            }
            ExtendedTransformStore.Tick();
            try { FrontierRuntime.Tick(); } catch { }
            try { LobbyCap.Tick(); } catch { }
            unchecked { s_frames++; }
            if ((s_frames % 30) == 0)
            {
                try { PlayerColors.EnsureExtended(); } catch { }
                try { Contracts.TickRefresh(); } catch { }
            }
            if ((s_frames % 90) == 0)
            {
                try { Zone.EnsureScaled(); } catch { }
                try { Zone.ApplyRootAtRuntime(); } catch { }
                try { Zone.ScaleBoundaryVisuals(); } catch { }
            }
            if ((s_frames % 450) == 0)
            {
                try { Zone.LogYardState(); } catch { }
                try { Zone.LogMatrixProbe(); } catch { }
                try { Zone.DiagPlatforms(); } catch { }
            }
        }
        catch { }
        finally { s_running = false; }
    }
}
