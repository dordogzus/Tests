using System;

namespace UnityEngine
{
    public enum RenderMode { ScreenSpaceOverlay = 0, ScreenSpaceCamera = 1, WorldSpace = 2 }

    public class Canvas : Behaviour
    {
        public Canvas(IntPtr pointer) : base(pointer) { }
        public RenderMode renderMode { get => throw null; set => throw null; }
        public int sortingOrder { get => throw null; set => throw null; }
    }
}
