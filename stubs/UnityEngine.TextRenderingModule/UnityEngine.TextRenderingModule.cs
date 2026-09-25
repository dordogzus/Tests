namespace UnityEngine
{
    public class Font : Object
    {
        public Font(System.IntPtr pointer) : base(pointer) { }
    }

    public enum HorizontalWrapMode { Wrap = 0, Overflow = 1 }

    public enum VerticalWrapMode { Truncate = 0, Overflow = 1 }

    public enum TextAnchor
    {
        UpperLeft = 0,
        UpperCenter = 1,
        UpperRight = 2,
        MiddleLeft = 3,
        MiddleCenter = 4,
        MiddleRight = 5,
        LowerLeft = 6,
        LowerCenter = 7,
        LowerRight = 8,
    }

    public enum FontStyle
    {
        Normal = 0,
        Bold = 1,
        Italic = 2,
        BoldAndItalic = 3,
    }
}
