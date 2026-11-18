namespace Echostep.UI;

/// <summary>
/// Proportional columns for a grid: the first column keeps a fixed width, the rest share the remaining
/// width by weight. Dragging a header divider changes the weights; making the window narrower or wider
/// scales the shared columns with it. The layout is saved as weights so it survives any window size.
/// </summary>
internal static class GridColumns
{
    /// <param name="fixedFirst">Width of the first column, which does not stretch.</param>
    /// <param name="weights">Starting share for each other column (their old pixel widths work well).</param>
    /// <param name="saved">Saved layout: first column width, then one weight per other column.</param>
    /// <param name="save">Called with the new layout after the user (or the window) changes a width.</param>
    public static void Setup(DataGridView grid, int fixedFirst, float[] weights, List<float> saved, Action<List<float>> save)
    {
        grid.AllowUserToResizeColumns = true;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        var first = grid.Columns[0];
        first.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        first.Resizable = DataGridViewTriState.True;
        first.MinimumWidth = 28;
        first.Width = fixedFirst;
        for (var i = 1; i < grid.Columns.Count; i++)
        {
            var c = grid.Columns[i];
            c.MinimumWidth = 40;
            c.Resizable = DataGridViewTriState.True;
            c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            c.FillWeight = i - 1 < weights.Length ? weights[i - 1] : 100;
        }

        if (saved.Count == grid.Columns.Count && saved.Skip(1).All(w => w is > 0 and < 100_000))
        {
            if (saved[0] is >= 28 and <= 400) first.Width = (int)saved[0];
            for (var i = 1; i < grid.Columns.Count; i++) grid.Columns[i].FillWeight = saved[i];
        }

        var loading = true;
        grid.HandleCreated += (_, _) => loading = false;
        grid.ColumnWidthChanged += (_, _) =>
        {
            if (loading) return;
            var layout = new List<float> { first.Width };
            for (var i = 1; i < grid.Columns.Count; i++) layout.Add(grid.Columns[i].FillWeight);
            save(layout);
        };
    }
}
