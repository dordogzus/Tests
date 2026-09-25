using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

// Game types (global namespace in the real interop). Only members whose exact
// signature is proven by the v2.20.6 build metadata are declared here.

public partial class Core : MonoBehaviour
{
    public Core(IntPtr pointer) : base(pointer) { }
    public static Core Get() => throw null;
    public Il2CppSystem.Collections.Generic.Dictionary<SCPrefab, EPC_SpaceshipComponent> _componentsMap { get => throw null; set => throw null; }
    public Il2CppReferenceArray<EPC_SpaceshipComponent> _spaceshipComponents { get => throw null; set => throw null; }
    public Il2CppReferenceArray<ObjectiveSetup> _objectives { get => throw null; set => throw null; }
    public DiskWorldSave _save { get => throw null; set => throw null; }
    public Entity _coreEntity { get => throw null; set => throw null; }

    public partial struct Singleton
    {
        public IntPtr _scPrefabsMap;
        public IntPtr _scPrefabDataMap;
        public IntPtr _scPrefabSizeOf;
    }

    public partial class DiskWorldSave : Il2CppSystem.Object
    {
        public DiskWorldSave(IntPtr pointer) : base(pointer) { }
        public WorldSave _world { get => throw null; set => throw null; }
    }

    public class WorldSave : Il2CppSystem.Object
    {
        public WorldSave(IntPtr pointer) : base(pointer) { }
        public SerializableDictionary<ObjectID, ObjectiveSave> _objectives { get => throw null; set => throw null; }

        public struct ObjectiveSave
        {
            private int __dummy;
            public bool IsCompleted() => throw null;
        }
    }
}

public class SerializableDictionary<TKey, TValue> : Il2CppSystem.Collections.Generic.Dictionary<TKey, TValue>
{
    public SerializableDictionary(IntPtr pointer) : base(pointer) { }
}

public enum ObjectID
{
    PlanetStation_Earth_Headquarters = 2000,
}

public enum ObjectiveType
{
    Package = 1,
}

public partial class ObjectiveSetup : ScriptableObject
{
    public ObjectiveSetup(IntPtr pointer) : base(pointer) { }
    public ObjectID _objectiveID { get => throw null; set => throw null; }
    public ObjectiveType _objectiveType { get => throw null; set => throw null; }
    public string GetTitle() => throw null;
}

public partial class EPC_SpaceshipComponent : MonoBehaviour
{
    public EPC_SpaceshipComponent(IntPtr pointer) : base(pointer) { }
    public int _availableAmount { get => throw null; set => throw null; }
    public string GetName() => throw null;
}

public partial struct SCPrefab
{
    public ulong _prefab;
    public SCPrefab(EPC_SpaceshipComponent component) => throw null;
}

public struct SCGuid
{
    public uint _a;
    public uint _b;
    public uint _c;
    public uint _d;
}

public partial struct GarageTransform
{
    public RigidTransform _transform;
    public float3 Position() => throw null;
    public uint Encode(Core.Singleton singleton, SCPrefab prefab) => throw null;
    public static GarageTransform Decode(Core.Singleton singleton, SCPrefab prefab, uint data) => throw null;
}

public struct GarageGrabberSingleton
{
    public double4x4 _Matrix;
    public double4x4 _MatrixInv;
    public quaternion _Rotation;
    public quaternion _RotationInv;
    public float3 _boundsSize;
    public void SetRootMatrix(double4x4 matrix, float3 boundsSize) => throw null;
}

public partial class GarageGrabber : Il2CppSystem.Object
{
    public GarageGrabber(IntPtr pointer) : base(pointer) { }
    public static void SetGarageTransform(EntityManager em, GarageTransform transform, Entity entity, GarageGrabberSingleton singleton, GarageTransform previous) => throw null;
}

public struct SpaceshipSingleton
{
    private IntPtr __dummy;
    public bool IsCreated() => throw null;
}

public partial class PlanetStation : MonoBehaviour
{
    public PlanetStation(IntPtr pointer) : base(pointer) { }
    public Entity _entity { get => throw null; set => throw null; }
    public float3 _garageMin { get => throw null; set => throw null; }
    public float3 _garageMax { get => throw null; set => throw null; }
}

public enum CRPLayer
{
    StaticObject = 10,
}

public struct MeshReference
{
    public int _instanceID;
}

public struct MaterialReference
{
    public int _instanceID;
}

public struct CRPRendererData
{
    public Header _header;
    public MaterialReference _material;
    public CRPRendererData(Mesh mesh, int submeshID, UnityEngine.Material material) => throw null;
    public DrawDataMeshReference GetDrawDataMeshReference() => throw null;

    public enum Type
    {
        MeshReference = 1,
    }

    public struct Header
    {
        public Type _type;
    }

    public struct DrawDataMeshReference
    {
        public MeshReference _meshReference;
        public int _submeshID;
    }
}

public struct CRPLocalBounds
{
    private float3 __center;
    private float3 __extents;
    public CRPLocalBounds(Bounds bounds) => throw null;
    public bool Equals(CRPLocalBounds other) => throw null;
}

public struct CRPWorldBounds
{
    public AABB _worldBounds;
    public CRPWorldBounds(float4x4 localToWorld, CRPLocalBounds localBounds) => throw null;
}

public partial class CRPRenderer : MonoBehaviour
{
    public CRPRenderer(IntPtr pointer) : base(pointer) { }
    public Entity _entity { get => throw null; set => throw null; }
    public CRPLayer _layer { get => throw null; set => throw null; }
    public Mesh _mesh { get => throw null; set => throw null; }
    public UnityEngine.Material _material { get => throw null; set => throw null; }
    public int _submeshID { get => throw null; set => throw null; }
}

public partial class UIMessenger : MonoBehaviour
{
    public UIMessenger(IntPtr pointer) : base(pointer) { }
    public void CreateNetcoreMessage(string message, float duration, bool local) => throw null;
}

public partial class UIInventory : MonoBehaviour
{
    public UIInventory(IntPtr pointer) : base(pointer) { }
}
