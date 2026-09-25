using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Mathematics;
using UnityEngine;

// Additional compile-only surface. Names are taken from the v2.20.6 build
// metadata (Harmony targets referenced by typeof/nameof) or from public mods
// that run against the same game (Core.Initialize registration, Core._steam,
// SteamManager lobby helpers, EPC_SpaceshipComponent.GetDescription, _mass).
// Harmony resolves every target by name at runtime against the real interop.

public partial class Core
{
    public static Core _singleton { get => throw null; set => throw null; }
    public SteamManager _steam { get => throw null; set => throw null; }
    public void Start() => throw null;
    public void Initialize() => throw null;

    public class ComponentAmount : Il2CppSystem.Object
    {
        public ComponentAmount(IntPtr pointer) : base(pointer) { }
        public ComponentAmount() : base(IntPtr.Zero) => throw null;
        public EPC_SpaceshipComponent _sc { get => throw null; set => throw null; }
        public int _amount { get => throw null; set => throw null; }
    }

    public partial struct Singleton
    {
        public int GetMaxAvailableComponents(SCPrefab scPrefab) => throw null;
    }
}

public partial class ObjectiveSetup
{
    public string GetObjective() => throw null;
    public string GetDescription() => throw null;
    public string GetHints() => throw null;
    public bool _hidden { get => throw null; set => throw null; }
    public bool _canBeCompletedLocked { get => throw null; set => throw null; }
    public ObjectID _start { get => throw null; set => throw null; }
    public ObjectID _end { get => throw null; set => throw null; }
    public ObjectID _objectivesLogArrowStart { get => throw null; set => throw null; }
    public ObjectID _objectivesLogArrowEnd { get => throw null; set => throw null; }
    public Il2CppReferenceArray<Core.ComponentAmount> _reward { get => throw null; set => throw null; }
    public Il2CppReferenceArray<Core.ComponentAmount> _objectiveComponents { get => throw null; set => throw null; }
    public Il2CppStructArray<ObjectID> _dependencies { get => throw null; set => throw null; }
}

public class ObjectiveSystem : Il2CppSystem.Object
{
    public ObjectiveSystem(IntPtr pointer) : base(pointer) { }
    public void ShowObjectiveCompletionUISuccess(ObjectiveSetup objective) => throw null;
}

public partial class EPC_SpaceshipComponent
{
    public string GetDescription() => throw null;
    public float _mass { get => throw null; set => throw null; }
}

public partial struct SCPrefab
{
    public SCPrefab(string name) => throw null;
}

public partial class GarageGrabber
{
    public void DoBeforeStop() => throw null;
    public void OnStartRunning() => throw null;
}

public partial class UIInventory
{
    public Il2CppSystem.Collections.Generic.List<UIInventoryListItem> _allListItems { get => throw null; set => throw null; }
    public void RefreshItems() => throw null;
}

public class UIInventoryListItem : MonoBehaviour
{
    public UIInventoryListItem(IntPtr pointer) : base(pointer) { }
    public EPC_SpaceshipComponent _prefab { get => throw null; set => throw null; }
}

public class UIObjectiveLog : MonoBehaviour
{
    public UIObjectiveLog(IntPtr pointer) : base(pointer) { }
    public Il2CppSystem.Collections.Generic.List<UIObjectiveLogListRow> _rows { get => throw null; set => throw null; }
    public void OnEnable() => throw null;
}

public class UIObjectiveLogListRow : MonoBehaviour
{
    public UIObjectiveLogListRow(IntPtr pointer) : base(pointer) { }
    public ObjectiveSetup _objectiveLinked { get => throw null; set => throw null; }
}

public class SteamManager : MonoBehaviour
{
    public SteamManager(IntPtr pointer) : base(pointer) { }
    public void StartHost() => throw null;
    public void ConnectToHost() => throw null;
    public void UpdateManager() => throw null;
    public void CreateLobby() => throw null;
    public bool IsHost() => throw null;
}

public struct UniversePosition
{
    public double3 _position;
}

public struct UniverseCoreSingleton
{
    public double3 _centerEntityUniversePosition;
}

public partial struct GarageTransform
{
    public double3 GetUniversePosition(GarageGrabberSingleton garage) => throw null;
    public float4x4 GetLTWMatrix(GarageGrabberSingleton garage, UniverseCoreSingleton universe) => throw null;
}

public class EPC_Renderer : MonoBehaviour
{
    public EPC_Renderer(IntPtr pointer) : base(pointer) { }
    public CRPLayer _layer { get => throw null; set => throw null; }
    public Mesh _mesh { get => throw null; set => throw null; }
    public UnityEngine.Material _material { get => throw null; set => throw null; }
    public int _submeshID { get => throw null; set => throw null; }
}
