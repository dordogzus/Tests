using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Core.Logging.Interpolation;
using BepInEx.Logging;
using HarmonyLib;

namespace KingdomEightCrowns;

internal static class SinglePcSelfTest
{
	private sealed class ProbeClient
	{
		internal int Number { get; }

		internal object Connection { get; }

		internal bool Ready { get; set; }

		internal ProbeClient(int number, object connection)
		{
			Number = number;
			Connection = connection;
		}
	}

	private const ushort HostPort = 37200;

	private const string LoopbackAddress = "127.0.0.1";

	private const double InitialDelaySeconds = 1.0;

	private const double ClientSpacingSeconds = 0.75;

	private const double ProbeDurationSeconds = 14.0;

	private const double StatusIntervalSeconds = 2.0;

	private static readonly object Gate = new object();

	private static readonly List<ProbeClient> Clients = new List<ProbeClient>();

	private static readonly Stopwatch Clock = new Stopwatch();

	private static ConstructorInfo? _connectionConstructor;

	private static MethodInfo? _pollMessages;

	private static MethodInfo? _dispose;

	private static PropertyInfo? _isReady;

	private static bool _active;

	private static bool _insideClientPoll;

	private static int _serverConnectCallbacks;

	private static double _nextStatusAt;

	internal static bool Active
	{
		get
		{
			lock (Gate)
			{
				return _active;
			}
		}
	}

	internal static void Configure(Type connectionType)
	{
		_connectionConstructor = connectionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault((ConstructorInfo constructor) => constructor.GetParameters().Length == 3);
		_pollMessages = AccessTools.Method(connectionType, "PollMessages");
		_dispose = AccessTools.Method(connectionType, "Dispose");
		_isReady = AccessTools.Property(connectionType, "IsReady");
		if ((object)_connectionConstructor == null || (object)_pollMessages == null || (object)_dispose == null || (object)_isReady == null)
		{
			throw new MissingMemberException("The 2.1.4 CustomNetworkConnection loopback members could not be resolved.");
		}
	}

	internal static void Start()
	{
		if (!Plugin.SinglePcProbeEnabled)
		{
			Plugin.LogSource.LogInfo("[Single-PC probe] Disabled in configuration; no loopback clients will be created.");
			return;
		}
		lock (Gate)
		{
			if (_active)
			{
				return;
			}
			if ((object)_connectionConstructor == null || (object)_pollMessages == null || (object)_dispose == null || (object)_isReady == null)
			{
				Plugin.LogSource.LogError("[Single-PC probe] Cannot start because its transport members were not configured.");
				return;
			}
			Clients.Clear();
			_serverConnectCallbacks = 0;
			_nextStatusAt = 1.0;
			Clock.Restart();
			_active = true;
		}
		Plugin.LogSource.LogWarning($"[Single-PC probe] Started. Creating {Plugin.SimulatedClients} temporary loopback clients for host plus {Plugin.SimulatedClients} = {Plugin.SimulatedClients + 1} transport slots. " + "Their gameplay handshake is suppressed.");
	}

	internal static void Tick()
	{
		lock (Gate)
		{
			if (!_active || _insideClientPoll)
			{
				return;
			}
			try
			{
				double totalSeconds = Clock.Elapsed.TotalSeconds;
				CreateDueClients(totalSeconds);
				PollClients();
				if (totalSeconds >= _nextStatusAt)
				{
					LogStatus(totalSeconds);
					_nextStatusAt += 2.0;
				}
				if (totalSeconds >= 14.0)
				{
					Finish();
				}
			}
			catch (Exception exception)
			{
				ManualLogSource logSource = Plugin.LogSource;
				bool isEnabled;
				BepInExErrorLogInterpolatedStringHandler bepInExErrorLogInterpolatedStringHandler = new BepInExErrorLogInterpolatedStringHandler(26, 1, out isEnabled);
				if (isEnabled)
				{
					bepInExErrorLogInterpolatedStringHandler.AppendLiteral("[Single-PC probe] Failed: ");
					bepInExErrorLogInterpolatedStringHandler.AppendFormatted(Unwrap(exception));
				}
				logSource.LogError(bepInExErrorLogInterpolatedStringHandler);
				StopCore();
			}
		}
	}

	internal static void RecordServerConnect()
	{
		lock (Gate)
		{
			if (_active)
			{
				_serverConnectCallbacks++;
			}
		}
	}

	internal static void Stop(string reason)
	{
		lock (Gate)
		{
			if (_active)
			{
				Plugin.LogSource.LogInfo("[Single-PC probe] Stopping early: " + reason + ".");
				StopCore();
			}
		}
	}

