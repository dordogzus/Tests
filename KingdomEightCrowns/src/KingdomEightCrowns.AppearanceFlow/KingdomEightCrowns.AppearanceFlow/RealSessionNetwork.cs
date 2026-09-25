using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;

namespace KingdomEightCrowns.AppearanceFlow;

internal static class RealSessionNetwork
{
	private sealed class PeerState
	{
		internal object Connection { get; set; }

		internal int PlayerId { get; private set; }

		internal int BodyNetId { get; private set; }

		internal object PeerToken { get; private set; }

		internal string Description { get; private set; }

		internal bool AssignmentSent { get; set; }

		internal bool CatchupPending { get; set; }

		internal bool JoiningOverlaySuppressed { get; set; }

		internal bool BootstrapRouteLogged { get; set; }

		internal bool BootstrapFlushedLogged { get; set; }

		internal int ReceivedPackets { get; set; }

		internal long AcceptedAt { get; set; }

		internal long LastPacketAt { get; set; }

		internal long CatchupRequestedAt { get; set; }

		internal bool InsideSkinSelection { get; set; }

		internal bool SelectionSeen { get; set; }

		internal bool LastSelectionWasFinal { get; set; }

		internal bool PreviewFanoutLogged { get; set; }

		internal object LastMonarch { get; set; }

		internal object LatestModel { get; set; }

		internal PeerState(object connection, int playerId, int bodyNetId, object peerToken, string description)
		{
			Connection = connection;
			PlayerId = playerId;
			BodyNetId = bodyNetId;
			PeerToken = peerToken;
			Description = description;
			AcceptedAt = Stopwatch.GetTimestamp();
		}
	}

	private sealed class CatchupRouteState
	{
		internal bool PreviousInsideCatchup { get; private set; }

		internal object PreviousConnection { get; private set; }

		internal RouterScopeState RouterState { get; private set; }

		internal CatchupRouteState(bool previousInsideCatchup, object previousConnection, RouterScopeState routerState)
		{
			PreviousInsideCatchup = previousInsideCatchup;
			PreviousConnection = previousConnection;
			RouterState = routerState;
		}
	}

	private sealed class RouterScopeState
	{
		internal object Connection { get; private set; }

		internal bool Connected { get; private set; }

		internal bool CaughtUp { get; private set; }

		internal RouterScopeState(object connection, bool connected, bool caughtUp)
		{
			Connection = connection;
			Connected = connected;
			CaughtUp = caughtUp;
		}
	}

	private sealed class RosterRow
	{
		internal object Panel { get; private set; }

		internal object GameObject { get; private set; }

		internal object Text { get; private set; }

		internal RosterRow(object panel, object gameObject, object text)
		{
			Panel = panel;
			GameObject = gameObject;
			Text = text;
		}
	}

	private sealed class SteamRosterMember
	{
		internal object SteamId { get; private set; }

		internal string Name { get; private set; }

		internal SteamRosterMember(object steamId, string name)
		{
			SteamId = steamId;
			Name = name;
		}
	}

	private sealed class NativeConnectionComparer : IEqualityComparer<object>
	{
		internal static readonly NativeConnectionComparer Instance = new NativeConnectionComparer();

		public new bool Equals(object left, object right)
		{
			if (left == right)
			{
				return true;
			}
			if (left == null || right == null)
			{
				return false;
			}
			IntPtr intPtr = TryReadNativePointer(left);
			IntPtr intPtr2 = TryReadNativePointer(right);
			if (intPtr != IntPtr.Zero && intPtr2 != IntPtr.Zero)
			{
				return intPtr == intPtr2;
			}
			return false;
		}

		public int GetHashCode(object value)
		{
			IntPtr intPtr = TryReadNativePointer(value);
			if (intPtr != IntPtr.Zero)
			{
				long num = intPtr.ToInt64();
				return (int)(num ^ (num >> 32));
			}
			return RuntimeHelpers.GetHashCode(value);
		}
	}

	private const int MaximumRemotePlayers = 7;

	private const int P2DefaultHorseNetId = 924;

	private const int P2DefaultSteedType = 9;

	private const byte SteamConnectMessageCode = 20;

	private const byte PlatformLeavingMessageCode = 21;

	private const byte LeaveNowMessageCode = 22;

	private const byte AllowRerollMessageCode = 60;

	private const byte HeartbeatMessageCode = 30;

	private const byte PlayerModelMessageCode = 43;

	private const int AssignmentMagic = 1258291200;

	private const int BodyMappingMagic = 1275068416;

	private const int BodyRemovalMagic = 1291845632;

	private const int BodyActivationMagic = 1308622848;

	private const int AssignmentMask = -16777216;

	private const int MaximumPlayers = 8;

	private const int RosterScrollWindow = 2;

	private const int MaximumPacketsPerPump = 256;

	private const int FirstPacketTimeoutSeconds = 60;

	private const int JoiningIdleTimeoutSeconds = 120;

	private const int JoiningTotalTimeoutSeconds = 300;

	private static readonly object Gate = new object();

	private static readonly Dictionary<object, PeerState> HostPeers = new Dictionary<object, PeerState>(NativeConnectionComparer.Instance);

	private static readonly Dictionary<object, object> CatchupRoutes = new Dictionary<object, object>(NativeConnectionComparer.Instance);

	private static readonly HashSet<int> SuppressedModelIds = new HashSet<int>();

	private static readonly HashSet<string> LoggedFaults = new HashSet<string>(StringComparer.Ordinal);

	private static Type _intMessageType;

	private static Type _skinSelectMessageType;

	private static Type _playerModelMessageType;

	private static Type _playerModelType;

	private static Type _networkBigBossType;

	private static Type _appearanceRegistryType;

	private static Type _dynamicPlayerRegistryType;

	private static Type _steamPlatformManagerType;

	private static Type _menuType;

	private static Type _gameType;

	private static Type _languageType;

	private static Type _curtainHandlerType;

	private static MethodInfo _steamSend;

	private static MethodInfo _steamReceive;

	private static MethodInfo _steamFlush;

	private static MethodInfo _steamPollMessages;

	private static MethodInfo _steamDispose;

	private static MethodInfo _isP2PPacketAvailable;

	private static MethodInfo _readP2PPacket;

	private static MethodInfo _sendP2PPacket;

	private static MethodInfo _getLocalSteamId;

	private static MethodInfo _messageDeserialize;

	private static MethodInfo _copyModel;

	private static MethodInfo _getNativePlayerTwoNetId;

	private static MethodInfo _reserveBodyNetId;

	private static MethodInfo _setLocalBodyOwner;

	private static MethodInfo _assignDynamicPlayer;

	private static MethodInfo _activateDynamicPlayer;

	private static MethodInfo _removeDynamicPlayer;

	private static MethodInfo _resetDynamicPlayers;

	private static MethodInfo _tryReapplyAppearance;

	private static MethodInfo _friendPersonaName;

	private static MethodInfo _getNumLobbyMembers;

	private static MethodInfo _getLobbyMemberByIndex;

	private static MethodInfo _getLobbyOwner;

	private static MethodInfo _localUsername;

	private static MethodInfo _remoteUsername;

	private static MethodInfo _getSystemLanguage;

	private static MethodInfo _isNetworkingPanelOpen;

	private static MethodInfo _setClientConnectingFinished;

	private static MethodInfo _curtainIsActive;

	private static MethodInfo _setCurtainActive;

	private static MethodInfo _hideCurtainText;

	private static MethodInfo _hideCurtainModal;

	private static MethodInfo _setCurtainAlpha;

	private static MethodInfo _clientHandleOnConnect;

	private static PropertyInfo _unetRouterProperty;

	private static MethodInfo _getLegacyScrollAxis;

	private static MethodInfo _getLegacyKeyDown;

	private static PropertyInfo _mouseScrollDelta;

	private static PropertyInfo _bossInstanceProperty;

	private static PropertyInfo _curtainInstanceProperty;

	private static FieldInfo _slotsField;

	private static object _hostRouter;

	private static object _hostGame;

	private static object _activeCatchupConnection;

	private static bool _packetPumpReadyLogged;

	private static object _rosterMenu;

	private static readonly List<RosterRow> RosterRows = new List<RosterRow>();

	private static string[] _cachedRosterSnapshot;

	private static object _rosterLocalText;

	private static object _rosterRemoteText;

	private static string _savedLocalText;

	private static string _savedRemoteText;

	private static bool _sourceTextsBlanked;

	private static bool _rosterUnavailable;

	private static bool _rosterWasOpen;

	private static bool _rosterReadyLogged;

	private static bool _rosterLayoutVerifiedLogged;

	private static bool _rosterVirtualViewportLogged;

	private static bool _scrollInputResolved;

	private static bool _steamRosterApiResolved;

	private static bool _steamRosterFallbackLogged;

	private static int _rosterFirstVisible;

	private static int _lastLoggedLobbyMemberCount = -1;

	private static long _nextRosterRefresh;

	private static long _nextRosterSnapshotRefresh;

	private static bool _lastInSteamLobby;

	private static bool? _lastInviteInteractable;

	private static long _nextPeerWatchdog;

	private static bool _clientConnectStarted;

	private static bool _duplicateClientConnectLogged;

	[ThreadStatic]
	private static object _receivingConnection;

	[ThreadStatic]
	private static bool _directSend;

	[ThreadStatic]
	private static object _acceptingRouter;

	[ThreadStatic]
	private static object _connectionBeforeAccept;

	[ThreadStatic]
	private static object _acceptingPeerToken;

	[ThreadStatic]
	private static bool _insideAdditionalPeerAcceptance;

	[ThreadStatic]
	private static object _suppressedConnectingPeerToken;

	[ThreadStatic]
	private static object _rejectedConnectionDuringAccept;

	[ThreadStatic]
	private static string _rejectedConnectionReason;

	[ThreadStatic]
	private static bool _isolatedPeerDispose;

	[ThreadStatic]
	private static string _isolatedDisposeReason;

	[ThreadStatic]
	private static object _catchupSendConnection;

	[ThreadStatic]
	private static bool _insideCatchupSend;

	private static bool _logicalAssignmentReceived;

	private static bool _localBodyActivated;

	private static readonly HashSet<object> WatchedCounterTexts = new HashSet<object>();

	private static int LoggedCounterSamples;

	[ThreadStatic]
	private static bool _insideUITextRewrite;

	private static object _overlayInstance;

	private static readonly HashSet<object> _restyledCounterTmp = new HashSet<object>();

	private static readonly Dictionary<object, object> CounterOverlays = new Dictionary<object, object>();

	private static object _tmpDonorLabel;

	private static bool _counterOverlayWarned;

	private static readonly HashSet<object> _counterWidgetProbed = new HashSet<object>();

	private static int _counterFontReassertLogs;

	private static bool _insideCounterStyleRewrite;

	private static int _lastBadgeCloneFaultTick = -60000;

	private static bool _badgeCloneUsesVersionLabel;

	private static object _versionBadgeSource;

	private static object _badgeCanvas;

	private static bool _badgeAnchorLogged;

	private static object _badgeCloneGap;

	private const string BadgeTextMarker = "KINGDOM EIGHT CROWNS";

	private static readonly string BadgeVersionTag = "(A" + "0.14.20-alpha".Replace("-alpha", string.Empty) + ")";

	private static readonly string RichBadgeSuffix = "\nKINGDOM EIGHT CROWNS <color=#F7F7FC21>" + BadgeVersionTag + "</color>";

	private static readonly string PlainBadgeSuffix = "\nKINGDOM EIGHT CROWNS " + BadgeVersionTag;

	private static object _versionLabel;

	private static long _versionLabelNextCheck;

	private static bool _versionLabelSamplesLogged;

	private static int _versionLabelQuickScans;

	private static bool _versionLabelDeepDone;

	private static object _menuButtonFont;

	private static object _menuButtonLabel;

	private static object _difficultyLabel;

	private static object _badgeClone;

	private static bool _badgeWaitingLogged;

	private static readonly HashSet<string> LoggedRelayRoutes = new HashSet<string>();

	private static readonly HashSet<int> LoggedModelRelays = new HashSet<int>();

	private static bool _rosterCrossfadeMode;

	private static object _crossfadeCheckedMenu;

	private static int _crossfadePhase;

	private static float _crossfadeAlpha = 1f;

	private static float _crossfadeDirection = 1f;

	private static int _crossfadeShown;

	private static object _crossfadeBaseTop;

	private static object _crossfadeBaseBottom;

	private const float RosterScrollLockSeconds = 0.22f;

	private static long _nextRosterScrollAllowed;

	private static float _scrollAccumulator;

	private static object _scrollbarTrack;

	private static object _scrollbarThumb;

	private static float _scrollbarBannerWidth = 162f;

	private static float _scrollbarBannerHeight = 45f;

	private static bool _scrollbarFailed;

	private static bool _scrollbarGeometryLogged;

	private static bool _scrollbarWidgetDumped;

	private static bool _menuTearingDown;

	private const float ScrollbarTrackScale = 1f;

	private const float ScrollbarThumbScale = 0.4f;

	private const float ScrollbarWidthRatio = 0.055f;

	private const float ScrollbarEdgeRatio = 0.55f;

	private static bool _scrollbarSpanLogged;

	private static readonly List<object> ScrollbarArtKeepAlive = new List<object>();

	private static object _crossfadeBaseTopBackup;

	private static object _pixelFontLegacy;

	private static object _pixelFontTmp;

	private static object _menuTmpFontAsset;

	private static bool _menuTmpFontAssetFailed;

	private static bool _nicknameKerningLogged;

	private static bool _menuFontGlyphsNormalized;

	private static int _glyphNormalizationAttempts;

	private static bool _nicknameISwapLogged;

	private static bool _insideTmpTextSwap;

	private static readonly List<object> _styledNicknameTmpLabels = new List<object>();

	private static string _menuFontMissingChars;

	private static bool _menuFontCoverageChecked;

	private static readonly HashSet<object> _nicknameUppercased = new HashSet<object>();

	private static readonly Dictionary<object, float> _tmpSizeTargets = new Dictionary<object, float>();

	private static bool _pixelFontSwapLogged;

	private static bool _nicknameMenuSizeLogged;

	private static readonly Dictionary<object, float> _nicknameSizeTargets = new Dictionary<object, float>();

	private static object _counterLegacyFont;

	private static bool _counterFontResolved;

	private static bool _legacyFontsDumped;

	private static readonly Dictionary<Type, MethodInfo> UnityAliveChecks = new Dictionary<Type, MethodInfo>();

	internal static int LocalPlayerId { get; private set; }

