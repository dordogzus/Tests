// Reference stub: members mirror Il2CppInterop's generated UnityEngine.CoreModule.dll. Bodies never run.
using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public Object(IntPtr pointer) : base(pointer) { }
        public string name { get => throw null; set => throw null; }
        public static void Destroy(Object obj) => throw null;
        public static T Instantiate<T>(T original, Transform parent, bool worldPositionStays) => throw null;
        public static bool operator ==(Object x, Object y) => throw null;
        public static bool operator !=(Object x, Object y) => throw null;
        public static implicit operator bool(Object exists) => throw null;
    }

    public class Component : Object
    {
        public Component(IntPtr pointer) : base(pointer) { }
        public GameObject gameObject => throw null;
        public Transform transform => throw null;
    }

    public class Behaviour : Component
    {
        public Behaviour(IntPtr pointer) : base(pointer) { }
    }

    public class MonoBehaviour : Behaviour
    {
        public MonoBehaviour(IntPtr pointer) : base(pointer) { }
    }

    public sealed class GameObject : Object
    {
        public GameObject(IntPtr pointer) : base(pointer) { }
        public bool activeInHierarchy => throw null;
        public bool activeSelf => throw null;
        public void SetActive(bool value) => throw null;
        public T GetComponent<T>() => throw null;
        public Il2CppArrayBase<T> GetComponentsInChildren<T>(bool includeInactive) => throw null;
    }

    public class Transform : Component
    {
        public Transform(IntPtr pointer) : base(pointer) { }
        public Transform parent => throw null;
        public Vector3 position { get => throw null; set => throw null; }
    }

    public class Renderer : Component
    {
        public Renderer(IntPtr pointer) : base(pointer) { }
        public bool enabled { get => throw null; set => throw null; }
    }

    public sealed class Application : Il2CppSystem.Object
    {
        public Application(IntPtr pointer) : base(pointer) { }
        public static string version => throw null;
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;
        public Color(float r, float g, float b, float a) => throw null;
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3(float x, float y, float z) => throw null;
        public static Vector3 operator +(Vector3 a, Vector3 b) => throw null;
    }
}
