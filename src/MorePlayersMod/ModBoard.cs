using BepInEx.Configuration;
using BepInEx.Logging;

namespace MorePlayersMod;

/// <summary>
/// v2.17: the old custom top-left mission board is intentionally disabled.
/// It was oversized, permanent, and duplicated information already present in
/// the vanilla objective log. Crew messages now use the game's UIMessenger and
/// automatically expire after Contracts.AnnounceSeconds.
///
/// The class remains as a no-op compatibility shim so older call sites/configs
/// cannot create a second UI layer.
/// </summary>
internal static class ModBoard
{
    internal static void Bind(ConfigFile config, ManualLogSource log)
    {
        // Keep the old key readable but force the custom board off.
        try
        {
            var old = config.Bind("Contracts", "ShowMissionBoard", false,
                "Deprecated in v2.17. Crew notices now use vanilla temporary messages.");
            if (old.Value) old.Value = false;
        }
        catch { }
    }

    internal static void EnsureBuilt() { }
    internal static void Rebuild() { }
}
