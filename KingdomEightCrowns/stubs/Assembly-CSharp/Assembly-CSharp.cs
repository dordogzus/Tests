// Reference stub: members mirror Il2CppInterop's generated Assembly-CSharp.dll for Kingdom Two Crowns 2.1.4
// (only what the core plugin binds to directly; everything else is reached by name through AccessTools).
// Bodies never run. Il2CppInterop exposes game fields as properties, hence the property shapes below.
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Il2CppDictionary = Il2CppSystem.Collections.Generic;

public enum CRPCType { }
public enum MonarchType { }
public enum SteedType { }

public struct PoolDespawn
{
    public short dynSyncID;
}

public class IUnitController : Il2CppSystem.Object
{
    public IUnitController(IntPtr pointer) : base(pointer) { }
}

public class RiderCrown : MonoBehaviour
{
    public RiderCrown(IntPtr pointer) : base(pointer) { }
    public enum CrownType { }
}

public class ItemOfPower : MonoBehaviour
{
    public ItemOfPower(IntPtr pointer) : base(pointer) { }
    public enum ItemType { }
}

public class PlayerModel : Il2CppSystem.Object
{
    public PlayerModel(IntPtr pointer) : base(pointer) { }
    public PlayerModel(Color skinColour, Color primaryColour, Color secondaryColour, Color emblemColour, MonarchType monarchType,
        int playerId, int steedNetID, SteedType steedType, RiderCrown.CrownType crownType, ItemOfPower.ItemType rulerItemType)
        : base(IntPtr.Zero) => throw null;
    public Color skinColour => throw null;
    public Color primaryColour => throw null;
    public Color secondaryColour => throw null;
    public Color emblemColour => throw null;
    public short monarchType => throw null;
    public int playerId => throw null;
    public int steedNetID { get => throw null; set => throw null; }
    public short steedType { get => throw null; set => throw null; }
    public short crownType => throw null;
    public short rulerItemType => throw null;
}

public class CRPCHeader : Il2CppSystem.Object
{
    public CRPCHeader(IntPtr pointer) : base(pointer) { }
    public short NetID => throw null;
    public CRPCType HeaderType => throw null;
    public CRPCStamp stampRef => throw null;
    public GameObject referencedGO => throw null;
    public Il2CppDictionary.List<NetworkPostbox.DynAction> RemoteMethodList => throw null;
    public void ReceiveNetIDOverride(int netId) => throw null;
}

public class CRPCStamp : MonoBehaviour
{
    public CRPCStamp(IntPtr pointer) : base(pointer) { }
    public short NetIDCacheHack { get => throw null; set => throw null; }
    public CRPCHeader parentHeaderRef { get => throw null; set => throw null; }
    public bool semiStatic { get => throw null; set => throw null; }
    public void Setup(CRPCHeader header, bool semiStatic) => throw null;
}

public class CRPCAutoRegister : MonoBehaviour
{
    public CRPCAutoRegister(IntPtr pointer) : base(pointer) { }
}

public class NetworkPostbox : MonoBehaviour
{
    public NetworkPostbox(IntPtr pointer) : base(pointer) { }
    public class DynAction : Il2CppSystem.Object
    {
        public DynAction(IntPtr pointer) : base(pointer) { }
    }
    public static NetworkPostbox Instance => throw null;
    public static short ClientIDOffset => throw null;
    public static short Player2ID => throw null;
    public Il2CppDictionary.Dictionary<short, CRPCHeader> DynamicObjects => throw null;
    public Il2CppDictionary.Dictionary<GameObject, CRPCHeader> MasterDynCRPCHLookup => throw null;
    public short ReserveNextNetId(CRPCType type) => throw null;
    public CRPCHeader RegisterObject(GameObject go, short netId, CRPCType type) => throw null;
}

public class NetworkBigBoss : MonoBehaviour
{
    public NetworkBigBoss(IntPtr pointer) : base(pointer) { }
    public static NetworkBigBoss Instance => throw null;
    public static bool HasWorldAuth => throw null;
    public PlayerModel p1Model => throw null;
    public PlayerModel p2Model => throw null;
}

public class Player : MonoBehaviour
{
    public Player(IntPtr pointer) : base(pointer) { }
    public int playerId { get => throw null; set => throw null; }
    public bool hasLocalAuthority { get => throw null; set => throw null; }
    public CRPCHeader parentHeaderRef { get => throw null; set => throw null; }
    public IUnitController CurrentUnitController => throw null;
    public MonarchType _model => throw null;
    public RiderCrown.CrownType _crownType => throw null;
    public Color _skinColor => throw null;
    public int _currencyDroppedFromDamageIndex { get => throw null; set => throw null; }
    public int _eatRPCIndex { get => throw null; set => throw null; }
    public int _rearRPCIndex { get => throw null; set => throw null; }
    public int _sendInput { get => throw null; set => throw null; }
    public int _sendTunneling { get => throw null; set => throw null; }
    public int _setCrownStateIndex { get => throw null; set => throw null; }
    public int _sparkleRPCIndex { get => throw null; set => throw null; }
    public int _staminaRPCIndex { get => throw null; set => throw null; }
    public int _walletCountIndex { get => throw null; set => throw null; }
    public PlayerModel GetAppearance() => throw null;
    public void SetupPlayerModel() => throw null;
}

public class Kingdom : MonoBehaviour
{
    public Kingdom(IntPtr pointer) : base(pointer) { }
    public Il2CppReferenceArray<Player> _activePlayers { get => throw null; set => throw null; }
    public Player playerTwo => throw null;
}

public class Game : MonoBehaviour
{
    public Game(IntPtr pointer) : base(pointer) { }
}

public class Payable : MonoBehaviour
{
    public Payable(IntPtr pointer) : base(pointer) { }
}

public class PayableUpgrade : Payable
{
    public PayableUpgrade(IntPtr pointer) : base(pointer) { }
}

public class LockIndicator : MonoBehaviour
{
    public LockIndicator(IntPtr pointer) : base(pointer) { }
}
