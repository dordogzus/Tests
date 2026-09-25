using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Unity.Collections;

namespace Unity.Entities
{
    public struct Entity
    {
        public int Index;
        public int Version;
        public bool Equals(Entity entity) => throw null;
        public static bool operator ==(Entity lhs, Entity rhs) => throw null;
        public static bool operator !=(Entity lhs, Entity rhs) => throw null;
    }

    public struct ComponentType
    {
        private int TypeIndex;
        private int AccessModeType;
        public bool IsSharedComponent => throw null;
        public static ComponentType ReadOnly(Il2CppSystem.Type type) => throw null;
        public static ComponentType ReadOnly<T>() => throw null;
        public static ComponentType ReadWrite(Il2CppSystem.Type type) => throw null;
        public static ComponentType ReadWrite<T>() => throw null;
    }

    [Flags]
    public enum EntityQueryOptions
    {
        Default = 0,
        IncludePrefab = 1,
        IncludeDisabledEntities = 2,
    }

    public class EntityQueryDesc : Il2CppSystem.Object
    {
        public EntityQueryDesc(IntPtr pointer) : base(pointer) { }
        public EntityQueryDesc() : base(IntPtr.Zero) => throw null;
        public Il2CppStructArray<ComponentType> All { set => throw null; }
        public Il2CppStructArray<ComponentType> Any { set => throw null; }
        public Il2CppStructArray<ComponentType> None { set => throw null; }
        public EntityQueryOptions Options { set => throw null; }
    }

    public struct EntityQuery
    {
        private IntPtr __impl;
        public int CalculateEntityCount() => throw null;
        public Entity GetSingletonEntity() => throw null;
        public NativeArray<Entity> ToEntityArray(AllocatorManager.AllocatorHandle allocator) => throw null;
        public void Dispose() => throw null;
    }

    public struct EntityManager
    {
        private IntPtr m_EntityDataAccess;
        public void CompleteAllTrackedJobs() => throw null;
        public Entity CreateEntity(params ComponentType[] types) => throw null;
        public EntityQuery CreateEntityQuery(params ComponentType[] requiredComponents) => throw null;
        public EntityQuery CreateEntityQuery(params EntityQueryDesc[] queriesDesc) => throw null;
        public void DestroyEntity(Entity entity) => throw null;
        public bool Exists(Entity entity) => throw null;
        public bool HasComponent<T>(Entity entity) => throw null;
        public bool HasComponent(Entity entity, ComponentType type) => throw null;
        public T GetComponentData<T>(Entity entity) => throw null;
        public void SetComponentData<T>(Entity entity, T componentData) => throw null;
        public bool AddComponentData<T>(Entity entity, T componentData) => throw null;
        public T GetSharedComponent<T>(Entity entity) => throw null;
        public void SetSharedComponent<T>(Entity entity, T componentData) => throw null;
        public bool AddSharedComponent<T>(Entity entity, T componentData) => throw null;
    }

    public class World : Il2CppSystem.Object
    {
        public World(IntPtr pointer) : base(pointer) { }
        public static World DefaultGameObjectInjectionWorld => throw null;
        public EntityManager EntityManager => throw null;
        public bool IsCreated => throw null;
        public ulong SequenceNumber => throw null;
    }

    public struct Prefab { private byte __dummy; }

    public struct Disabled { private byte __dummy; }

    public class BlobAssetReference<T> : Il2CppSystem.Object
    {
        public BlobAssetReference(IntPtr pointer) : base(pointer) { }
        public bool IsCreated => throw null;
        public void Dispose() => throw null;
    }
}
