using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using System;
using System.Runtime.InteropServices;

namespace MorePlayersMod;

/// <summary>
/// Raises Approximate Up's hard 4-player cap, then (host-side) unlocks extra
/// crew parts and runs co-op supply contracts so 5-12 players have jobs,
/// not just seats to sit in.
///
/// Layout: this file touches NO game types and NO Harmony. All game-API code
/// lives in Features/Contracts/CrewParts/Patches so a game update can only
/// disable the bonus modules (caught below), never the native cap unlock.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class MorePlayersPlugin : BasePlugin
{
    public const string PluginGuid = "com.bondi.moreplayers";
    public const string PluginName = "More Players";
    public const string PluginVersion = "2.20.6";

    private const string GameModule = "GameAssembly.dll";

    // RVA of "mov edx, 4" immediate inside SteamManager.CreateLobby (build 6000.4.7f1)
    private const int LobbyCreateMovEdx4Rva = 0x0CFCD2C;
    private static readonly byte[] LobbyCreateExpect = { 0xBA, 0x04, 0x00, 0x00, 0x00 };

    // RVA of "cmp eax, 4" inside Netcore.CreateNetcoreClient, followed by "jge rel32" reject path
    private const int ConnectCapCmpEax4Rva = 0x0C28FA6;
    private static readonly byte[] ConnectCapExpect = { 0x83, 0xF8, 0x04, 0x0F, 0x8D };

    private ConfigEntry<int> _maxPlayers;

    public override void Load()
    {
        _maxPlayers = Config.Bind("General", "MaxPlayers", 12,
            new ConfigDescription(
                "Maximum players in a session (lobby size + server accept limit). Vanilla is 4. " +
                "Only the host's copy of the mod matters for the cap.",
                new AcceptableValueRange<int>(2, 32)));

        int n = _maxPlayers.Value;
        try
        {
            Log.LogInfo($"{PluginName} {PluginVersion}: run {DateTime.Now:HH:mm:ss} pid={System.Diagnostics.Process.GetCurrentProcess().Id}, cap -> {n}");
        }
        catch { Log.LogInfo($"{PluginName} {PluginVersion}: raising player cap to {n}"); }

        bool lobbyOk = PatchImmediate(LobbyCreateMovEdx4Rva, LobbyCreateExpect, 1, (byte)n,
            "SteamManager.CreateLobby (lobby member limit)");
        bool connOk = PatchImmediate(ConnectCapCmpEax4Rva, ConnectCapExpect, 2, (byte)n,
            "Netcore.CreateNetcoreClient (ServerIsFull check)");

        if (lobbyOk && connOk)
            Log.LogInfo($"Done - lobbies now allow {n} players. Enjoy!");
        else
            Log.LogWarning("Some patches failed (game update?). Mod may have no effect.");

        try
        {
            Features.Init(Config, Log, n);
        }
        catch (Exception e)
        {
            Log.LogWarning($"Bonus modules (parts/contracts) disabled, cap unlock still active: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Verifies the expected byte pattern at base+Rva, then rewrites one byte of it.
    /// </summary>
    private bool PatchImmediate(int rva, byte[] expect, int byteIndexToPatch, byte newValue, string label)
    {
        try
        {
            IntPtr basePtr = Win32.GetModuleHandle(GameModule);
            if (basePtr == IntPtr.Zero)
            {
                Log.LogError($"{label}: {GameModule} not loaded");
                return false;
            }

            IntPtr addr = IntPtr.Add(basePtr, rva);

            // read current bytes
            var current = new byte[expect.Length];
            Marshal.Copy(IntPtr.Add(addr, 0), current, 0, expect.Length);

            // pattern check: the un-patched original starts with the same opcode bytes we expect,
            // except possibly at byteIndexToPatch if a previous run already patched it.
            var expected = (byte[])expect.Clone();
            expected[byteIndexToPatch] = current[byteIndexToPatch];
            for (int i = 0; i < expect.Length; i++)
            {
                if (current[i] != expected[i])
                {
                    Log.LogError($"{label}: byte pattern mismatch at RVA 0x{rva:X} " +
                                 $"(found {BitConverter.ToString(current)}, expected {BitConverter.ToString(expect)}). " +
                                 "Different game build? Patch skipped.");
                    return false;
                }
            }

            byte old = current[byteIndexToPatch];
            if (old == newValue)
            {
                Log.LogInfo($"{label}: already set to {newValue}, skipping");
                return true;
            }

            if (!Win32.VirtualProtect(addr, (UIntPtr)expect.Length, Win32.PAGE_EXECUTE_READWRITE, out uint oldProt))
            {
                Log.LogError($"{label}: VirtualProtect failed");
                return false;
            }

            Marshal.WriteByte(IntPtr.Add(addr, byteIndexToPatch), newValue);
            Win32.VirtualProtect(addr, (UIntPtr)expect.Length, oldProt, out _);

            Log.LogInfo($"{label}: {old} -> {newValue} @ RVA 0x{rva:X}");
            return true;
        }
        catch (Exception e)
        {
            Log.LogError($"{label}: patch failed - {e}");
            return false;
        }
    }
}

internal static class Win32
{
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

    public const uint PAGE_EXECUTE_READWRITE = 0x40;
}
