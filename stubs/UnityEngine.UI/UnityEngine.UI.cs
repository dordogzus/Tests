using System;

namespace UnityEngine.UI
{
    public class CanvasScaler : MonoBehaviour
    {
        public CanvasScaler(IntPtr pointer) : base(pointer) { }
        public ScaleMode uiScaleMode { get => throw null; set => throw null; }
        public Vector2 referenceResolution { get => throw null; set => throw null; }
        public float matchWidthOrHeight { get => throw null; set => throw null; }

        public enum ScaleMode { ConstantPixelSize = 0, ScaleWithScreenSize = 1, ConstantPhysicalSize = 2 }
    }

    public class Graphic : MonoBehaviour
    {
        public Graphic(IntPtr pointer) : base(pointer) { }
        public Color color { get => throw null; set => throw null; }
        public bool raycastTarget { get => throw null; set => throw null; }
    }

    public class MaskableGraphic : Graphic
    {
        public MaskableGraphic(IntPtr pointer) : base(pointer) { }
    }

    public class Image : MaskableGraphic
    {
        public Image(IntPtr pointer) : base(pointer) { }
    }

    public class Text : MaskableGraphic
    {
        public Text(IntPtr pointer) : base(pointer) { }
        public string text { get => throw null; set => throw null; }
        public Font font { get => throw null; set => throw null; }
        public int fontSize { get => throw null; set => throw null; }
        public FontStyle fontStyle { get => throw null; set => throw null; }
        public TextAnchor alignment { get => throw null; set => throw null; }
        public bool supportRichText { get => throw null; set => throw null; }
        public HorizontalWrapMode horizontalOverflow { get => throw null; set => throw null; }
        public VerticalWrapMode verticalOverflow { get => throw null; set => throw null; }
    }
}
