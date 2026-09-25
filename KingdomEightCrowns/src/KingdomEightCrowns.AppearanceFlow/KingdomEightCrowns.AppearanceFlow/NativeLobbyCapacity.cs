using System;
using System.Runtime.InteropServices;

namespace KingdomEightCrowns.AppearanceFlow;

internal static class NativeLobbyCapacity
{
	private const string GameAssemblyModule = "GameAssembly.dll";

	private const int SteamLobbyLimitImmediateRva = 6926848;

	private const uint PageExecuteReadWrite = 64u;

	internal static void Apply(int maximumPlayers)
	{
		IntPtr moduleHandle = GetModuleHandle("GameAssembly.dll");
		if (moduleHandle == IntPtr.Zero)
		{
			throw new InvalidOperationException("The loaded GameAssembly.dll module was not found.");
		}
		IntPtr intPtr = IntPtr.Add(moduleHandle, 6926848);
		byte b = Marshal.ReadByte(intPtr);
		if (b != 2 && b != maximumPlayers)
		{
			throw new InvalidOperationException("Unexpected Steam lobby capacity byte at RVA 0x" + 6926848.ToString("X") + ": expected 2, found " + b + ".");
		}
		if (b == maximumPlayers)
		{
			Plugin.LogSource.LogInfo("[Dynamic session] Steam lobby member limit is already " + maximumPlayers + ".");
			return;
		}
		if (!VirtualProtect(intPtr, new UIntPtr(1u), 64u, out var oldProtection))
		{
			throw new InvalidOperationException("VirtualProtect failed for the Steam lobby capacity patch.");
		}
		try
		{
			Marshal.WriteByte(intPtr, (byte)maximumPlayers);
			if (!FlushInstructionCache(GetCurrentProcess(), intPtr, new UIntPtr(1u)))
			{
				throw new InvalidOperationException("FlushInstructionCache failed for the Steam lobby capacity patch.");
			}
		}
		finally
		{
			VirtualProtect(intPtr, new UIntPtr(1u), oldProtection, out var _);
		}
		byte b2 = Marshal.ReadByte(intPtr);
		if (b2 != maximumPlayers)
		{
			throw new InvalidOperationException("Steam lobby capacity patch verification failed: read " + b2 + ", expected " + maximumPlayers + ".");
		}
		Plugin.LogSource.LogWarning("[Dynamic session] Expanded the native Steam lobby member limit from " + b + " to " + b2 + ".");
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string moduleName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint newProtection, out uint oldProtection);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool FlushInstructionCache(IntPtr process, IntPtr baseAddress, UIntPtr size);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();
}
