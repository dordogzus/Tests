using System;

namespace Unity.Mathematics
{
    public struct float3
    {
        public float x;
        public float y;
        public float z;
        public float3(float x, float y, float z) => throw null;
        public static float3 operator +(float3 lhs, float3 rhs) => throw null;
        public static float3 operator -(float3 lhs, float3 rhs) => throw null;
        public static float3 operator *(float3 lhs, float rhs) => throw null;
        public static implicit operator float3(UnityEngine.Vector3 v) => throw null;
    }

    public struct float4
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public float4(float x, float y, float z, float w) => throw null;
        public float this[int index] => throw null;
        public float3 xyz => throw null;
    }

    public struct float4x4
    {
        public float4 c0;
        public float4 c1;
        public float4 c2;
        public float4 c3;
        public float4x4(float4 c0, float4 c1, float4 c2, float4 c3) => throw null;
    }

    public struct double3
    {
        public double x;
        public double y;
        public double z;
        public double3(double x, double y, double z) => throw null;
        public static implicit operator double3(float3 v) => throw null;
    }

    public struct double4
    {
        public double x;
        public double y;
        public double z;
        public double w;
        public double4(double x, double y, double z, double w) => throw null;
    }

    public struct double4x4
    {
        public double4 c0;
        public double4 c1;
        public double4 c2;
        public double4 c3;
    }

    public struct quaternion
    {
        public float4 value;
        public static quaternion identity => throw null;
    }

    public struct RigidTransform
    {
        public quaternion rot;
        public float3 pos;
    }

    public class math : Il2CppSystem.Object
    {
        public math(IntPtr pointer) : base(pointer) { }
        public static double4x4 mul(double4x4 a, double4x4 b) => throw null;
    }
}
