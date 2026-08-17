namespace Airp.Terminal.Ui;

/// <summary>
/// Selection and scroll position for a vertical list.
/// </summary>
/// <remarks>
/// Extracted from the views because every list-shaped screen needs exactly this and getting
/// the viewport arithmetic subtly wrong in five places is how terminal UIs end up feeling
/// broken. The scroll offset keeps a margin above and below the selection so the cursor
/// never sits flush against the edge while there is more list to see.
/// </remarks>
internal sealed class ListState
{
    private const int ScrollMargin = 2;

    private int _count;
    private int _selected;
    private int _offset;

    /// <summary>Number of items in the list.</summary>
    public int Count => _count;

    /// <summary>Index of the selected item, or -1 when the list is empty.</summary>
    public int Selected => _count == 0 ? -1 : _selected;

    /// <summary>Index of the first visible item.</summary>
    public int Offset => _offset;

    /// <summary>Updates the item count, keeping the selection in range.</summary>
    /// <param name="count">The new count.</param>
    public void SetCount(int count)
    {
        _count = Math.Max(0, count);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _count - 1));
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _count - 1));
    }

    /// <summary>Moves the selection by a number of rows.</summary>
    /// <param name="delta">Rows to move; negative moves up.</param>
    public void Move(int delta)
    {
        if (_count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, _count - 1);
    }

    /// <summary>Selects an absolute index.</summary>
    /// <param name="index">The index to select; clamped to the list.</param>
    public void Select(int index)
    {
        if (_count == 0)
        {
            return;
        }

        _selected = Math.Clamp(index, 0, _count - 1);
    }

    /// <summary>Selects the first item.</summary>
    public void SelectFirst() => _selected = 0;

    /// <summary>Selects the last item.</summary>
    public void SelectLast() => _selected = Math.Max(0, _count - 1);

    /// <summary>
    /// Recomputes the scroll offset for a viewport of the given height and returns the range
    /// of items to draw.
    /// </summary>
    /// <param name="viewportHeight">How many rows are available.</param>
    /// <returns>The first visible index and how many items fit.</returns>
    public (int Start, int Length) Viewport(int viewportHeight)
    {
        var height = Math.Max(1, viewportHeight);

        if (_count <= height)
        {
            _offset = 0;
            return (0, _count);
        }

        var margin = Math.Min(ScrollMargin, (height - 1) / 2);

        if (_selected - margin < _offset)
        {
            _offset = _selected - margin;
        }
        else if (_selected + margin >= _offset + height)
        {
            _offset = _selected + margin - height + 1;
        }

        _offset = Math.Clamp(_offset, 0, _count - height);
        return (_offset, height);
    }

    /// <summary>Maps a viewport row to an absolute index, for mouse clicks.</summary>
    /// <param name="row">Zero-based row inside the viewport.</param>
    /// <param name="viewportHeight">How many rows are available.</param>
    /// <returns>The absolute index, or -1 when the row is past the end of the list.</returns>
    public int IndexAtRow(int row, int viewportHeight)
    {
        var (start, length) = Viewport(viewportHeight);
        var index = start + row;
        return row >= 0 && row < length && index < _count ? index : -1;
    }
}
