using System;

namespace Unity.Collections
{
    public class AllocatorManager : Il2CppSystem.Object
    {
        public AllocatorManager(IntPtr pointer) : base(pointer) { }

        public struct AllocatorHandle
        {
            private ushort Index;
            private ushort Version;
            public static implicit operator AllocatorHandle(Allocator a) => throw null;
        }
    }
}
