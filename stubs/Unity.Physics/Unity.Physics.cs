using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Physics
{
    public struct CollisionFilter
    {
        public uint BelongsTo;
        public uint CollidesWith;
        public int GroupIndex;
    }

    public struct Material { private int __dummy; }

    public struct Collider { private int __dummy; }

    public struct BoxGeometry
    {
        private float3 m_Center;
        private quaternion m_Orientation;
        private float3 m_Size;
        private float m_BevelRadius;
        public float3 Center { set => throw null; }
        public quaternion Orientation { set => throw null; }
        public float3 Size { set => throw null; }
        public float BevelRadius { set => throw null; }
    }

    public class BoxCollider : Il2CppSystem.Object
    {
        public BoxCollider(IntPtr pointer) : base(pointer) { }
        public static BlobAssetReference<Collider> Create(BoxGeometry geometry, CollisionFilter filter, Material material) => throw null;
    }

    public class PhysicsCollider : Il2CppSystem.Object
    {
        public PhysicsCollider(IntPtr pointer) : base(pointer) { }
        public PhysicsCollider() : base(IntPtr.Zero) => throw null;
        public BlobAssetReference<Collider> Value { get => throw null; set => throw null; }
    }

    public struct PhysicsWorldIndex
    {
        public uint Value;
        public PhysicsWorldIndex(uint worldIndex) => throw null;
    }

    public struct PhysicsVelocity
    {
        public Unity.Mathematics.float3 Linear;
        public Unity.Mathematics.float3 Angular;
    }
}
