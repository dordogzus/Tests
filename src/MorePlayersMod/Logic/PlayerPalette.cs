using System;
using System.Collections.Generic;

namespace MorePlayersMod.Logic;

/// <summary>Distinct player colours for slots beyond the vanilla four (pure, unit tested).</summary>
public static class PlayerPalette
{
    private const double GoldenAngle = 0.38196601125;

    /// <summary>RGB (0..1) colours for player indices [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public static List<(float r, float g, float b)> Generate(int from, int to)
    {
        var list = new List<(float, float, float)>();
        for (int i = Math.Max(0, from); i < to; i++)
        {
            double hue = (0.08 + i * GoldenAngle) % 1.0;
            double sat = (i % 3) switch { 0 => 0.85, 1 => 0.65, _ => 0.95 };
            double val = (i % 2) == 0 ? 0.95 : 0.78;
            list.Add(HsvToRgb(hue, sat, val));
        }
        return list;
    }

    public static (float r, float g, float b) HsvToRgb(double h, double s, double v)
    {
        h = (h % 1.0 + 1.0) % 1.0 * 6.0;
        int sector = (int)Math.Floor(h);
        double f = h - sector;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (double r, double g, double b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return ((float)r, (float)g, (float)b);
    }
}
