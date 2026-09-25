using System;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object(IntPtr pointer) : base(pointer) { }
        public Type GetIl2CppType() => throw null;
        public override string ToString() => throw null;
    }

    public class Type : Object
    {
        public Type(IntPtr pointer) : base(pointer) { }
        public static Type GetType(string typeName) => throw null;
        public string FullName => throw null;
        public static bool operator ==(Type left, Type right) => throw null;
        public static bool operator !=(Type left, Type right) => throw null;
    }
}

namespace Il2CppSystem.Collections.Generic
{
    public class List<T> : Il2CppSystem.Object
    {
        public List(IntPtr pointer) : base(pointer) { }
        public List() : base(IntPtr.Zero) => throw null;
        public List(int capacity) : base(IntPtr.Zero) => throw null;
        public int Count => throw null;
        public T this[int index] { get => throw null; set => throw null; }
        public void Add(T item) => throw null;
        public Enumerator GetEnumerator() => throw null;

        public class Enumerator : Il2CppSystem.Object
        {
            public Enumerator(IntPtr pointer) : base(pointer) { }
            public T Current => throw null;
            public bool MoveNext() => throw null;
        }
    }

    public class KeyValuePair<TKey, TValue> : Il2CppSystem.Object
    {
        public KeyValuePair(IntPtr pointer) : base(pointer) { }
        public TKey Key => throw null;
        public TValue Value => throw null;
    }

    public class Dictionary<TKey, TValue> : Il2CppSystem.Object
    {
        public Dictionary(IntPtr pointer) : base(pointer) { }
        public int Count => throw null;
        public TValue this[TKey key] { get => throw null; set => throw null; }
        public bool ContainsKey(TKey key) => throw null;
        public bool TryGetValue(TKey key, out TValue value) => throw null;
        public Enumerator GetEnumerator() => throw null;

        public class Enumerator : Il2CppSystem.Object
        {
            public Enumerator(IntPtr pointer) : base(pointer) { }
            public KeyValuePair<TKey, TValue> Current => throw null;
            public bool MoveNext() => throw null;
        }
    }
}