	internal static void Install(Harmony harmony)
	{
		NativeLobbyCapacity.Apply(8);
		Type type = RequireGameType("SteamNetworkConnection");
		Type type2 = RequireGameType("UNetRouter");
		Type type3 = RequireGameType("NetworkPostbox");
		Type type4 = RequireGameType("Player");
		_steamPlatformManagerType = RequireGameType("SteamPlatformManager");
		_menuType = RequireGameType("Menu");
		_gameType = RequireGameType("Game");
		_languageType = RequireGameType("Language");
		_curtainHandlerType = RequireGameType("CurtainHandler");
		_intMessageType = RequireGameType("IntMessage");
		_skinSelectMessageType = RequireGameType("SkinSelectMessage");
		_playerModelMessageType = RequireGameType("PlayerModelMessage");
		_playerModelType = RequireGameType("PlayerModel");
		_networkBigBossType = RequireGameType("NetworkBigBoss");
		_appearanceRegistryType = RequireCoreType("KingdomEightCrowns.PlayerAppearanceRegistry");
		_dynamicPlayerRegistryType = RequireCoreType("KingdomEightCrowns.DynamicPlayerRegistry");
		_getNativePlayerTwoNetId = FindUnique(_dynamicPlayerRegistryType, "GetNativePlayerTwoNetId", (MethodInfo method) => method.IsStatic && method.ReturnType == typeof(int) && method.GetParameters().Length == 0);
		_reserveBodyNetId = FindUnique(_dynamicPlayerRegistryType, "ReserveBodyNetId", (MethodInfo method) => method.IsStatic && method.ReturnType == typeof(int) && method.GetParameters().Length == 0);
		_setLocalBodyOwner = FindUnique(_dynamicPlayerRegistryType, "SetLocalOwner", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 1);
		_assignDynamicPlayer = FindUnique(_dynamicPlayerRegistryType, "AssignPlayer", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 2);
		_activateDynamicPlayer = FindUnique(_dynamicPlayerRegistryType, "ActivatePlayer", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 1);
		_removeDynamicPlayer = FindUnique(_dynamicPlayerRegistryType, "RemovePlayer", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 1);
		_resetDynamicPlayers = FindUnique(_dynamicPlayerRegistryType, "ResetSession", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 1);
		_tryReapplyAppearance = FindUnique(_dynamicPlayerRegistryType, "TryReapplyAppearance", (MethodInfo method) => method.IsStatic && method.ReturnType == typeof(void) && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(int));
		_steamSend = FindUnique(type, "Send", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 3 && parameters[0].ParameterType == typeof(byte) && parameters[2].ParameterType == typeof(bool);
		});
		_steamReceive = FindUnique(type, "Receive", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 2 && parameters[1].ParameterType == typeof(int);
		});
		MethodInfo original = FindUnique(type, "SetTargetID", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.ReturnType == typeof(bool) && parameters.Length == 2 && parameters[1].ParameterType == typeof(bool);
		});
		_steamDispose = FindUnique(type, "Dispose", (MethodInfo method) => method.GetParameters().Length == 0);
		_steamPollMessages = FindUnique(type, "PollMessages", (MethodInfo method) => method.GetParameters().Length == 0);
		_steamFlush = FindUnique(type, "SteamSend", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.ReturnType == typeof(bool) && parameters.Length == 3 && parameters[1].ParameterType == typeof(int);
		});
		MethodInfo original2 = FindUnique(type2, "AcceptP2PConnection", (MethodInfo method) => method.GetParameters().Length == 1);
		MethodInfo original3 = FindUnique(type2, "Server_HandleOnConnect", (MethodInfo method) => method.GetParameters().Length == 0);
		_clientHandleOnConnect = FindUnique(type2, "Client_HandleOnConnect", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original4 = FindUnique(type2, "Client_HandleOnDisconnect", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original5 = FindUnique(type2, "RecvCatchupRequest", (MethodInfo method) => method.GetParameters().Length == 1);
		MethodInfo original6 = FindUnique(type2, "RecvClientReady", (MethodInfo method) => method.GetParameters().Length == 1);
		MethodInfo methodInfo = FindUnique(type2, "SendCatchup", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo methodInfo2 = FindUnique(type2, "SendWorldSyncRoutine", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo methodInfo3 = FindUnique(type3, "FinalCatchup", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo methodInfo4 = FindUnique(type3, "CatchupFinalGrabs", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo methodInfo5 = FindUnique(type3, "CatchupPoolSpawns", (MethodInfo method) => method.GetParameters().Length == 0);
		Type type5 = FindIteratorStateMachine(type2, methodInfo, "SendCatchup");
		Type type6 = FindIteratorStateMachine(type2, methodInfo2, "SendWorldSyncRoutine");
		Type type7 = FindIteratorStateMachine(type3, methodInfo3, "FinalCatchup");
		Type type8 = FindIteratorStateMachine(type3, methodInfo4, "CatchupFinalGrabs");
		Type type9 = FindIteratorStateMachine(type3, methodInfo5, "CatchupPoolSpawns");
		MethodInfo original7 = FindUnique(type5, "MoveNext", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original8 = FindUnique(type6, "MoveNext", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original9 = FindUnique(type7, "MoveNext", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original10 = FindUnique(type8, "MoveNext", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original11 = FindUnique(type9, "MoveNext", (MethodInfo method) => method.GetParameters().Length == 0);
		MethodInfo original12 = FindUnique(_gameType, "SetClientConnecting", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0);
		_setClientConnectingFinished = FindUnique(_gameType, "SetClientConnectingFinished", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0);
		_curtainInstanceProperty = RequireProperty(_curtainHandlerType, "Inst", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
		_curtainIsActive = FindUnique(_curtainHandlerType, "CurtainIsActive", (MethodInfo method) => !method.IsStatic && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0);
		_setCurtainActive = FindUnique(_curtainHandlerType, "SetCurtainActive", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(bool));
		_hideCurtainText = FindUnique(_curtainHandlerType, "HideCurtainText", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0);
		_hideCurtainModal = FindUnique(_curtainHandlerType, "HideCurtainModal", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0);
		_setCurtainAlpha = FindUnique(_curtainHandlerType, "SetCurtainAlpha", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(float));
		_unetRouterProperty = RequireProperty(_networkBigBossType, "UNetRouter", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		ResolveSteamPacketApi();
		ResolveSteamIdentityApi();
		_messageDeserialize = FindUnique(_intMessageType, "Deserialize", (MethodInfo method) => method.GetParameters().Length == 1);
		_bossInstanceProperty = RequireProperty(_networkBigBossType, "Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		_slotsField = RequireField(_appearanceRegistryType, "Slots", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		_copyModel = FindUnique(_appearanceRegistryType, "CopyModel", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.IsStatic && parameters.Length == 3 && parameters[0].ParameterType == _playerModelType && parameters[1].ParameterType == typeof(int);
		});
		_getSystemLanguage = FindOptionalUnique(_languageType, "GetSystemLanguage", (MethodInfo method) => method.IsStatic && method.GetParameters().Length == 0, "roster language lookup");
		_isNetworkingPanelOpen = FindOptionalUnique(_menuType, "IsNetworkingPanelOpen", (MethodInfo method) => !method.IsStatic && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0, "Online-panel visibility lookup");
		_localUsername = FindOptionalUnique(_steamPlatformManagerType, "LocalUsername", (MethodInfo method) => !method.IsStatic && method.ReturnType == typeof(string) && method.GetParameters().Length == 0, "local roster-name fallback");
		_remoteUsername = FindOptionalUnique(_steamPlatformManagerType, "RemoteUsername", (MethodInfo method) => !method.IsStatic && method.ReturnType == typeof(string) && method.GetParameters().Length == 0, "remote roster-name fallback");
		Patch(harmony, original, null, "AfterSteamSetTargetId");
		Patch(harmony, _steamReceive, "BeforeSteamReceive", "AfterSteamReceive");
		Patch(harmony, _steamPollMessages, "BeforeSteamPollMessages", "AfterSteamPollMessages");
		Patch(harmony, _steamSend, "BeforeSteamSend", "AfterSteamSend");
		Patch(harmony, _steamDispose, "BeforeSteamDispose", null);
		Patch(harmony, original2, "BeforeAcceptP2PConnection", "AfterAcceptP2PConnection");
		Patch(harmony, original3, "BeforeServerHandleOnConnect", "AfterServerHandleOnConnect");
		Patch(harmony, original5, "BeforeRecvCatchupRequest", null);
		Patch(harmony, original6, "BeforeRecvClientReady", "AfterRecvClientReady");
		Patch(harmony, methodInfo, null, "AfterCreateCatchupIterator");
		Patch(harmony, methodInfo2, null, "AfterCreateCatchupIterator");
		Patch(harmony, methodInfo3, null, "AfterCreateCatchupIterator");
		Patch(harmony, methodInfo4, null, "AfterCreateCatchupIterator");
		Patch(harmony, methodInfo5, null, "AfterCreateCatchupIterator");
		Patch(harmony, original7, "BeforeCatchupMoveNext", "AfterCatchupMoveNext");
		Patch(harmony, original8, "BeforeCatchupMoveNext", "AfterCatchupMoveNext");
		Patch(harmony, original9, "BeforeCatchupMoveNext", "AfterCatchupMoveNext");
		Patch(harmony, original10, "BeforeCatchupMoveNext", "AfterCatchupMoveNext");
		Patch(harmony, original11, "BeforeCatchupMoveNext", "AfterCatchupMoveNext");
		Patch(harmony, original12, "BeforeSetClientConnecting", null);
		Patch(harmony, _clientHandleOnConnect, "BeforeClientHandleOnConnect", null);
		Patch(harmony, original4, null, "AfterClientHandleOnDisconnect");
		Patch(harmony, FindUnique(_steamPlatformManagerType, "NotifyClientLeft", (MethodInfo method) => method.GetParameters().Length == 0), "BeforeNotifyClientLeft", null);
		Patch(harmony, FindUnique(_steamPlatformManagerType, "OnLobbyChatUpdate", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeLobbyChatUpdate", null);
		Patch(harmony, FindUnique(_steamPlatformManagerType, "OnP2PSessionFailed", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeP2PSessionFailed", null);
		Patch(harmony, FindUnique(type2, "Server_HandleOnDisconnect", (MethodInfo method) => method.GetParameters().Length == 0), "BeforeServerHandleOnDisconnect", null);
		Patch(harmony, FindUnique(type2, "RecvClientHandshake", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeRecvClientHandshake", null);
		Patch(harmony, FindUnique(type2, "RecvAllowPlayerReroll", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeRecvAllowPlayerReroll", null);
		Patch(harmony, FindUnique(type2, "RecvP2SelectSkin", (MethodInfo method) => method.GetParameters().Length == 1), "BeforeRecvP2SelectSkin", "AfterRecvP2SelectSkin");
		Patch(harmony, FindUnique(type2, "SendPlayerData", (MethodInfo method) => method.GetParameters().Length == 0), "BeforeSendPlayerData", "AfterSendPlayerData");
		Patch(harmony, FindUnique(_skinSelectMessageType, "Deserialize", (MethodInfo method) => method.GetParameters().Length == 1), null, "AfterSkinSelectMessageDeserialize");
		Patch(harmony, FindUnique(_playerModelMessageType, "Serialize", (MethodInfo method) => method.GetParameters().Length == 1), "BeforePlayerModelMessageSerialize", "AfterPlayerModelMessageSerialize");
		Patch(harmony, FindUnique(_networkBigBossType, "HandleRecvPlayerData", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 1 && parameters[0].ParameterType == _playerModelType;
		}), "BeforeHandleRecvPlayerData", null);
		Patch(harmony, FindUnique(type4, "SetupAsPlayer", delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
		}), "BeforeSetupAsPlayer", null);
		TryPatchCapability(harmony, "invite availability getter", () => FindUnique(_gameType, "get_AcceptsMultiplayerInvites", (MethodInfo method) => method.GetParameters().Length == 0), null, "AfterAcceptsMultiplayerInvites");
		TryPatchCapability(harmony, "per-frame invite and roster refresh", () => FindUnique(_menuType, "Update", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0), null, "AfterMenuUpdate");
		TryPatchCapability(harmony, "multiplayer menu setup refresh", () => FindUnique(_menuType, "SetupMultiplayerUI", (MethodInfo method) => method.GetParameters().Length == 5), null, "AfterMenuNetworkStateChanged");
		TryPatchCapability(harmony, "network-button refresh", () => FindUnique(_menuType, "ChangeNetworkButtons", (MethodInfo method) => method.GetParameters().Length == 1), null, "AfterMenuNetworkStateChanged");
		TryPatchCapability(harmony, "remote-user banner refresh", () => FindUnique(_menuType, "SetRemoteUserPanel", (MethodInfo method) => method.GetParameters().Length == 2), null, "AfterMenuNetworkStateChanged");
		TryPatchCapability(harmony, "Online-panel refresh", () => FindUnique(_menuType, "OnButtonOpenNetMenu", (MethodInfo method) => method.GetParameters().Length == 0), null, "AfterMenuNetworkStateChanged");
		TryPatchCapability(harmony, "menu-hide roster cleanup", () => FindUnique(_menuType, "Hide", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0), "BeforeMenuHide", null);
		TryPatchCapability(harmony, "menu-disable roster cleanup", () => FindUnique(_menuType, "OnDisable", (MethodInfo method) => !method.IsStatic && method.GetParameters().Length == 0), "BeforeMenuHide", null);
		InstallOverlayTweaks(harmony);
		Plugin.LogSource.LogWarning("[Dynamic session] Installed collision-free dynamic body IDs, stable native-identity ownership, Steam-ID slot deduplication, peer-scoped transport bootstraps, ready-peer live routing, isolated join catch-up routing, the eight-peer inbound packet pump, per-peer native output-buffer flushes, native ready transitions, and non-destructive Player 3+ acceptance. Each real peer keeps one logical PlayerId, body, transport, and native ruler selector.");
	}

	private static void InstallOverlayTweaks(Harmony harmony)
	{
		if (AppearanceFlowSettings.OverlayDisableAll)
		{
			Plugin.LogSource.LogWarning("[Overlay] OverlayDisableAll=true — every menu overlay tweak (badge, scrollbar, font swaps, day counters) is skipped for ESC-crash isolation.");
			return;
		}
		try
		{
			Type type = RequireGameType("InterfaceOverlay");
			string[] array = new string[2] { "StartDayCounterShow", "DayCounterShow" };
			foreach (string methodName in array)
			{
				MethodInfo methodInfo = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == methodName && method.GetParameters().Length == 1);
				if (!(methodInfo == null))
				{
					harmony.Patch(methodInfo, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterStartDayCounterShow", BindingFlags.Static | BindingFlags.NonPublic)));
				}
			}
			Type type2 = FindLoadedType("UnityEngine.UI.Text");
			MethodInfo methodInfo2 = type2.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "set_text" && method.GetParameters().Length == 1);
			if (methodInfo2 == null)
			{
				throw new MissingMethodException("UnityEngine.UI.Text.set_text(String)");
			}
			harmony.Patch(methodInfo2, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("BeforeUITextSet", BindingFlags.Static | BindingFlags.NonPublic)), new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterUITextSet", BindingFlags.Static | BindingFlags.NonPublic)));
			MethodInfo methodInfo3 = type2.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "set_font" && method.GetParameters().Length == 1);
			MethodInfo methodInfo4 = type2.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "set_fontSize" && method.GetParameters().Length == 1);
			if (methodInfo3 != null)
			{
				harmony.Patch(methodInfo3, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterCounterFontSet", BindingFlags.Static | BindingFlags.NonPublic)));
			}
			if (methodInfo4 != null)
			{
				harmony.Patch(methodInfo4, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterCounterFontSizeSet", BindingFlags.Static | BindingFlags.NonPublic)));
			}
			MethodInfo methodInfo5 = type2.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "set_fontStyle" && method.GetParameters().Length == 1);
			if (methodInfo5 != null)
			{
				harmony.Patch(methodInfo5, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterCounterFontStyleSet", BindingFlags.Static | BindingFlags.NonPublic)));
			}
			MethodInfo methodInfo6 = (FindLoadedType("TMPro.TextMeshProUGUI") ?? FindLoadedType("TextMeshProUGUI"))?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "set_text" && method.GetParameters().Length == 1);
			if (methodInfo6 != null)
			{
				harmony.Patch(methodInfo6, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterTmpNicknameTextSet", BindingFlags.Static | BindingFlags.NonPublic)));
			}
			MethodInfo methodInfo7 = RequireGameType("Menu").GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault((MethodInfo method) => method.Name == "OnDisable" && method.GetParameters().Length == 0);
			if (methodInfo7 != null)
			{
				harmony.Patch(methodInfo7, null, new HarmonyMethod(typeof(RealSessionNetwork).GetMethod("AfterMenuDisableForBadge", BindingFlags.Static | BindingFlags.NonPublic)));
			}
			Plugin.LogSource.LogWarning("[Overlay] Day counters render Arabic numerals in the game's own font.");
		}
		catch (Exception exception)
		{
			LogFaultOnce("overlay tweaks install", exception);
		}
	}

	private static void EnsureCounterOverlay(object counter)
	{
		if (!AppearanceFlowSettings.CounterPixelOverlay)
		{
			return;
		}
		try
		{
			if (_tmpDonorLabel == null || !IsUnityAlive(_tmpDonorLabel))
			{
				return;
			}
			string text = Convert.ToString(ReadMember(counter, "text"));
			if (string.IsNullOrEmpty(text) || !text.All(char.IsDigit))
			{
				return;
			}
			if (!CounterOverlays.TryGetValue(counter, out var value) || value == null || !IsUnityAlive(value))
			{
				object instance = ReadMember(counter, "transform");
				object obj = ReadMember(instance, "parent");
				MethodInfo methodInfo = FindPanelInstantiate();
				if (methodInfo == null || obj == null)
				{
					return;
				}
				value = methodInfo.Invoke(null, new object[3] { _tmpDonorLabel, obj, false });
				if (value == null)
				{
					return;
				}
				CounterOverlays[counter] = value;
				WriteMember(instance, "localScale", MakeVectorLike(ReadMember(instance, "localScale"), 0.001f, 0.001f, 1f));
				if (!_counterOverlayWarned)
				{
					_counterOverlayWarned = true;
					Plugin.LogSource.LogWarning("[Overlay] Counter pixel overlay active (CounterPixelOverlay=true).");
				}
			}
			WriteMember(value, "text", text);
			WriteMember(value, "fontSize", 10f);
			WriteMember(value, "color", Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), 1f, 1f, 1f, 1f));
			object instance2 = CastIl2CppObject(ReadMember(value, "transform"), "UnityEngine.RectTransform");
			object instance3 = ReadMember(ReadMember(counter, "transform"), "position");
			WriteMember(instance2, "position", MakeVectorLike(ReadMember(instance2, "position"), Convert.ToSingle(ReadMember(instance3, "x")), Convert.ToSingle(ReadMember(instance3, "y")), Convert.ToSingle(ReadMember(instance3, "z"))));
		}
		catch
		{
		}
	}

	private static void AfterStartDayCounterShow(object __instance)
	{
		try
		{
			_overlayInstance = __instance;
			bool flag = false;
			string[] array = new string[2] { "dayCounter", "gameOverDays" };
			foreach (string name in array)
			{
				object obj = ReadMember(__instance, name);
				if (obj != null)
				{
					lock (Gate)
					{
						flag |= WatchedCounterTexts.Add(obj);
					}
				}
			}
			if (flag)
			{
				Plugin.LogSource.LogWarning("[Overlay] Watching the day-counter text components for Roman numeral conversion.");
			}
		}
		catch
		{
		}
	}

	private static void AfterCounterFontSet(object __instance)
	{
		try
		{
			if (_insideCounterStyleRewrite)
			{
				return;
			}
			lock (Gate)
			{
				if (!WatchedCounterTexts.Contains(__instance))
				{
					return;
				}
			}
			object obj = ResolveCounterLegacyFont();
			if (obj == null)
			{
				return;
			}
			if (_counterFontReassertLogs < 6)
			{
				_counterFontReassertLogs++;
				Plugin.LogSource.LogWarning("[Overlay] Game wrote a counter font; re-asserting the pixel font on top.");
			}
			_insideCounterStyleRewrite = true;
			try
			{
				WriteMember(__instance, "font", obj);
			}
			finally
			{
				_insideCounterStyleRewrite = false;
			}
		}
		catch
		{
		}
	}

	private static void AfterCounterFontSizeSet(object __instance)
	{
		try
		{
			if (_insideCounterStyleRewrite)
			{
				return;
			}
			lock (Gate)
			{
				if (!WatchedCounterTexts.Contains(__instance))
				{
					return;
				}
			}
			_insideCounterStyleRewrite = true;
			try
			{
				WriteMember(__instance, "fontSize", CounterFontSizeTarget());
			}
			finally
			{
				_insideCounterStyleRewrite = false;
			}
		}
		catch
		{
		}
	}

	private static void AfterCounterFontStyleSet(object __instance)
	{
		try
		{
			if (_insideCounterStyleRewrite)
			{
				return;
			}
			lock (Gate)
			{
				if (!WatchedCounterTexts.Contains(__instance))
				{
					return;
				}
			}
			_insideCounterStyleRewrite = true;
			try
			{
				WriteMember(__instance, "fontStyle", 0);
			}
			finally
			{
				_insideCounterStyleRewrite = false;
			}
		}
		catch
		{
		}
	}

	private static void AfterTmpNicknameTextSet(object __instance, string value)
	{
		try
		{
			if (_insideTmpTextSwap || string.IsNullOrEmpty(value) || !value.Contains('i') || _menuTmpFontAsset == null)
			{
				return;
			}
			object obj = ReadMember(__instance, "font");
			if (obj == null || !AreSameUnityObject(obj, _menuTmpFontAsset))
			{
				return;
			}
			_insideTmpTextSwap = true;
			try
			{
				WriteMember(__instance, "text", value.Replace('i', 'I'));
			}
			finally
			{
				_insideTmpTextSwap = false;
			}
		}
		catch
		{
		}
	}

	private static float CounterFontSizeTarget()
	{
		float num = 0f;
		try
		{
			num = Convert.ToSingle(ReadMember(_menuButtonLabel, "fontSize")) * AppearanceFlowSettings.CounterScale;
		}
		catch
		{
		}
		if (num <= 0f)
		{
			num = 8f * AppearanceFlowSettings.CounterScale;
		}
		return num;
	}

	private static void ProbeCounterWidgetAndRestyle(object counter)
	{
		try
		{
			object obj = ReadMember(counter, "gameObject");
			if (obj == null || !IsUnityAlive(obj))
			{
				return;
			}
			lock (Gate)
			{
				if (!_counterWidgetProbed.Add(obj))
				{
					return;
				}
			}
			object obj2 = null;
			try
			{
				obj2 = ReadMember(ReadMember(ReadMember(counter, "transform"), "parent"), "gameObject");
			}
			catch
			{
			}
			if (obj2 != null && !IsUnityAlive(obj2))
			{
				obj2 = null;
			}
			object[] array = new object[2] { obj, obj2 };
			foreach (object obj4 in array)
			{
				if (obj4 != null)
				{
					DumpAndRestyleWidgetComponents(obj4);
				}
			}
		}
		catch
		{
		}
	}

	private static void DumpAndRestyleWidgetComponents(object gameObject)
	{
		try
		{
			MethodInfo methodInfo = gameObject.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
			{
				if (method.Name != "GetComponents")
				{
					return false;
				}
				ParameterInfo[] parameters = method.GetParameters();
				return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
			});
			if (methodInfo == null)
			{
				return;
			}
			Type type = FindLoadedType("UnityEngine.Component");
			if (type == null)
			{
				return;
			}
			object obj = ToIl2CppType(type) ?? type;
			object obj2 = methodInfo.Invoke(gameObject, new object[1] { obj });
			if (obj2 == null)
			{
				return;
			}
			object pixelLegacy = ResolveCounterLegacyFont();
			object pixelTmp = ResolvePixelFontTmp();
			float sizeTarget = CounterFontSizeTarget();
			foreach (object item in (IEnumerable)obj2)
			{
				try
				{
					if (item != null && IsUnityAlive(item))
					{
						RestyleWidgetComponent(item, pixelLegacy, pixelTmp, sizeTarget);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static void RestyleWidgetComponent(object component, object pixelLegacy, object pixelTmp, float sizeTarget)
	{
		try
		{
			string text = null;
			try
			{
				text = Convert.ToString(ReadMember(component.GetType().GetMethod("GetObjectClass")?.Invoke(component, null), "name"));
			}
			catch
			{
			}
			if (string.IsNullOrEmpty(text))
			{
				text = component.GetType().Name;
			}
			Type type = component.GetType();
			List<MemberInfo> list = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Cast<MemberInfo>().Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				.Where(delegate(MemberInfo member)
				{
					string text3 = member.Name.ToLowerInvariant();
					return text3.Contains("font") || text3.Contains("typeface");
				})
				.Distinct()
				.ToList();
			if (list.Count == 0)
			{
				return;
			}
			List<string> values = list.Select((MemberInfo member) => member.Name).ToList();
			Plugin.LogSource.LogWarning("[Overlay] Counter widget probe: " + text + " font-like members: " + string.Join(", ", values) + ".");
			foreach (MemberInfo item in list)
			{
				try
				{
					object obj2;
					try
					{
						obj2 = ((item is PropertyInfo propertyInfo) ? propertyInfo.GetValue(component, null) : ((FieldInfo)item).GetValue(component));
					}
					catch
					{
						goto end_IL_0126;
					}
					string text2 = obj2?.GetType().Name ?? "null";
					if (text2 == "Font" && pixelLegacy != null)
					{
						WriteMember(component, item.Name, pixelLegacy);
						Plugin.LogSource.LogWarning("[Overlay] Counter widget probe: wrote pixel legacy font into " + text + "." + item.Name + " (was Font).");
					}
					else if (text2.Contains("TMP_FontAsset") && pixelTmp != null)
					{
						WriteMember(component, item.Name, pixelTmp);
						Plugin.LogSource.LogWarning("[Overlay] Counter widget probe: wrote pixel TMP asset into " + text + "." + item.Name + ".");
					}
					else if (item.Name.ToLowerInvariant() == "fontsize")
					{
						WriteMember(component, item.Name, sizeTarget);
						Plugin.LogSource.LogWarning("[Overlay] Counter widget probe: wrote size " + sizeTarget + " into " + text + "." + item.Name + " (was " + text2 + ").");
					}
					else if (item.Name.ToLowerInvariant() == "fontstyle")
					{
						WriteMember(component, item.Name, 0);
						Plugin.LogSource.LogWarning("[Overlay] Counter widget probe: wrote style Normal into " + text + "." + item.Name + " (was " + text2 + ").");
					}
					end_IL_0126:;
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static void BeforeUITextSet(object __instance, ref string value)
	{
		try
		{
			if (_insideUITextRewrite || string.IsNullOrEmpty(value) || value.Length > 8)
			{
				return;
			}
			int num = ParseRomanNumeral(value);
			if (num <= 0)
			{
				return;
			}
			if (LoggedCounterSamples < 6)
			{
				LoggedCounterSamples++;
				Plugin.LogSource.LogWarning("[Overlay] Prefix converting day counter \"" + value + "\" to \"" + num + "\".");
			}
			value = num.ToString();
			PixelizeRosterText(__instance, scaleUp: false, counter: true);
			ProbeCounterWidgetAndRestyle(__instance);
			if (LoggedCounterSamples >= 6)
			{
				return;
			}
			LoggedCounterSamples++;
			try
			{
				Type type = __instance.GetType();
				List<string> values = (from memberName in (from property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
						select property.Name).Concat(from field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
						select field.Name)
					where memberName.ToLowerInvariant().Contains("font") || memberName.ToLowerInvariant().Contains("typeface") || memberName.ToLowerInvariant().Contains("asset")
					select memberName).Distinct().ToList();
				Plugin.LogSource.LogWarning("[Overlay] Counter label type " + type.FullName + ", font-like members: " + string.Join(", ", values) + ".");
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	private static void AfterUITextSet(object __instance, string value)
	{
		try
		{
			if (_insideUITextRewrite || string.IsNullOrEmpty(value) || value.Length > 8)
			{
				return;
			}
			lock (Gate)
			{
				if (!WatchedCounterTexts.Contains(__instance))
				{
					return;
				}
			}
			if (LoggedCounterSamples < 6)
			{
				LoggedCounterSamples++;
				Plugin.LogSource.LogWarning("[Overlay] Day counter text sample: \"" + value + "\" (length " + value.Length + ").");
			}
			int num = ParseRomanNumeral(value);
			if (num <= 0)
			{
				return;
			}
			_insideUITextRewrite = true;
			try
			{
				WriteMember(__instance, "text", num.ToString());
				Plugin.LogSource.LogWarning("[Overlay] Converted day counter \"" + value + "\" to \"" + num + "\".");
			}
			finally
			{
				_insideUITextRewrite = false;
			}
		}
		catch
		{
		}
	}

	private static void AfterMenuDisableForBadge()
	{
		_menuTearingDown = true;
		HideVersionBadge();
	}

	private static bool IsRomanNumeral(string value)
	{
		foreach (char c in value)
		{
			if (c != 'I' && c != 'V' && c != 'X' && c != 'L' && c != 'C' && c != 'D' && c != 'M')
			{
				return false;
			}
		}
		return true;
	}

	private static int ParseRomanNumeral(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return 0;
		}
		value = value.ToUpperInvariant();
		if (!IsRomanNumeral(value))
		{
			return 0;
		}
		int num = 0;
		int num2 = 0;
		for (int num3 = value.Length - 1; num3 >= 0; num3--)
		{
			int num4 = value[num3] switch
			{
				'I' => 1, 
				'V' => 5, 
				'X' => 10, 
				'L' => 50, 
				'C' => 100, 
				'D' => 500, 
				'M' => 1000, 
				_ => 0, 
			};
			if (num4 == 0)
			{
				return 0;
			}
			if (num4 < num2)
			{
				num -= num4;
			}
			else
			{
				num += num4;
				num2 = num4;
			}
		}
		if (num <= 0 || num >= 10000)
		{
			return 0;
		}
		return num;
	}

	private static void ShowVersionBadge(object menu)
	{
		if (!AppearanceFlowSettings.OverlayBadge)
		{
			return;
		}
		try
		{
			if (_badgeClone != null)
			{
				if (IsUnityAlive(_badgeClone))
				{
					PositionBadgeClone(menu);
					return;
				}
				_badgeClone = null;
				_menuButtonLabel = null;
				_badgeWaitingLogged = false;
			}
			if (_menuButtonLabel == null || !IsUnityAlive(_menuButtonLabel))
			{
				_menuButtonLabel = null;
				FindVersionLabel();
			}
			EnsureBadgeClone(menu);
		}
		catch (Exception exception)
		{
			LogFaultOnce("version badge display", exception);
		}
	}

	private static void EnsureVersionBadgeClone(object versionLabel)
	{
		if (_badgeClone != null && _badgeCloneUsesVersionLabel && IsUnityAlive(_badgeClone) && _versionBadgeSource != null && IsUnityAlive(_versionBadgeSource) && AreSameUnityObject(_versionBadgeSource, versionLabel))
		{
			PositionVersionBadgeClone(versionLabel, _badgeClone);
			return;
		}
		DestroyUnityObject(_badgeClone);
		_badgeClone = null;
		_badgeCloneUsesVersionLabel = false;
		_versionBadgeSource = null;
		object obj = ReadMember(CastIl2CppObject(ReadMember(versionLabel, "transform"), "UnityEngine.RectTransform"), "parent");
		MethodInfo methodInfo = FindPanelInstantiate();
		if (obj == null || methodInfo == null)
		{
			return;
		}
		object instance = methodInfo.Invoke(null, new object[3] { versionLabel, obj, true });
		instance = CastIl2CppObject(instance, versionLabel.GetType());
		if (instance != null)
		{
			WriteMember(instance, "text", BadgeLineFor(instance));
			ForceLabelOverflow(instance);
			object obj2 = CastIl2CppObject(ReadMember(instance, "transform"), "UnityEngine.RectTransform");
			try
			{
				SetGameObjectActive(CastIl2CppObject(ReadMember(instance, "gameObject"), "UnityEngine.GameObject"), active: true);
				obj2.GetType().GetMethod("SetAsLastSibling", Type.EmptyTypes)?.Invoke(obj2, null);
			}
			catch
			{
			}
			_badgeClone = instance;
			_badgeCloneUsesVersionLabel = true;
			_versionBadgeSource = versionLabel;
			PositionVersionBadgeClone(versionLabel, instance);
			Plugin.LogSource.LogWarning("[Overlay] Mod badge cloned from the vanilla version caption with measured left-edge and line-gap alignment.");
		}
	}

	private static void PositionVersionBadgeClone(object versionLabel, object clone)
	{
		try
		{
			if (versionLabel != null && clone != null && IsUnityAlive(versionLabel) && IsUnityAlive(clone))
			{
				object instance = CastIl2CppObject(ReadMember(versionLabel, "transform"), "UnityEngine.RectTransform");
				object instance2 = CastIl2CppObject(ReadMember(clone, "transform"), "UnityEngine.RectTransform");
				object instance3 = ReadMember(instance, "position");
				object instance4 = ReadMember(instance, "lossyScale");
				float num = Math.Abs(Convert.ToSingle(ReadMember(instance4, "x")));
				float num2 = Math.Abs(Convert.ToSingle(ReadMember(instance4, "y")));
				float num3 = ReadPositiveFloat(versionLabel, "preferredWidth", 0f);
				float num4 = ReadPositiveFloat(clone, "preferredWidth", num3);
				float num5 = ReadPositiveFloat(versionLabel, "preferredHeight", ReadPositiveFloat(versionLabel, "fontSize", 20f));
				float val = ReadPositiveFloat(clone, "preferredHeight", num5);
				float num6 = HorizontalAlignmentFactor(versionLabel);
				float num7 = (num4 - num3) * num * num6;
				float num8 = Math.Max(num5, val) * num2 * (1f + Math.Max(0f, AppearanceFlowSettings.BadgeLineGapRatio));
				WriteMember(instance2, "position", MakeVectorLike(ReadMember(instance2, "position"), Convert.ToSingle(ReadMember(instance3, "x")) + num7, Convert.ToSingle(ReadMember(instance3, "y")) - num8, Convert.ToSingle(ReadMember(instance3, "z"))));
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("version badge alignment", exception);
		}
	}

	private static float ReadPositiveFloat(object instance, string member, float fallback)
	{
		try
		{
			float num = Convert.ToSingle(ReadMember(instance, member));
			return (num > 0.0001f) ? num : fallback;
		}
		catch
		{
			return fallback;
		}
	}

	private static float HorizontalAlignmentFactor(object label)
	{
		try
		{
			object obj = ReadMember(label, "alignment");
			string text = Convert.ToString(obj) ?? string.Empty;
			if (text.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return 1f;
			}
			if (text.IndexOf("Center", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Midline", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return 0.5f;
			}
			if (text.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return 0f;
			}
			int num = Convert.ToInt32(obj);
			if ((obj.GetType().FullName ?? string.Empty).IndexOf("TextAnchor", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return (float)(num % 3) * 0.5f;
			}
			if ((num & 4) != 0)
			{
				return 1f;
			}
			if ((num & 2) != 0)
			{
				return 0.5f;
			}
		}
		catch
		{
		}
		return 0f;
	}

	private static void EnsureBadgeClone(object menu)
	{
		if (_badgeClone != null)
		{
			return;
		}
		if (_menuButtonLabel == null)
		{
			if (!_badgeWaitingLogged)
			{
				_badgeWaitingLogged = true;
				Plugin.LogSource.LogWarning("[Overlay] Badge waiting for anchor labels: button=" + (_menuButtonLabel != null) + ".");
			}
			return;
		}
		try
		{
			if (!IsUnityAlive(_menuButtonLabel))
			{
				_menuButtonLabel = null;
				return;
			}
			try
			{
				Convert.ToString(ReadMember(_menuButtonLabel, "text"));
			}
			catch
			{
				_menuButtonLabel = null;
				return;
			}
			object obj2 = ReadMember(menu, "remoteUserPanel");
			if (obj2 == null || !IsUnityAlive(obj2))
			{
				return;
			}
			object obj3 = ReadMember(CastIl2CppObject(ReadMember(obj2, "transform"), "UnityEngine.RectTransform"), "parent");
			if (obj3 == null)
			{
				return;
			}
			MethodInfo methodInfo = FindPanelInstantiate();
			if (methodInfo == null)
			{
				return;
			}
			object instance;
			try
			{
				instance = methodInfo.Invoke(null, new object[3] { _menuButtonLabel, obj3, true });
			}
			catch
			{
				_menuButtonLabel = null;
				_badgeWaitingLogged = false;
				int tickCount = Environment.TickCount;
				if (tickCount - _lastBadgeCloneFaultTick >= 30000)
				{
					_lastBadgeCloneFaultTick = tickCount;
					Plugin.LogSource.LogWarning("[Overlay] Badge donor went stale; recapturing from the open menu.");
				}
				return;
			}
			instance = CastIl2CppObject(instance, _menuButtonLabel.GetType());
			if (instance == null)
			{
				return;
			}
			WriteMember(instance, "text", BadgeLineFor(instance));
			object obj5 = CastIl2CppObject(ReadMember(instance, "transform"), "UnityEngine.RectTransform");
			try
			{
				SetGameObjectActive(CastIl2CppObject(ReadMember(instance, "gameObject"), "UnityEngine.GameObject"), active: true);
				obj5.GetType().GetMethod("SetAsLastSibling", Type.EmptyTypes)?.Invoke(obj5, null);
			}
			catch
			{
			}
			string[] array = new string[2] { "horizontalOverflow", "verticalOverflow" };
			foreach (string name in array)
			{
				try
				{
					object obj7 = ReadMember(instance, name);
					if (obj7 != null)
					{
						WriteMember(instance, name, Enum.ToObject(obj7.GetType(), 1));
					}
				}
				catch
				{
				}
			}
			try
			{
				object value = Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), 0.97f, 0.97f, 0.99f, 0.26f);
				WriteMember(instance, "color", value);
			}
			catch
			{
			}
			try
			{
				float num = ((AppearanceFlowSettings.BadgeFontSize > 0f) ? (AppearanceFlowSettings.BadgeFontSize * AppearanceFlowSettings.BadgeScale) : (Convert.ToSingle(ReadMember(instance, "fontSize")) * AppearanceFlowSettings.BadgeScale));
				WriteMember(instance, "fontSize", num);
			}
			catch
			{
			}
			_badgeClone = instance;
			_badgeCloneUsesVersionLabel = false;
			_versionBadgeSource = null;
			PositionBadgeClone(menu);
			Plugin.LogSource.LogWarning("[Overlay] Mod badge cloned beside the banners.");
		}
		catch (Exception exception)
		{
			int tickCount2 = Environment.TickCount;
			if (tickCount2 - _lastBadgeCloneFaultTick >= 30000)
			{
				_lastBadgeCloneFaultTick = tickCount2;
				Plugin.LogSource.LogWarning("[Overlay] Badge clone retrying: " + Unwrap(exception));
			}
		}
	}

	private static void PositionBadgeClone(object menu)
	{
		if (_menuTearingDown)
		{
			return;
		}
		if (_badgeClone == null || !IsUnityAlive(_badgeClone))
		{
			_badgeClone = null;
			return;
		}
		try
		{
			object obj = ReadMember(menu, "remoteUserPanel");
			if (obj == null || !IsUnityAlive(obj))
			{
				return;
			}
			object instance = CastIl2CppObject(ReadMember(obj, "transform"), "UnityEngine.RectTransform");
			object instance2 = ReadMember(instance, "position");
			object instance3 = ReadMember(instance, "lossyScale");
			object instance4 = ReadMember(instance, "rect");
			float num = Convert.ToSingle(ReadMember(instance3, "x")) * Convert.ToSingle(ReadMember(instance4, "width"));
			float num2 = Convert.ToSingle(ReadMember(instance3, "y")) * Convert.ToSingle(ReadMember(instance4, "height"));
			float num3 = Convert.ToSingle(ReadMember(instance2, "x")) + num * AppearanceFlowSettings.BadgeXRatio;
			float y = Convert.ToSingle(ReadMember(instance2, "y")) - num2 * AppearanceFlowSettings.BadgeYMultiplier;
			object instance5 = CastIl2CppObject(ReadMember(_badgeClone, "transform"), "UnityEngine.RectTransform");
			WriteMember(instance5, "position", MakeVectorLike(ReadMember(instance5, "position"), num3, y, Convert.ToSingle(ReadMember(instance2, "z"))));
			if (_badgeCloneGap != null && IsUnityAlive(_badgeCloneGap))
			{
				try
				{
					float num4 = Convert.ToSingle(ReadMember(_badgeClone, "preferredWidth"));
					object instance6 = CastIl2CppObject(ReadMember(_badgeCloneGap, "transform"), "UnityEngine.RectTransform");
					WriteMember(instance6, "position", MakeVectorLike(ReadMember(instance6, "position"), num3 + num4 - 14f, y, Convert.ToSingle(ReadMember(instance2, "z"))));
				}
				catch
				{
				}
			}
			if (!_badgeAnchorLogged)
			{
				_badgeAnchorLogged = true;
				Plugin.LogSource.LogWarning("[Overlay] Badge placed banner-relative at " + num3 + ", " + y + " (banner " + num + "x" + num2 + ").");
			}
		}
		catch
		{
		}
	}

	private static void HideVersionBadge()
	{
		DestroyUnityObject(_badgeClone);
		_badgeClone = null;
		_badgeCloneUsesVersionLabel = false;
		_versionBadgeSource = null;
		DestroyUnityObject(_badgeCloneGap);
		_badgeCloneGap = null;
		if (_versionLabel == null)
		{
			return;
		}
		if (!IsUnityAlive(_versionLabel))
		{
			_versionLabel = null;
			_versionLabelQuickScans = 0;
			_versionLabelDeepDone = false;
			return;
		}
		try
		{
			string text = Convert.ToString(ReadMember(_versionLabel, "text"));
			if (!string.IsNullOrEmpty(text) && text.Contains("KINGDOM EIGHT CROWNS"))
			{
				WriteMember(_versionLabel, "text", StripBadgeLines(text));
			}
		}
		catch
		{
		}
		_versionLabel = null;
	}

	private static string BadgeLineFor(object label)
	{
		string text = BadgeSuffixFor(label);
		if (text.Length <= 0 || text[0] != '\n')
		{
			return text;
		}
		return text.Substring(1);
	}

	private static string BadgeSuffixFor(object label)
	{
		try
		{
			if ((label.GetType().FullName ?? string.Empty).IndexOf("TMPro", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return RichBadgeSuffix;
			}
			if (EnableRichText(label))
			{
				return RichBadgeSuffix;
			}
		}
		catch
		{
		}
		return PlainBadgeSuffix;
	}

	private static bool EnableRichText(object label)
	{
		string[] array = new string[2] { "richText", "supportRichText" };
		foreach (string name in array)
		{
			try
			{
				object obj = ReadMember(label, name);
				if (obj == null)
				{
					continue;
				}
				if (!Convert.ToBoolean(obj))
				{
					WriteMember(label, name, true);
				}
				return true;
			}
			catch
			{
			}
		}
		return false;
	}

	private static string StripBadgeLines(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		int num = text.IndexOf("\nKINGDOM EIGHT CROWNS", StringComparison.Ordinal);
		if (num < 0)
		{
			return text;
		}
		int num2 = text.IndexOf('\n', num + 1);
		if (num2 >= 0)
		{
			return text.Remove(num, num2 - num);
		}
		return text.Substring(0, num);
	}

	private static void ForceLabelOverflow(object label)
	{
		string[] array = new string[2] { "horizontalOverflow", "verticalOverflow" };
		foreach (string name in array)
		{
			try
			{
				object obj = ReadMember(label, name);
				if (obj != null)
				{
					WriteMember(label, name, Enum.ToObject(obj.GetType(), 1));
				}
			}
			catch
			{
			}
		}
		try
		{
			object obj3 = ReadMember(label, "overflowMode");
			if (obj3 != null)
			{
				WriteMember(label, "overflowMode", Enum.ToObject(obj3.GetType(), 0));
			}
		}
		catch
		{
		}
	}

	private static bool IsVersionLabel(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 160)
		{
			return false;
		}
		string text = value.ToUpperInvariant();
		if (text.Contains("EIGHT CROWNS"))
		{
			return false;
		}
		if (text.Contains("KINGDOM TWO CROWNS") && text.IndexOfAny(new char[3] { '0', '2', 'R' }) >= 0)
		{
			return true;
		}
		if (text.Contains("CROWNS"))
		{
			return text.IndexOfAny(new char[11]
			{
				'0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
				'R'
			}) >= 0;
		}
		return false;
	}

	private static object FindVersionLabel()
	{
		List<string> list = new List<string>();
		int num = 0;
		Type[] array = new Type[4]
		{
			FindLoadedType("UnityEngine.UI.Text"),
			FindLoadedType("TMPro.TextMeshProUGUI"),
			FindLoadedType("TMPro.TextMeshPro"),
			FindLoadedType("TMPro.TMP_Text")
		};
		foreach (Type type in array)
		{
			if (type == null)
			{
				continue;
			}
			try
			{
				MethodInfo methodInfo = FindLoadedType("UnityEngine.Resources")?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
				{
					if (method.Name != "FindObjectsOfTypeAll" || method.IsGenericMethod)
					{
						return false;
					}
					ParameterInfo[] parameters = method.GetParameters();
					return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
				});
				if (methodInfo == null)
				{
					continue;
				}
				object obj = ToIl2CppType(type) ?? type;
				object obj2 = methodInfo.Invoke(null, new object[1] { obj });
				if (obj2 == null)
				{
					continue;
				}
				foreach (object item in (IEnumerable)obj2)
				{
					if (item == null)
					{
						continue;
					}
					try
					{
						string text = Convert.ToString(ReadMember(item, "text"));
						num++;
						if (IsVersionLabel(text))
						{
							return item;
						}
						CaptureMenuButtonFont(item, text);
						if (!string.IsNullOrEmpty(text) && list.Count < 60)
						{
							list.Add(item.GetType().FullName + " = \"" + ((text.Length > 72) ? text.Substring(0, 72) : text) + "\"");
						}
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}
		if (!_versionLabelSamplesLogged && num > 0)
		{
			_versionLabelSamplesLogged = true;
			Plugin.LogSource.LogWarning("[Overlay] Version label scan saw " + num + " labels without a match:");
			foreach (string item2 in list)
			{
				Plugin.LogSource.LogWarning("[Overlay]   " + item2);
			}
		}
		return null;
	}

	private static bool IsVersionCaption(string value)
	{
		if (string.IsNullOrEmpty(value) || value.Length > 128)
		{
			return false;
		}
		string text = value.ToUpperInvariant();
		if (IsVersionLabel(value))
		{
			return true;
		}
		if (!text.Contains("EIGHT CROWNS"))
		{
			if (!value.Contains("22452") && !value.Contains("2.1.4"))
			{
				return text.Contains("CROWNS");
			}
			return true;
		}
		return false;
	}

	private static object DeepProbeVersionLabel()
	{
		Type type = FindLoadedType("UnityEngine.Component");
		MethodInfo? obj = FindLoadedType("UnityEngine.Resources")?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
		{
			if (method.Name != "FindObjectsOfTypeAll" || method.IsGenericMethod)
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
		});
		object obj2 = ((type == null) ? null : (ToIl2CppType(type) ?? type));
		object obj3 = obj?.Invoke(null, new object[1] { obj2 });
		if (obj3 == null)
		{
			return null;
		}
		HashSet<string> hashSet = new HashSet<string>();
		int num = 0;
		foreach (object item in (IEnumerable)obj3)
		{
			if (item == null)
			{
				continue;
			}
			try
			{
				string fullName = item.GetType().FullName;
				if (fullName == null || hashSet.Contains(fullName))
				{
					continue;
				}
				try
				{
					object obj4 = ReadMember(item, "gameObject");
					string text = ((obj4 == null) ? null : Convert.ToString(ReadMember(obj4, "name")));
					if (!string.IsNullOrEmpty(text) && text.IndexOf("version", StringComparison.OrdinalIgnoreCase) >= 0 && num < 6)
					{
						num++;
						Plugin.LogSource.LogWarning("[Overlay] Scouted component on GameObject \"" + text + "\" of type " + fullName + ".");
					}
				}
				catch
				{
				}
				bool flag = false;
				string[] array = new string[9] { "text", "m_text", "label", "caption", "content", "value", "stringValue", "key", "format" };
				foreach (string text2 in array)
				{
					try
					{
						if (ReadMember(item, text2) is string text3)
						{
							flag = true;
							if (IsVersionCaption(text3))
							{
								Plugin.LogSource.LogWarning("[Overlay] Deep probe matched member \"" + text2 + "\" on " + fullName + " with \"" + ((text3.Length > 64) ? text3.Substring(0, 64) : text3) + "\".");
								return item;
							}
							break;
						}
					}
					catch
					{
					}
				}
				if (!flag)
				{
					hashSet.Add(fullName);
				}
			}
			catch
			{
			}
		}
		return null;
	}

	private static void CaptureMenuButtonFont(object label, string value)
	{
		if (_menuButtonLabel != null || string.IsNullOrEmpty(value))
		{
			return;
		}
		string text = value.Trim();
		if (text.Length < 2 || ParseRomanNumeral(text) > 0)
		{
			return;
		}
		try
		{
			object obj = ReadMember(label, "font");
			if (obj != null)
			{
				_menuButtonFont = obj;
				_menuButtonLabel = label;
				ManualLogSource logSource = Plugin.LogSource;
				string? fullName = obj.GetType().FullName;
				logSource.LogWarning("[Overlay] Captured menu label font \"" + ((fullName != null && fullName.Contains("TMP")) ? (Convert.ToString(ReadMember(obj, "name")) + " (TMP)") : (Convert.ToString(ReadMember(obj, "name")) + " (legacy)")) + "\" for nicknames and the badge.");
			}
		}
		catch
		{
		}
	}

	private static object ToIl2CppType(Type managedType)
	{
		try
		{
			return ((from assembly in AppDomain.CurrentDomain.GetAssemblies()
				select assembly.GetType("Il2CppInterop.Runtime.Il2CppType")).FirstOrDefault((Type type) => type != null)?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
			{
				if (method.Name != "From" || method.IsGenericMethod)
				{
					return false;
				}
				ParameterInfo[] parameters = method.GetParameters();
				return parameters.Length == 1 && parameters[0].ParameterType == typeof(Type);
			}))?.Invoke(null, new object[1] { managedType });
		}
		catch
		{
			return null;
		}
	}

	private static object MakeVector2(float x, float y)
	{
		Type type = FindLoadedType("UnityEngine.Vector2");
		if (type == null)
		{
			throw new TypeLoadException("UnityEngine.Vector2 missing.");
		}
		return Activator.CreateInstance(type, x, y);
	}

	private static object InvokeTransformGetChild(object transform, int index)
	{
		return transform.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
		{
			if (method.Name != "GetChild")
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
		})?.Invoke(transform, new object[1] { index });
	}

	private static void AfterSteamSetTargetId(object __instance, object[] __args, bool __result)
	{
		try
		{
			AfterSteamSetTargetIdCore(__instance, __args, __result);
		}
		catch (Exception exception)
		{
			LogFaultOnce("peer registration", exception);
		}
	}

	private static void AfterSteamSetTargetIdCore(object __instance, object[] __args, bool __result)
	{
		if (!__result || __args == null || __args.Length != 2 || !Convert.ToBoolean(__args[1]))
		{
			return;
		}
		if (ConnectionsEqual(__instance, _rejectedConnectionDuringAccept))
		{
			Plugin.LogSource.LogWarning("[Dynamic session] Did not register the incoming Steam transport because the native host rejected this join" + (string.IsNullOrWhiteSpace(_rejectedConnectionReason) ? "." : (" (" + _rejectedConnectionReason + ").")));
			return;
		}
		lock (Gate)
		{
			if (HostPeers.TryGetValue(__instance, out var value))
			{
				if (ConnectionsEqual(_activeCatchupConnection, __instance))
				{
					_activeCatchupConnection = value.Connection;
				}
				return;
			}
			object peerToken = __args[0];
			PeerState existing = FindPeerByTokenUnsafe(peerToken);
			if (existing != null)
			{
				object connection = existing.Connection;
				bool flag = ConnectionsEqual(_activeCatchupConnection, connection) || ConnectionsEqual(_activeCatchupConnection, __instance);
				object[] array = (from pair in HostPeers
					where pair.Value == existing || SteamIdsEqual(pair.Value.PeerToken, peerToken)
					select pair.Key).ToArray();
				foreach (object key in array)
				{
					HostPeers.Remove(key);
				}
				existing.Connection = __instance;
				existing.AssignmentSent = false;
				existing.CatchupPending = true;
				existing.BootstrapRouteLogged = false;
				existing.BootstrapFlushedLogged = false;
				existing.JoiningOverlaySuppressed = IsSuppressedConnectingPeer(peerToken);
				existing.ReceivedPackets = 0;
				existing.AcceptedAt = Stopwatch.GetTimestamp();
				existing.LastPacketAt = 0L;
				existing.CatchupRequestedAt = 0L;
				HostPeers[__instance] = existing;
				AssignDynamicBody(existing.PlayerId, existing.BodyNetId);
				RebindCatchupRoutesUnsafe(connection, __instance);
				if (flag)
				{
					_activeCatchupConnection = __instance;
				}
				if (_hostRouter != null && ConnectionsEqual(ReadMember(_hostRouter, "connection"), connection))
				{
					WriteMember(_hostRouter, "connection", __instance);
				}
				Plugin.LogSource.LogWarning("[Dynamic session] Rebound " + existing.Description + " to its existing Player " + (existing.PlayerId + 1) + " slot after a native connection-wrapper refresh. Catch-up ownership moved to transport " + DescribeTransport(__instance) + "; no duplicate body or slot was created.");
				return;
			}
			int num2 = FindFreePlayerId();
			if (num2 < 1)
			{
				Plugin.LogSource.LogError("[Dynamic session] An additional remote peer attempted to join after all seven remote slots were occupied. The session already contains the maximum seven remote players, so no logical slot was assigned.");
				return;
			}
			int bodyNetId = ((num2 == 1) ? Convert.ToInt32(_getNativePlayerTwoNetId.Invoke(null, null)) : Convert.ToInt32(_reserveBodyNetId.Invoke(null, null)));
			SetLocalBodyOwner(0);
			AssignDynamicBody(num2, bodyNetId);
			PeerState peerState = new PeerState(__instance, num2, bodyNetId, peerToken, DescribePeerToken(peerToken));
			peerState.CatchupPending = true;
			peerState.JoiningOverlaySuppressed = IsSuppressedConnectingPeer(peerToken);
			HostPeers.Add(__instance, peerState);
			if (ConnectionsEqual(_activeCatchupConnection, __instance))
			{
				_activeCatchupConnection = __instance;
			}
			Plugin.LogSource.LogWarning("[Dynamic session] Accepted " + peerState.Description + " as Player " + (num2 + 1) + " with body NetID " + bodyNetId + " on transport " + DescribeTransport(__instance) + ". Occupied players=" + (HostPeers.Count + 1) + "/8; stage=accepted-awaiting-first-packet.");
		}
		RefreshCurrentMenu();
	}

	private static void BeforeAcceptP2PConnection(object __instance, object[] __args)
	{
		try
		{
			_acceptingRouter = __instance;
			_hostRouter = __instance;
			_rejectedConnectionDuringAccept = null;
			_rejectedConnectionReason = null;
			_acceptingPeerToken = ReadAcceptingPeerToken(__args);
			_connectionBeforeAccept = ReadMember(__instance, "connection");
		}
		catch (Exception exception)
		{
			_acceptingRouter = null;
			_acceptingPeerToken = null;
			_connectionBeforeAccept = null;
			LogFaultOnce("P2P acceptance context", exception);
		}
	}

	private static object ReadAcceptingPeerToken(object[] arguments)
	{
		if (arguments == null || arguments.Length != 1 || arguments[0] == null)
		{
			return null;
		}
		object obj = arguments[0];
		try
		{
			return ReadMember(obj, "m_steamIDRemote") ?? obj;
		}
		catch (MissingMemberException)
		{
			return obj;
		}
	}

	private static void AfterAcceptP2PConnection()
	{
		try
		{
			if (_rejectedConnectionDuringAccept != null)
			{
				bool flag;
				lock (Gate)
				{
					flag = RepairHostRouterConnection(_rejectedConnectionDuringAccept);
				}
				if (flag)
				{
					Plugin.LogSource.LogWarning("[Dynamic session] Restored the host router to a surviving peer after the rejected transport finished its native acceptance callback.");
				}
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("rejected peer router restoration", exception);
		}
		finally
		{
			_insideAdditionalPeerAcceptance = false;
			_suppressedConnectingPeerToken = null;
			_acceptingRouter = null;
			_acceptingPeerToken = null;
			_connectionBeforeAccept = null;
			_rejectedConnectionDuringAccept = null;
			_rejectedConnectionReason = null;
		}
	}

	private static void BeforeServerHandleOnConnect(object __instance, ref bool __state)
	{
		__state = false;
		try
		{
			if (!ConnectionsEqual(__instance, _acceptingRouter) || _connectionBeforeAccept == null)
			{
				return;
			}
			int num = ConnectedRemoteCount();
			if (num <= 0)
			{
				return;
			}
			object incoming = ReadMember(__instance, "connection");
			if (incoming == null || ConnectionsEqual(incoming, _connectionBeforeAccept))
			{
				return;
			}
			PeerState reconnectingPeer = null;
			bool flag;
			lock (Gate)
			{
				object acceptingPeerToken = _acceptingPeerToken;
				reconnectingPeer = ((acceptingPeerToken == null) ? HostPeers.Values.FirstOrDefault((PeerState peer) => ConnectionsEqual(peer.Connection, incoming)) : FindPeerByTokenUnsafe(acceptingPeerToken));
				flag = HostPeers.Values.Any((PeerState peer) => peer.CatchupPending && peer != reconnectingPeer);
			}
			if (num >= 7 && reconnectingPeer == null)
			{
				RejectCurrentRegistration(incoming, "the eight-player session is full");
				return;
			}
			if (flag)
			{
				RejectCurrentRegistration(incoming, "another player is still completing native catch-up");
				return;
			}
			WriteMember(__instance, "host_hasClientConnected", false);
			WriteMember(__instance, "host_hasClientCaughtUp", false);
			_insideAdditionalPeerAcceptance = true;
			__state = true;
			Plugin.LogSource.LogWarning("[Dynamic session] Opened one isolated native connection slot for Player " + ((reconnectingPeer == null) ? (num + 2) : (reconnectingPeer.PlayerId + 1)) + "; the new peer starts uncaught-up while existing ready peers remain registered in the packet pump.");
		}
		catch (Exception exception)
		{
			LogFaultOnce("Player 3+ native acceptance", exception);
		}
	}

	private static void AfterServerHandleOnConnect(object __instance, bool __state)
	{
		if (!__state)
		{
			return;
		}
		try
		{
			object obj = ReadMember(__instance, "connection");
			if (!Convert.ToBoolean(ReadMember(__instance, "host_hasClientConnected")) || obj == null)
			{
				RejectCurrentRegistration(obj, "the stock host rejected the current campaign state");
				Plugin.LogSource.LogWarning("[Dynamic session] The stock host rejected the isolated connection for its normal campaign-state reason. The transport was left unregistered without changing the surviving peers.");
			}
			else
			{
				_activeCatchupConnection = obj;
				FinishAdditionalPeerCurtainImmediately();
				Plugin.LogSource.LogWarning("[Dynamic session] Isolated the newly accepted peer as the router's active uncaught-up connection. Live-world traffic continues only to ready players; handshake and catch-up traffic is routed only to this joining peer.");
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("native connection-state completion", exception);
		}
		finally
		{
			_insideAdditionalPeerAcceptance = false;
		}
	}

	private static void FinishAdditionalPeerCurtainImmediately()
	{
		ForceCloseClientJoiningCurtain("Player 3+ acceptance stack");
	}

	private static void RejectCurrentRegistration(object connection, string reason)
	{
		_rejectedConnectionDuringAccept = connection;
		_rejectedConnectionReason = reason;
	}

	private static bool BeforeSteamPollMessages(object __instance)
	{
		try
		{
			object[] peers;
			lock (Gate)
			{
				if (HostPeers.Count < 1 || !HostPeers.ContainsKey(__instance))
				{
					return true;
				}
				peers = (from peer in HostPeers.Values
					orderby peer.PlayerId
					select peer.Connection).ToArray();
			}
			PumpSteamPackets(peers);
			FlushSteamPeerQueues(peers);
			if (!_packetPumpReadyLogged)
			{
				_packetPumpReadyLogged = true;
				Plugin.LogSource.LogWarning("[Dynamic session] The shared Steam inbound queue is now owned from Player 2 onward and dispatched by sender. Every peer's native outgoing buffer is then flushed separately, so adding a later player cannot be the event that finally releases Player 2 traffic or another peer's STEAM_CONNECT bootstrap.");
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("multi-peer Steam packet pump", exception);
		}
		return false;
	}

	private static void FlushSteamPeerQueues(object[] peers)
	{
		foreach (object obj in peers)
		{
			if (obj == null)
			{
				continue;
			}
			short num = Convert.ToInt16(ReadMember(obj, "_numMessages"));
			int num2 = Convert.ToInt32(ReadMember(obj, "_messageGroupSize"));
			if (num > 0 && num2 > 2)
			{
				object obj2 = ReadMember(obj, "_outgoingDataBuffer");
				object obj3 = ReadMember(obj, "_targetID");
				if (obj2 == null || obj3 == null)
				{
					throw new InvalidOperationException("A queued Steam peer is missing its outgoing buffer or target.");
				}
				if (!Convert.ToBoolean(_steamFlush.Invoke(obj, new object[3] { obj2, num2, obj3 })))
				{
					throw new IOException("Steam rejected a queued peer buffer containing " + num + " message(s) and " + num2 + " bytes.");
				}
				PeerState peerState = FindPeerByConnection(obj);
				if (peerState != null && peerState.BootstrapRouteLogged && !peerState.BootstrapFlushedLogged)
				{
					peerState.BootstrapFlushedLogged = true;
					Plugin.LogSource.LogWarning("[Dynamic session] Flushed Player " + (peerState.PlayerId + 1) + "'s queued native bootstrap through that player's own Steam target (messages=" + num + ", bytes=" + num2 + ").");
				}
			}
		}
	}

	private static void AfterSteamPollMessages()
	{
		try
		{
			TickPeerWatchdog();
		}
		catch (Exception exception)
		{
			LogFaultOnce("peer-scoped join watchdog", exception);
		}
	}

	private static void PumpSteamPackets(object[] peers)
	{
		if (peers == null || peers.Length == 0)
		{
			return;
		}
		object obj = ReadMember(peers[0], "_incomingDataBuffer");
		if (obj == null)
		{
			throw new MissingMemberException("The Steam incoming packet buffer is unavailable.");
		}
		for (int i = 0; i < 256; i++)
		{
			object[] array = new object[2] { 0u, 0 };
			if (!Convert.ToBoolean(_isP2PPacketAvailable.Invoke(null, array)))
			{
				break;
			}
			uint num = Convert.ToUInt32(array[0]);
			if (num > 512000)
			{
				throw new InvalidDataException("Steam reported an oversized P2P packet (" + num + " bytes; game limit is 512000).");
			}
			ParameterInfo parameterInfo = _readP2PPacket.GetParameters()[3];
			Type type = (parameterInfo.ParameterType.IsByRef ? parameterInfo.ParameterType.GetElementType() : parameterInfo.ParameterType);
			object[] array2 = new object[5]
			{
				obj,
				512000u,
				0u,
				(type == null) ? null : Activator.CreateInstance(type),
				0
			};
			if (!Convert.ToBoolean(_readP2PPacket.Invoke(null, array2)))
			{
				throw new InvalidOperationException("Steam exposed a packet but did not return it.");
			}
			int num2 = checked((int)Convert.ToUInt32(array2[2]));
			object obj2 = array2[3];
			PeerState peerState = FindPeerByToken(obj2);
			if (peerState == null)
			{
				LogUnknownSteamPeerOnce(obj2);
				continue;
			}
			if (peerState.ReceivedPackets == 0)
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Received the first native packet from Player " + (peerState.PlayerId + 1) + " (SteamId=" + DescribePeerToken(obj2) + ", transport=" + DescribeTransport(peerState.Connection) + "); sender-scoped dispatch is active.");
			}
			peerState.ReceivedPackets++;
			peerState.LastPacketAt = Stopwatch.GetTimestamp();
			if (peerState.ReceivedPackets == 100 || peerState.ReceivedPackets == 1000 || peerState.ReceivedPackets % 5000 == 0)
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Player " + (peerState.PlayerId + 1) + " inbound traffic sample: " + peerState.ReceivedPackets + " native packet(s) processed from that peer.");
			}
			RouterScopeState state = BeginHostPeerScope(peerState);
			try
			{
				_steamReceive.Invoke(peerState.Connection, new object[2] { obj, num2 });
			}
			finally
			{
				EndHostPeerScope(state);
			}
			RelayPacketToReadyPeers(peerState, obj, num2);
		}
	}

	private static void RelayPacketToReadyPeers(PeerState source, object buffer, int bytesRead)
	{
		if (bytesRead <= 8)
		{
			return;
		}
		lock (Gate)
		{
			if (source.CatchupPending)
			{
				return;
			}
		}
		PeerState[] array;
		lock (Gate)
		{
			array = (from target in HostPeers.Values
				where target != source && !target.CatchupPending && target.Connection != null
				orderby target.PlayerId
				select target).ToArray();
		}
		PeerState[] array2 = array;
		foreach (PeerState peerState in array2)
		{
			try
			{
				object obj = ReadMember(peerState.Connection, "_targetID");
				if (obj != null)
				{
					Type parameterType = _sendP2PPacket.GetParameters()[3].ParameterType;
					Convert.ToBoolean(_sendP2PPacket.Invoke(null, new object[5]
					{
						obj,
						buffer,
						(uint)bytesRead,
						Enum.ToObject(parameterType, 2),
						0
					}));
					string item = source.PlayerId + "->" + peerState.PlayerId;
					if (LoggedRelayRoutes.Add(item))
					{
						Plugin.LogSource.LogWarning("[Relay] Player " + (source.PlayerId + 1) + "'s gameplay packets are now relayed to Player " + (peerState.PlayerId + 1) + ".");
					}
				}
			}
			catch (Exception exception)
			{
				LogFaultOnce("packet relay", exception);
				break;
			}
		}
	}

	private static PeerState FindPeerByToken(object sender)
	{
		lock (Gate)
		{
			return FindPeerByTokenUnsafe(sender);
		}
	}

	private static PeerState FindPeerByTokenUnsafe(object sender)
	{
		return HostPeers.Values.FirstOrDefault((PeerState peer) => SteamIdsEqual(peer.PeerToken, sender));
	}

	private static PeerState FindPeerByConnection(object connection)
	{
		if (connection == null)
		{
			return null;
		}
		lock (Gate)
		{
			if (HostPeers.TryGetValue(connection, out var value))
			{
				return value;
			}
			return HostPeers.Values.FirstOrDefault((PeerState candidate) => ConnectionsEqual(candidate.Connection, connection));
		}
	}

	private static void LogUnknownSteamPeerOnce(object sender)
	{
		string text = "unknown Steam packet sender " + ((sender == null) ? "<null>" : sender.ToString());
		lock (Gate)
		{
			if (!LoggedFaults.Add(text))
			{
				return;
			}
		}
		Plugin.LogSource.LogWarning("[Dynamic session] Dropped one packet from " + text + ". No registered player's queue or lobby state was changed.");
	}

	private static void BeforeSteamReceive(object __instance, ref object __state)
	{
		__state = _receivingConnection;
		_receivingConnection = __instance;
	}

	private static void AfterSteamReceive(object __state)
	{
		_receivingConnection = __state;
	}

	private static bool BeforeSteamSend(object __instance, object[] __args)
	{
		try
		{
			return BeforeSteamSendCore(__instance, __args);
		}
		catch (Exception exception)
		{
			LogFaultOnce("Steam fan-out", exception);
			return true;
		}
	}

	private static void AfterSteamSend(object __instance, object[] __args)
	{
		try
		{
			if (__args == null || __args.Length != 3 || Convert.ToByte(__args[0]) != 20)
			{
				return;
			}
			PeerState value;
			lock (Gate)
			{
				if (!HostPeers.TryGetValue(__instance, out value) || value.PlayerId <= 1 || !value.CatchupPending)
				{
					return;
				}
			}
			if (RestoreReadyHostRouterState())
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Player " + (value.PlayerId + 1) + " queued its native bootstrap on its own transport; the stock router's shared connection/ready flags were returned to Player 2 between joining-peer packets.");
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("steady router state after additional bootstrap", exception);
		}
	}

	private static bool BeforeSteamSendCore(object __instance, object[] __args)
	{
		if (__args == null || __args.Length != 3)
		{
			return true;
		}
		if (_directSend)
		{
			return true;
		}
		byte b = Convert.ToByte(__args[0]);
		if (IsPeerScopedTransportControl(b))
		{
			LogNativeBootstrapRoute(__instance, b);
			return true;
		}
		object[] array;
		lock (Gate)
		{
			if (!HostPeers.ContainsKey(__instance) || HostPeers.Count < 2)
			{
				return true;
			}
			PeerState value = null;
			if (_receivingConnection != null)
			{
				HostPeers.TryGetValue(_receivingConnection, out value);
			}
			array = (_insideCatchupSend ? ((_catchupSendConnection == null || !HostPeers.ContainsKey(_catchupSendConnection)) ? new object[0] : new object[1] { _catchupSendConnection }) : ((value != null && value.CatchupPending) ? new object[1] { value.Connection } : ((value != null && b == 30) ? new object[1] { value.Connection } : ((b != 30) ? (from peer in HostPeers.Values
				where !peer.CatchupPending
				orderby peer.PlayerId
				select peer.Connection).ToArray() : (from peer in HostPeers.Values
				orderby peer.PlayerId
				select peer.Connection).ToArray()))));
		}
		object[] array2 = array;
		for (int num = 0; num < array2.Length; num++)
		{
			DirectSend(array2[num], b, __args[1], Convert.ToBoolean(__args[2]));
		}
		return false;
	}

	private static bool IsPeerScopedTransportControl(byte messageCode)
	{
		if (messageCode != 20 && messageCode != 21)
		{
			return messageCode == 22;
		}
		return true;
	}

	private static void LogNativeBootstrapRoute(object connection, byte messageCode)
	{
		if (messageCode != 20)
		{
			return;
		}
		PeerState value;
		lock (Gate)
		{
			if (!HostPeers.TryGetValue(connection, out value) || value.BootstrapRouteLogged)
			{
				return;
			}
			value.BootstrapRouteLogged = true;
		}
		Plugin.LogSource.LogWarning("[Dynamic session] Kept Player " + (value.PlayerId + 1) + "'s native Steam-connect bootstrap on that player's own transport. The multi-peer poll flushes this connection's native output buffer separately instead of broadcasting or stranding it behind Player 2's queue.");
	}

	private static void BeforeRecvCatchupRequest()
	{
		PeerState peerState = CurrentPeer();
		if (peerState != null)
		{
			lock (Gate)
			{
				peerState.CatchupPending = true;
				peerState.CatchupRequestedAt = Stopwatch.GetTimestamp();
				_activeCatchupConnection = peerState.Connection;
			}
			Plugin.LogSource.LogWarning("[Dynamic session] Player " + (peerState.PlayerId + 1) + " requested native catch-up; its snapshot stream is isolated from all ready players.");
		}
	}

	private static void AfterCreateCatchupIterator(object __result)
	{
		try
		{
			if (__result == null)
			{
				return;
			}
			object value = null;
			lock (Gate)
			{
				if (_insideCatchupSend && _catchupSendConnection != null && HostPeers.ContainsKey(_catchupSendConnection))
				{
					value = _catchupSendConnection;
				}
				else if (_receivingConnection != null && HostPeers.ContainsKey(_receivingConnection))
				{
					value = _receivingConnection;
				}
				else if (_activeCatchupConnection != null && HostPeers.ContainsKey(_activeCatchupConnection))
				{
					value = _activeCatchupConnection;
				}
				CatchupRoutes[__result] = value;
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("catch-up iterator ownership", exception);
		}
	}

	private static void BeforeCatchupMoveNext(object __instance, ref object __state)
	{
		bool insideCatchupSend = _insideCatchupSend;
		object catchupSendConnection = _catchupSendConnection;
		_insideCatchupSend = true;
		object value;
		lock (Gate)
		{
			if (!CatchupRoutes.TryGetValue(__instance, out value))
			{
				value = ((_insideCatchupSend && _catchupSendConnection != null && HostPeers.ContainsKey(_catchupSendConnection)) ? _catchupSendConnection : ((_receivingConnection != null && HostPeers.ContainsKey(_receivingConnection)) ? _receivingConnection : _activeCatchupConnection));
				CatchupRoutes[__instance] = value;
			}
			_catchupSendConnection = value;
		}
		PeerState peer = FindPeerByConnection(value);
		__state = new CatchupRouteState(insideCatchupSend, catchupSendConnection, BeginHostPeerScope(peer));
	}

	private static void AfterCatchupMoveNext(object __instance, object __state, bool __result)
	{
		if (!(__state is CatchupRouteState catchupRouteState))
		{
			_insideCatchupSend = false;
			_catchupSendConnection = null;
			return;
		}
		EndHostPeerScope(catchupRouteState.RouterState);
		_insideCatchupSend = catchupRouteState.PreviousInsideCatchup;
		_catchupSendConnection = catchupRouteState.PreviousConnection;
		if (__result)
		{
			return;
		}
		lock (Gate)
		{
			CatchupRoutes.Remove(__instance);
		}
	}

	private static bool BeforeRecvClientReady(ref bool __state)
	{
		__state = true;
		return true;
	}

	private static void AfterRecvClientReady(bool __state)
	{
		if (!__state)
		{
			return;
		}
		try
		{
			PeerState peer = CurrentPeer();
			if (peer == null)
			{
				return;
			}
			PeerState[] array;
			lock (Gate)
			{
				peer.CatchupPending = false;
				if (ConnectionsEqual(_activeCatchupConnection, peer.Connection))
				{
					_activeCatchupConnection = (from candidate in HostPeers.Values
						where candidate.CatchupPending
						orderby candidate.PlayerId
						select candidate.Connection).FirstOrDefault();
				}
				array = (from candidate in HostPeers.Values.Distinct()
					where !candidate.CatchupPending
					orderby (candidate != peer) ? 1 : 0, candidate.PlayerId
					select candidate).ToArray();
			}
			ActivateDynamicBody(peer.PlayerId);
			PeerState[] array2 = array;
			foreach (PeerState peerState in array2)
			{
				if (peerState == peer)
				{
					PeerState[] array3 = array;
					foreach (PeerState peerState2 in array3)
					{
						SendBodyControl(peerState.Connection, 1308622848, peerState2.PlayerId, peerState2.BodyNetId, "ready roster activation");
					}
				}
				else
				{
					SendBodyControl(peerState.Connection, 1308622848, peer.PlayerId, peer.BodyNetId, "new ready body activation");
				}
			}
			Plugin.LogSource.LogWarning("[Dynamic session] Player " + (peer.PlayerId + 1) + " completed native world catch-up and entered the ready live-traffic set (SteamId=" + DescribePeerToken(peer.PeerToken) + ", transport=" + DescribeTransport(peer.Connection) + ").");
			object[] array4 = ReadAllSlots();
			object[] array5 = array4;
			foreach (object model in array5)
			{
				SendPlayerModel(peer.Connection, model);
			}
			if (array4.Length != 0)
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Delivered " + array4.Length + " stored appearance model(s) to Player " + (peer.PlayerId + 1) + " after its world load.");
			}
			ReassertHostUiAfterAdditionalReady(peer);
		}
		catch (Exception exception)
		{
			LogFaultOnce("ready-peer transition", exception);
		}
	}

	private static void ReassertHostUiAfterAdditionalReady(PeerState peer)
	{
		if (peer != null && peer.PlayerId >= 2)
		{
			ForceCloseClientJoiningCurtain("Player " + (peer.PlayerId + 1) + " ready transition");
			if (IsSuppressedConnectingPeer(peer.PeerToken))
			{
				_suppressedConnectingPeerToken = null;
			}
		}
	}

	private static void BeforeSteamDispose(object __instance)
	{
		try
		{
			BeforeSteamDisposeCore(__instance);
		}
		catch (Exception exception)
		{
			LogFaultOnce("peer cleanup", exception);
		}
	}

	private static void BeforeSteamDisposeCore(object __instance)
	{
		string reason = (_isolatedPeerDispose ? _isolatedDisposeReason : null);
		RemovePeerByConnection(__instance, reason);
	}

	private static bool RemovePeerByConnection(object connection, string reason)
	{
		PeerState value;
		lock (Gate)
		{
			if (!HostPeers.TryGetValue(connection, out value))
			{
				value = HostPeers.Values.FirstOrDefault((PeerState candidate) => ConnectionsEqual(candidate.Connection, connection));
			}
		}
		return RemovePeer(value, reason);
	}

	private static bool RemovePeer(PeerState peer, string reason)
	{
		if (peer == null)
		{
			return false;
		}
		bool flag = false;
		bool catchupPending = peer.CatchupPending;
		lock (Gate)
		{
			object[] keys = (from pair in HostPeers
				where pair.Value == peer || SteamIdsEqual(pair.Value.PeerToken, peer.PeerToken)
				select pair.Key).ToArray();
			object[] array = keys;
			foreach (object key in array)
			{
				flag |= HostPeers.Remove(key);
			}
			if (!flag)
			{
				return false;
			}
			object[] array2 = (from pair in CatchupRoutes
				where keys.Any((object connection) => ConnectionsEqual(pair.Value, connection)) || ConnectionsEqual(pair.Value, peer.Connection)
				select pair.Key).ToArray();
			array = array2;
			foreach (object key2 in array)
			{
				CatchupRoutes.Remove(key2);
			}
			ClearSlot(peer.PlayerId);
			RepairHostRouterAfterRemoval(peer);
			if (ConnectionsEqual(_connectionBeforeAccept, peer.Connection))
			{
				_connectionBeforeAccept = null;
			}
			Plugin.LogSource.LogWarning("[Dynamic session] Removed Player " + (peer.PlayerId + 1) + (string.IsNullOrWhiteSpace(reason) ? " after connection disposal" : (" after " + reason)) + " (SteamId=" + DescribePeerToken(peer.PeerToken) + ", transport=" + DescribeTransport(peer.Connection) + ", clearedCatchupRoutes=" + array2.Length + "). Occupied players=" + (HostPeers.Count + 1) + "/8; the released slot can be reused.");
		}
		try
		{
			RemoveDynamicBody(peer.PlayerId);
			PeerState[] array3;
			lock (Gate)
			{
				array3 = (from candidate in HostPeers.Values.Distinct()
					orderby candidate.PlayerId
					select candidate).ToArray();
			}
			PeerState[] array4 = array3;
			for (int num = 0; num < array4.Length; num++)
			{
				SendBodyControl(array4[num].Connection, 1291845632, peer.PlayerId, 0, "departed body removal");
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("dynamic body departure synchronization", exception);
		}
		if (catchupPending)
		{
			FinishAbortedClientJoin(peer, reason);
		}
		RefreshCurrentMenu();
		return true;
	}

	private static bool BeforeSetClientConnecting(object __instance)
	{
		try
		{
			if (!IsLocalLobbyHost())
			{
				return true;
			}
			_hostGame = __instance;
			if (!_insideAdditionalPeerAcceptance)
			{
				return true;
			}
			_suppressedConnectingPeerToken = _acceptingPeerToken;
			Plugin.LogSource.LogWarning("[Dynamic session] Kept the native Client Joining curtain inactive for Player 3+. The additional peer will handshake and catch up in isolation while the host and every ready client remain visible and playable.");
			return false;
		}
		catch (Exception exception)
		{
			LogFaultOnce("client-joining overlay ownership", exception);
			return true;
		}
	}

	private static bool IsSuppressedConnectingPeer(object peerToken)
	{
		if (_suppressedConnectingPeerToken != null)
		{
			return SteamIdsEqual(_suppressedConnectingPeerToken, peerToken);
		}
		return false;
	}

	private static void FinishAbortedClientJoin(PeerState peer, string reason)
	{
		try
		{
			if (IsLocalLobbyHost() && peer != null)
			{
				bool flag = ForceCloseClientJoiningCurtain("failed Player " + (peer.PlayerId + 1) + " join");
				Plugin.LogSource.LogWarning("[Dynamic session] Recovered the Client Joining UI after Player " + (peer.PlayerId + 1) + " failed before ready" + (string.IsNullOrWhiteSpace(reason) ? "." : (" (" + reason + ").")) + (flag ? " The real CurtainHandler is inactive and host controls were restored." : " The Game callback ran, but the curtain singleton could not be verified inactive."));
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("aborted client-joining overlay cleanup", exception);
		}
	}

	private static bool ForceCloseClientJoiningCurtain(string context)
	{
		bool flag = false;
		if (_hostGame != null && _setClientConnectingFinished != null)
		{
			try
			{
				_setClientConnectingFinished.Invoke(_hostGame, null);
				flag = true;
			}
			catch (Exception exception)
			{
				LogFaultOnce("Game joining-state cleanup during " + context, Unwrap(exception));
			}
		}
		object obj = null;
		try
		{
			obj = ((_curtainInstanceProperty == null) ? null : _curtainInstanceProperty.GetValue(null, null));
		}
		catch (Exception exception2)
		{
			LogFaultOnce("CurtainHandler singleton lookup during " + context, Unwrap(exception2));
		}
		if (obj == null)
		{
			Plugin.LogSource.LogWarning("[Dynamic session] " + context + (flag ? " invoked Game.SetClientConnectingFinished, but " : " had no Game callback and ") + "the CurtainHandler singleton was unavailable for direct verification.");
			return flag;
		}
		TryInvokeCurtainMethod(obj, _hideCurtainModal, null, "modal cleanup", context);
		TryInvokeCurtainMethod(obj, _hideCurtainText, null, "text cleanup", context);
		TryInvokeCurtainMethod(obj, _setCurtainActive, new object[1] { false }, "active-state cleanup", context);
		TryInvokeCurtainMethod(obj, _setCurtainAlpha, new object[1] { 0f }, "alpha cleanup", context);
		try
		{
			bool flag2 = _curtainIsActive != null && Convert.ToBoolean(_curtainIsActive.Invoke(obj, null));
			Plugin.LogSource.LogWarning("[Dynamic session] " + context + (flag2 ? " left CurtainHandler ACTIVE after direct cleanup." : " directly verified CurtainHandler inactive; host input is no longer covered by Client Joining."));
			return !flag2;
		}
		catch (Exception exception3)
		{
			LogFaultOnce("CurtainHandler verification during " + context, Unwrap(exception3));
			return flag;
		}
	}

	private static void TryInvokeCurtainMethod(object curtain, MethodInfo method, object[] arguments, string operation, string context)
	{
		if (method == null)
		{
			return;
		}
		try
		{
			method.Invoke(curtain, arguments);
		}
		catch (Exception exception)
		{
			LogFaultOnce("CurtainHandler " + operation + " during " + context, Unwrap(exception));
		}
	}

	private static void RepairHostRouterAfterRemoval(PeerState removedPeer)
	{
		if (ConnectionsEqual(_activeCatchupConnection, removedPeer.Connection))
		{
			_activeCatchupConnection = (from candidate in HostPeers.Values
				where candidate.CatchupPending
				orderby candidate.PlayerId
				select candidate.Connection).FirstOrDefault();
		}
		RepairHostRouterConnection(removedPeer.Connection);
	}

	private static bool RepairHostRouterConnection(object departingConnection)
	{
		if (_hostRouter == null)
		{
			return false;
		}
		if (!ConnectionsEqual(ReadMember(_hostRouter, "connection"), departingConnection))
		{
			return false;
		}
		PeerState peerState = (from candidate in HostPeers.Values
			orderby candidate.CatchupPending ? 1 : 0, candidate.PlayerId
			select candidate).FirstOrDefault();
		WriteMember(_hostRouter, "connection", peerState?.Connection);
		WriteMember(_hostRouter, "host_hasClientConnected", peerState != null);
		WriteMember(_hostRouter, "host_hasClientCaughtUp", peerState != null && !peerState.CatchupPending);
		return true;
	}

	private static RouterScopeState BeginHostPeerScope(PeerState peer)
	{
		if (_hostRouter == null || peer == null)
		{
			return null;
		}
		RouterScopeState result = new RouterScopeState(ReadMember(_hostRouter, "connection"), Convert.ToBoolean(ReadMember(_hostRouter, "host_hasClientConnected")), Convert.ToBoolean(ReadMember(_hostRouter, "host_hasClientCaughtUp")));
		WriteMember(_hostRouter, "connection", peer.Connection);
		WriteMember(_hostRouter, "host_hasClientConnected", true);
		WriteMember(_hostRouter, "host_hasClientCaughtUp", !peer.CatchupPending);
		return result;
	}

	private static void EndHostPeerScope(RouterScopeState state)
	{
		RestoreReadyHostRouterState();
	}

	private static bool RestoreReadyHostRouterState()
	{
		if (_hostRouter == null)
		{
			return false;
		}
		PeerState peerState;
		lock (Gate)
		{
			peerState = (from peer in HostPeers.Values
				orderby peer.CatchupPending ? 1 : 0, peer.PlayerId
				select peer).FirstOrDefault();
		}
		if (peerState == null)
		{
			WriteMember(_hostRouter, "connection", null);
			WriteMember(_hostRouter, "host_hasClientConnected", false);
			WriteMember(_hostRouter, "host_hasClientCaughtUp", false);
			return true;
		}
		WriteMember(_hostRouter, "connection", peerState.Connection);
		WriteMember(_hostRouter, "host_hasClientConnected", true);
		WriteMember(_hostRouter, "host_hasClientCaughtUp", true);
		return true;
	}

	private static bool BeforeNotifyClientLeft()
	{
		try
		{
			if (!IsLocalLobbyHost())
			{
				return true;
			}
			if (ConnectedRemoteCount() == 0)
			{
				object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
				if (obj != null)
				{
					FieldInfo field = obj.GetType().GetField("cachedRemoteUserID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					if (field != null)
					{
						field.SetValue(obj, Activator.CreateInstance(field.FieldType));
					}
				}
			}
			RefreshCurrentMenu();
			Plugin.LogSource.LogWarning("[Dynamic session] Contained the stock two-player NotifyClientLeft path; the lobby remains available for the remaining open slots.");
			return false;
		}
		catch (Exception exception)
		{
			LogFaultOnce("safe client-left bookkeeping", exception);
			return false;
		}
	}

	private static bool BeforeLobbyChatUpdate(object __instance, object[] __args)
	{
		try
		{
			if (__args == null || __args.Length != 1 || __args[0] == null || !IsInSteamLobby())
			{
				return true;
			}
			object instance = __args[0];
			uint num = Convert.ToUInt32(ReadMember(instance, "m_rgfChatMemberStateChange"));
			object obj = ReadMember(instance, "m_ulSteamIDUserChanged");
			object second = _getLocalSteamId.Invoke(null, null);
			bool flag = SteamIdsEqual(obj, second);
			if ((num & 1) != 0)
			{
				bool flag2 = IsLocalLobbyHost();
				bool changedIsLobbyOwner = !flag && !flag2 && IsCurrentLobbyOwner(obj);
				if (!ShouldContainLobbyMemberEntered(flag2, ConnectedRemoteCount(), flag, changedIsLobbyOwner))
				{
					return true;
				}
				RefreshCurrentMenu();
				Plugin.LogSource.LogWarning(IsLocalLobbyHost() ? "[Dynamic session] Contained Player 3+ entering the Steam lobby before the stock two-player callback could replace the host's existing remote identity. Native P2P acceptance remains active." : "[Dynamic session] Another client entered the Steam lobby; this client preserved its cached lobby-owner route and refreshed only the roster.");
				return false;
			}
			if ((num & 0x1E) == 0)
			{
				return true;
			}
			if (flag)
			{
				return true;
			}
			if (!IsLocalLobbyHost())
			{
				object second2 = ReadNullableValue(ReadMember(__instance, "cachedRemoteUserID"));
				if (SteamIdsEqual(obj, second2))
				{
					return true;
				}
				RefreshCurrentMenu();
				Plugin.LogSource.LogWarning("[Dynamic session] Another client left the Steam lobby; this client kept its host connection and refreshed only that departed nickname slot.");
				return false;
			}
			PeerState peerState = FindPeerByToken(obj);
			if (peerState != null)
			{
				DisposePeerIsolated(peerState, "Steam lobby member departure");
			}
			RefreshCurrentMenu();
			Plugin.LogSource.LogWarning("[Dynamic session] A remote member left; the host kept the lobby and every other peer online. The stock two-player LeaveActiveLobby branch was skipped.");
			return false;
		}
		catch (Exception exception)
		{
			LogFaultOnce("multi-peer lobby departure", exception);
			return !IsLocalLobbyHost();
		}
	}

	private static bool ShouldContainLobbyMemberEntered(bool localIsHost, int connectedRemoteCount, bool changedIsLocal, bool changedIsLobbyOwner)
	{
		if (changedIsLocal)
		{
			return false;
		}
		if (localIsHost)
		{
			return connectedRemoteCount > 0;
		}
		return !changedIsLobbyOwner;
	}

	private static bool IsCurrentLobbyOwner(object member)
	{
		if (member == null || !IsInSteamLobby())
		{
			return false;
		}
		ResolveSteamRosterApi();
		object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
		if (obj == null)
		{
			return false;
		}
		object obj2 = ReadMember(obj, "activeLobbyID");
		object second = _getLobbyOwner.Invoke(null, new object[1] { obj2 });
		return SteamIdsEqual(member, second);
	}

	private static bool BeforeP2PSessionFailed(object[] __args)
	{
		try
		{
			if (!IsLocalLobbyHost())
			{
				return true;
			}
			object sender = null;
			if (__args != null && __args.Length == 1 && __args[0] != null)
			{
				sender = ReadMember(__args[0], "m_steamIDRemote");
			}
			PeerState peerState = FindPeerByToken(sender);
			if (peerState != null)
			{
				DisposePeerIsolated(peerState, "Steam P2P session failure");
			}
			RefreshCurrentMenu();
			Plugin.LogSource.LogWarning("[Dynamic session] Contained a peer-scoped Steam P2P failure; the host lobby and unaffected players remain online.");
			return false;
		}
		catch (Exception exception)
		{
			LogFaultOnce("peer-scoped P2P failure", exception);
			return false;
		}
	}

	private static void DisposePeerIsolated(PeerState peer, string reason)
	{
		if (peer == null || peer.Connection == null)
		{
			return;
		}
		try
		{
			_isolatedPeerDispose = true;
			_isolatedDisposeReason = reason;
			_steamDispose.Invoke(peer.Connection, null);
			RemovePeer(peer, reason);
		}
		finally
		{
			_isolatedDisposeReason = null;
			_isolatedPeerDispose = false;
		}
	}

	private static object MakeVectorLike(object template, float x, float y, float z)
	{
		object obj = Activator.CreateInstance(template.GetType());
		WriteMember(obj, "x", x);
		WriteMember(obj, "y", y);
		try
		{
			WriteMember(obj, "z", z);
		}
		catch
		{
		}
		return obj;
	}

	private static object ReadNullableValue(object nullable)
	{
		if (nullable == null)
		{
			return null;
		}
		try
		{
			return Convert.ToBoolean(ReadMember(nullable, "HasValue")) ? ReadMember(nullable, "Value") : null;
		}
		catch
		{
			return nullable;
		}
	}

	private static void AfterMenuUpdate(object __instance)
	{
		_menuTearingDown = false;
		if (AppearanceFlowSettings.DynamicSessionDisablePeerWatchdog)
		{
			try
			{
				RefreshMenu(__instance);
				return;
			}
			catch (Exception exception)
			{
				LogFaultOnce("per-frame Open Friends refresh", exception);
				return;
			}
		}
		try
		{
			TickPeerWatchdog();
		}
		catch (Exception exception2)
		{
			LogFaultOnce("peer-scoped join watchdog", exception2);
		}
		try
		{
			RefreshMenu(__instance);
		}
		catch (Exception exception3)
		{
			LogFaultOnce("per-frame Open Friends refresh", exception3);
		}
		try
		{
			TickNativeRoster();
		}
		catch (Exception exception4)
		{
			_rosterUnavailable = true;
			LogFaultOnce("native eight-flag roster", exception4);
		}
	}

	private static bool BeforeClientHandleOnConnect()
	{
		try
		{
			if (_clientConnectStarted)
			{
				if (!_duplicateClientConnectLogged)
				{
					_duplicateClientConnectLogged = true;
					Plugin.LogSource.LogWarning("[Dynamic session] Ignored a duplicate native client-connect trigger after this machine had already started its handshake.");
				}
				return false;
			}
			_clientConnectStarted = true;
			return true;
		}
		catch (Exception exception)
		{
			LogFaultOnce("idempotent client-connect lifecycle", exception);
			return true;
		}
	}

	private static void AfterClientHandleOnDisconnect()
	{
		_clientConnectStarted = false;
		_duplicateClientConnectLogged = false;
	}

	private static void BeforeMenuHide()
	{
		try
		{
			_menuTearingDown = true;
			_rosterWasOpen = false;
			_rosterFirstVisible = 0;
			HideRosterOverlay();
		}
		catch (Exception exception)
		{
			LogFaultOnce("roster menu-close cleanup", exception);
		}
	}

	private static bool BeforeServerHandleOnDisconnect(object __instance)
	{
		try
		{
			if (!IsLocalLobbyHost())
			{
				return true;
			}
			if (_isolatedPeerDispose)
			{
				RefreshCurrentMenu();
				Plugin.LogSource.LogWarning("[Dynamic session] Contained the nested global disconnect callback while disposing one isolated peer; the host and surviving transports kept their live state.");
				return false;
			}
			if (Convert.ToBoolean(ReadMember(__instance, "intendedDisconnect")))
			{
				return true;
			}
			RefreshCurrentMenu();
			Plugin.LogSource.LogWarning((ConnectedRemoteCount() > 0) ? "[Dynamic session] One peer left while other peers remain; skipped the stock global P2 teardown." : "[Dynamic session] Ignored a stale remote-disconnect callback after final peer cleanup; the host lobby remained online and ready for another invitation.");
			return false;
		}
		catch (Exception exception)
		{
			LogFaultOnce("multi-peer disconnect isolation", exception);
			return !IsLocalLobbyHost();
		}
	}

	private static void AfterAcceptsMultiplayerInvites(ref bool __result)
	{
		try
		{
			if (IsLocalLobbyHost())
			{
				__result = ConnectedRemoteCount() < 7;
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("invite availability", exception);
		}
	}

	private static void AfterMenuNetworkStateChanged(object __instance)
	{
		try
		{
			RefreshMenu(__instance);
		}
		catch (Exception exception)
		{
			LogFaultOnce("native lobby UI refresh", exception);
		}
	}

	private static void BeforeRecvClientHandshake()
	{
		try
		{
			BeforeRecvClientHandshakeCore();
		}
		catch (Exception exception)
		{
			LogFaultOnce("logical-slot assignment", exception);
		}
	}

	private static void BeforeRecvClientHandshakeCore()
	{
		PeerState peerState = CurrentPeer();
		if (peerState == null || peerState.AssignmentSent)
		{
			return;
		}
		SendBodyControl(peerState.Connection, 1258291200, peerState.PlayerId, peerState.BodyNetId, "private logical-slot/body assignment");
		PeerState[] array;
		lock (Gate)
		{
			array = (from candidate in HostPeers.Values.Distinct()
				orderby candidate.PlayerId
				select candidate).ToArray();
		}
		PeerState[] array2 = array;
		foreach (PeerState peerState2 in array2)
		{
			SendBodyControl(peerState.Connection, 1275068416, peerState2.PlayerId, peerState2.BodyNetId, "body roster mapping");
		}
		array2 = array;
		foreach (PeerState peerState3 in array2)
		{
			if (peerState3 != peerState)
			{
				SendBodyControl(peerState3.Connection, 1275068416, peerState.PlayerId, peerState.BodyNetId, "new pending-peer body mapping");
			}
		}
		peerState.AssignmentSent = true;
		Plugin.LogSource.LogWarning("[Dynamic session] Sent Player " + (peerState.PlayerId + 1) + "'s private logical-slot assignment and body NetID " + peerState.BodyNetId + " before catch-up; synchronized " + array.Length + " body mapping(s).");
	}

	private static void SendBodyControl(object connection, int packetType, int playerId, int bodyNetId, string operation)
	{
		object obj = Activator.CreateInstance(_intMessageType);
		WriteMember(obj, "value", packetType | ((bodyNetId & 0x7FFF) << 8) | (playerId & 0xFF));
		if (!DirectSend(connection, 60, obj, checkQueue: false))
		{
			throw new InvalidOperationException("Could not send " + operation + " for Player " + (playerId + 1) + ".");
		}
	}

	private static bool BeforeRecvAllowPlayerReroll(object[] __args)
	{
		try
		{
			return BeforeRecvAllowPlayerRerollCore(__args);
		}
		catch (Exception exception)
		{
			LogFaultOnce("assignment receiver", exception);
			return false;
		}
	}

	private static bool BeforeRecvAllowPlayerRerollCore(object[] __args)
	{
		if (__args == null || __args.Length != 1 || __args[0] == null)
		{
			return true;
		}
		object obj = Activator.CreateInstance(_intMessageType);
		_messageDeserialize.Invoke(obj, new object[1] { __args[0] });
		int num = Convert.ToInt32(ReadMember(obj, "value"));
		int num2 = num & -16777216;
		if (num2 == 1258291200 || num2 == 1275068416 || num2 == 1291845632 || num2 == 1308622848)
		{
			int num3 = num & 0xFF;
			if (num3 < 1 || num3 > 7)
			{
				Plugin.LogSource.LogError("[Dynamic session] Ignored invalid logical PlayerId " + num3 + ".");
				return false;
			}
			int num4 = (num >> 8) & 0x7FFF;
			if (num2 != 1291845632 && num4 <= 0)
			{
				Plugin.LogSource.LogError("[Dynamic session] Ignored invalid body NetID " + num4 + " for Player " + (num3 + 1) + ".");
				return false;
			}
			if (num2 == 1291845632)
			{
				RemoveDynamicBody(num3);
				Plugin.LogSource.LogWarning("[Dynamic session] Removed departed Player " + (num3 + 1) + "'s independently routed body.");
				return false;
			}
			AssignDynamicBody(num3, num4);
			switch (num2)
			{
			case 1308622848:
				if (_logicalAssignmentReceived && num3 == LocalPlayerId)
				{
					SetLocalBodyOwner(num3);
					_localBodyActivated = true;
				}
				ActivateDynamicBody(num3);
				Plugin.LogSource.LogWarning("[Dynamic session] Activated ready Player " + (num3 + 1) + " body NetID " + num4 + ".");
				if (num3 == LocalPlayerId && LocalPlayerId >= 2)
				{
					SendLocalRulerModelToHost();
				}
				return false;
			case 1275068416:
				Plugin.LogSource.LogWarning("[Dynamic session] Registered Player " + (num3 + 1) + " body NetID " + num4 + ".");
				return false;
			default:
				LocalPlayerId = num3;
				_logicalAssignmentReceived = true;
				Plugin.LogSource.LogWarning("[Dynamic session] Reserved Player " + (LocalPlayerId + 1) + " on independent body NetID " + num4 + ". The stock Player 2 body remains unchanged until native catch-up reports this client ready.");
				return false;
			}
		}
		object value = _bossInstanceProperty.GetValue(null, null);
		if (value != null)
		{
			WriteMember(value, "allowClientApperanceReroll", num > 0);
		}
		return false;
	}

	private static void BeforeRecvP2SelectSkin()
	{
		PeerState peerState = CurrentPeer();
		if (peerState != null)
		{
			peerState.InsideSkinSelection = true;
			peerState.SelectionSeen = false;
		}
	}

	private static void AfterSkinSelectMessageDeserialize(object __instance)
	{
		try
		{
			AfterSkinSelectMessageDeserializeCore(__instance);
		}
		catch (Exception exception)
		{
			LogFaultOnce("native selector capture", exception);
		}
	}

	private static void AfterSkinSelectMessageDeserializeCore(object __instance)
	{
		PeerState peerState = CurrentPeer();
		if (peerState != null && peerState.InsideSkinSelection)
		{
			peerState.LastMonarch = ReadMember(__instance, "model");
			peerState.LastSelectionWasFinal = Convert.ToBoolean(ReadMember(__instance, "isFinal"));
			peerState.SelectionSeen = true;
		}
	}

	private static void BeforeSendPlayerData()
	{
		try
		{
			BeforeSendPlayerDataCore();
		}
		catch (Exception exception)
		{
			LogFaultOnce("PlayerModel rewrite", exception);
		}
	}

	private static void BeforeSendPlayerDataCore()
	{
		PeerState peerState = CurrentPeer();
		if (peerState == null || !peerState.InsideSkinSelection || !peerState.SelectionSeen)
		{
			return;
		}
		object obj = ReadMember(_bossInstanceProperty.GetValue(null, null), "p2Model");
		if (obj != null)
		{
			object obj2 = peerState.LastMonarch ?? ConvertMonarch(ReadMember(obj, "monarchType"));
			object obj3 = _copyModel.Invoke(null, new object[3] { obj, peerState.PlayerId, obj2 });
			if (obj3 != null)
			{
				WriteMember(obj3, "steedNetID", 924);
				WriteMember(obj3, "steedType", 9);
				SetSlot(peerState.PlayerId, obj3);
				peerState.LatestModel = obj3;
			}
		}
	}

	private static void AfterSendPlayerData()
	{
		try
		{
			AfterSendPlayerDataCore();
		}
		catch (Exception exception)
		{
			LogFaultOnce("PlayerModel restore", exception);
		}
	}

	private static void AfterSendPlayerDataCore()
	{
		PeerState peerState = CurrentPeer();
		if (peerState != null && peerState.InsideSkinSelection && peerState.SelectionSeen && !peerState.LastSelectionWasFinal && peerState.LatestModel != null)
		{
			BroadcastRulerPreview(peerState);
		}
	}

	private static void BeforePlayerModelMessageSerialize(object __instance, ref object __state)
	{
		try
		{
			PeerState peerState = CurrentPeer();
			if (peerState != null && peerState.InsideSkinSelection && peerState.SelectionSeen && peerState.LatestModel != null)
			{
				object obj = ReadMember(__instance, "payload");
				if (obj != null && Convert.ToInt32(ReadMember(obj, "playerId")) == 1)
				{
					__state = obj;
					WriteMember(__instance, "payload", peerState.LatestModel);
				}
			}
		}
		catch (Exception exception)
		{
			LogFaultOnce("serialized PlayerModel substitution", exception);
		}
	}

	private static void AfterPlayerModelMessageSerialize(object __instance, object __state)
	{
		if (__state == null)
		{
			return;
		}
		try
		{
			WriteMember(__instance, "payload", __state);
		}
		catch (Exception exception)
		{
			LogFaultOnce("serialized PlayerModel restoration", exception);
		}
	}

	private static void BroadcastRulerPreview(PeerState selectingPeer)
	{
		object[] array;
		lock (Gate)
		{
			array = (from peer in HostPeers.Values
				where peer != selectingPeer && !peer.CatchupPending
				orderby peer.PlayerId
				select peer.Connection).ToArray();
		}
		object[] array2 = array;
		for (int num = 0; num < array2.Length; num++)
		{
			SendPlayerModel(array2[num], selectingPeer.LatestModel);
		}
		if (array.Length != 0 && !selectingPeer.PreviewFanoutLogged)
		{
			selectingPeer.PreviewFanoutLogged = true;
			Plugin.LogSource.LogWarning("[Dynamic session] Player " + (selectingPeer.PlayerId + 1) + "'s native ruler previews are now mirrored live to " + array.Length + " already-ready remote spectator(s); the host keeps the stock local spectator view.");
		}
	}

	private static void AfterRecvP2SelectSkin()
	{
		try
		{
			AfterRecvP2SelectSkinCore();
		}
		catch (Exception exception)
		{
			LogFaultOnce("appearance synchronization", exception);
		}
	}

	private static void AfterRecvP2SelectSkinCore()
	{
		PeerState peer = CurrentPeer();
		if (peer == null)
		{
			return;
		}
		peer.InsideSkinSelection = false;
		if (!peer.SelectionSeen)
		{
			return;
		}
		Plugin.LogSource.LogWarning("[Dynamic session] Player " + (peer.PlayerId + 1) + (peer.LastSelectionWasFinal ? " confirmed " : " previewed ") + DescribeObject(peer.LastMonarch) + " through that player's own native selector.");
		if (!peer.LastSelectionWasFinal || peer.LatestModel == null)
		{
			return;
		}
		object[] array;
		lock (Gate)
		{
			array = (from other in HostPeers.Values
				where other != peer && !other.CatchupPending
				select other.Connection).ToArray();
		}
		object[] array2 = array;
		for (int num = 0; num < array2.Length; num++)
		{
			SendPlayerModel(array2[num], peer.LatestModel);
		}
		Plugin.LogSource.LogWarning("[Dynamic session] Player " + (peer.PlayerId + 1) + "'s chosen ruler was committed to slot " + peer.PlayerId + " and synchronized to " + array.Length + " already-ready client(s); this client's full appearance set is deferred until its own catch-up completes.");
	}

	private static bool BeforeHandleRecvPlayerData(object[] __args)
	{
		try
		{
			return BeforeHandleRecvPlayerDataCore(__args);
		}
		catch (Exception exception)
		{
			LogFaultOnce("expanded PlayerModel receiver", exception);
			return false;
		}
	}

	private static bool BeforeHandleRecvPlayerDataCore(object[] __args)
	{
		if (__args == null || __args.Length != 1 || __args[0] == null)
		{
			return true;
		}
		object obj = __args[0];
		int num = Convert.ToInt32(ReadMember(obj, "playerId"));
		if (num < 0 || num > 7)
		{
			Plugin.LogSource.LogError("[Dynamic session] Dropped PlayerModel with invalid PlayerId " + num + ".");
			return false;
		}
		SetSlot(num, obj);
		if (num == 0 || num == LocalPlayerId || (num == 1 && LocalPlayerId <= 1))
		{
			return true;
		}
		lock (Gate)
		{
			if (SuppressedModelIds.Add(num))
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Stored remote Player " + (num + 1) + "'s appearance in the expanded registry. The stock receiver was skipped because this machine represents that remote ruler through a dynamic body clone.");
			}
		}
		BroadcastStoredModel(num, obj);
		TryReapplyStoredAppearance(num);
		return false;
	}

	private static void BroadcastStoredModel(int playerId, object model)
	{
		object[] array;
		lock (Gate)
		{
			if (HostPeers.Count == 0 || model == null)
			{
				return;
			}
			array = (from peer in HostPeers.Values
				where !peer.CatchupPending && peer.Connection != null
				orderby peer.PlayerId
				select peer.Connection).ToArray();
		}
		object[] array2 = array;
		for (int num = 0; num < array2.Length; num++)
		{
			SendPlayerModel(array2[num], model);
		}
		if (array.Length != 0 && LoggedModelRelays.Add(playerId))
		{
			Plugin.LogSource.LogWarning("[Dynamic session] Forwarded Player " + (playerId + 1) + "'s ruler model to " + array.Length + " other ready client(s).");
		}
	}

	private static void TryReapplyStoredAppearance(int playerId)
	{
		if (_tryReapplyAppearance == null || playerId < 2)
		{
			return;
		}
		try
		{
			_tryReapplyAppearance.Invoke(null, new object[1] { playerId });
		}
		catch (Exception exception)
		{
			LogFaultOnce("stored-appearance re-apply", exception);
		}
	}

	private static void SendLocalRulerModelToHost()
	{
		try
		{
			SendLocalRulerModelToHostCore();
		}
		catch (Exception exception)
		{
			LogFaultOnce("local ruler model push", exception);
		}
	}

	private static void SendLocalRulerModelToHostCore()
	{
		if (IsLocalLobbyHost() || HostPeers.Count > 0)
		{
			return;
		}
		object value = _bossInstanceProperty.GetValue(null, null);
		if (value == null)
		{
			return;
		}
		object obj = ReadMember(value, "p2Model");
		if (obj == null)
		{
			Plugin.LogSource.LogWarning("[Dynamic session] This client has no ruler model to push yet; the machines representing it keep the fallback look until one arrives through the skin-select chain.");
			return;
		}
		object obj2 = _copyModel.Invoke(null, new object[3]
		{
			obj,
			LocalPlayerId,
			ConvertMonarch(ReadMember(obj, "monarchType"))
		});
		if (obj2 != null)
		{
			WriteMember(obj2, "steedNetID", 924);
			WriteMember(obj2, "steedType", 9);
			object obj3 = ReadClientHostConnection();
			if (obj3 == null)
			{
				Plugin.LogSource.LogWarning("[Dynamic session] Could not resolve the host connection to push this client's ruler model.");
				return;
			}
			SendPlayerModel(obj3, obj2);
			Plugin.LogSource.LogWarning("[Dynamic session] Pushed this client's own ruler model to the host as logical Player " + (LocalPlayerId + 1) + ".");
		}
	}

	private static object ReadClientHostConnection()
	{
		object obj = null;
		string[] array = new string[2] { "_activeNetRouter", "_unetRouter" };
		foreach (string name in array)
		{
			try
			{
				obj = ReadStaticMember(_networkBigBossType, name);
			}
			catch
			{
				obj = null;
			}
			if (obj != null)
			{
				break;
			}
		}
		if (obj == null)
		{
			return null;
		}
		try
		{
			return ReadMember(obj, "connection");
		}
		catch
		{
			return null;
		}
	}

	private static void BeforeSetupAsPlayer(object[] __args)
	{
		try
		{
			BeforeSetupAsPlayerCore(__args);
		}
		catch (Exception exception)
		{
			LogFaultOnce("local PlayerId binding", exception);
		}
	}

	private static void BeforeSetupAsPlayerCore(object[] __args)
	{
		if (__args == null || __args.Length != 1 || Convert.ToInt32(__args[0]) != 1)
		{
			return;
		}
		if (!_logicalAssignmentReceived)
		{
			int num = ResolveLocalPlayerIdFromLobby();
			if (num > 1)
			{
				LocalPlayerId = num;
				Plugin.LogSource.LogWarning("[Dynamic session] The private assignment packet had not arrived before body creation, so Steam lobby order reserved logical Player " + (LocalPlayerId + 1) + " as a collision-safe fallback.");
			}
		}
		if (LocalPlayerId > 1 && _localBodyActivated)
		{
			__args[0] = LocalPlayerId;
			Plugin.LogSource.LogWarning("[Dynamic session] Bound this client's game-created ruler to logical Player " + (LocalPlayerId + 1) + ".");
		}
	}

	private static int ResolveLocalPlayerIdFromLobby()
	{
		if (IsLocalLobbyHost() || !IsInSteamLobby())
		{
			return 0;
		}
		ResolveSteamRosterApi();
		object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
		if (obj == null)
		{
			return 0;
		}
		object obj2 = ReadMember(obj, "activeLobbyID");
		object obj3 = _getLobbyOwner.Invoke(null, new object[1] { obj2 });
		object second = _getLocalSteamId.Invoke(null, null);
		if (SteamIdsEqual(obj3, second))
		{
			return 0;
		}
		int num = Math.Min(8, Math.Max(0, Convert.ToInt32(_getNumLobbyMembers.Invoke(null, new object[1] { obj2 }))));
		int num2 = 1;
		for (int i = 0; i < num; i++)
		{
			object first = _getLobbyMemberByIndex.Invoke(null, new object[2] { obj2, i });
			if (!SteamIdsEqual(first, obj3))
			{
				if (SteamIdsEqual(first, second))
				{
					return num2;
				}
				num2++;
			}
		}
		return 0;
	}

	private static void SendPlayerModel(object connection, object model)
	{
		if (connection != null && model != null)
		{
			object obj = Activator.CreateInstance(_playerModelMessageType);
			WriteMember(obj, "payload", model);
			DirectSend(connection, 43, obj, checkQueue: false);
		}
	}

	private static bool DirectSend(object connection, byte messageCode, object message, bool checkQueue)
	{
		if (connection == null || message == null)
		{
			return false;
		}
		try
		{
			_directSend = true;
			object obj = connection;
			Type declaringType = _steamSend.DeclaringType;
			if (declaringType != null && !declaringType.IsInstanceOfType(obj))
			{
				obj = ConvertValue(obj, declaringType);
			}
			_steamSend.Invoke(obj, new object[3] { messageCode, message, checkQueue });
			return true;
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogError("[Dynamic session] Send to " + DescribePeer(connection) + " failed: " + Unwrap(exception));
			return false;
		}
		finally
		{
			_directSend = false;
		}
	}

	private static PeerState CurrentPeer()
	{
		if (_receivingConnection == null)
		{
			return null;
		}
		lock (Gate)
		{
			PeerState value;
			return HostPeers.TryGetValue(_receivingConnection, out value) ? value : null;
		}
	}

	private static void SetLocalBodyOwner(int playerId)
	{
		_setLocalBodyOwner.Invoke(null, new object[1] { playerId });
	}

	private static void AssignDynamicBody(int playerId, int bodyNetId)
	{
		_assignDynamicPlayer.Invoke(null, new object[2] { playerId, bodyNetId });
	}

	private static void ActivateDynamicBody(int playerId)
	{
		_activateDynamicPlayer.Invoke(null, new object[1] { playerId });
	}

	private static void RemoveDynamicBody(int playerId)
	{
		_removeDynamicPlayer.Invoke(null, new object[1] { playerId });
	}

	private static void ResetDynamicBodies(string reason)
	{
		_resetDynamicPlayers.Invoke(null, new object[1] { reason });
	}

	private static int FindFreePlayerId()
	{
		int playerId;
		for (playerId = 1; playerId <= 7; playerId++)
		{
			if (!HostPeers.Values.Any((PeerState peer) => peer.PlayerId == playerId))
			{
				return playerId;
			}
		}
		return -1;
	}

	private static int ConnectedRemoteCount()
	{
		lock (Gate)
		{
			return HostPeers.Count;
		}
	}

	private static bool IsLocalLobbyHost()
	{
		if (_steamPlatformManagerType == null)
		{
			return false;
		}
		object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
		if (obj != null)
		{
			return Convert.ToBoolean(ReadMember(obj, "lobbyHost"));
		}
		return false;
	}

	private static bool IsInSteamLobby()
	{
		if (_steamPlatformManagerType == null)
		{
			return false;
		}
		object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
		if (obj != null)
		{
			return Convert.ToBoolean(ReadMember(obj, "inLobby"));
		}
		return false;
	}

	private static void RefreshCurrentMenu()
	{
		if (!(_menuType == null))
		{
			object obj = ReadStaticMember(_menuType, "Inst");
			if (obj != null)
			{
				RefreshMenu(obj);
			}
		}
	}

	private static void RefreshMenu(object menu)
	{
		if (AppearanceFlowSettings.DynamicSessionDisableOpenFriends || menu == null || !IsLocalLobbyHost())
		{
			return;
		}
		int num = ConnectedRemoteCount();
		object obj = ReadMember(menu, "rootNetConnectUI_OpenFriends");
		if (obj != null)
		{
			bool flag = num < 7;
			WriteMember(obj, "interactable", flag);
			if (!_lastInviteInteractable.HasValue || _lastInviteInteractable.Value != flag)
			{
				_lastInviteInteractable = flag;
				Plugin.LogSource.LogWarning("[Dynamic session] Open Friends is " + (flag ? "enabled" : "disabled") + " at " + (num + 1) + "/" + 8 + " occupied player slots.");
			}
		}
	}

	private static void TickNativeRoster()
	{
		if (_menuType == null || _menuTearingDown)
		{
			return;
		}
		RetireDeadOverlayRefs();
		if (AppearanceFlowSettings.OverlayFontSwaps)
		{
			lock (Gate)
			{
				foreach (object watchedCounterText in WatchedCounterTexts)
				{
					try
					{
						PixelizeRosterText(watchedCounterText, scaleUp: false, counter: true);
					}
					catch
					{
					}
					try
					{
						object obj2 = ReadMember(watchedCounterText, "gameObject");
						Type type = FindLoadedType("TMPro.TextMeshProUGUI") ?? FindLoadedType("TextMeshProUGUI");
						if (obj2 != null && type != null && !_restyledCounterTmp.Contains(obj2))
						{
							object obj3 = FindScrollbarComponent(obj2, type);
							object obj4 = ResolvePixelFontTmp();
							if (obj3 != null && IsUnityAlive(obj3) && obj4 != null)
							{
								WriteMember(obj3, "font", obj4);
								WriteMember(obj3, "fontSize", 10f);
								WriteMember(obj3, "color", Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), 1f, 1f, 1f, 1f));
								_restyledCounterTmp.Add(obj2);
								Plugin.LogSource.LogWarning("[Overlay] Counter TMP component restyled to the pixel font.");
							}
						}
					}
					catch
					{
					}
				}
			}
			if (!_menuFontGlyphsNormalized && _menuTmpFontAsset != null)
			{
				TickMenuFontGlyphNormalization();
			}
		}
		bool flag = IsInSteamLobby();
		if (flag != _lastInSteamLobby)
		{
			bool flag2 = _lastInSteamLobby && !flag;
			_lastInSteamLobby = flag;
			_cachedRosterSnapshot = null;
			_nextRosterSnapshotRefresh = 0L;
			_lastLoggedLobbyMemberCount = -1;
			if (flag2)
			{
				ResetDynamicBodies("left Steam lobby");
			}
		}
		if (!flag)
		{
			LocalPlayerId = 0;
			_logicalAssignmentReceived = false;
			_localBodyActivated = false;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (timestamp < _nextRosterRefresh)
		{
			return;
		}
		_nextRosterRefresh = timestamp + Math.Max(1L, Stopwatch.Frequency / 60);
		object obj6 = ReadStaticMember(_menuType, "Inst");
		bool flag3 = obj6 != null && UsesCrossfadeRoster(obj6);
		if (!(flag3 ? (!IsNetworkingPanelOpen(obj6)) : ShouldShowRoster(obj6)) || AppearanceFlowSettings.OverlayDisableAll)
		{
			if (!_rosterWasOpen)
			{
				return;
			}
			_rosterWasOpen = false;
			_rosterFirstVisible = 0;
			_crossfadePhase = 0;
			_crossfadeAlpha = 1f;
			_crossfadeShown = 0;
			if (flag3)
			{
				object obj7 = ReadMember(obj6, "userPanel");
				object obj8 = ReadMember(obj6, "remoteUserPanel");
				if (obj7 != null && IsUnityAlive(obj7))
				{
					WriteMember(obj7, "alpha", 1f);
				}
				if (obj8 != null && IsUnityAlive(obj8))
				{
					WriteMember(obj8, "alpha", 1f);
				}
			}
			HideRosterOverlay();
			HideVersionBadge();
			DestroyRosterScrollbar();
			RestoreRosterSourceTexts();
			return;
		}
		ShowVersionBadge(obj6);
		EnsureRosterScrollbar(obj6);
		if (!_rosterWasOpen)
		{
			_rosterWasOpen = true;
			_rosterFirstVisible = 0;
			_crossfadeShown = 0;
			EnsureRosterScrollbar(obj6);
		}
		else
		{
			int num = ReadRosterScrollStep();
			if (num != 0)
			{
				long timestamp2 = Stopwatch.GetTimestamp();
				if (timestamp2 >= _nextRosterScrollAllowed)
				{
					_nextRosterScrollAllowed = timestamp2 + (long)((float)Stopwatch.Frequency * 0.22f);
					int rosterFirstVisible = _rosterFirstVisible;
					_rosterFirstVisible = Math.Max(0, Math.Min(6, _rosterFirstVisible + num * 2));
					if (rosterFirstVisible != _rosterFirstVisible)
					{
						_scrollAccumulator = 0f;
						Plugin.LogSource.LogInfo("[Dynamic session] Native roster scrolled to slots " + (_rosterFirstVisible + 1) + "-" + (_rosterFirstVisible + 2) + " of 8.");
					}
					EnsureRosterScrollbar(obj6);
				}
			}
		}
		if (flag3)
		{
			TickCrossfadeRoster(obj6);
		}
		else
		{
			RefreshNativeRoster(obj6);
		}
	}

	private static void DestroyRosterScrollbar()
	{
		DestroyUnityObject(_scrollbarThumb);
		DestroyUnityObject(_scrollbarTrack);
		_scrollbarThumb = null;
		_scrollbarTrack = null;
	}

	private static bool ComputeRosterSpan(object menu, out float spanCenter, out float trackScaleY, out float trackCenterY)
	{
		spanCenter = 0f;
		trackScaleY = 1f;
		trackCenterY = 0f;
		try
		{
			object obj = ReadMember(menu, "remoteUserPanel");
			object obj2 = ReadMember(menu, "userPanel");
			if (obj == null || !IsUnityAlive(obj))
			{
				return false;
			}
			if (obj2 != null && !IsUnityAlive(obj2))
			{
				obj2 = null;
			}
			object obj3 = CastIl2CppObject(ReadMember(obj, "transform"), "UnityEngine.RectTransform");
			object obj4 = ((obj2 == null) ? null : CastIl2CppObject(ReadMember(obj2, "transform"), "UnityEngine.RectTransform"));
			float num = PanelWorldCenterY(obj3);
			float val = ((obj4 == null) ? num : PanelWorldCenterY(obj4));
			float num2 = PanelWorldHeight(obj3);
			float num3 = num2 * 0.42f;
			float num4 = Convert.ToSingle(ReadMember(ReadMember(obj3, "localScale"), "y"));
			float num5 = ((num4 > 0.0001f) ? (num2 / num4) : num2);
			bool flag = false;
			object obj5 = FindLargestChildTransform(obj3);
			object obj6 = ((obj4 == null) ? null : FindLargestChildTransform(obj4));
			float num8;
			float num9;
			if (obj5 != null && (obj4 == null || obj6 != null))
			{
				float num6 = PanelWorldCenterY(obj5) + PanelWorldHeight(obj5) / 2f;
				float num7 = PanelWorldCenterY(obj5) - PanelWorldHeight(obj5) / 2f;
				float val2 = ((obj6 == null) ? num6 : (PanelWorldCenterY(obj6) + PanelWorldHeight(obj6) / 2f));
				float val3 = ((obj6 == null) ? num7 : (PanelWorldCenterY(obj6) - PanelWorldHeight(obj6) / 2f));
				num8 = Math.Max(val2, num6);
				num9 = Math.Min(val3, num7);
				flag = num8 - num9 > 0f;
				if (flag && !_scrollbarSpanLogged)
				{
					_scrollbarSpanLogged = true;
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar span measured from the bar children: top " + num8 + ", bottom " + num9 + ", height " + (num8 - num9) + ".");
				}
			}
			else
			{
				num8 = Math.Max(val, num) + num3 / 2f;
				num9 = Math.Min(val, num) - num3 / 2f;
			}
			if (num8 - num9 <= 0f)
			{
				return false;
			}
			spanCenter = (num8 + num9) / 2f;
			if (flag)
			{
				trackScaleY = (num8 - num9) / num5;
				trackCenterY = spanCenter;
			}
			else
			{
				float num10 = 0.42f;
				float num11 = 0f;
				try
				{
					MethodInfo method = obj3.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
					int num12 = Convert.ToInt32(ReadMember(obj3, "childCount"));
					float num13 = 0f;
					for (int i = 0; i < num12; i++)
					{
						object obj7 = method?.Invoke(obj3, new object[1] { i });
						object obj8 = ((obj7 == null) ? null : CastIl2CppObject(obj7, "UnityEngine.Transform"));
						if (obj8 != null && IsUnityAlive(obj8))
						{
							float num14 = PanelWorldHeight(obj8);
							if (!(num14 <= num13))
							{
								num13 = num14;
								num10 = Math.Max(0.05f, num14 / num2);
								num11 = (PanelWorldCenterY(obj8) - num) / num2;
							}
						}
					}
				}
				catch
				{
				}
				trackScaleY = (num8 - num9) / (num10 * num5);
				trackCenterY = spanCenter - num11 * trackScaleY * num5;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static object FindLargestChildTransform(object transform)
	{
		try
		{
			MethodInfo method = transform.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
			if (method == null)
			{
				return null;
			}
			int num = Convert.ToInt32(ReadMember(transform, "childCount"));
			object result = null;
			float num2 = 0f;
			for (int i = 0; i < num; i++)
			{
				object obj = method.Invoke(transform, new object[1] { i });
				object obj2 = ((obj == null) ? null : CastIl2CppObject(obj, "UnityEngine.Transform"));
				if (obj2 != null && IsUnityAlive(obj2))
				{
					float num3 = PanelWorldHeight(obj2);
					if (num3 > num2)
					{
						num2 = num3;
						result = obj2;
					}
				}
			}
			return result;
		}
		catch
		{
			return null;
		}
	}

	private static float PanelWorldCenterY(object transform)
	{
		float num = PanelWorldHeight(transform);
		float num2 = Convert.ToSingle(ReadMember(ReadMember(transform, "pivot"), "y"));
		return Convert.ToSingle(ReadMember(ReadMember(transform, "position"), "y")) + (0.5f - num2) * num;
	}

	private static float PanelWorldHeight(object transform)
	{
		return Convert.ToSingle(ReadMember(ReadMember(transform, "lossyScale"), "y")) * Convert.ToSingle(ReadMember(ReadMember(transform, "rect"), "height"));
	}

	private static void EnsureRosterScrollbar(object menu)
	{
		if (!AppearanceFlowSettings.OverlayScrollbar || _scrollbarFailed)
		{
			return;
		}
		try
		{
			object obj = ReadMember(menu, "remoteUserPanel");
			object obj2 = ReadMember(menu, "remoteUserText");
			if (_scrollbarTrack == null)
			{
				if (obj == null)
				{
					return;
				}
				object instance = CastIl2CppObject(ReadMember(obj, "transform"), "UnityEngine.RectTransform");
				object obj3 = ReadMember(instance, "parent");
				if (obj3 == null)
				{
					return;
				}
				object instance2 = ReadMember(instance, "localPosition");
				try
				{
					object instance3 = ReadMember(instance, "rect");
					_scrollbarBannerWidth = Convert.ToSingle(ReadMember(instance3, "width"));
					_scrollbarBannerHeight = Convert.ToSingle(ReadMember(instance3, "height"));
				}
				catch
				{
					_scrollbarBannerWidth = 162f;
					_scrollbarBannerHeight = 45f;
				}
				if (!_scrollbarGeometryLogged)
				{
					_scrollbarGeometryLogged = true;
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar banner rect: " + _scrollbarBannerWidth + " x " + _scrollbarBannerHeight + ".");
				}
				MethodInfo methodInfo = FindPanelInstantiate();
				if (methodInfo == null)
				{
					throw new MissingMethodException("UnityEngine.Object.Instantiate(Object, Transform, bool)");
				}
				string text = null;
				if (obj2 != null)
				{
					text = ReadMember(obj2, "text") as string;
					WriteMember(obj2, "text", string.Empty);
				}
				try
				{
					_scrollbarTrack = CloneScrollbarPanel(methodInfo, obj, obj3, 0.3f);
					object parent = CastIl2CppObject(ReadMember(_scrollbarTrack, "transform"), "UnityEngine.RectTransform");
					_scrollbarThumb = CloneScrollbarPanel(methodInfo, obj, parent, 1f);
				}
				finally
				{
					if (obj2 != null && text != null)
					{
						WriteMember(obj2, "text", text);
					}
				}
				float x = 0.055f * _scrollbarBannerHeight / _scrollbarBannerWidth;
				float num = _scrollbarBannerWidth * 0.55f;
				object obj5 = CastIl2CppObject(ReadMember(_scrollbarTrack, "transform"), "UnityEngine.RectTransform");
				object obj6 = CastIl2CppObject(ReadMember(_scrollbarThumb, "transform"), "UnityEngine.RectTransform");
				WriteMember(obj5, "pivot", MakeVector2(0.5f, 0.5f));
				WriteMember(obj6, "pivot", MakeVector2(0.5f, 0.5f));
				ComputeRosterSpan(menu, out var spanCenter, out var trackScaleY, out var trackCenterY);
				WriteMember(obj5, "localScale", MakeVectorLike(ReadMember(obj5, "localScale"), x, trackScaleY, 1f));
				WriteMember(obj6, "localScale", MakeVectorLike(ReadMember(obj6, "localScale"), 1.6f, 0.4f, 1f));
				object template = ReadMember(obj5, "localPosition");
				WriteMember(obj5, "localPosition", MakeVectorLike(template, Convert.ToSingle(ReadMember(instance2, "x")) + num, Convert.ToSingle(ReadMember(instance2, "y")), 0f));
				object obj7 = ReadMember(obj5, "position");
				WriteMember(obj5, "position", MakeVectorLike(obj7, Convert.ToSingle(ReadMember(obj7, "x")), trackCenterY, Convert.ToSingle(ReadMember(obj7, "z"))));
				object template2 = ReadMember(obj6, "localPosition");
				WriteMember(obj6, "localPosition", MakeVectorLike(template2, 0f, 0f, 0f));
				object obj8 = null;
				try
				{
					int num2 = Convert.ToInt32(ReadMember(obj5, "childCount"));
					MethodInfo method = obj5.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
					List<object> list = new List<object>();
					if (method != null)
					{
						for (int i = 0; i < num2; i++)
						{
							object obj9 = method.Invoke(obj5, new object[1] { i });
							if (obj9 != null && !AreSameUnityObject(obj9, obj6))
							{
								list.Add(ReadMember(obj9, "gameObject"));
							}
						}
					}
					foreach (object item in list)
					{
						try
						{
							if (item != null)
							{
								DestroyUnityObject(item);
							}
						}
						catch
						{
						}
					}
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar track: destroyed " + list.Count + " internal art children (kept the thumb).");
					object obj11 = ReadMember(obj5, "gameObject");
					Type type = FindLoadedType("UnityEngine.UI.Image");
					object value = Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), AppearanceFlowSettings.ScrollbarTrackOutlineR / 255f, AppearanceFlowSettings.ScrollbarTrackOutlineG / 255f, AppearanceFlowSettings.ScrollbarTrackOutlineB / 255f, 1f);
					object obj12 = ((type == null) ? null : FindScrollbarComponent(obj11, type));
					if (obj12 != null)
					{
						obj12 = CastIl2CppObject(obj12, "UnityEngine.UI.Image");
						obj8 = obj12;
						Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: existing root Image found — tinting.");
						try
						{
							WriteMember(obj12, "sprite", null);
							WriteMember(obj12, "color", value);
							Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: outline (" + AppearanceFlowSettings.ScrollbarTrackOutlineR + "," + AppearanceFlowSettings.ScrollbarTrackOutlineG + "," + AppearanceFlowSettings.ScrollbarTrackOutlineB + ") written OK — sprite cleared.");
						}
						catch (Exception ex)
						{
							Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: outline fill FAILED: " + ex.Message);
						}
					}
					else
					{
						MethodInfo methodInfo2 = obj11.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo methodInfo3)
						{
							if (methodInfo3.Name != "AddComponent")
							{
								return false;
							}
							ParameterInfo[] parameters = methodInfo3.GetParameters();
							return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
						});
						if (methodInfo2 == null || type == null)
						{
							Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: no existing Image and AddComponent unavailable.");
						}
						else
						{
							object obj13 = ToIl2CppType(type) ?? type;
							object obj14 = methodInfo2.Invoke(obj11, new object[1] { obj13 });
							if (obj14 != null)
							{
								obj14 = CastIl2CppObject(obj14, "UnityEngine.UI.Image") ?? obj14;
								obj8 = obj14;
								WriteMember(obj14, "color", value);
								object instance4 = CastIl2CppObject(ReadMember(obj14, "rectTransform"), "UnityEngine.RectTransform");
								WriteMember(instance4, "anchorMin", MakeVector2(0f, 0f));
								WriteMember(instance4, "anchorMax", MakeVector2(1f, 1f));
								WriteMember(instance4, "offsetMin", MakeVector2(0f, 0f));
								WriteMember(instance4, "offsetMax", MakeVector2(0f, 0f));
								Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: new solid brown Image added.");
							}
							else
							{
								Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: AddComponent returned null.");
							}
						}
					}
				}
				catch (Exception ex2)
				{
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip build failed: " + ex2.Message);
				}
				object obj15 = null;
				try
				{
					object obj16 = CastIl2CppObject(ReadMember(_scrollbarThumb, "transform"), "UnityEngine.RectTransform");
					int num3 = Convert.ToInt32(ReadMember(obj16, "childCount"));
					MethodInfo method2 = obj16.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
					if (method2 != null)
					{
						for (int num4 = 0; num4 < num3; num4++)
						{
							try
							{
								DestroyUnityObject(ReadMember(method2.Invoke(obj16, new object[1] { num4 }), "gameObject"));
							}
							catch
							{
							}
						}
					}
					object gameObject = ReadMember(obj16, "gameObject");
					Type type2 = FindLoadedType("UnityEngine.UI.Image");
					object obj18 = ((type2 == null) ? null : FindScrollbarComponent(gameObject, type2));
					if (obj18 != null)
					{
						obj18 = CastIl2CppObject(obj18, "UnityEngine.UI.Image");
						obj15 = obj18;
						WriteMember(obj18, "sprite", null);
						WriteMember(obj18, "color", Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), AppearanceFlowSettings.ScrollbarTrackOutlineR / 255f, AppearanceFlowSettings.ScrollbarTrackOutlineG / 255f, AppearanceFlowSettings.ScrollbarTrackOutlineB / 255f, 1f));
						Plugin.LogSource.LogWarning("[Overlay] Scrollbar thumb: art cleared, dark outline applied.");
					}
				}
				catch (Exception ex3)
				{
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar thumb flatten failed: " + ex3.Message);
				}
				try
				{
					float scrollbarBottomExtendRatio = AppearanceFlowSettings.ScrollbarBottomExtendRatio;
					if (scrollbarBottomExtendRatio > 0f && obj8 != null)
					{
						object instance5 = CastIl2CppObject(ReadMember(obj8, "rectTransform"), "UnityEngine.RectTransform");
						float num5 = Convert.ToSingle(ReadMember(ReadMember(instance5, "rect"), "height"));
						float num6 = Math.Abs(Convert.ToSingle(ReadMember(ReadMember(instance5, "lossyScale"), "y")));
						float num7 = num5 * num6;
						object instance6 = ReadMember(instance5, "sizeDelta");
						WriteMember(instance5, "sizeDelta", MakeVector2(Convert.ToSingle(ReadMember(instance6, "x")), num5 * (1f + scrollbarBottomExtendRatio)));
						object obj19 = ReadMember(obj5, "position");
						WriteMember(obj5, "position", MakeVectorLike(obj19, Convert.ToSingle(ReadMember(obj19, "x")), trackCenterY - num7 * scrollbarBottomExtendRatio / 2f, Convert.ToSingle(ReadMember(obj19, "z"))));
						Plugin.LogSource.LogWarning("[Overlay] Scrollbar track extended " + scrollbarBottomExtendRatio * 100f + "% downward (" + num5 + " -> " + num5 * (1f + scrollbarBottomExtendRatio) + " local, " + num7 + " world).");
					}
				}
				catch (Exception ex4)
				{
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar track extension failed: " + ex4.Message);
				}
				try
				{
					if (obj8 != null)
					{
						object obj20 = ApplyScrollbarArt(obj8, "kc8_scrollbar_track.png", isThumb: false);
						if (obj20 != null)
						{
							WriteMember(obj8, "sprite", obj20);
							WriteMember(obj8, "color", Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), 1f, 1f, 1f, 1f));
							Plugin.LogSource.LogWarning("[Overlay] Scrollbar strip: pixel-art sprite applied.");
						}
					}
					if (obj15 != null)
					{
						object obj21 = ApplyScrollbarArt(obj15, "kc8_scrollbar_thumb.png", isThumb: true);
						if (obj21 != null)
						{
							WriteMember(obj15, "sprite", obj21);
							WriteMember(obj15, "color", Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), 1f, 1f, 1f, 1f));
							Plugin.LogSource.LogWarning("[Overlay] Scrollbar thumb: pixel-art sprite applied.");
						}
					}
					obj6.GetType().GetMethods().FirstOrDefault((MethodInfo methodInfo3) => methodInfo3.Name == "SetAsLastSibling" && methodInfo3.GetParameters().Length == 0)?.Invoke(obj6, null);
				}
				catch (Exception ex5)
				{
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art failed: " + ex5.Message);
				}
				try
				{
					WriteMember(_scrollbarTrack, "alpha", 1f);
				}
				catch
				{
				}
				try
				{
					WriteMember(_scrollbarThumb, "alpha", 1f);
				}
				catch
				{
				}
				object instance7 = ReadMember(obj5, "localPosition");
				object instance8 = ReadMember(obj5, "pivot");
				Plugin.LogSource.LogWarning("[Overlay] Scrollbar track offset applied " + num + ", readback " + Convert.ToSingle(ReadMember(instance7, "x")) + ", pivot y readback " + Convert.ToSingle(ReadMember(instance8, "y")) + ", span center " + spanCenter + ", visible-bar center " + trackCenterY + ".");
			}
			if (_scrollbarThumb != null && _scrollbarTrack != null)
			{
				int num8 = 4;
				int num9 = Math.Max(0, Math.Min(num8 - 1, _rosterFirstVisible / 2));
				object instance9 = CastIl2CppObject(ReadMember(_scrollbarTrack, "transform"), "UnityEngine.RectTransform");
				float num10;
				try
				{
					num10 = Convert.ToSingle(ReadMember(ReadMember(instance9, "rect"), "height"));
				}
				catch
				{
					num10 = _scrollbarBannerHeight;
				}
				float num11 = num10 * 0.6f;
				float y = num11 / 2f - (float)num9 * (num11 / (float)(num8 - 1));
				object instance10 = CastIl2CppObject(ReadMember(_scrollbarThumb, "transform"), "UnityEngine.RectTransform");
				object template3 = ReadMember(instance10, "localPosition");
				WriteMember(instance10, "localPosition", MakeVectorLike(template3, 0f, y, 0f));
			}
		}
		catch (Exception exception)
		{
			_scrollbarFailed = true;
			DestroyRosterScrollbar();
			LogFaultOnce("roster scrollbar", exception);
		}
	}

	private static void TintScrollbarTree(object panel, float r, float g, float b, float a)
	{
		TintScrollbarPanel(panel, r, g, b, a);
		try
		{
			object obj = CastIl2CppObject(ReadMember(panel, "transform"), "UnityEngine.Transform");
			MethodInfo methodInfo = obj?.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
			if (obj == null || methodInfo == null)
			{
				return;
			}
			int num = Convert.ToInt32(ReadMember(obj, "childCount"));
			for (int i = 0; i < num; i++)
			{
				object obj2 = methodInfo.Invoke(obj, new object[1] { i });
				if (obj2 != null)
				{
					object obj3 = CastIl2CppObject(ReadMember(obj2, "gameObject"), "UnityEngine.GameObject");
					if (obj3 != null)
					{
						TintScrollbarTree(obj3, r, g, b, a);
					}
				}
			}
		}
		catch
		{
		}
	}

	private static void SetScrollbarSolid(object panel, float r, float g, float b, float a)
	{
		try
		{
			Type type = FindLoadedType("UnityEngine.UI.Image");
			object value = Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), r, g, b, a);
			object obj = CastIl2CppObject(ReadMember(panel, "transform"), "UnityEngine.Transform");
			MethodInfo methodInfo = obj?.GetType().GetMethod("GetChild", new Type[1] { typeof(int) });
			if (obj == null || methodInfo == null)
			{
				return;
			}
			int num = Convert.ToInt32(ReadMember(obj, "childCount"));
			for (int i = -1; i < num; i++)
			{
				object obj2 = ((i < 0) ? panel : ReadMember(methodInfo.Invoke(obj, new object[1] { i }), "gameObject"));
				if (obj2 == null)
				{
					continue;
				}
				try
				{
					if (type != null)
					{
						object obj3 = FindScrollbarComponent(obj2, type);
						if (obj3 != null)
						{
							WriteMember(obj3, "sprite", null);
							WriteMember(obj3, "color", value);
						}
					}
					Type type2 = FindLoadedType("UnityEngine.SpriteRenderer");
					if (type2 != null)
					{
						object obj4 = FindScrollbarComponent(obj2, type2);
						if (obj4 != null)
						{
							WriteMember(obj4, "enabled", false);
						}
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static object FindScrollbarComponent(object gameObject, Type componentType)
	{
		MethodInfo? methodInfo = gameObject.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
		{
			if (method.Name != "GetComponent")
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
		});
		object obj = ToIl2CppType(componentType) ?? componentType;
		return methodInfo?.Invoke(gameObject, new object[1] { obj });
	}

	private static void TintScrollbarPanel(object panel, float r, float g, float b, float a)
	{
		try
		{
			Type type = FindLoadedType("UnityEngine.UI.Image");
			if (type == null || panel == null)
			{
				return;
			}
			object obj = ReadMember(panel, "gameObject");
			MethodInfo methodInfo = obj.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
			{
				if (method.Name != "GetComponent")
				{
					return false;
				}
				ParameterInfo[] parameters = method.GetParameters();
				return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
			});
			object obj2 = ToIl2CppType(type) ?? type;
			object obj3 = methodInfo?.Invoke(obj, new object[1] { obj2 });
			if (obj3 == null)
			{
				object obj4 = ReadMember(panel, "transform");
				int num = Convert.ToInt32(ReadMember(obj4, "childCount"));
				for (int num2 = 0; num2 < num; num2++)
				{
					if (obj3 != null)
					{
						break;
					}
					object obj5 = InvokeTransformGetChild(obj4, num2);
					if (obj5 != null)
					{
						object obj6 = ReadMember(obj5, "gameObject");
						obj3 = methodInfo?.Invoke(obj6, new object[1] { obj2 });
					}
				}
			}
			if (obj3 == null)
			{
				return;
			}
			object value = Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), r, g, b, a);
			WriteMember(obj3, "color", value);
			try
			{
				Type type2 = FindLoadedType("UnityEngine.SpriteRenderer");
				if (!(type2 != null))
				{
					return;
				}
				object obj7 = ToIl2CppType(type2) ?? type2;
				object obj8 = methodInfo?.Invoke(obj, new object[1] { obj7 });
				if (obj8 != null)
				{
					WriteMember(obj8, "color", value);
				}
				object obj9 = ReadMember(obj, "transform");
				int num3 = Convert.ToInt32(ReadMember(obj9, "childCount"));
				for (int num4 = 0; num4 < num3; num4++)
				{
					object obj10 = InvokeTransformGetChild(obj9, num4);
					if (obj10 != null)
					{
						object obj11 = ReadMember(obj10, "gameObject");
						object obj12 = methodInfo?.Invoke(obj11, new object[1] { obj7 });
						if (obj12 != null)
						{
							WriteMember(obj12, "color", value);
						}
					}
				}
			}
			catch
			{
			}
		}
		catch
		{
		}
	}

	private static object CloneScrollbarPanel(MethodInfo instantiate, object sourcePanel, object parent, float alpha)
	{
		object obj = CastIl2CppObject(instantiate.Invoke(null, new object[3] { sourcePanel, parent, true }), sourcePanel.GetType()) ?? throw new InvalidOperationException("A scrollbar banner clone was null.");
		WriteMember(obj, "alpha", alpha);
		WriteMember(obj, "interactable", false);
		WriteMember(obj, "blocksRaycasts", false);
		return obj;
	}

	private static object ApplyScrollbarArt(object image, string resourceName, bool isThumb)
	{
		try
		{
			byte[] array = ReadEmbeddedPng(resourceName);
			if (array != null)
			{
				object obj = CreateResampledArtSpriteFromPng(image, array, isThumb);
				if (obj != null)
				{
					return obj;
				}
			}
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art " + resourceName + " failed (" + ex.Message + "); using generated art.");
		}
		return CreatePixelScrollbarSprite(image, isThumb);
	}

	private static byte[] ReadEmbeddedPng(string resourceName)
	{
		Assembly assembly = typeof(RealSessionNetwork).GetTypeInfo().Assembly;
		using Stream stream = assembly.GetManifestResourceStream(resourceName);
		if (stream == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Embedded resource " + resourceName + " not found. Present: " + string.Join(", ", assembly.GetManifestResourceNames()));
			return null;
		}
		using MemoryStream memoryStream = new MemoryStream();
		stream.CopyTo(memoryStream);
		return memoryStream.ToArray();
	}

	private static bool TryDecodePngIntoTexture(object texture, Type texType, byte[] png)
	{
		foreach (MethodInfo item in from method in texType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
			where method.Name == "LoadImage" && method.GetParameters().Length == 1
			select method)
		{
			ParameterInfo parameterInfo = item.GetParameters()[0];
			object obj = ConvertByteArrayFor(png, parameterInfo.ParameterType, "LoadImage(instance)");
			if (obj == null)
			{
				continue;
			}
			try
			{
				if (Convert.ToBoolean(item.Invoke(texture, new object[1] { obj })))
				{
					Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: decoded via " + texType.Name + "." + item.Name + "(" + parameterInfo.ParameterType.Name + ").");
					return true;
				}
			}
			catch
			{
			}
		}
		Type type = FindLoadedType("UnityEngine.ImageConversion");
		if (type != null)
		{
			foreach (MethodInfo item2 in from method in type.GetMethods(BindingFlags.Static | BindingFlags.Public)
				where method.Name == "LoadImage" && method.GetParameters().Length == 2
				select method)
			{
				ParameterInfo parameterInfo2 = item2.GetParameters()[1];
				object obj3 = ConvertByteArrayFor(png, parameterInfo2.ParameterType, "LoadImage(static)");
				if (obj3 == null)
				{
					continue;
				}
				try
				{
					if (Convert.ToBoolean(item2.Invoke(null, new object[2] { texture, obj3 })))
					{
						Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: decoded via ImageConversion.LoadImage(" + parameterInfo2.ParameterType.Name + ").");
						return true;
					}
				}
				catch
				{
				}
			}
		}
		return false;
	}

	private static object ConvertByteArrayFor(byte[] png, Type arrayType, string label)
	{
		if (arrayType == typeof(byte[]))
		{
			return png;
		}
		if (!arrayType.Name.StartsWith("Il2Cpp"))
		{
			return null;
		}
		try
		{
			return Activator.CreateInstance(arrayType, png);
		}
		catch
		{
		}
		try
		{
			object obj2 = Activator.CreateInstance(arrayType, png.Length);
			MethodInfo methodInfo = arrayType.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "set_Item" && method.GetParameters().Length == 2);
			if (methodInfo == null)
			{
				Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art " + label + ": no copy ctor and no indexer on " + arrayType.Name + ".");
				return null;
			}
			for (int num = 0; num < png.Length; num++)
			{
				methodInfo.Invoke(obj2, new object[2]
				{
					num,
					png[num]
				});
			}
			return obj2;
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art " + label + ": array conversion failed: " + ex.Message);
			return null;
		}
	}

	private static object CreateResampledArtSpriteFromPng(object image, byte[] png, bool isThumb)
	{
		Type texType = FindLoadedType("UnityEngine.Texture2D");
		Type type = FindLoadedType("UnityEngine.TextureFormat");
		Type type2 = FindLoadedType("UnityEngine.Sprite");
		Type type3 = FindLoadedType("UnityEngine.Rect");
		Type type4 = FindLoadedType("UnityEngine.Vector2");
		Type type5 = FindLoadedType("UnityEngine.Color");
		if (texType == null || type == null || type2 == null || type3 == null || type4 == null || type5 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: a UnityEngine type was not loaded.");
			return null;
		}
		object instance = CastIl2CppObject(ReadMember(image, "rectTransform"), "UnityEngine.RectTransform");
		object instance2 = ReadMember(instance, "rect");
		float num = Convert.ToSingle(ReadMember(instance2, "width"));
		float num2 = Convert.ToSingle(ReadMember(instance2, "height"));
		object instance3 = ReadMember(instance, "lossyScale");
		float num3 = Math.Abs(Convert.ToSingle(ReadMember(instance3, "x")));
		float num4 = Math.Abs(Convert.ToSingle(ReadMember(instance3, "y")));
		float num5 = 1f;
		try
		{
			object obj = ReadMember(image, "canvas");
			if (obj != null)
			{
				num5 = Math.Max(0.25f, Math.Min(4f, Convert.ToSingle(ReadMember(obj, "scaleFactor"))));
			}
		}
		catch
		{
		}
		float num6 = Math.Max(1f, num * num3 * num5);
		float num7 = Math.Max(1f, num2 * num4 * num5);
		float num8 = Math.Max(1f, AppearanceFlowSettings.ScrollbarPixelScale);
		int num9 = Math.Max(4, Math.Min(64, (int)Math.Round(num6 / num8)));
		int num10 = Math.Max(8, Math.Min(1200, (int)Math.Round(num7 / num8)));
		ConstructorInfo constructor = texType.GetConstructor(new Type[4]
		{
			typeof(int),
			typeof(int),
			type,
			typeof(bool)
		});
		MethodInfo method = texType.GetMethod("GetPixel", new Type[2]
		{
			typeof(int),
			typeof(int)
		});
		MethodInfo method2 = texType.GetMethod("Apply", Type.EmptyTypes);
		if (constructor == null || method == null || method2 == null)
		{
			return null;
		}
		object obj3 = constructor.Invoke(new object[4]
		{
			4,
			4,
			Enum.Parse(type, "RGBA32"),
			false
		});
		if (!TryDecodePngIntoTexture(obj3, texType, png))
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: PNG decode failed.");
			return null;
		}
		int num11 = Convert.ToInt32(ReadMember(obj3, "width"));
		int num12 = Convert.ToInt32(ReadMember(obj3, "height"));
		float[,,] array = new float[num11, num12, 4];
		for (int i = 0; i < num12; i++)
		{
			for (int j = 0; j < num11; j++)
			{
				object instance4 = method.Invoke(obj3, new object[2] { j, i });
				array[j, i, 0] = Convert.ToSingle(ReadMember(instance4, "r")) * 255f;
				array[j, i, 1] = Convert.ToSingle(ReadMember(instance4, "g")) * 255f;
				array[j, i, 2] = Convert.ToSingle(ReadMember(instance4, "b")) * 255f;
				array[j, i, 3] = Convert.ToSingle(ReadMember(instance4, "a")) * 255f;
			}
		}
		object obj4 = constructor.Invoke(new object[4]
		{
			num9,
			num10,
			Enum.Parse(type, "RGBA32"),
			false
		});
		WriteMember(obj4, "filterMode", Enum.Parse(FindLoadedType("UnityEngine.FilterMode"), "Point"));
		WriteMember(obj4, "wrapMode", Enum.Parse(FindLoadedType("UnityEngine.TextureWrapMode"), "Clamp"));
		try
		{
			WriteMember(obj4, "hideFlags", Enum.Parse(FindLoadedType("UnityEngine.HideFlags"), "HideAndDontSave"));
		}
		catch
		{
		}
		MethodInfo method3 = texType.GetMethod("SetPixel", new Type[3]
		{
			typeof(int),
			typeof(int),
			type5
		});
		ConstructorInfo constructor2 = type5.GetConstructor(new Type[4]
		{
			typeof(float),
			typeof(float),
			typeof(float),
			typeof(float)
		});
		if (method3 == null || constructor2 == null)
		{
			return null;
		}
		object obj6 = constructor2.Invoke(new object[4] { 0f, 0f, 0f, 0f });
		for (int k = 0; k < num10; k++)
		{
			int num13 = Math.Min(num12 - 1, k * num12 / num10);
			for (int l = 0; l < num9; l++)
			{
				int num14 = Math.Min(num11 - 1, l * num11 / num9);
				WriteMember(obj6, "r", array[num14, num13, 0] / 255f);
				WriteMember(obj6, "g", array[num14, num13, 1] / 255f);
				WriteMember(obj6, "b", array[num14, num13, 2] / 255f);
				WriteMember(obj6, "a", array[num14, num13, 3] / 255f);
				method3.Invoke(obj4, new object[3] { l, k, obj6 });
			}
		}
		method2.Invoke(obj4, null);
		MethodInfo methodInfo = type2.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo methodInfo2)
		{
			if (methodInfo2.Name != "Create" || methodInfo2.IsGenericMethod)
			{
				return false;
			}
			ParameterInfo[] parameters = methodInfo2.GetParameters();
			return parameters.Length == 4 && parameters[0].ParameterType == texType && parameters[1].ParameterType.FullName == "UnityEngine.Rect" && parameters[2].ParameterType.FullName == "UnityEngine.Vector2" && parameters[3].ParameterType == typeof(float);
		});
		if (methodInfo == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: Sprite.Create(4) missing.");
			return null;
		}
		object obj7 = Activator.CreateInstance(type3, 0f, 0f, (float)num9, (float)num10);
		object obj8 = Activator.CreateInstance(type4, 0.5f, 0.5f);
		object obj9 = methodInfo.Invoke(null, new object[4] { obj4, obj7, obj8, 100f });
		if (obj9 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art: Sprite.Create returned null.");
			return null;
		}
		ScrollbarArtKeepAlive.Add(obj4);
		ScrollbarArtKeepAlive.Add(obj9);
		Plugin.LogSource.LogWarning("[Overlay] Scrollbar user art (" + (isThumb ? "thumb" : "track") + "): resampled " + num11 + "x" + num12 + " -> " + num9 + "x" + num10 + " px for " + (int)Math.Round(num6) + "x" + (int)Math.Round(num7) + " screen px (full-rect sprite, no borders).");
		return obj9;
	}

	private static object CreatePixelScrollbarSprite(object image, bool isThumb)
	{
		Type type = FindLoadedType("UnityEngine.Color");
		Type texType = FindLoadedType("UnityEngine.Texture2D");
		Type type2 = FindLoadedType("UnityEngine.Sprite");
		Type type3 = FindLoadedType("UnityEngine.TextureFormat");
		Type type4 = FindLoadedType("UnityEngine.Rect");
		Type type5 = FindLoadedType("UnityEngine.Vector2");
		if (type == null || texType == null || type2 == null || type3 == null || type4 == null || type5 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art: a UnityEngine type was not loaded.");
			return null;
		}
		ConstructorInfo constructor = type.GetConstructor(new Type[4]
		{
			typeof(float),
			typeof(float),
			typeof(float),
			typeof(float)
		});
		ConstructorInfo constructor2 = texType.GetConstructor(new Type[4]
		{
			typeof(int),
			typeof(int),
			type3,
			typeof(bool)
		});
		if (constructor == null || constructor2 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art: Texture2D/Color constructor missing.");
			return null;
		}
		object instance = CastIl2CppObject(ReadMember(image, "rectTransform"), "UnityEngine.RectTransform");
		object instance2 = ReadMember(instance, "rect");
		float num = Convert.ToSingle(ReadMember(instance2, "width"));
		float num2 = Convert.ToSingle(ReadMember(instance2, "height"));
		object instance3 = ReadMember(instance, "lossyScale");
		float num3 = Math.Abs(Convert.ToSingle(ReadMember(instance3, "x")));
		float num4 = Math.Abs(Convert.ToSingle(ReadMember(instance3, "y")));
		float num5 = 1f;
		try
		{
			object obj = ReadMember(image, "canvas");
			if (obj != null)
			{
				num5 = Math.Max(0.25f, Math.Min(4f, Convert.ToSingle(ReadMember(obj, "scaleFactor"))));
			}
		}
		catch
		{
		}
		float num6 = Math.Max(1f, num * num3 * num5);
		float num7 = Math.Max(1f, num2 * num4 * num5);
		float num8 = Math.Max(1f, AppearanceFlowSettings.ScrollbarPixelScale);
		bool flag = num6 >= num7;
		float num9 = (flag ? num6 : num7);
		float num10 = (flag ? num7 : num6);
		int num11 = Math.Max(8, Math.Min(600, (int)Math.Round(num9 / num8)));
		int num12 = Math.Max(4, Math.Min(24, (int)Math.Round(num10 / num8)));
		int num13 = (flag ? num11 : num12);
		int num14 = (flag ? num12 : num11);
		float num15 = (isThumb ? AppearanceFlowSettings.ScrollbarThumbR : AppearanceFlowSettings.ScrollbarTrackR);
		float num16 = (isThumb ? AppearanceFlowSettings.ScrollbarThumbG : AppearanceFlowSettings.ScrollbarTrackG);
		float num17 = (isThumb ? AppearanceFlowSettings.ScrollbarThumbB : AppearanceFlowSettings.ScrollbarTrackB);
		float scrollbarTrackOutlineR = AppearanceFlowSettings.ScrollbarTrackOutlineR;
		float scrollbarTrackOutlineG = AppearanceFlowSettings.ScrollbarTrackOutlineG;
		float scrollbarTrackOutlineB = AppearanceFlowSettings.ScrollbarTrackOutlineB;
		float num18 = (isThumb ? 1.28f : 1.45f);
		float num19 = Math.Min(255f, num15 * num18);
		float num20 = Math.Min(255f, num16 * num18);
		float num21 = Math.Min(255f, num17 * num18);
		float num22 = (isThumb ? 0.72f : 0.6f);
		float num23 = num15 * num22;
		float num24 = num16 * num22;
		float num25 = num17 * num22;
		float num26 = num15 * 0.8f;
		float num27 = num16 * 0.8f;
		float num28 = num17 * 0.8f;
		object obj3 = Enum.Parse(type3, "RGBA32");
		object obj4 = constructor2.Invoke(new object[4] { num13, num14, obj3, false });
		WriteMember(obj4, "filterMode", Enum.Parse(FindLoadedType("UnityEngine.FilterMode"), "Point"));
		WriteMember(obj4, "wrapMode", Enum.Parse(FindLoadedType("UnityEngine.TextureWrapMode"), "Clamp"));
		try
		{
			WriteMember(obj4, "hideFlags", Enum.Parse(FindLoadedType("UnityEngine.HideFlags"), "HideAndDontSave"));
		}
		catch
		{
		}
		MethodInfo method = texType.GetMethod("SetPixel", new Type[3]
		{
			typeof(int),
			typeof(int),
			type
		});
		MethodInfo method2 = texType.GetMethod("Apply", Type.EmptyTypes);
		if (method == null || method2 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art: SetPixel/Apply missing.");
			return null;
		}
		for (int i = 0; i < num14; i++)
		{
			for (int j = 0; j < num13; j++)
			{
				int num29 = (flag ? j : i);
				int num30 = (flag ? i : j);
				float num31;
				float num32;
				float num33;
				if (num29 == 0 || num29 == num11 - 1 || num30 == 0 || num30 == num12 - 1)
				{
					num31 = scrollbarTrackOutlineR;
					num32 = scrollbarTrackOutlineG;
					num33 = scrollbarTrackOutlineB;
				}
				else if (num29 == 1 || num30 == num12 - 2)
				{
					num31 = num23;
					num32 = num24;
					num33 = num25;
				}
				else if (num29 == num11 - 2 || num30 == 1)
				{
					num31 = num19;
					num32 = num20;
					num33 = num21;
				}
				else if (!isThumb && (num29 * 31 + num30 * 17) % 11 < 2)
				{
					num31 = num26;
					num32 = num27;
					num33 = num28;
				}
				else if (isThumb && num12 >= 6 && num30 == num12 / 2)
				{
					num31 = num26;
					num32 = num27;
					num33 = num28;
				}
				else
				{
					num31 = num15;
					num32 = num16;
					num33 = num17;
				}
				method.Invoke(obj4, new object[3]
				{
					j,
					i,
					constructor.Invoke(new object[4]
					{
						num31 / 255f,
						num32 / 255f,
						num33 / 255f,
						1f
					})
				});
			}
		}
		method2.Invoke(obj4, null);
		MethodInfo methodInfo = type2.GetMethods().FirstOrDefault(delegate(MethodInfo methodInfo2)
		{
			if (methodInfo2.Name != "Create" || methodInfo2.IsGenericMethod)
			{
				return false;
			}
			ParameterInfo[] parameters = methodInfo2.GetParameters();
			return parameters.Length == 4 && parameters[0].ParameterType == texType && parameters[1].ParameterType.FullName == "UnityEngine.Rect" && parameters[2].ParameterType.FullName == "UnityEngine.Vector2" && parameters[3].ParameterType == typeof(float);
		});
		if (methodInfo == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art: Sprite.Create(4) missing.");
			return null;
		}
		object obj6 = Activator.CreateInstance(type4, 0f, 0f, (float)num13, (float)num14);
		object obj7 = Activator.CreateInstance(type5, 0.5f, 0.5f);
		object obj8 = methodInfo.Invoke(null, new object[4] { obj4, obj6, obj7, 100f });
		if (obj8 == null)
		{
			Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art: Sprite.Create returned null.");
			return null;
		}
		ScrollbarArtKeepAlive.Add(obj4);
		ScrollbarArtKeepAlive.Add(obj8);
		Plugin.LogSource.LogWarning("[Overlay] Scrollbar pixel art (" + (isThumb ? "thumb" : "track") + "): " + num13 + "x" + num14 + " art px for " + (int)Math.Round(num6) + "x" + (int)Math.Round(num7) + " screen px (canvas " + num5 + ", pixel " + num8 + ").");
		return obj8;
	}

	private static MethodInfo FindPanelInstantiate()
	{
		Type type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
			select assembly.GetType("UnityEngine.Object", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type2) => type2 != null);
		if (type == null)
		{
			return null;
		}
		return type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(delegate(MethodInfo method)
		{
			if (method.Name != "Instantiate" || method.IsGenericMethod)
			{
				return false;
			}
			ParameterInfo[] parameters = method.GetParameters();
			return parameters.Length == 3 && parameters[1].ParameterType.FullName == "UnityEngine.Transform" && parameters[2].ParameterType == typeof(bool);
		});
	}

	private static bool UsesCrossfadeRoster(object menu)
	{
		if (menu == _crossfadeCheckedMenu)
		{
			return _rosterCrossfadeMode;
		}
		_crossfadeCheckedMenu = menu;
		_rosterCrossfadeMode = false;
		try
		{
			object instance = ReadMember(menu, "userPanel");
			object instance2 = ReadMember(menu, "remoteUserPanel");
			object instance3 = CastIl2CppObject(ReadMember(instance, "transform"), "UnityEngine.RectTransform");
			object instance4 = CastIl2CppObject(ReadMember(instance2, "transform"), "UnityEngine.RectTransform");
			_rosterCrossfadeMode = VectorMagnitudeSquared(SubtractVector(baseline: ReadMember(instance3, "position"), value: ReadMember(instance4, "position"))) < 324f;
		}
		catch
		{
			_rosterCrossfadeMode = false;
		}
		if (_rosterCrossfadeMode)
		{
			DestroyRosterOverlay();
			_crossfadeAlpha = 1f;
			_crossfadePhase = 0;
			_crossfadeShown = _rosterFirstVisible;
			Plugin.LogSource.LogWarning("[Dynamic session] Coincident banner layout detected: the roster uses the two native banners with an animated crossfade on scroll (no cloned text, no font fallbacks).");
		}
		return _rosterCrossfadeMode;
	}

	private static void TickCrossfadeRoster(object menu)
	{
		if (_crossfadePhase == 0 && _rosterFirstVisible != _crossfadeShown)
		{
			_crossfadePhase = 1;
			_crossfadeDirection = ((_rosterFirstVisible > _crossfadeShown) ? 1f : (-1f));
			try
			{
				object instance = ReadMember(menu, "userPanel");
				object instance2 = ReadMember(menu, "remoteUserPanel");
				object instance3 = CastIl2CppObject(ReadMember(instance, "transform"), "UnityEngine.RectTransform");
				object instance4 = CastIl2CppObject(ReadMember(instance2, "transform"), "UnityEngine.RectTransform");
				_crossfadeBaseTop = ReadMember(instance3, "position");
				_crossfadeBaseBottom = ReadMember(instance4, "position");
			}
			catch
			{
				_crossfadeBaseTop = null;
				_crossfadeBaseBottom = null;
				_crossfadeDirection = 0f;
			}
		}
		if (_crossfadePhase == 1)
		{
			if (Math.Abs(_rosterFirstVisible - _crossfadeShown) > 2)
			{
				_crossfadeShown = _rosterFirstVisible;
				_crossfadePhase = 2;
			}
			else
			{
				_crossfadeAlpha -= 0.09f;
				if (_crossfadeAlpha <= 0f)
				{
					_crossfadeAlpha = 0f;
					_crossfadeShown = _rosterFirstVisible;
					_crossfadePhase = 2;
				}
			}
		}
		else if (_crossfadePhase == 2)
		{
			_crossfadeAlpha += 0.14f;
			if (_crossfadeAlpha >= 1f)
			{
				_crossfadeAlpha = 1f;
				_crossfadePhase = 0;
				RestoreCrossfadeBasePositions(menu);
			}
		}
		ApplyFallbackRosterWindow(menu, BuildRosterSnapshot());
		object obj2 = ReadMember(menu, "userPanel");
		object obj3 = ReadMember(menu, "remoteUserPanel");
		if (obj2 != null && IsUnityAlive(obj2))
		{
			WriteMember(obj2, "alpha", 1f);
		}
		if (obj3 != null && IsUnityAlive(obj3))
		{
			WriteMember(obj3, "alpha", 1f);
		}
		if (!AppearanceFlowSettings.OverlayCrossfadeMotion || _crossfadePhase == 0 || _crossfadeBaseTop == null || _crossfadeBaseBottom == null)
		{
			return;
		}
		try
		{
			object obj4 = CastIl2CppObject(ReadMember(obj2, "transform"), "UnityEngine.RectTransform");
			object obj5 = CastIl2CppObject(ReadMember(obj3, "transform"), "UnityEngine.RectTransform");
			if (obj4 != null && obj5 != null && IsUnityAlive(obj4) && IsUnityAlive(obj5))
			{
				float num = 64f;
				float num2 = ((_crossfadePhase == 1) ? (1f - _crossfadeAlpha) : (0f - _crossfadeAlpha));
				float scale = _crossfadeDirection * num * 0.22f * num2;
				object delta = MakeVectorLike(_crossfadeBaseTop, 0f, 1f, 0f);
				WriteMember(obj4, "position", AddScaledVector(_crossfadeBaseTop, delta, scale));
				WriteMember(obj5, "position", AddScaledVector(_crossfadeBaseBottom, delta, scale));
			}
		}
		catch
		{
		}
	}

	private static void RestoreCrossfadeBasePositions(object menu)
	{
		if (_crossfadeBaseTop == null || _crossfadeBaseBottom == null)
		{
			return;
		}
		try
		{
			object obj = ReadMember(menu, "userPanel");
			object obj2 = ReadMember(menu, "remoteUserPanel");
			if (obj != null && obj2 != null && IsUnityAlive(obj) && IsUnityAlive(obj2))
			{
				object obj3 = CastIl2CppObject(ReadMember(obj, "transform"), "UnityEngine.RectTransform");
				object obj4 = CastIl2CppObject(ReadMember(obj2, "transform"), "UnityEngine.RectTransform");
				if (obj3 != null && obj4 != null && IsUnityAlive(obj3) && IsUnityAlive(obj4))
				{
					WriteMember(obj3, "position", _crossfadeBaseTop);
					WriteMember(obj4, "position", _crossfadeBaseBottom);
				}
			}
		}
		catch
		{
		}
	}

	private static int ReadRosterScrollStep()
	{
		if (!_scrollInputResolved)
		{
			_scrollInputResolved = true;
			Type type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
				select assembly.GetType("UnityEngine.Input", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type2) => type2 != null);
			if (type != null)
			{
				_mouseScrollDelta = type.GetProperty("mouseScrollDelta", BindingFlags.Static | BindingFlags.Public);
				_getLegacyScrollAxis = type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
				{
					ParameterInfo[] parameters = method.GetParameters();
					return (method.Name == "GetAxisRaw" || method.Name == "GetAxis") && method.ReturnType == typeof(float) && parameters.Length == 1 && parameters[0].ParameterType == typeof(string);
				});
				_getLegacyKeyDown = type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
				{
					ParameterInfo[] parameters = method.GetParameters();
					return method.Name == "GetKeyDown" && method.ReturnType == typeof(bool) && parameters.Length == 1 && parameters[0].ParameterType.IsEnum;
				});
			}
		}
		try
		{
			float num = 0f;
			if (_mouseScrollDelta != null)
			{
				num = Convert.ToSingle(ReadMember(_mouseScrollDelta.GetValue(null, null), "y"));
			}
			if (Math.Abs(num) < 0.01f && _getLegacyScrollAxis != null)
			{
				num = Convert.ToSingle(_getLegacyScrollAxis.Invoke(null, new object[1] { "Mouse ScrollWheel" }));
			}
			if (Math.Abs(num) >= 0.01f)
			{
				_scrollAccumulator += num;
				if (_scrollAccumulator <= -0.05f)
				{
					_scrollAccumulator = 0f;
					return 1;
				}
				if (_scrollAccumulator >= 0.05f)
				{
					_scrollAccumulator = 0f;
					return -1;
				}
				return 0;
			}
			_scrollAccumulator = 0f;
			if (ReadRosterKey("PageDown"))
			{
				return 1;
			}
			if (ReadRosterKey("PageUp"))
			{
				return -1;
			}
			return 0;
		}
		catch
		{
			_getLegacyScrollAxis = null;
			_getLegacyKeyDown = null;
			_mouseScrollDelta = null;
			return 0;
		}
	}

	private static bool ReadRosterKey(string keyName)
	{
		if (_getLegacyKeyDown == null)
		{
			return false;
		}
		object obj = Enum.Parse(_getLegacyKeyDown.GetParameters()[0].ParameterType, keyName, ignoreCase: false);
		return Convert.ToBoolean(_getLegacyKeyDown.Invoke(null, new object[1] { obj }));
	}

	private static bool IsNetworkingPanelOpen(object menu)
	{
		if (menu != null && _isNetworkingPanelOpen != null)
		{
			return Convert.ToBoolean(_isNetworkingPanelOpen.Invoke(menu, null));
		}
		return false;
	}

	private static bool ShouldShowRoster(object menu)
	{
		if (menu == null || IsNetworkingPanelOpen(menu))
		{
			return false;
		}
		try
		{
			object obj = ReadMember(menu, "userPanel");
			object obj2 = ReadMember(menu, "remoteUserPanel");
			if (obj == null || obj2 == null)
			{
				return false;
			}
			return Convert.ToSingle(ReadMember(obj, "alpha")) >= 0.5f;
		}
		catch
		{
			return false;
		}
	}

	private static void RefreshNativeRoster(object menu)
	{
		string[] roster = BuildRosterSnapshot();
		if (!_rosterUnavailable)
		{
			try
			{
				EnsureRosterOverlay(menu);
			}
			catch (Exception exception)
			{
				_rosterUnavailable = true;
				DestroyRosterOverlay();
				LogFaultOnce("isolated native eight-row roster", exception);
			}
		}
		if (RosterRows.Count == 8)
		{
			try
			{
				RefreshRosterRowTexts(roster);
				if (PresentFullRosterStack(menu))
				{
					return;
				}
			}
			catch (Exception exception2)
			{
				_rosterUnavailable = true;
				DestroyRosterOverlay();
				LogFaultOnce("isolated roster presentation", exception2);
			}
		}
		ApplyFallbackRosterWindow(menu, roster);
	}

	private static void EnsureRosterOverlay(object menu)
	{
		if (menu == _rosterMenu && RosterRows.Count == 8)
		{
			return;
		}
		DestroyRosterOverlay();
		_rosterMenu = menu;
		object obj = ReadMember(menu, "userPanel");
		object obj2 = ReadMember(menu, "remoteUserPanel");
		object obj3 = ReadMember(menu, "localUserText");
		object obj4 = ReadMember(menu, "remoteUserText");
		if (obj == null || obj2 == null || obj3 == null || obj4 == null)
		{
			throw new MissingMemberException("The native local or remote flag row is unavailable.");
		}
		object obj5 = CastIl2CppObject(ReadMember(obj, "transform"), "UnityEngine.RectTransform");
		object obj6 = CastIl2CppObject(ReadMember(obj2, "transform"), "UnityEngine.RectTransform");
		object obj7 = CastIl2CppObject(ReadMember(obj6, "parent"), "UnityEngine.Transform");
		if (obj5 == null || obj6 == null || obj7 == null)
		{
			throw new MissingMemberException("The native flag-row hierarchy is unavailable.");
		}
		_rosterLocalText = obj3;
		_rosterRemoteText = obj4;
		_savedLocalText = ReadMember(obj3, "text") as string;
		_savedRemoteText = ReadMember(obj4, "text") as string;
		Type? type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
			select assembly.GetType("UnityEngine.Object", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type2) => type2 != null);
		if (type == null)
		{
			throw new TypeLoadException("UnityEngine.Object is unavailable.");
		}
		MethodInfo methodInfo = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.Name == "Instantiate" && !method.IsGenericMethod && parameters.Length == 3 && parameters[1].ParameterType.FullName == "UnityEngine.Transform" && parameters[2].ParameterType == typeof(bool);
		});
		if (methodInfo == null)
		{
			throw new MissingMethodException("UnityEngine.Object.Instantiate(Object, Transform, bool)");
		}
		string savedRemoteText = _savedRemoteText;
		try
		{
			WriteMember(obj4, "text", string.Empty);
			for (int num = 0; num < 8; num++)
			{
				object instance = methodInfo.Invoke(null, new object[3] { obj2, obj7, true });
				instance = CastIl2CppObject(instance, obj2.GetType());
				if (instance == null)
				{
					throw new InvalidOperationException("A native flag CanvasGroup clone was null.");
				}
				WriteMember(instance, "alpha", 1f);
				WriteMember(instance, "interactable", false);
				WriteMember(instance, "blocksRaycasts", false);
				object obj8 = CastIl2CppObject(ReadMember(instance, "gameObject"), "UnityEngine.GameObject");
				WriteMember(obj8, "name", "EightCrowns_Overlay_Player" + (num + 1));
				object obj9 = CastIl2CppObject(ReadMember(obj8, "transform"), "UnityEngine.Transform");
				object instance2 = methodInfo.Invoke(null, new object[3] { obj4, obj9, true });
				instance2 = CastIl2CppObject(instance2, obj4.GetType());
				if (instance2 == null)
				{
					throw new InvalidOperationException("A roster row's independent native text was null.");
				}
				WriteMember(instance2, "text", string.Empty);
				SetGameObjectActive(obj8, active: false);
				RosterRows.Add(new RosterRow(instance, obj8, instance2));
			}
		}
		finally
		{
			WriteMember(obj4, "text", savedRemoteText);
		}
		if (RosterRows.Count != 8)
		{
			throw new InvalidOperationException("The native roster did not create all eight rows.");
		}
		if (!_rosterReadyLogged)
		{
			_rosterReadyLogged = true;
			Plugin.LogSource.LogWarning("[Dynamic session] Rebuilt roster created eight isolated, non-interactive native overlay rows beneath the stock P2 banner artwork. No stock CanvasGroup, active state, animation, or hierarchy is modified. Exactly two rows are visible; Mouse wheel or Page Up/Down selects the next two-slot viewport.");
		}
	}

	private static void RefreshRosterRowTexts(string[] roster)
	{
		if (RosterRows.Count == 8)
		{
			for (int i = 0; i < RosterRows.Count; i++)
			{
				WriteMember(RosterRows[i].Text ?? throw new MissingMemberException("A roster row has no independent native text."), "text", BuildRosterSlotText(i, roster));
			}
		}
	}

	private static bool PresentFullRosterStack(object menu)
	{
		if (RosterRows.Count != 8)
		{
			return false;
		}
		object instance = ReadMember(menu, "userPanel");
		object instance2 = ReadMember(menu, "remoteUserPanel");
		object instance3 = CastIl2CppObject(ReadMember(instance, "transform"), "UnityEngine.RectTransform");
		object instance4 = CastIl2CppObject(ReadMember(instance2, "transform"), "UnityEngine.RectTransform");
		object obj = ReadMember(instance3, "position");
		object value = ReadMember(instance4, "position");
		object obj2 = SubtractVector(value, obj);
		if (VectorMagnitudeSquared(obj2) < 0.001f)
		{
			if (!_rosterVirtualViewportLogged)
			{
				_rosterVirtualViewportLogged = true;
				Plugin.LogSource.LogWarning("[Dynamic session] The game exposes both stock banner CanvasGroups at one transform position. Using the two original native banners as a virtual viewport for all eight ordered slots; scrolling and blank unused slots remain enabled.");
			}
			return false;
		}
		CaptureAndBlankSourceTexts(menu);
		for (int i = 0; i < RosterRows.Count; i++)
		{
			RosterRow rosterRow = RosterRows[i];
			bool flag = IsRosterSlotVisible(i, _rosterFirstVisible);
			if (flag)
			{
				int num = i - _rosterFirstVisible;
				WriteMember(CastIl2CppObject(ReadMember(rosterRow.GameObject, "transform"), "UnityEngine.RectTransform"), "position", AddScaledVector(obj, obj2, num));
				WriteMember(rosterRow.Panel, "alpha", 1f);
			}
			SetGameObjectActive(rosterRow.GameObject, flag);
		}
		if (!_rosterLayoutVerifiedLogged)
		{
			_rosterLayoutVerifiedLogged = true;
			Plugin.LogSource.LogWarning("[Dynamic session] Rebuilt roster verified an exact two-banner native viewport for eight ordered slots: top=" + DescribeObject(obj) + ", bottom=" + DescribeObject(value) + ".");
		}
		return true;
	}

	private static bool IsRosterSlotVisible(int slot, int firstVisible)
	{
		if (slot >= firstVisible)
		{
			return slot < firstVisible + 2;
		}
		return false;
	}

	private static void CaptureAndBlankSourceTexts(object menu)
	{
		object obj = ReadMember(menu, "localUserText");
		object obj2 = ReadMember(menu, "remoteUserText");
		if (obj == null || obj2 == null)
		{
			throw new MissingMemberException("The native source labels are unavailable.");
		}
		string value = ReadMember(obj, "text") as string;
		string value2 = ReadMember(obj2, "text") as string;
		if (!string.IsNullOrEmpty(value))
		{
			_savedLocalText = StripRosterCounter(value);
		}
		if (!string.IsNullOrEmpty(value2))
		{
			_savedRemoteText = StripRosterCounter(value2);
		}
		_rosterLocalText = obj;
		_rosterRemoteText = obj2;
		WriteMember(obj, "text", string.Empty);
		WriteMember(obj2, "text", string.Empty);
		_sourceTextsBlanked = true;
	}

	private static void ApplyFallbackRosterWindow(object menu, string[] roster)
	{
		RestoreRosterSourceTexts();
		object obj = ReadMember(menu, "localUserText");
		object obj2 = ReadMember(menu, "remoteUserText");
		object obj3 = ReadMember(menu, "userPanel");
		object obj4 = ReadMember(menu, "remoteUserPanel");
		if (obj3 != null && IsUnityAlive(obj3))
		{
			WriteMember(obj3, "alpha", 1f);
		}
		if (obj4 != null && IsUnityAlive(obj4))
		{
			WriteMember(obj4, "alpha", 1f);
		}
		if (obj != null && IsUnityAlive(obj))
		{
			WriteMember(obj, "text", BuildRosterSlotText(_rosterFirstVisible, roster));
			PixelizeRosterText(obj, scaleUp: true);
		}
		if (obj2 != null && IsUnityAlive(obj2))
		{
			WriteMember(obj2, "text", BuildRosterSlotText(_rosterFirstVisible + 1, roster));
			PixelizeRosterText(obj2, scaleUp: true);
		}
	}

	private static void CheckMenuFontGlyphCoverage(object legacyFont)
	{
		if (_menuFontCoverageChecked)
		{
			return;
		}
		try
		{
			MethodInfo methodInfo = legacyFont.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
			{
				if (method.Name != "HasCharacter")
				{
					return false;
				}
				ParameterInfo[] parameters = method.GetParameters();
				return parameters.Length == 1 && (parameters[0].ParameterType == typeof(char) || parameters[0].ParameterType.FullName == "System.Char" || parameters[0].ParameterType.FullName == "Il2CppSystem.Char");
			});
			if (methodInfo == null)
			{
				return;
			}
			string text = string.Empty;
			string text2 = "abcdefghijklmnopqrstuvwxyz";
			for (int num = 0; num < text2.Length; num++)
			{
				char c = text2[num];
				try
				{
					object obj = methodInfo.Invoke(legacyFont, new object[1] { c });
					if (obj == null || !Convert.ToBoolean(obj))
					{
						text += c;
					}
				}
				catch
				{
				}
			}
			_menuFontCoverageChecked = true;
			_menuFontMissingChars = text;
			if (text.Length > 0)
			{
				Plugin.LogSource.LogWarning("[Overlay] KingdomMenu is missing glyphs: " + text + " — TMP falls back to another font for those characters; nicknames will be uppercased.");
			}
			else
			{
				Plugin.LogSource.LogWarning("[Overlay] KingdomMenu covers the full lowercase alphabet.");
			}
		}
		catch
		{
		}
	}

	private static void TickMenuFontGlyphNormalization()
	{
		try
		{
			if (_menuTmpFontAsset == null || !IsUnityAlive(_menuTmpFontAsset))
			{
				_menuFontGlyphsNormalized = true;
				return;
			}
			object obj = ReadMember(_menuTmpFontAsset, "glyphTable");
			int num = 0;
			if (obj != null)
			{
				try
				{
					num = Convert.ToInt32(ReadMember(obj, "Count"));
				}
				catch
				{
				}
			}
			if (num == 0)
			{
				_glyphNormalizationAttempts++;
				if (_glyphNormalizationAttempts > 600)
				{
					_menuFontGlyphsNormalized = true;
					Plugin.LogSource.LogWarning("[Overlay] Glyph normalization gave up: glyphTable stayed empty.");
					return;
				}
				try
				{
					_menuTmpFontAsset.GetType().GetMethods().FirstOrDefault((MethodInfo method) => method.Name == "TryAddCharacters" && method.GetParameters().Length == 1)?.Invoke(_menuTmpFontAsset, new object[1] { "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,!?-+:;'\"()/" });
					return;
				}
				catch
				{
					return;
				}
			}
			NormalizeMenuFontGlyphs(_menuTmpFontAsset);
			_menuFontGlyphsNormalized = true;
			foreach (object item in _styledNicknameTmpLabels.ToList())
			{
				try
				{
					if (item == null || !IsUnityAlive(item))
					{
						continue;
					}
					MethodInfo methodInfo = item.GetType().GetMethods().FirstOrDefault((MethodInfo method) => method.Name == "ForceMeshUpdate" && method.GetParameters().Length == 1);
					if (methodInfo != null)
					{
						methodInfo.Invoke(item, new object[1] { true });
					}
					else
					{
						item.GetType().GetMethods().FirstOrDefault((MethodInfo method) => method.Name == "ForceMeshUpdate" && method.GetParameters().Length == 0)?.Invoke(item, null);
					}
				}
				catch
				{
				}
			}
		}
		catch
		{
		}
	}

	private static void NormalizeMenuFontGlyphs(object asset)
	{
		try
		{
			MethodInfo methodInfo = asset.GetType().GetMethods().FirstOrDefault((MethodInfo method) => !(method.Name != "TryAddCharacters") && method.GetParameters().Length == 1);
			if (methodInfo != null)
			{
				methodInfo.Invoke(asset, new object[1] { "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,!?-+:;'\"()/" });
			}
			object obj = ReadMember(asset, "glyphTable");
			if (obj == null)
			{
				Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: glyphTable is null.");
				return;
			}
			List<object> list = new List<object>();
			int num = 0;
			MethodInfo methodInfo2 = null;
			try
			{
				num = Convert.ToInt32(ReadMember(obj, "Count"));
				methodInfo2 = obj.GetType().GetMethods().FirstOrDefault(delegate(MethodInfo method)
				{
					if (method.Name != "get_Item")
					{
						return false;
					}
					ParameterInfo[] parameters = method.GetParameters();
					return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
				});
			}
			catch (Exception ex)
			{
				Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: cannot enumerate glyphTable (" + ex.Message + ").");
				return;
			}
			if (methodInfo2 == null)
			{
				Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: glyphTable has no get_Item indexer.");
				return;
			}
			for (int num2 = 0; num2 < num; num2++)
			{
				try
				{
					object obj2 = methodInfo2.Invoke(obj, new object[1] { num2 });
					if (obj2 != null)
					{
						list.Add(obj2);
					}
				}
				catch
				{
				}
			}
			if (list.Count == 0)
			{
				Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: glyphTable is empty after population.");
				return;
			}
			List<float> list2 = new List<float>();
			string text = null;
			string fullName = list[0].GetType().FullName;
			foreach (object item in list)
			{
				try
				{
					list2.Add(Convert.ToSingle(ReadMember(ReadMember(item, "metrics"), "advance")));
				}
				catch (Exception ex2)
				{
					if (text == null)
					{
						try
						{
							object obj4 = ReadMember(item, "metrics");
							text = "metrics type " + obj4?.GetType().FullName + ", members: " + string.Join(", ", (from property in obj4?.GetType().GetProperties()
								select property.Name) ?? new string[0]) + " — read error: " + ex2.Message;
						}
						catch (Exception ex3)
						{
							text = "no metrics member: " + ex3.Message;
						}
					}
					list2.Add(-1f);
				}
			}
			List<float> list3 = (from value in list2
				where value > 0f
				orderby value
				select value).ToList();
			if (list3.Count == 0)
			{
				Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: no readable advances on any of " + list.Count + " glyphs. First glyph type: " + fullName + ". First failure: " + (text ?? "?") + ".");
				return;
			}
			float num3 = list3[list3.Count / 2];
			Plugin.LogSource.LogWarning("[Overlay] Glyph normalization: " + list.Count + " glyphs, median advance " + num3 + ", first glyph type " + fullName + ".");
			int num4 = 0;
			for (int num5 = 0; num5 < list.Count; num5++)
			{
				if (list2[num5] <= 0f || list2[num5] <= num3 * 1.02f)
				{
					continue;
				}
				try
				{
					object obj5 = ReadMember(list[num5], "metrics");
					WriteMember(obj5, "advance", num3);
					WriteMember(list[num5], "metrics", obj5);
					object obj6 = null;
					try
					{
						obj6 = ReadMember(list[num5], "glyphIndex");
					}
					catch
					{
					}
					Plugin.LogSource.LogWarning("[Overlay] Normalized glyph advance " + list2[num5] + " -> " + num3 + " (glyphIndex " + obj6?.ToString() + ").");
					num4++;
				}
				catch
				{
				}
			}
			Plugin.LogSource.LogWarning("[Overlay] Glyph advance normalization: " + num4 + " of " + list.Count + " glyphs corrected (median " + num3 + ").");
		}
		catch (Exception ex4)
		{
			Plugin.LogSource.LogWarning("[Overlay] Glyph normalization failed: " + ex4.Message);
		}
	}

	private static void PixelizeRosterText(object textComponent, bool scaleUp, bool counter = false)
	{
		if (textComponent == null)
		{
			return;
		}
		if (!IsUnityAlive(textComponent))
		{
			_tmpSizeTargets.Remove(textComponent);
			_nicknameSizeTargets.Remove(textComponent);
		}
		else
		{
			if (!AppearanceFlowSettings.OverlayFontSwaps)
			{
				return;
			}
			try
			{
				object obj = ReadMember(textComponent, "font");
				if (obj == null)
				{
					if (!_pixelFontSwapLogged)
					{
						_pixelFontSwapLogged = true;
						Plugin.LogSource.LogWarning("[Overlay] Nickname swap skipped: the label's font member is null or unreadable on " + textComponent.GetType().FullName + ".");
					}
					return;
				}
				string text = obj.GetType().FullName ?? string.Empty;
				bool flag = textComponent.GetType().FullName?.Contains("TextMeshPro") ?? false;
				if (flag && _tmpDonorLabel == null)
				{
					_tmpDonorLabel = textComponent;
				}
				object obj2;
				if (!counter)
				{
					obj2 = ((!flag) ? (text.Contains("TMP_FontAsset") ? ResolvePixelFontTmp() : ResolvePixelFontLegacy()) : (ResolveMenuPixelFontTmp() ?? ResolvePixelFontTmp() ?? ResolvePixelFontLegacy()));
				}
				else
				{
					obj2 = ((_menuButtonFont != null && (_menuButtonFont.GetType().FullName ?? string.Empty) == "UnityEngine.Font") ? _menuButtonFont : ResolveCounterLegacyFont());
					if (obj2 != null && ((obj2.GetType().FullName ?? string.Empty) != "UnityEngine.Font" || AreSameUnityObject(obj2, obj)))
					{
						obj2 = null;
					}
					if (obj2 == null)
					{
						DumpLegacyFontsOnce();
						return;
					}
				}
				if (obj2 == null)
				{
					if (!_pixelFontSwapLogged)
					{
						_pixelFontSwapLogged = true;
						Plugin.LogSource.LogWarning("[Overlay] Nickname swap skipped: no donor for font kind " + text + " on " + textComponent.GetType().FullName + ".");
					}
					return;
				}
				WriteMember(textComponent, "font", obj2);
				if (!_pixelFontSwapLogged)
				{
					_pixelFontSwapLogged = true;
					Plugin.LogSource.LogWarning("[Overlay] Nickname font swap on " + textComponent.GetType().Name + ".");
				}
				if (flag)
				{
					try
					{
						if (ReadMember(textComponent, "enableKerning") != null)
						{
							bool flag2 = false;
							try
							{
								flag2 = !Convert.ToBoolean(ReadMember(textComponent, "enableKerning"));
							}
							catch
							{
							}
							if (!flag2)
							{
								WriteMember(textComponent, "enableKerning", false);
								if (!_nicknameKerningLogged)
								{
									_nicknameKerningLogged = true;
									Plugin.LogSource.LogWarning("[Overlay] Kerning disabled on nicknames (bogus pixel-font kerning pair caused the Orb-i-s-on gap).");
								}
							}
						}
					}
					catch
					{
					}
				}
				if (flag && !string.IsNullOrEmpty(_menuFontMissingChars))
				{
					lock (Gate)
					{
						if (!_nicknameUppercased.Contains(textComponent))
						{
							_nicknameUppercased.Add(textComponent);
							try
							{
								string text2 = Convert.ToString(ReadMember(textComponent, "text"));
								string text3 = text2?.ToUpperInvariant();
								if (!string.IsNullOrEmpty(text2) && text2 != text3)
								{
									WriteMember(textComponent, "text", text3);
									Plugin.LogSource.LogWarning("[Overlay] Uppercased nickname \"" + text2 + "\" — the font lacks lowercase glyphs (" + _menuFontMissingChars + ").");
								}
							}
							catch
							{
							}
						}
					}
				}
				if (flag && _menuTmpFontAsset != null)
				{
					try
					{
						object obj6 = ReadMember(textComponent, "font");
						if (obj6 != null && AreSameUnityObject(obj6, _menuTmpFontAsset))
						{
							string text4 = Convert.ToString(ReadMember(textComponent, "text"));
							if (!string.IsNullOrEmpty(text4) && text4.Contains('i'))
							{
								WriteMember(textComponent, "text", text4.Replace('i', 'I'));
								if (!_nicknameISwapLogged)
								{
									_nicknameISwapLogged = true;
									Plugin.LogSource.LogWarning("[Overlay] Replaced lowercase i with capital I in nicknames — the baked i glyph carries a broken advance (\"" + text4 + "\").");
								}
							}
						}
					}
					catch
					{
					}
				}
				if (flag)
				{
					bool flag3 = false;
					foreach (object styledNicknameTmpLabel in _styledNicknameTmpLabels)
					{
						if (AreSameUnityObject(styledNicknameTmpLabel, textComponent))
						{
							flag3 = true;
							break;
						}
					}
					if (!flag3)
					{
						_styledNicknameTmpLabels.Add(textComponent);
					}
				}
				string? fullName = obj2.GetType().FullName;
				if (fullName != null && fullName.Contains("TMP") && _menuButtonLabel != null && IsUnityAlive(_menuButtonLabel))
				{
					string? fullName2 = _menuButtonLabel.GetType().FullName;
					if (fullName2 != null && fullName2.Contains("TextMeshPro"))
					{
						try
						{
							object obj8 = ReadMember(_menuButtonLabel, "fontSharedMaterial");
							if (obj8 != null)
							{
								WriteMember(textComponent, "fontSharedMaterial", obj8);
							}
						}
						catch
						{
						}
					}
				}
				if (!(scaleUp || counter))
				{
					return;
				}
				try
				{
					object obj10 = ReadMember(textComponent, "enableAutoSizing");
					if (obj10 != null && Convert.ToBoolean(obj10))
					{
						WriteMember(textComponent, "enableAutoSizing", false);
					}
				}
				catch
				{
				}
				try
				{
					object obj12 = ReadMember(textComponent, "resizeTextForBestFit");
					if (obj12 != null && Convert.ToBoolean(obj12))
					{
						WriteMember(textComponent, "resizeTextForBestFit", false);
					}
				}
				catch
				{
				}
				if (!_nicknameSizeTargets.TryGetValue(textComponent, out var value))
				{
					float num = Convert.ToSingle(ReadMember(textComponent, "fontSize"));
					if (counter)
					{
						value = CounterFontSizeTarget();
					}
					else
					{
						float num2 = 8f;
						try
						{
							num2 = Convert.ToSingle(ReadMember(_menuButtonLabel, "fontSize"));
						}
						catch
						{
						}
						if (num2 <= 0f)
						{
							num2 = 8f;
						}
						value = num2 * AppearanceFlowSettings.NicknameScale;
					}
					if (value <= 0f)
					{
						return;
					}
					_nicknameSizeTargets[textComponent] = value;
					Plugin.LogSource.LogWarning("[Overlay] Pixel font size target " + num + " -> " + value + ".");
				}
				WriteMember(textComponent, "fontSize", value);
				if (counter)
				{
					try
					{
						if (ReadMember(textComponent, "fontStyle") != null)
						{
							WriteMember(textComponent, "fontStyle", 0);
						}
					}
					catch
					{
					}
					if (AppearanceFlowSettings.CounterForceColor)
					{
						try
						{
							object value2 = Activator.CreateInstance(FindLoadedType("UnityEngine.Color"), AppearanceFlowSettings.CounterColorR / 255f, AppearanceFlowSettings.CounterColorG / 255f, AppearanceFlowSettings.CounterColorB / 255f, 1f);
							WriteMember(textComponent, "color", value2);
						}
						catch
						{
						}
					}
				}
				try
				{
					if (ReadMember(textComponent, "resizeTextMinSize") != null)
					{
						WriteMember(textComponent, "resizeTextMinSize", value);
						WriteMember(textComponent, "resizeTextMaxSize", value);
					}
				}
				catch
				{
				}
			}
			catch
			{
			}
		}
	}

	private static bool AreSameUnityObject(object a, object b)
	{
		if (a == null || b == null)
		{
			return false;
		}
		try
		{
			MethodInfo methodInfo = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
				select assembly.GetType("UnityEngine.Object", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type) => type != null)?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "op_Equality" && method.GetParameters().Length == 2);
			if (methodInfo == null)
			{
				return a == b;
			}
			return Convert.ToBoolean(methodInfo.Invoke(null, new object[2] { a, b }));
		}
		catch
		{
			return a == b;
		}
	}

	private static object ResolveCounterLegacyFont()
	{
		if (_counterFontResolved)
		{
			return _counterLegacyFont;
		}
		_counterFontResolved = true;
		try
		{
			Type type = FindLoadedType("UnityEngine.Font");
			MethodInfo methodInfo = FindLoadedType("UnityEngine.Resources")?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "FindObjectsOfTypeAll" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.Name == "Type");
			MethodInfo methodInfo2 = null;
			object obj = null;
			if (type != null && methodInfo != null)
			{
				object obj2 = ToIl2CppType(type) ?? type;
				object obj3 = methodInfo.Invoke(null, new object[1] { obj2 });
				int num = Convert.ToInt32(ReadMember(obj3, "Length"));
				methodInfo2 = obj3.GetType().GetMethods().FirstOrDefault((MethodInfo method) => method.Name == "get_Item" && method.GetParameters().Length == 1);
				for (int num2 = 0; num2 < num; num2++)
				{
					object obj4 = methodInfo2?.Invoke(obj3, new object[1] { num2 });
					if (obj4 != null)
					{
						string text = Convert.ToString(ReadMember(obj4, "name"));
						if (text == "KingdomMenu")
						{
							_counterLegacyFont = obj4;
							break;
						}
						if (text == "Kingdom" && obj == null)
						{
							obj = obj4;
						}
					}
				}
			}
			if (_counterLegacyFont == null)
			{
				_counterLegacyFont = obj;
			}
			Plugin.LogSource.LogWarning("[Overlay] Counter pixel font bound: " + ((_counterLegacyFont != null) ? Convert.ToString(ReadMember(_counterLegacyFont, "name")) : "none — will need a TMP overlay") + ".");
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Overlay] Counter font bind failed: " + ex.Message);
		}
		return _counterLegacyFont;
	}

	private static void DumpLegacyFontsOnce()
	{
		if (_legacyFontsDumped)
		{
			return;
		}
		_legacyFontsDumped = true;
		try
		{
			Type type = FindLoadedType("UnityEngine.Font");
			MethodInfo methodInfo = FindLoadedType("UnityEngine.Resources")?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "FindObjectsOfTypeAll" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.Name == "Type");
			if (type == null || methodInfo == null)
			{
				Plugin.LogSource.LogWarning("[Overlay] Legacy font probe unavailable.");
				return;
			}
			object obj = ToIl2CppType(type) ?? type;
			object obj2 = methodInfo.Invoke(null, new object[1] { obj });
			int num = Convert.ToInt32(ReadMember(obj2, "Length"));
			List<string> list = new List<string>();
			MethodInfo methodInfo2 = obj2.GetType().GetMethods().FirstOrDefault((MethodInfo method) => method.Name == "get_Item" && method.GetParameters().Length == 1);
			for (int num2 = 0; num2 < num; num2++)
			{
				if (list.Count >= 40)
				{
					break;
				}
				object obj3 = methodInfo2?.Invoke(obj2, new object[1] { num2 });
				if (obj3 != null)
				{
					try
					{
						list.Add(Convert.ToString(ReadMember(obj3, "name")));
					}
					catch
					{
						list.Add("?");
					}
				}
			}
			Plugin.LogSource.LogWarning("[Overlay] Loaded legacy fonts: " + string.Join(", ", list) + ".");
		}
		catch (Exception ex)
		{
			Plugin.LogSource.LogWarning("[Overlay] Legacy font probe failed: " + ex.Message);
		}
	}

	private static object ResolvePixelFontLegacy()
	{
		if (_menuButtonFont != null)
		{
			return _menuButtonFont;
		}
		if (_pixelFontLegacy == null)
		{
			lock (Gate)
			{
				foreach (object watchedCounterText in WatchedCounterTexts)
				{
					try
					{
						object obj = ReadMember(watchedCounterText, "font");
						if (obj != null)
						{
							_pixelFontLegacy = obj;
							break;
						}
					}
					catch
					{
					}
				}
			}
		}
		return _pixelFontLegacy;
	}

	private static object ResolvePixelFontTmp()
	{
		if (_pixelFontTmp == null)
		{
			try
			{
				Type type = FindLoadedType("TMPro.TMP_FontAsset");
				MethodInfo? obj = FindLoadedType("UnityEngine.Resources")?.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
				{
					if (method.Name != "FindObjectsOfTypeAll" || method.IsGenericMethod)
					{
						return false;
					}
					ParameterInfo[] parameters = method.GetParameters();
					return parameters.Length == 1 && (parameters[0].ParameterType == typeof(Type) || parameters[0].ParameterType.FullName == "Il2CppSystem.Type");
				});
				object obj2 = ((type == null) ? null : (ToIl2CppType(type) ?? type));
				object? obj3 = obj?.Invoke(null, new object[1] { obj2 });
				string text = string.Empty;
				foreach (object item in ((IEnumerable)obj3) ?? Enumerable.Empty<object>())
				{
					try
					{
						string text2 = Convert.ToString(ReadMember(item, "name"));
						text = ((text.Length < 80) ? (text + " " + text2) : text);
						if (!string.IsNullOrEmpty(text2) && text2.IndexOf("Kingdom", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							_pixelFontTmp = item;
							Plugin.LogSource.LogWarning("[Overlay] Banner nicknames now use the game's own pixel font (" + text2 + ").");
							break;
						}
					}
					catch
					{
					}
				}
				if (_pixelFontTmp == null && !_pixelFontSwapLogged)
				{
					Plugin.LogSource.LogWarning("[Overlay] No Kingdom TMP font asset loaded; assets seen:" + text + ".");
				}
			}
			catch
			{
			}
		}
		return _pixelFontTmp;
	}

	private static object ResolveMenuPixelFontTmp()
	{
		if (_menuTmpFontAsset != null)
		{
			return _menuTmpFontAsset;
		}
		if (_menuTmpFontAssetFailed)
		{
			return null;
		}
		try
		{
			object obj = ((_menuButtonFont != null && (_menuButtonFont.GetType().FullName ?? string.Empty) == "UnityEngine.Font") ? _menuButtonFont : ResolveCounterLegacyFont());
			if (obj == null || !IsUnityAlive(obj))
			{
				return null;
			}
			object obj2 = CastIl2CppObject(obj, "UnityEngine.Font");
			if (obj2 != null)
			{
				obj = obj2;
			}
			Type type = FindLoadedType("TMPro.TMP_FontAsset") ?? FindLoadedType("TMP_FontAsset");
			if (type == null)
			{
				_menuTmpFontAssetFailed = true;
				return null;
			}
			MethodInfo methodInfo = type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => !(method.Name != "CreateFontAsset") && method.GetParameters().Length == 8);
			object obj3 = null;
			string text = "default-overload";
			string text2 = null;
			if (methodInfo != null)
			{
				Assembly assembly = type.Assembly;
				Type type2 = type.GetNestedType("GlyphRenderMode") ?? assembly.GetType("TMPro.GlyphRenderMode") ?? assembly.GetType("GlyphRenderMode") ?? FindLoadedType("UnityEngine.TextCore.LowLevel.GlyphRenderMode") ?? FindLoadedType("TMPro.GlyphRenderMode") ?? FindLoadedType("GlyphRenderMode");
				Type type3 = type.GetNestedType("AtlasPopulationMode") ?? assembly.GetType("TMPro.AtlasPopulationMode") ?? assembly.GetType("AtlasPopulationMode") ?? FindLoadedType("TMPro.AtlasPopulationMode") ?? FindLoadedType("AtlasPopulationMode");
				object obj4 = null;
				object obj5 = null;
				string text3 = null;
				if (type3 != null)
				{
					try
					{
						obj5 = Enum.Parse(type3, "Dynamic");
					}
					catch
					{
					}
				}
				if (type2 != null)
				{
					string[] array = new string[2] { "SDFAA", "SDFAA_HINTED" };
					foreach (string text4 in array)
					{
						try
						{
							obj4 = Enum.Parse(type2, text4);
							text3 = text4;
						}
						catch
						{
							continue;
						}
						break;
					}
				}
				if (obj4 != null && obj5 != null)
				{
					try
					{
						obj3 = methodInfo.Invoke(null, new object[8] { obj, 64, 9, obj4, 1024, 1024, obj5, true });
						if (obj3 != null)
						{
							text = "sampling64-" + text3;
						}
					}
					catch (Exception ex)
					{
						obj3 = null;
						text2 = "invoke failed: " + ex.Message;
					}
				}
				else if (text2 == null)
				{
					text2 = "types unresolved: renderModeType=" + (type2?.FullName ?? "null") + " atlasModeType=" + (type3?.FullName ?? "null");
				}
			}
			else
			{
				text2 = "no 8-param CreateFontAsset found";
			}
			if (obj3 == null && text2 != null)
			{
				Plugin.LogSource.LogWarning("[Overlay] Precise font bake skipped (" + text2 + "); used the TMP default bake.");
			}
			if (obj3 == null)
			{
				MethodInfo methodInfo2 = type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(delegate(MethodInfo method)
				{
					if (method.Name != "CreateFontAsset")
					{
						return false;
					}
					ParameterInfo[] parameters = method.GetParameters();
					return parameters.Length == 1 && parameters[0].ParameterType.Name == "Font";
				});
				if (methodInfo2 == null)
				{
					_menuTmpFontAssetFailed = true;
					Plugin.LogSource.LogWarning("[Overlay] TMP_FontAsset.CreateFontAsset(Font) not found; nicknames keep Kingdom_TMP glyphs.");
					return null;
				}
				obj3 = methodInfo2.Invoke(null, new object[1] { obj });
			}
			if (obj3 == null || !IsUnityAlive(obj3))
			{
				_menuTmpFontAssetFailed = true;
				Plugin.LogSource.LogWarning("[Overlay] CreateFontAsset returned nothing; nicknames keep Kingdom_TMP glyphs.");
				return null;
			}
			_menuTmpFontAsset = obj3;
			CheckMenuFontGlyphCoverage(obj);
			Plugin.LogSource.LogWarning("[Overlay] Built a TMP font asset from KingdomMenu (" + text + ") — nicknames now use the vanilla menu glyphs.");
			return _menuTmpFontAsset;
		}
		catch (Exception ex2)
		{
			_menuTmpFontAssetFailed = true;
			Plugin.LogSource.LogWarning("[Overlay] CreateFontAsset failed: " + ex2.Message);
			return null;
		}
	}

	private static object SubtractVector(object value, object baseline)
	{
		return AddScaledVector(value, baseline, -1f);
	}

	private static object AddScaledVector(object baseline, object delta, float scale)
	{
		if (baseline == null || delta == null)
		{
			throw new ArgumentNullException((baseline == null) ? "baseline" : "delta");
		}
		object obj = Activator.CreateInstance(baseline.GetType());
		string[] array = new string[3] { "x", "y", "z" };
		foreach (string name in array)
		{
			float num = Convert.ToSingle(ReadMember(baseline, name));
			float num2 = Convert.ToSingle(ReadMember(delta, name));
			WriteMember(obj, name, num + num2 * scale);
		}
		return obj;
	}

	private static float VectorMagnitudeSquared(object value)
	{
		float num = Convert.ToSingle(ReadMember(value, "x"));
		float num2 = Convert.ToSingle(ReadMember(value, "y"));
		float num3 = Convert.ToSingle(ReadMember(value, "z"));
		return num * num + num2 * num2 + num3 * num3;
	}

	private static void SetGameObjectActive(object gameObject, bool active)
	{
		if (gameObject != null)
		{
			gameObject = CastIl2CppObject(gameObject, "UnityEngine.GameObject");
			MethodInfo? method = gameObject.GetType().GetMethod("SetActive", BindingFlags.Instance | BindingFlags.Public, null, new Type[1] { typeof(bool) }, null);
			if (method == null)
			{
				throw new MissingMethodException(gameObject.GetType().FullName, "SetActive");
			}
			method.Invoke(gameObject, new object[1] { active });
		}
	}

	private static object CastIl2CppObject(object instance, string targetTypeName)
	{
		if (instance == null)
		{
			return null;
		}
		Type type = FindLoadedType(targetTypeName);
		if (type == null)
		{
			throw new TypeLoadException(targetTypeName + " is unavailable.");
		}
		return CastIl2CppObject(instance, type);
	}

	private static object CastIl2CppObject(object instance, Type targetType)
	{
		if (instance == null)
		{
			return null;
		}
		if (targetType == null)
		{
			throw new ArgumentNullException("targetType");
		}
		if (targetType.IsInstanceOfType(instance))
		{
			return instance;
		}
		IntPtr intPtr = ReadIl2CppPointer(instance);
		if (intPtr == IntPtr.Zero)
		{
			throw new InvalidOperationException("Cannot cast a null IL2CPP pointer to " + targetType.FullName + ".");
		}
		ConstructorInfo? constructor = targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(IntPtr) }, null);
		if (constructor == null)
		{
			throw new MissingMethodException(targetType.FullName, ".ctor(IntPtr)");
		}
		return constructor.Invoke(new object[1] { intPtr });
	}

	private static IntPtr ReadIl2CppPointer(object instance)
	{
		Type type = instance.GetType();
		while (type != null)
		{
			PropertyInfo property = type.GetProperty("Pointer", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null && property.CanRead)
			{
				return (IntPtr)property.GetValue(instance, null);
			}
			FieldInfo field = type.GetField("Pointer", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				return (IntPtr)field.GetValue(instance);
			}
			type = type.BaseType;
		}
		throw new MissingMemberException(instance.GetType().FullName, "Pointer");
	}

	private static void DestroyRosterOverlay()
	{
		RestoreRosterSourceTexts();
		RosterRow[] array = RosterRows.ToArray();
		foreach (RosterRow rosterRow in array)
		{
			try
			{
				SetGameObjectActive(rosterRow.GameObject, active: false);
				DestroyUnityObject(rosterRow.GameObject);
			}
			catch
			{
			}
		}
		RosterRows.Clear();
		_rosterMenu = null;
		_rosterLocalText = null;
		_rosterRemoteText = null;
		_savedLocalText = null;
		_savedRemoteText = null;
		_sourceTextsBlanked = false;
	}

	private static void DestroyUnityObject(object instance)
	{
		if (instance == null || !IsUnityAlive(instance))
		{
			return;
		}
		try
		{
			Type type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
				select assembly.GetType("UnityEngine.Object", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type2) => type2 != null);
			MethodInfo methodInfo = ((type == null) ? null : type.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "Destroy" && method.GetParameters().Length == 1));
			if (methodInfo != null)
			{
				methodInfo.Invoke(null, new object[1] { instance });
			}
		}
		catch
		{
		}
	}

	private static bool IsUnityAlive(object instance)
	{
		if (instance == null)
		{
			return false;
		}
		try
		{
			Type type = instance.GetType();
			MethodInfo value;
			lock (UnityAliveChecks)
			{
				if (!UnityAliveChecks.TryGetValue(type, out value))
				{
					Type type2 = type;
					while (type2 != null)
					{
						if (type2.FullName == "UnityEngine.Object")
						{
							value = type2.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault((MethodInfo method) => method.Name == "op_Equality" && method.GetParameters().Length == 2);
							break;
						}
						type2 = type2.BaseType;
					}
					UnityAliveChecks[type] = value;
				}
			}
			if (value == null)
			{
				return true;
			}
			return !Convert.ToBoolean(value.Invoke(null, new object[2] { instance, null }));
		}
		catch
		{
			return false;
		}
	}

	private static void RetireDeadOverlayRefs()
	{
		if (_scrollbarTrack != null && !IsUnityAlive(_scrollbarTrack))
		{
			_scrollbarTrack = null;
		}
		if (_scrollbarThumb != null && !IsUnityAlive(_scrollbarThumb))
		{
			_scrollbarThumb = null;
		}
		if (_badgeClone != null && !IsUnityAlive(_badgeClone))
		{
			_badgeClone = null;
			_menuButtonLabel = null;
			_badgeWaitingLogged = false;
		}
		if (_versionLabel != null && !IsUnityAlive(_versionLabel))
		{
			_versionLabel = null;
			_versionLabelQuickScans = 0;
			_versionLabelDeepDone = false;
		}
	}

	private static void RestoreRosterSourceTexts()
	{
		if (!_sourceTextsBlanked)
		{
			return;
		}
		try
		{
			if (_rosterLocalText != null && _savedLocalText != null)
			{
				WriteMember(_rosterLocalText, "text", _savedLocalText);
			}
			if (_rosterRemoteText != null && _savedRemoteText != null)
			{
				WriteMember(_rosterRemoteText, "text", _savedRemoteText);
			}
		}
		catch
		{
		}
		finally
		{
			_sourceTextsBlanked = false;
		}
	}

	private static void HideRosterOverlay()
	{
		RestoreRosterSourceTexts();
		for (int i = 0; i < RosterRows.Count; i++)
		{
			try
			{
				object gameObject = RosterRows[i].GameObject;
				if (gameObject != null && IsUnityAlive(gameObject))
				{
					SetGameObjectActive(gameObject, active: false);
				}
			}
			catch
			{
			}
		}
	}

	private static string[] BuildRosterSnapshot()
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (_cachedRosterSnapshot != null && timestamp < _nextRosterSnapshotRefresh)
		{
			return _cachedRosterSnapshot;
		}
		if (TryBuildSteamRosterSnapshot(out var roster))
		{
			_cachedRosterSnapshot = roster;
			_nextRosterSnapshotRefresh = timestamp + Math.Max(1L, Stopwatch.Frequency / 4);
			return _cachedRosterSnapshot;
		}
		string[] array = new string[8];
		object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
		if (obj != null)
		{
			try
			{
				string text = _localUsername.Invoke(obj, null) as string;
				string text2 = _remoteUsername.Invoke(obj, null) as string;
				if (!IsInSteamLobby())
				{
					array[0] = text;
				}
				else if (IsLocalLobbyHost())
				{
					array[0] = text;
					if (!IsPlaceholderUsername(text2))
					{
						array[1] = text2;
					}
				}
				else
				{
					if (!IsPlaceholderUsername(text2))
					{
						array[0] = text2;
					}
					int num = ((LocalPlayerId < 1 || LocalPlayerId >= array.Length) ? 1 : LocalPlayerId);
					array[num] = text;
				}
			}
			catch
			{
			}
		}
		lock (Gate)
		{
			foreach (PeerState value in HostPeers.Values)
			{
				if (value.PlayerId >= 1 && value.PlayerId < array.Length)
				{
					array[value.PlayerId] = ResolvePeerName(value);
				}
			}
		}
		_cachedRosterSnapshot = array;
		_nextRosterSnapshotRefresh = timestamp + Math.Max(1L, Stopwatch.Frequency / 4);
		return _cachedRosterSnapshot;
	}

	private static bool TryBuildSteamRosterSnapshot(out string[] roster)
	{
		roster = null;
		try
		{
			ResolveSteamRosterApi();
			object obj = ReadStaticMember(_steamPlatformManagerType, "Inst");
			if (obj == null || !Convert.ToBoolean(ReadMember(obj, "inLobby")))
			{
				return false;
			}
			object obj2 = ReadMember(obj, "activeLobbyID");
			object lobbyOwner = _getLobbyOwner.Invoke(null, new object[1] { obj2 });
			int num = Math.Min(8, Math.Max(0, Convert.ToInt32(_getNumLobbyMembers.Invoke(null, new object[1] { obj2 }))));
			if (_lastLoggedLobbyMemberCount != num)
			{
				_lastLoggedLobbyMemberCount = num;
				Plugin.LogSource.LogWarning("[Dynamic session] Local Steam roster sees " + num + "/8 lobby member(s).");
			}
			List<SteamRosterMember> list = new List<SteamRosterMember>();
			for (int i = 0; i < num; i++)
			{
				object obj3 = _getLobbyMemberByIndex.Invoke(null, new object[2] { obj2, i });
				string text = _friendPersonaName.Invoke(null, new object[1] { obj3 }) as string;
				if (string.IsNullOrWhiteSpace(text))
				{
					text = ((obj3 == null) ? "Steam player" : obj3.ToString());
				}
				list.Add(new SteamRosterMember(obj3, text));
			}
			string[] array = new string[8];
			SteamRosterMember steamRosterMember = list.FirstOrDefault((SteamRosterMember member) => SteamIdsEqual(member.SteamId, lobbyOwner));
			if (steamRosterMember == null && list.Count > 0)
			{
				steamRosterMember = list[0];
			}
			if (steamRosterMember != null)
			{
				array[0] = steamRosterMember.Name;
			}
			int num2 = 1;
			foreach (SteamRosterMember item in list)
			{
				if (!SteamIdsEqual(item.SteamId, steamRosterMember?.SteamId))
				{
					if (num2 >= array.Length)
					{
						break;
					}
					array[num2++] = item.Name;
				}
			}
			roster = array;
			return true;
		}
		catch (Exception exception)
		{
			if (!_steamRosterFallbackLogged)
			{
				_steamRosterFallbackLogged = true;
				Plugin.LogSource.LogWarning("[Dynamic session] Steam lobby roster enumeration is temporarily unavailable; the native two-name fallback remains active. Details: " + Unwrap(exception));
			}
			roster = null;
			return false;
		}
	}

	private static void ResolveSteamRosterApi()
	{
		if (_steamRosterApiResolved)
		{
			if (_getNumLobbyMembers == null || _getLobbyMemberByIndex == null || _getLobbyOwner == null || _friendPersonaName == null)
			{
				throw new MissingMethodException("The Steam lobby roster API is unavailable.");
			}
			return;
		}
		Type type = FindLoadedType("Steamworks.SteamMatchmaking");
		Type type2 = FindLoadedType("Steamworks.SteamFriends");
		if (type == null || type2 == null)
		{
			throw new TypeLoadException("The Steamworks roster types are unavailable.");
		}
		_getNumLobbyMembers = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault((MethodInfo method) => method.Name == "GetNumLobbyMembers" && method.GetParameters().Length == 1);
		_getLobbyMemberByIndex = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault((MethodInfo method) => method.Name == "GetLobbyMemberByIndex" && method.GetParameters().Length == 2 && method.GetParameters()[1].ParameterType == typeof(int));
		_getLobbyOwner = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault((MethodInfo method) => method.Name == "GetLobbyOwner" && method.GetParameters().Length == 1);
		_friendPersonaName = type2.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault((MethodInfo method) => method.Name == "GetFriendPersonaName" && method.GetParameters().Length == 1);
		if (_getNumLobbyMembers == null || _getLobbyMemberByIndex == null || _getLobbyOwner == null || _friendPersonaName == null)
		{
			throw new MissingMethodException("A required Steam lobby roster method is unavailable.");
		}
		_steamRosterApiResolved = true;
	}

	private static void ResolveSteamPacketApi()
	{
		Type type = FindLoadedType("Steamworks.SteamNetworking");
		if (type == null)
		{
			throw new TypeLoadException("Steamworks.SteamNetworking is unavailable.");
		}
		_isP2PPacketAvailable = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.Name == "IsP2PPacketAvailable" && method.ReturnType == typeof(bool) && parameters.Length == 2 && parameters[0].ParameterType.IsByRef && parameters[1].ParameterType == typeof(int);
		});
		_readP2PPacket = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.Name == "ReadP2PPacket" && method.ReturnType == typeof(bool) && parameters.Length == 5 && parameters[1].ParameterType == typeof(uint) && parameters[2].ParameterType.IsByRef && parameters[3].ParameterType.IsByRef && parameters[4].ParameterType == typeof(int);
		});
		if (_isP2PPacketAvailable == null || _readP2PPacket == null)
		{
			throw new MissingMethodException("The Steamworks.NET P2P packet read API expected by Kingdom Two Crowns 2.1.4 was not found.");
		}
		_sendP2PPacket = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(delegate(MethodInfo method)
		{
			ParameterInfo[] parameters = method.GetParameters();
			return method.Name == "SendP2PPacket" && method.ReturnType == typeof(bool) && parameters.Length == 5 && parameters[2].ParameterType == typeof(uint) && parameters[4].ParameterType == typeof(int);
		});
		if (_sendP2PPacket == null)
		{
			throw new MissingMethodException("The Steamworks.NET SendP2PPacket API expected by Kingdom Two Crowns 2.1.4 was not found.");
		}
	}

	private static void ResolveSteamIdentityApi()
	{
		Type type = FindLoadedType("Steamworks.SteamUser");
		if (type == null)
		{
			throw new TypeLoadException("Steamworks.SteamUser is unavailable.");
		}
		_getLocalSteamId = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault((MethodInfo method) => method.Name == "GetSteamID" && method.GetParameters().Length == 0);
		if (_getLocalSteamId == null)
		{
			throw new MissingMethodException("Steamworks.SteamUser.GetSteamID was not found.");
		}
	}

	private static Type FindLoadedType(string fullName)
	{
		return (from assembly in AppDomain.CurrentDomain.GetAssemblies()
			select assembly.GetType(fullName, throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type) => type != null);
	}

	private static bool SteamIdsEqual(object first, object second)
	{
		if (first == null || second == null)
		{
			return false;
		}
		if (!first.Equals(second))
		{
			return string.Equals(first.ToString(), second.ToString(), StringComparison.Ordinal);
		}
		return true;
	}

	private static bool IsPlaceholderUsername(string value)
	{
		if (!string.IsNullOrWhiteSpace(value) && !(value == "--") && !(value == "..."))
		{
			return value == "…";
		}
		return true;
	}

	private static string StripRosterCounter(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return value;
		}
		int num = value.LastIndexOf("  [", StringComparison.Ordinal);
		if (num < 0 || !value.EndsWith("/8]", StringComparison.Ordinal))
		{
			return value;
		}
		return value.Substring(0, num);
	}

	private static string BuildRosterSlotText(int displaySlot, string[] roster)
	{
		if (roster == null || displaySlot < 0 || displaySlot >= roster.Length || string.IsNullOrWhiteSpace(roster[displaySlot]))
		{
			return string.Empty;
		}
		return roster[displaySlot];
	}

	private static string LocalizedWaitingForPlayer()
	{
		string text = "English";
		try
		{
			object obj = _getSystemLanguage.Invoke(null, null);
			if (obj != null)
			{
				text = obj.ToString();
			}
		}
		catch
		{
		}
		switch (text)
		{
		case "Russian":
			return "Ожидание игрока";
		case "Ukrainian":
			return "Очікування гравця";
		case "German":
			return "Warten auf Spieler";
		case "French":
			return "En attente d’un joueur";
		case "Spanish":
			return "Esperando jugador";
		case "Italian":
			return "In attesa di un giocatore";
		case "Portuguese":
			return "Aguardando jogador";
		case "Polish":
			return "Oczekiwanie na gracza";
		case "Turkish":
			return "Oyuncu bekleniyor";
		case "Chinese":
		case "ChineseSimplified":
			return "等待玩家";
		case "ChineseTraditional":
			return "等待玩家";
		case "Japanese":
			return "プレイヤーを待機中";
		case "Korean":
			return "플레이어 대기 중";
		default:
			return "Waiting for player";
		}
	}

	private static string ResolvePeerName(PeerState peer)
	{
		if (peer == null || peer.PeerToken == null)
		{
			return "Steam player";
		}
		try
		{
			if (_friendPersonaName == null)
			{
				Type type = (from assembly in AppDomain.CurrentDomain.GetAssemblies()
					select assembly.GetType("Steamworks.SteamFriends", throwOnError: false, ignoreCase: false)).FirstOrDefault((Type type2) => type2 != null);
				if (type != null)
				{
					_friendPersonaName = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault(delegate(MethodInfo method)
					{
						ParameterInfo[] parameters = method.GetParameters();
						return method.Name == "GetFriendPersonaName" && parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(peer.PeerToken);
					});
				}
			}
			if (_friendPersonaName != null)
			{
				string text = _friendPersonaName.Invoke(null, new object[1] { peer.PeerToken }) as string;
				if (!string.IsNullOrWhiteSpace(text))
				{
					return text;
				}
			}
		}
		catch
		{
		}
		if (!peer.Description.StartsWith("Steam peer ", StringComparison.Ordinal))
		{
			return peer.Description;
		}
		return peer.Description.Substring("Steam peer ".Length);
	}

	private static object ConvertMonarch(object value)
	{
		return Enum.ToObject(RequireGameType("MonarchType"), Convert.ToInt32(value));
	}

	private static void SetSlot(int playerId, object model)
	{
		if (playerId < 0 || playerId > 7 || model == null)
		{
			return;
		}
		object value = _slotsField.GetValue(null);
		if (value == null)
		{
			return;
		}
		if (value is Array array)
		{
			if (playerId < array.Length)
			{
				array.SetValue(model, playerId);
			}
			return;
		}
		PropertyInfo property = value.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null)
		{
			property.SetValue(value, model, new object[1] { playerId });
		}
	}

	private static void ClearSlot(int playerId)
	{
		object value = _slotsField.GetValue(null);
		if (value == null)
		{
			return;
		}
		if (value is Array array)
		{
			if (playerId >= 0 && playerId < array.Length)
			{
				array.SetValue(null, playerId);
			}
			return;
		}
		PropertyInfo property = value.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null)
		{
			property.SetValue(value, null, new object[1] { playerId });
		}
	}

	private static object[] ReadAllSlots()
	{
		object value = _slotsField.GetValue(null);
		if (value == null)
		{
			return new object[0];
		}
		List<object> list = new List<object>();
		if (value is Array array)
		{
			foreach (object item in array)
			{
				if (item != null)
				{
					list.Add(item);
				}
			}
			return list.ToArray();
		}
		PropertyInfo property = value.GetType().GetProperty("Length", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		PropertyInfo property2 = value.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property == null || property2 == null)
		{
			return list.ToArray();
		}
		int num = Convert.ToInt32(property.GetValue(value, null));
		for (int i = 0; i < num; i++)
		{
			object value2 = property2.GetValue(value, new object[1] { i });
			if (value2 != null)
			{
				list.Add(value2);
			}
		}
		return list.ToArray();
	}

	private static object ReadMember(object instance, string name)
	{
		if (instance == null)
		{
			return null;
		}
		Type type = instance.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null && property.CanRead)
		{
			return property.GetValue(instance, null);
		}
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (field != null)
		{
			return field.GetValue(instance);
		}
		throw new MissingMemberException(type.FullName, name);
	}

	private static object ReadStaticMember(Type type, string name)
	{
		if (type == null)
		{
			return null;
		}
		PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
		if (property != null && property.CanRead)
		{
			return property.GetValue(null, null);
		}
		FieldInfo field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
		if (field != null)
		{
			return field.GetValue(null);
		}
		throw new MissingMemberException(type.FullName, name);
	}

	private static void WriteMember(object instance, string name, object value)
	{
		if (instance == null)
		{
			throw new ArgumentNullException("instance");
		}
		Type type = instance.GetType();
		PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (property != null && property.CanWrite)
		{
			SetReflectedValue(instance, property, value);
			return;
		}
		FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (field != null)
		{
			SetReflectedValue(instance, field, value);
			return;
		}
		throw new MissingMemberException(type.FullName, name);
	}

	private static void SetReflectedValue(object instance, MemberInfo member, object value)
	{
		PropertyInfo propertyInfo = member as PropertyInfo;
		FieldInfo fieldInfo = member as FieldInfo;
		Type targetType = ((propertyInfo != null) ? propertyInfo.PropertyType : fieldInfo.FieldType);
		try
		{
			if (propertyInfo != null)
			{
				propertyInfo.SetValue(instance, value, null);
			}
			else
			{
				fieldInfo.SetValue(instance, value);
			}
		}
		catch (ArgumentException)
		{
			object value2 = ConvertValue(value, targetType);
			if (propertyInfo != null)
			{
				propertyInfo.SetValue(instance, value2, null);
			}
			else
			{
				fieldInfo.SetValue(instance, value2);
			}
		}
	}

	private static object ConvertValue(object value, Type targetType)
	{
		if (value == null || targetType.IsInstanceOfType(value))
		{
			return value;
		}
		if (targetType.IsEnum)
		{
			return Enum.ToObject(targetType, Convert.ToInt32(value));
		}
		if (!targetType.IsValueType)
		{
			IntPtr intPtr = TryReadNativePointer(value);
			if (intPtr != IntPtr.Zero)
			{
				ConstructorInfo constructor = targetType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[1] { typeof(IntPtr) }, null);
				if (constructor != null)
				{
					return constructor.Invoke(new object[1] { intPtr });
				}
			}
		}
		return Convert.ChangeType(value, targetType);
	}

	private static string DescribePeer(object connection)
	{
		if (connection == null)
		{
			return "<no connection>";
		}
		lock (Gate)
		{
			PeerState value;
			return HostPeers.TryGetValue(connection, out value) ? value.Description : "Steam peer";
		}
	}

	private static string DescribePeerToken(object peerToken)
	{
		if (peerToken == null)
		{
			return "Steam peer";
		}
		try
		{
			return "Steam peer " + peerToken;
		}
		catch
		{
			return "Steam peer";
		}
	}

	private static string DescribeObject(object value)
	{
		if (value == null)
		{
			return "<unknown ruler>";
		}
		try
		{
			return value.ToString();
		}
		catch
		{
			return "<" + value.GetType().Name + ">";
		}
	}

	private static void LogFaultOnce(string operation, Exception exception)
	{
		lock (Gate)
		{
			if (!LoggedFaults.Add(operation))
			{
				return;
			}
		}
		Plugin.LogSource.LogError("[Dynamic session] " + operation + " failed once and has been contained; this callback will not emit a repeating exception loop. Details: " + Unwrap(exception));
	}

	private static Type RequireGameType(string name)
	{
		return RequireType("Assembly-CSharp", name);
	}

	private static Type RequireCoreType(string name)
	{
		return RequireType("KingdomEightCrowns", name);
	}

	private static Type RequireType(string assemblyName, string typeName)
	{
		Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly candidate) => string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
		if (assembly == null)
		{
			throw new TypeLoadException("Required assembly is not loaded: " + assemblyName);
		}
		Type? type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
		if (type == null)
		{
			throw new TypeLoadException("Required type was not found: " + typeName);
		}
		return type;
	}

	private static FieldInfo RequireField(Type type, string name, BindingFlags flags)
	{
		FieldInfo? field = type.GetField(name, flags);
		if (field == null)
		{
			throw new MissingFieldException(type.FullName, name);
		}
		return field;
	}

	private static PropertyInfo RequireProperty(Type type, string name, BindingFlags flags)
	{
		PropertyInfo? property = type.GetProperty(name, flags);
		if (property == null)
		{
			throw new MissingMemberException(type.FullName, name);
		}
		return property;
	}

	private static MethodInfo FindUnique(Type type, string name, Func<MethodInfo, bool> filter)
	{
		MethodInfo[] array = (from method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
			where method.Name == name && filter(method)
			select method).ToArray();
		if (array.Length != 1)
		{
			throw new MissingMethodException("Expected one " + type.Name + "." + name + " method in 2.1.4, found " + array.Length + ".");
		}
		return array[0];
	}

	private static Type FindIteratorStateMachine(Type declaringType, MethodInfo iteratorFactory, string operationName)
	{
		if (declaringType == null)
		{
			throw new ArgumentNullException("declaringType");
		}
		if (iteratorFactory == null)
		{
			throw new ArgumentNullException("iteratorFactory");
		}
		try
		{
			object[] customAttributes = iteratorFactory.GetCustomAttributes(inherit: false);
			foreach (object obj in customAttributes)
			{
				PropertyInfo property = obj.GetType().GetProperty("StateMachineType", BindingFlags.Instance | BindingFlags.Public);
				if (!(property == null) && !(property.PropertyType != typeof(Type)))
				{
					Type type = property.GetValue(obj, null) as Type;
					if (HasIteratorMoveNext(type))
					{
						return type;
					}
				}
			}
		}
		catch
		{
		}
		string normalizedOperation = NormalizeGeneratedName(operationName);
		Type[] array = (from type2 in declaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
			where HasIteratorMoveNext(type2) && NormalizeGeneratedName(type2.Name).IndexOf(normalizedOperation, StringComparison.Ordinal) >= 0
			select type2).ToArray();
		if (array.Length == 1)
		{
			Plugin.LogSource.LogWarning("[Dynamic session] Resolved native " + operationName + " coroutine as " + array[0].Name + " (IL2CPP-safe generated-name matching).");
			return array[0];
		}
		string text = string.Join(", ", (from type2 in declaringType.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(HasIteratorMoveNext)
			select type2.Name).ToArray());
		throw new MissingMethodException("Could not uniquely resolve the " + operationName + " iterator state machine in " + declaringType.FullName + "; matched " + array.Length + ". Nested MoveNext types: " + (string.IsNullOrEmpty(text) ? "<none>" : text) + ".");
	}

	private static MethodInfo FindOptionalUnique(Type type, string name, Func<MethodInfo, bool> filter, string capability)
	{
		try
		{
			return FindUnique(type, name, filter);
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogError("[Dynamic session] Optional " + capability + " is unavailable; its fallback remains active and the eight-player network mode will continue. Details: " + Unwrap(exception));
			return null;
		}
	}

	private static bool HasIteratorMoveNext(Type type)
	{
		if (type == null)
		{
			return false;
		}
		return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any((MethodInfo method) => method.Name == "MoveNext" && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0);
	}

	private static string NormalizeGeneratedName(string value)
	{
		return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
	}

	private static void Patch(Harmony harmony, MethodInfo original, string prefixName, string postfixName)
	{
		HarmonyMethod prefix = ((prefixName == null) ? null : new HarmonyMethod(FindPatch(prefixName)));
		HarmonyMethod postfix = ((postfixName == null) ? null : new HarmonyMethod(FindPatch(postfixName)));
		harmony.Patch(original, prefix, postfix);
	}

	private static bool TryPatchCapability(Harmony harmony, string capability, Func<MethodInfo> resolveOriginal, string prefixName, string postfixName)
	{
		try
		{
			Patch(harmony, resolveOriginal(), prefixName, postfixName);
			return true;
		}
		catch (Exception exception)
		{
			Plugin.LogSource.LogError("[Dynamic session] Optional " + capability + " patch was unavailable and was contained without unloading transport, player assignment, or ruler synchronization. Details: " + Unwrap(exception));
			return false;
		}
	}

	private static MethodInfo FindPatch(string name)
	{
		MethodInfo? method = typeof(RealSessionNetwork).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
		if (method == null)
		{
			throw new MissingMethodException("Dynamic-session callback was not found: " + name);
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

	private static bool ConnectionsEqual(object left, object right)
	{
		return NativeConnectionComparer.Instance.Equals(left, right);
	}

	private static void RebindCatchupRoutesUnsafe(object previousConnection, object replacementConnection)
	{
		object[] array = (from pair in CatchupRoutes
			where ConnectionsEqual(pair.Value, previousConnection)
			select pair.Key).ToArray();
		foreach (object key in array)
		{
			CatchupRoutes[key] = replacementConnection;
		}
	}

	private static void TickPeerWatchdog()
	{
		if (!IsLocalLobbyHost())
		{
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		if (timestamp < _nextPeerWatchdog)
		{
			return;
		}
		_nextPeerWatchdog = timestamp + Math.Max(1L, Stopwatch.Frequency);
		PeerState[] array;
		lock (Gate)
		{
			array = (from peerState in HostPeers.Values.Where((PeerState peerState) => peerState.CatchupPending).Distinct()
				orderby peerState.PlayerId
				select peerState).ToArray();
		}
		PeerState[] array2 = array;
		foreach (PeerState peer in array2)
		{
			string peerTimeoutReason = GetPeerTimeoutReason(peer, timestamp);
			if (peerTimeoutReason == null)
			{
				continue;
			}
			lock (Gate)
			{
				if (!HostPeers.Values.Any((PeerState candidate) => candidate == peer) || !peer.CatchupPending)
				{
					continue;
				}
				goto IL_0124;
			}
			IL_0124:
			Plugin.LogSource.LogWarning("[Dynamic session] Join watchdog timed out Player " + (peer.PlayerId + 1) + " only: " + peerTimeoutReason + " (SteamId=" + DescribePeerToken(peer.PeerToken) + ", transport=" + DescribeTransport(peer.Connection) + ", packets=" + peer.ReceivedPackets + "). The host and every ready peer remain online.");
			DisposePeerIsolated(peer, "peer-scoped join timeout: " + peerTimeoutReason);
		}
	}

	private static string GetPeerTimeoutReason(PeerState peer, long now)
	{
		if (peer == null || !peer.CatchupPending || peer.AcceptedAt <= 0 || now <= peer.AcceptedAt)
		{
			return null;
		}
		double num = ElapsedSeconds(peer.AcceptedAt, now);
		if (num >= 300.0)
		{
			return "native initialization exceeded " + 300 + " seconds";
		}
		if (peer.ReceivedPackets == 0 && num >= 60.0)
		{
			return "no first native packet arrived within " + 60 + " seconds";
		}
		double num2 = ElapsedSeconds((peer.LastPacketAt > 0) ? peer.LastPacketAt : peer.AcceptedAt, now);
		if (peer.ReceivedPackets > 0 && num2 >= 120.0)
		{
			return "joining transport was idle for " + 120 + " seconds at stage " + ((peer.CatchupRequestedAt > 0) ? "catch-up" : "handshake");
		}
		return null;
	}

	private static double ElapsedSeconds(long start, long end)
	{
		return (double)Math.Max(0L, end - start) / (double)Stopwatch.Frequency;
	}

	private static string DescribeTransport(object connection)
	{
		IntPtr intPtr = TryReadNativePointer(connection);
		if (!(intPtr == IntPtr.Zero))
		{
			return "native:0x" + intPtr.ToInt64().ToString("X");
		}
		return "managed:" + ((connection == null) ? "null" : RuntimeHelpers.GetHashCode(connection).ToString("X8"));
	}

	private static IntPtr TryReadNativePointer(object value)
	{
		if (value == null)
		{
			return IntPtr.Zero;
		}
		try
		{
			return ReadIl2CppPointer(value);
		}
		catch
		{
			return IntPtr.Zero;
		}
	}
}
