using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;

namespace KingdomEightCrowns;

internal static class NativePatch
{
	private const string GameAssemblyModule = "GameAssembly.dll";

	private const int MaxConnectionsImmediateRva = 5731141;

	private const int PlayerIdUpperBoundImmediateRva = 6026875;

	private const uint PageExecuteReadWrite = 64u;

	internal static void ApplyConnectionLimit(int maxPlayers)
	{
		IntPtr intPtr = IntPtr.Add(GetGameAssembly(), 5731141);
		int num = Marshal.ReadInt32(intPtr);
		if (num != 2 && num != maxPlayers)
		{
			throw new InvalidOperationException($"Unexpected native bytes at RVA 0x{5731141:X}: expected connection limit 2, found {num}.");
		}
		bool isEnabled;
		BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler;
		if (num == maxPlayers)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(36, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("Native connection limit is already ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(maxPlayers);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
			return;
		}
		WriteInt32(intPtr, maxPlayers, "connection-limit");
		int num2 = Marshal.ReadInt32(intPtr);
		if (num2 != maxPlayers)
		{
			throw new InvalidOperationException($"Native patch verification failed: read back {num2}, expected {maxPlayers}.");
		}
		ManualLogSource logSource2 = Plugin.LogSource;
		bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(51, 2, out isEnabled);
		if (isEnabled)
		{
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("Patched UNetRouter host connection limit from ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(num);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" to ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(num2);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
		}
		logSource2.LogInfo(bepInExInfoLogInterpolatedStringHandler);
	}

	internal static void ApplyPlayerIdLimit(int maxPlayerId)
	{
		IntPtr intPtr = IntPtr.Add(GetGameAssembly(), 6026875);
		byte b = Marshal.ReadByte(intPtr);
		if (b != 1 && b != maxPlayerId)
		{
			throw new InvalidOperationException($"Unexpected native byte at RVA 0x{6026875:X}: expected player ID limit 1, found {b}.");
		}
		bool isEnabled;
		BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler;
		if (b == maxPlayerId)
		{
			ManualLogSource logSource = Plugin.LogSource;
			bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(35, 1, out isEnabled);
			if (isEnabled)
			{
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral("Native player ID limit is already ");
				bepInExInfoLogInterpolatedStringHandler.AppendFormatted(maxPlayerId);
				bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
			}
			logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
			return;
		}
		WriteByte(intPtr, checked((byte)maxPlayerId), "player-ID");
		byte b2 = Marshal.ReadByte(intPtr);
		if (b2 != maxPlayerId)
		{
			throw new InvalidOperationException($"Player-ID patch verification failed: read back {b2}, expected {maxPlayerId}.");
		}
		ManualLogSource logSource2 = Plugin.LogSource;
		bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(49, 2, out isEnabled);
		if (isEnabled)
		{
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("Extended Player.SetupAsPlayer ID limit from ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(b);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" to ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(b2);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
		}
		logSource2.LogInfo(bepInExInfoLogInterpolatedStringHandler);
	}

	private static IntPtr GetGameAssembly()
	{
		IntPtr moduleHandle = GetModuleHandle("GameAssembly.dll");
		if (moduleHandle == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to locate the loaded GameAssembly.dll module.");
		}
		return moduleHandle;
	}

	private static void WriteInt32(IntPtr address, int value, string patchName)
	{
		if (!VirtualProtect(address, new UIntPtr(4u), 64u, out var oldProtection))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect failed while enabling the " + patchName + " patch.");
		}
		try
		{
			Marshal.WriteInt32(address, value);
			Flush(address, 4, patchName);
		}
		finally
		{
			VirtualProtect(address, new UIntPtr(4u), oldProtection, out var _);
		}
	}

	private static void WriteByte(IntPtr address, byte value, string patchName)
	{
		if (!VirtualProtect(address, new UIntPtr(1u), 64u, out var oldProtection))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect failed while enabling the " + patchName + " patch.");
		}
		try
		{
			Marshal.WriteByte(address, value);
			Flush(address, 1, patchName);
		}
		finally
		{
			VirtualProtect(address, new UIntPtr(1u), oldProtection, out var _);
		}
	}

	private static void Flush(IntPtr address, int size, string patchName)
	{
		if (!FlushInstructionCache(GetCurrentProcess(), address, new UIntPtr((uint)size)))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "FlushInstructionCache failed after applying the " + patchName + " patch.");
		}
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string moduleName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint set_skinColor, out uint oldProtection);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool FlushInstructionCache(IntPtr process, IntPtr set_model, UIntPtr size);

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetCurrentProcess();
}
