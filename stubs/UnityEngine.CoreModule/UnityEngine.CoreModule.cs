using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace UnityEngine
{
    public class Object : Il2CppSystem.Object
    {
        public Object(IntPtr pointer) : base(pointer) { }
        public string name { get => throw null; set => throw null; }
        public HideFlags hideFlags { get => throw null; set => throw null; }
        public int GetInstanceID() => throw null;
        public static void Destroy(Object obj) => throw null;
        public static void DontDestroyOnLoad(Object target) => throw null;
        public static T Instantiate<T>(T original, Transform parent, bool instantiateInWorldSpace) where T : Object => throw null;
        public static bool operator ==(Object x, Object y) => throw null;
        public static bool operator !=(Object x, Object y) => throw null;
    }

    public class Component : Object
    {
        public Component(IntPtr pointer) : base(pointer) { }
        public GameObject gameObject => throw null;
        public Transform transform => throw null;
        public Il2CppArrayBase<T> GetComponentsInChildren<T>(bool includeInactive) => throw null;
        public T GetComponentInParent<T>(bool includeInactive) => throw null;
        public T GetComponent<T>() => throw null;
    }

    public class Behaviour : Component
    {
        public Behaviour(IntPtr pointer) : base(pointer) { }
        public bool enabled { get => throw null; set => throw null; }
    }

    public class MonoBehaviour : Behaviour
    {
        public MonoBehaviour(IntPtr pointer) : base(pointer) { }
    }

    public class ScriptableObject : Object
    {
        public ScriptableObject(IntPtr pointer) : base(pointer) { }
        public static T CreateInstance<T>() where T : ScriptableObject => throw null;
    }

    public class GameObject : Object
    {
        public GameObject(IntPtr pointer) : base(pointer) { }
        public GameObject(string name) : base(IntPtr.Zero) => throw null;
        public Transform transform => throw null;
        public bool activeSelf => throw null;
        public bool activeInHierarchy => throw null;
        public SceneManagement.Scene scene => throw null;
        public void SetActive(bool value) => throw null;
        public T AddComponent<T>() => throw null;
        public T GetComponent<T>() => throw null;
        public int layer { get => throw null; set => throw null; }
    }

    public class Transform : Component
    {
        public Transform(IntPtr pointer) : base(pointer) { }
        public Transform parent => throw null;
        public Transform root => throw null;
        public int childCount => throw null;
        public Vector3 position => throw null;
        public Transform GetChild(int index) => throw null;
        public void SetParent(Transform parent, bool worldPositionStays) => throw null;
        public void SetAsLastSibling() => throw null;
    }

    public class RectTransform : Transform
    {
        public RectTransform(IntPtr pointer) : base(pointer) { }
        public Vector2 anchorMin { get => throw null; set => throw null; }
        public Vector2 anchorMax { get => throw null; set => throw null; }
        public Vector2 pivot { get => throw null; set => throw null; }
        public Vector2 anchoredPosition { get => throw null; set => throw null; }
        public Vector2 sizeDelta { get => throw null; set => throw null; }
    }

    public class Camera : Behaviour
    {
        public Camera(IntPtr pointer) : base(pointer) { }
        public static Camera main => throw null;
    }

    public class Material : Object
    {
        public Material(IntPtr pointer) : base(pointer) { }
        public bool HasProperty(int nameID) => throw null;
        public Color GetColor(int nameID) => throw null;
    }

    public class Shader : Object
    {
        public Shader(IntPtr pointer) : base(pointer) { }
        public static int PropertyToID(string name) => throw null;
    }

    public class Texture : Object
    {
        public Texture(IntPtr pointer) : base(pointer) { }
        public TextureWrapMode wrapMode { set => throw null; }
        public FilterMode filterMode { set => throw null; }
    }

    public class Texture2D : Texture
    {
        public Texture2D(IntPtr pointer) : base(pointer) { }
        public Texture2D(int width, int height, TextureFormat textureFormat, bool mipChain) : base(IntPtr.Zero) => throw null;
        public void SetPixel(int x, int y, Color color) => throw null;
        public void Apply() => throw null;
    }

    public class Mesh : Object
    {
        public Mesh(IntPtr pointer) : base(pointer) { }
        public Mesh() : base(IntPtr.Zero) => throw null;
        public Il2CppStructArray<Vector3> vertices => throw null;
        public Il2CppStructArray<Vector3> normals => throw null;
        public Il2CppStructArray<Vector4> tangents => throw null;
        public Il2CppStructArray<Color32> colors32 => throw null;
        public int vertexCount => throw null;
        public int subMeshCount => throw null;
        public bool isReadable => throw null;
        public Bounds bounds => throw null;
        public Rendering.IndexFormat indexFormat { get => throw null; set => throw null; }
        public Il2CppStructArray<int> GetTriangles(int submesh) => throw null;
        public void GetUVs(int channel, Il2CppSystem.Collections.Generic.List<Vector4> uvs) => throw null;
        public void SetVertices(Il2CppSystem.Collections.Generic.List<Vector3> inVertices) => throw null;
        public void SetNormals(Il2CppSystem.Collections.Generic.List<Vector3> inNormals) => throw null;
        public void SetTangents(Il2CppSystem.Collections.Generic.List<Vector4> inTangents) => throw null;
        public void SetColors(Il2CppSystem.Collections.Generic.List<Color> inColors) => throw null;
        public void SetUVs(int channel, Il2CppSystem.Collections.Generic.List<Vector4> uvs) => throw null;
        public void SetTriangles(Il2CppSystem.Collections.Generic.List<int> triangles, int submesh) => throw null;
        public void RecalculateBounds() => throw null;
    }

    public class Resources : Il2CppSystem.Object
    {
        public Resources(IntPtr pointer) : base(pointer) { }
        public static Il2CppArrayBase<T> FindObjectsOfTypeAll<T>() where T : Object => throw null;
        public static T GetBuiltinResource<T>(string path) where T : Object => throw null;
    }

    public class Time : Il2CppSystem.Object
    {
        public Time(IntPtr pointer) : base(pointer) { }
        public static int frameCount => throw null;
        public static float realtimeSinceStartup => throw null;
        public static float unscaledTime => throw null;
        public static float unscaledDeltaTime => throw null;
        public static float deltaTime => throw null;
    }

    public class Application : Il2CppSystem.Object
    {
        public Application(IntPtr pointer) : base(pointer) { }
        public static string version => throw null;
        public static string productName => throw null;
        public static string unityVersion => throw null;
    }

    public class Screen : Il2CppSystem.Object
    {
        public Screen(IntPtr pointer) : base(pointer) { }
        public static int width => throw null;
        public static int height => throw null;
    }

    public struct Vector2
    {
        public float x;
        public float y;
        public Vector2(float x, float y) => throw null;
        public static Vector2 zero => throw null;
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
        public Vector3(float x, float y, float z) => throw null;
        public static Vector3 operator +(Vector3 a, Vector3 b) => throw null;
        public static Vector3 operator *(Vector3 a, float d) => throw null;
    }

    public struct Vector4
    {
        public float x;
        public float y;
        public float z;
        public float w;
        public Vector4(float x, float y, float z, float w) => throw null;
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;
        public Color(float r, float g, float b, float a) => throw null;
        public static Color white => throw null;
    }

    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;
        public static implicit operator Color(Color32 c) => throw null;
    }

    public struct Rect
    {
        private float m_XMin;
        private float m_YMin;
        private float m_Width;
        private float m_Height;
        public Rect(float x, float y, float width, float height) => throw null;
    }

    public struct Bounds
    {
        private Vector3 m_Center;
        private Vector3 m_Extents;
        public Bounds(Vector3 center, Vector3 size) => throw null;
        public Vector3 center => throw null;
        public Vector3 size => throw null;
    }

    public struct Matrix4x4
    {
        public float m00, m10, m20, m30;
        public float m01, m11, m21, m31;
        public float m02, m12, m22, m32;
        public float m03, m13, m23, m33;
        public Matrix4x4(Vector4 column0, Vector4 column1, Vector4 column2, Vector4 column3) => throw null;
        public Matrix4x4 inverse => throw null;
        public float this[int row, int column] => throw null;
        public Vector3 MultiplyPoint3x4(Vector3 point) => throw null;
        public Vector3 MultiplyVector(Vector3 vector) => throw null;
    }

    public enum HideFlags
    {
        None = 0,
        HideInHierarchy = 1,
        HideInInspector = 2,
        DontSaveInEditor = 4,
        NotEditable = 8,
        DontSaveInBuild = 16,
        DontUnloadUnusedAsset = 32,
        DontSave = 52,
        HideAndDontSave = 61,
    }

    public enum TextureFormat
    {
        RGBA32 = 4,
        ARGB32 = 5,
    }

    public enum TextureWrapMode
    {
        Repeat = 0,
        Clamp = 1,
    }

    public enum FilterMode
    {
        Point = 0,
        Bilinear = 1,
    }

    public enum KeyCode
    {
        None = 0,
        Tab = 9,
        Escape = 27,
        BackQuote = 96,
        Insert = 277,
        Home = 278,
        End = 279,
        PageUp = 280,
        PageDown = 281,
        F1 = 282, F2 = 283, F3 = 284, F4 = 285, F5 = 286, F6 = 287,
        F7 = 288, F8 = 289, F9 = 290, F10 = 291, F11 = 292, F12 = 293,
        RightShift = 303,
        LeftShift = 304,
        RightControl = 305,
        LeftControl = 306,
        RightAlt = 307,
        LeftAlt = 308,
    }
}

namespace UnityEngine.Rendering
{
    public enum IndexFormat
    {
        UInt16 = 0,
        UInt32 = 1,
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene
    {
        private int m_Handle;
        public bool IsValid() => throw null;
        public bool isLoaded => throw null;
    }
}

namespace Unity.Collections
{
    public enum Allocator
    {
        Invalid = 0,
        None = 1,
        Temp = 2,
        TempJob = 3,
        Persistent = 4,
    }

    public class NativeArray<T> : Il2CppSystem.Object
    {
        public NativeArray(IntPtr pointer) : base(pointer) { }
        public int Length => throw null;
        public T this[int index] { get => throw null; set => throw null; }
        public void Dispose() => throw null;
    }
}
