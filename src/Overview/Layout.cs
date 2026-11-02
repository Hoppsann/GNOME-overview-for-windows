using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Overview;

/// <summary>
/// GNOME-like window layout: windows keep their relative sizes and are packed into
/// centred rows; the row count is chosen to maximise the common scale.
/// </summary>
internal static class Layout
{
    public static Rect[] Compute(IReadOnlyList<Size> sizes, Rect area, double spacing, double labelSpace, double maxScale)
    {
        int n = sizes.Count;
        var result = new Rect[n];
        if (n == 0 || area.Width <= 0 || area.Height <= 0) return result;

        double bestScale = -1;
        List<List<int>>? bestRows = null;

        for (int rows = 1; rows <= n; rows++)
        {
            var parts = Partition(sizes, rows);
            double scale = maxScale;
            double heights = 0;
            foreach (var row in parts)
            {
                double rowW = row.Sum(i => sizes[i].Width);
                double avail = area.Width - spacing * (row.Count - 1);
                if (avail <= 0) { scale = 0; break; }
                scale = Math.Min(scale, avail / rowW);
                heights += row.Max(i => sizes[i].Height);
            }
            double availH = area.Height - spacing * (parts.Count - 1) - labelSpace * parts.Count;
            if (availH <= 0 || scale <= 0) continue;
            scale = Math.Min(scale, availH / heights);
            if (scale > bestScale + 1e-6)
            {
                bestScale = scale;
                bestRows = parts;
            }
        }

        if (bestRows == null) // absurdly many windows: one tiny row
        {
            bestRows = new List<List<int>> { Enumerable.Range(0, n).ToList() };
            bestScale = Math.Max(0.01, (area.Width - spacing * (n - 1)) / sizes.Sum(s => s.Width));
        }

        double s = bestScale;
        double contentH = bestRows.Sum(r => r.Max(i => sizes[i].Height) * s) + labelSpace * bestRows.Count + spacing * (bestRows.Count - 1);
        double y = area.Top + Math.Max(0, (area.Height - contentH) / 2);

        foreach (var row in bestRows)
        {
            double rowH = row.Max(i => sizes[i].Height) * s;
            double rowW = row.Sum(i => sizes[i].Width * s) + spacing * (row.Count - 1);
            double x = area.Left + (area.Width - rowW) / 2;
            foreach (int i in row)
            {
                double w = sizes[i].Width * s, h = sizes[i].Height * s;
                result[i] = new Rect(x, y + (rowH - h) / 2, w, h);
                x += w + spacing;
            }
            y += rowH + labelSpace + spacing;
        }
        return result;
    }

    /// <summary>Splits windows (in order) into <paramref name="rows"/> rows of roughly equal total width.</summary>
    static List<List<int>> Partition(IReadOnlyList<Size> sizes, int rows)
    {
        double total = sizes.Sum(s => s.Width);
        double target = total / rows;
        var parts = new List<List<int>>();
        for (int r = 0; r < rows; r++) parts.Add(new List<int>());
        double acc = 0;
        for (int i = 0; i < sizes.Count; i++)
        {
            int r = (int)Math.Min(rows - 1, Math.Floor((acc + sizes[i].Width / 2) / target));
            parts[r].Add(i);
            acc += sizes[i].Width;
        }
        parts.RemoveAll(p => p.Count == 0);
        return parts;
    }
}
