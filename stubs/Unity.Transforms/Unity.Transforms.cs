using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Transforms
{
    public struct LocalToWorld
    {
        public float4x4 Value;
    }

    public struct LocalTransform
    {
        public float3 Position;
        public float Scale;
        public quaternion Rotation;
        public float4x4 ToMatrix() => throw null;
    }

    public struct Parent
    {
        public Entity Value;
    }

    public struct Child
    {
        public Entity Value;
    }

    public struct PostTransformMatrix
    {
        public float4x4 Value;
    }
}
