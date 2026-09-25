using System;

namespace MorePlayersMod.Logic;

/// <summary>Slot grid for the crew roster panel (pure, unit tested).</summary>
public static class RosterLayout
{
    /// <summary>Columns used for a lobby of <paramref name="capacity"/> slots: 1 for vanilla-size, up to 3 for 24+.</summary>
    public static int Columns(int capacity)
    {
        if (capacity <= 6) return 1;
        if (capacity <= 12) return 2;
        return 3;
    }

    public static int Rows(int capacity) => (int)Math.Ceiling(Math.Max(1, capacity) / (double)Columns(capacity));

    /// <summary>Column-major placement so slot 1..8 read down the first column like the vanilla list.</summary>
    public static void Cell(int index, int capacity, out int column, out int row)
    {
        int rows = Rows(capacity);
        column = index / rows;
        row = index % rows;
    }

    /// <summary>Rows that fit on screen; the panel scrolls beyond this.</summary>
    public static int VisibleRows(int capacity, float screenHeight, float rowHeight, float chrome)
    {
        int fit = (int)Math.Floor((screenHeight * 0.8f - chrome) / Math.Max(1f, rowHeight));
        return Math.Max(4, Math.Min(Rows(capacity), fit));
    }
}