	private static void CreateDueClients(double elapsed)
	{
		while (Clients.Count < Plugin.SimulatedClients)
		{
			double num = 1.0 + (double)Clients.Count * 0.75;
			if (!(elapsed < num))
			{
				int num2 = Clients.Count + 1;
				object connection = _connectionConstructor.Invoke(new object[3]
				{
					(ushort)37200,
					"127.0.0.1",
					false
				});
				Clients.Add(new ProbeClient(num2, connection));
				ManualLogSource logSource = Plugin.LogSource;
				bool isEnabled;
				BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(44, 2, out isEnabled);
				if (isEnabled)
				{
					bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Single-PC probe] Loopback client ");
					bepInExInfoLogInterpolatedStringHandler.AppendFormatted(num2);
					bepInExInfoLogInterpolatedStringHandler.AppendLiteral("/");
					bepInExInfoLogInterpolatedStringHandler.AppendFormatted(Plugin.SimulatedClients);
					bepInExInfoLogInterpolatedStringHandler.AppendLiteral(" created.");
				}
				logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
				continue;
			}
			break;
		}
	}

	private static void PollClients()
	{
		_insideClientPoll = true;
		try
		{
			foreach (ProbeClient client in Clients)
			{
				_pollMessages.Invoke(client.Connection, null);
				client.Ready = ReadReady(client.Connection);
			}
		}
		finally
		{
			_insideClientPoll = false;
		}
	}

	private static bool ReadReady(object connection)
	{
		object value = _isReady.GetValue(connection);
		if (value is bool)
		{
			return (bool)value;
		}
		return false;
	}

	private static void LogStatus(double elapsed)
	{
		string t = ((Clients.Count == 0) ? "no clients created yet" : string.Join(", ", Clients.Select((ProbeClient client) => string.Format("C{0}={1}", client.Number, client.Ready ? "READY" : "WAITING"))));
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExInfoLogInterpolatedStringHandler bepInExInfoLogInterpolatedStringHandler = new BepInExInfoLogInterpolatedStringHandler(49, 3, out isEnabled);
		if (isEnabled)
		{
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("[Single-PC probe] ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(elapsed, "0.0");
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("s: ");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(t);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral("; server-connect callbacks=");
			bepInExInfoLogInterpolatedStringHandler.AppendFormatted(_serverConnectCallbacks);
			bepInExInfoLogInterpolatedStringHandler.AppendLiteral(".");
		}
		logSource.LogInfo(bepInExInfoLogInterpolatedStringHandler);
	}

	private static void Finish()
	{
		int num = Clients.Count((ProbeClient client) => client.Ready);
		int count = Clients.Count;
		ManualLogSource logSource = Plugin.LogSource;
		bool isEnabled;
		BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(102, 3, out isEnabled);
		if (isEnabled)
		{
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Single-PC probe] RESULT: ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(num);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral("/");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(count);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" loopback clients reached READY; host received ");
			bepInExWarningLogInterpolatedStringHandler.AppendFormatted(_serverConnectCallbacks);
			bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" server-connect callback(s).");
		}
		logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
		if (num == 1 && count > 1)
		{
			Plugin.LogSource.LogWarning("[Single-PC probe] CONFIRMED: the original transport accepted one remote peer and left the additional clients waiting. The multi-peer fan-out replacement is required.");
		}
		else if (num == count && count > 1)
		{
			Plugin.LogSource.LogWarning("[Single-PC probe] All loopback transports became ready; inspect the callback count before selecting the next patch.");
		}
		else
		{
			Plugin.LogSource.LogWarning("[Single-PC probe] The transport produced a different result; return this log for analysis.");
		}
		StopCore();
	}

	private static void StopCore()
	{
		_insideClientPoll = true;
		try
		{
			foreach (ProbeClient client in Clients)
			{
				try
				{
					_dispose?.Invoke(client.Connection, null);
				}
				catch (Exception exception)
				{
					ManualLogSource logSource = Plugin.LogSource;
					bool isEnabled;
					BepInExWarningLogInterpolatedStringHandler bepInExWarningLogInterpolatedStringHandler = new BepInExWarningLogInterpolatedStringHandler(43, 2, out isEnabled);
					if (isEnabled)
					{
						bepInExWarningLogInterpolatedStringHandler.AppendLiteral("[Single-PC probe] Client ");
						bepInExWarningLogInterpolatedStringHandler.AppendFormatted(client.Number);
						bepInExWarningLogInterpolatedStringHandler.AppendLiteral(" cleanup warning: ");
						bepInExWarningLogInterpolatedStringHandler.AppendFormatted(Unwrap(exception));
					}
					logSource.LogWarning(bepInExWarningLogInterpolatedStringHandler);
				}
			}
		}
		finally
		{
			Clients.Clear();
			Clock.Reset();
			_serverConnectCallbacks = 0;
			_active = false;
			_insideClientPoll = false;
		}
		Plugin.LogSource.LogInfo("[Single-PC probe] Temporary clients removed; normal host gameplay callbacks restored.");
	}

	private static Exception Unwrap(Exception exception)
	{
		if (!(exception is TargetInvocationException) || exception.InnerException == null)
		{
			return exception;
		}
		return exception.InnerException;
	}
}
