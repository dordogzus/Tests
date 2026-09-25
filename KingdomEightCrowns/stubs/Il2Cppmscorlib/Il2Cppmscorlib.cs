// Reference stub: members mirror Il2CppInterop's generated Il2Cppmscorlib.dll. Bodies never run.
// Interface implementations are virtual in the interop assembly, so they are virtual here too (callvirt parity).
using System;
using Il2CppInterop.Runtime.InteropTypes;

namespace Il2CppSystem
{
    public class Object : Il2CppObjectBase
    {
        public Object(IntPtr pointer) : base(pointer) { }
    }
}

namespace Il2CppSystem.Collections.Generic
{
    public class Dictionary<TKey, TValue> : Il2CppSystem.Object
    {
        public Dictionary(IntPtr pointer) : base(pointer) { }
        public virtual TValue this[TKey key] { get => throw null; set => throw null; }
        public virtual int Count => throw null;
        public virtual bool ContainsKey(TKey key) => throw null;
        public virtual bool Remove(TKey key) => throw null;
        public virtual bool TryGetValue(TKey key, ref TValue value) => throw null;
    }

    public class List<T> : Il2CppSystem.Object
    {
        public List(IntPtr pointer) : base(pointer) { }
        public virtual int Count => throw null;
    }
}
