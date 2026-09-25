using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace KingdomEightCrowns.AppearanceFlow;

internal static class SafeLoopbackDiagnostic
{
	private enum ProbeState
	{
		WaitingForCampaign,
		InitialDelay,
		Testing,
		Done
	}

	private const ushort ProbePort = 37208;

	private const string LoopbackAddress = "127.0.0.1";

	private const double InitialDelaySeconds = 1.0;

	private const double TimeoutSeconds = 12.0;

	private const double StatusIntervalSeconds = 2.0;

	private static readonly object Gate = new object();

	private static readonly Stopwatch Clock = new Stopwatch();

	private static ConstructorInfo _connectionConstructor;

	private static MethodInfo _pollMessages;

	private static MethodInfo _dispose;

	private static PropertyInfo _isReady;

	private static ProbeState _state = ProbeState.WaitingForCampaign;

	private static object _host;

	private static object _client;

	private static bool _hostReady;

	private static bool _clientReady;

	private static double _nextStatusAt;

	internal static void Install(Harmony harmony)
	{
		Type type = RequireGameType("CustomNetworkConnection");
		Type type2 = RequireCoreType("KingdomEightCrowns.PlayerBodyProbe");
		_connectionConstructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public).SingleOrDefault(delegate(ConstructorInfo constructor)
		{
			ParameterInfo[] parameters = constructor.GetParameters();
			return parameters.Length == 3 && parameters[0].ParameterType == typeof(ushort) && parameters[1].ParameterType == typeof(string) && parameters[2].ParameterType == typeof(bool);
		});
		_pollMessages = FindUnique(type, "PollMessages");
		_dispose = FindUnique(type, "Dispose");
		_isReady = type.GetProperty("IsReady", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (_connectionConstructor == null || _isReady == null)
		{
			throw new MissingMemberException("The required 2.1.4 CustomNetworkConnection loopback constructor or IsReady property was not found.");
		}
		PatchPostfix(harmony, FindUnique(type2, "Tick"), "AfterCampaignTick");
		Plugin.LogSource.LogWarning("[Safe loopback] Installed the self-contained one-PC transport diagnostic. It will start automatically after a campaign begins updating; the game's host/router flow is not required.");
	}

	private static void AfterCampaignTick()
	{
		lock (Gate)
		{
			if (_state == ProbeState.Done)
			{
				return;
			}
			try
			{
				if (_state == ProbeState.WaitingForCampaign)
				{
					StopConnectionsNoThrow();
					_hostReady = false;
					_clientReady = false;
					_nextStatusAt = 3.0;
					Clock.Restart();
					_state = ProbeState.InitialDelay;
					Plugin.LogSource.LogWarning("[Safe loopback] TEST START: campaign updates detected. Creating a private localhost host/client pair; no game hosting or second computer is needed.");
					return;
				}
				double totalSeconds = Clock.Elapsed.TotalSeconds;
				if (_state == ProbeState.InitialDelay && totalSeconds >= 1.0)
				{
					_host = _connectionConstructor.Invoke(new object[3]
					{
						(ushort)37208,
						"127.0.0.1",
						true
					});
					_client = _connectionConstructor.Invoke(new object[3]
					{
						(ushort)37208,
						"127.0.0.1",
						false
					});
					_state = ProbeState.Testing;
					Plugin.LogSource.LogInfo("[Safe loopback] Private host and client created on 127.0.0.1:37208.");
				}
				if (_state != ProbeState.Testing || _host == null || _client == null)
				{
					return;
				}
				_pollMessages.Invoke(_host, null);
				_pollMessages.Invoke(_client, null);
				bool num = ReadReady(_host);
				bool flag = ReadReady(_client);
				if (num && !_hostReady)
				{
					_hostReady = true;
					Plugin.LogSource.LogInfo("[Safe loopback] Private host transport reached READY.");
				}
				if (flag && !_clientReady)
				{
					_clientReady = true;
					Plugin.LogSource.LogInfo("[Safe loopback] Private client transport reached READY.");
				}
				if (_hostReady && _clientReady)
				{
					Finish(passed: true, "private localhost host and client both reached READY");
					return;
				}
				if (totalSeconds >= _nextStatusAt)
				{
					Plugin.LogSource.LogInfo("[Safe loopback] STATUS " + totalSeconds.ToString("0.0") + "s: HOST=" + (_hostReady ? "READY" : "WAITING") + ", CLIENT=" + (_clientReady ? "READY" : "WAITING") + ".");
					_nextStatusAt += 2.0;
				}
				if (totalSeconds >= 12.0)
				{
					Finish(passed: false, "timed out with HOST=" + (_hostReady ? "READY" : "WAITING") + " and CLIENT=" + (_clientReady ? "READY" : "WAITING"));
				}
			}
			catch (Exception exception)
			{
				Finish(passed: false, "contained exception: " + Unwrap(exception));
			}
		}
	}

	private static bool ReadReady(object connection)
	{
		object value = _isReady.GetValue(connection, null);
		if (value is bool)
		{
			return (bool)value;
		}
		return false;
	}

	private static void Finish(bool passed, string details)
	{
		Plugin.LogSource.LogWarning("[Safe loopback] RESULT: " + (passed ? "PASS" : "FAIL") + " - " + details + ".");
		StopConnectionsNoThrow();
		_state = ProbeState.Done;
		Clock.Reset();
		Plugin.LogSource.LogWarning("[Safe loopback] CLEANUP: the private host and client were disposed. Game router, campaign, players, and bodies were never attached to the test.");
	}

	private static void StopConnectionsNoThrow()
	{
		StopConnectionNoThrow(ref _client, "client");
		StopConnectionNoThrow(ref _host, "host");
	}

	private static void StopConnectionNoThrow(ref object connection, string label)
	{
		object obj = connection;
		connection = null;
		if (obj == null || _dispose == null)
		{
			return;
		}
		try
		{
			_dispose.Invoke(obj, null);
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogWarning("[Safe loopback] Private " + label + " cleanup warning: " + Unwrap(exception));
		}
	}

	private static Type RequireGameType(string name)
	{
		Type type = AccessTools.TypeByName(name);
		if (type == null)
		{
			throw new TypeLoadException("Required 2.1.4 game type was not found: " + name);
		}
		return type;
	}

	private static Type RequireCoreType(string fullName)
	{
		Type? type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
			select assembly.GetType(fullName, throwOnError: false)).FirstOrDefault((Type candidate) => candidate != null);
		if (type == null)
		{
			throw new TypeLoadException("Required stable-core type was not found: " + fullName);
		}
		return type;
	}

	private static MethodInfo FindUnique(Type type, string name)
	{
		MethodInfo[] array = (from method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where method.Name == name
			select method).ToArray();
		if (array.Length != 1)
		{
			throw new MissingMethodException("Expected one " + type.Name + "." + name + " method in 2.1.4, found " + array.Length + ".");
		}
		return array[0];
	}

	private static void PatchPostfix(Harmony harmony, MethodInfo original, string patchName)
	{
		harmony.Patch(original, null, new HarmonyMethod(FindPatch(patchName)));
	}

	private static MethodInfo FindPatch(string patchName)
	{
		MethodInfo? method = typeof(SafeLoopbackDiagnostic).GetMethod(patchName, BindingFlags.Static | BindingFlags.NonPublic);
		if (method == null)
		{
			throw new MissingMethodException("Safe-loopback callback was not found: " + patchName);
		}
		return method;
	}

	private static Exception Unwrap(Exception exception)
	{
		if (!(exception is TargetInvocationException { InnerException: not null } ex))
		{
			return exception;
		}
		return ex.InnerException;
	}
}
