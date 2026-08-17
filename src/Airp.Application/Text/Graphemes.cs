using System.Globalization;

namespace Airp.Application.Text;

/// <summary>
/// Grapheme-cluster boundaries within a string.
/// </summary>
/// <remarks>
/// <para>
/// Everything the user thinks of as "a character" is a grapheme cluster, and in .NET that is
/// very often not one <see cref="char"/>. An emoji is a surrogate pair — two chars. An emoji
/// with a skin-tone modifier is four. A family emoji joined by zero-width joiners is eleven.
/// Stepping the caret or deleting by <see cref="char"/> therefore lands the caret <em>inside</em>
/// a character, and writing the halves back out produces a lone surrogate: the tofu box that
/// tells the user their message is corrupt.
/// </para>
/// <para>
/// Offsets here are ordinary UTF-16 indices, not cluster ordinals. That is deliberate: the
/// document, the search index and the wrapper all address text by <see cref="string"/> index,
/// and a second coordinate system would have to be converted at every boundary between them.
/// What these helpers provide is not a different index space but the guarantee that an index
/// sits <em>between</em> clusters rather than inside one.
/// </para>
/// <para>All members are pure and thread-safe.</para>
/// </remarks>
public static class Graphemes
{
    /// <summary>
    /// The index of the first cluster boundary strictly after <paramref name="index"/>.
    /// </summary>
    /// <param name="text">The text to walk.</param>
    /// <param name="index">A UTF-16 index into <paramref name="text"/>.</param>
    /// <returns>
    /// The next boundary, or the length of <paramref name="text"/> when there is none.
    /// </returns>
    public static int Next(string? text, int index)
    {
        var value = text ?? string.Empty;

        if (index >= value.Length)
        {
            return value.Length;
        }

        var from = Snap(value, index);
        var length = StringInfo.GetNextTextElementLength(value.AsSpan(from));
        return Math.Min(value.Length, from + Math.Max(1, length));
    }

    /// <summary>
    /// The index of the last cluster boundary strictly before <paramref name="index"/>.
    /// </summary>
    /// <param name="text">The text to walk.</param>
    /// <param name="index">A UTF-16 index into <paramref name="text"/>.</param>
    /// <returns>The previous boundary, or zero when there is none.</returns>
    public static int Previous(string? text, int index)
    {
        var value = text ?? string.Empty;
        var target = Math.Min(index, value.Length);

        if (target <= 0)
        {
            return 0;
        }

        // There is no backwards form of the cluster rules, so the line is walked from its
        // start. Composer lines are short enough that this is not worth an index for.
        var previous = 0;
        var position = 0;

        while (position < target)
        {
            previous = position;
            position += Math.Max(1, StringInfo.GetNextTextElementLength(value.AsSpan(position)));
        }

        return previous;
    }

    /// <summary>
    /// Moves an index back to the start of the cluster it lands in, if it landed inside one.
    /// </summary>
    /// <remarks>
    /// Applied wherever an index arrives from outside — a click, a search hit, a restored undo
    /// snapshot — so that no later edit can split a cluster that a caret was parked inside.
    /// </remarks>
    /// <param name="text">The text to walk.</param>
    /// <param name="index">A UTF-16 index into <paramref name="text"/>.</param>
    /// <returns>The index, moved back to a boundary and clamped to the text.</returns>
    public static int Snap(string? text, int index)
    {
        var value = text ?? string.Empty;
        var target = Math.Clamp(index, 0, value.Length);

        if (target is 0 || target == value.Length)
        {
            return target;
        }

        var position = 0;
        while (position < target)
        {
            var next = position + Math.Max(1, StringInfo.GetNextTextElementLength(value.AsSpan(position)));
            if (next > target)
            {
                return position;
            }

            position = next;
        }

        return target;
    }

    /// <summary>Enumerates the clusters in a string as index ranges.</summary>
    /// <param name="text">The text to walk.</param>
    /// <returns>Each cluster's start index and UTF-16 length, in order.</returns>
    public static IEnumerable<(int Start, int Length)> Enumerate(string? text)
    {
        var value = text ?? string.Empty;
        var position = 0;

        while (position < value.Length)
        {
            var length = Math.Max(1, StringInfo.GetNextTextElementLength(value.AsSpan(position)));
            yield return (position, length);
            position += length;
        }
    }

    /// <summary>Counts the clusters in a string — what a reader would call its length.</summary>
    /// <param name="text">The text to count.</param>
    /// <returns>The number of clusters.</returns>
    public static int Count(string? text)
    {
        var total = 0;
        foreach (var _ in Enumerate(text))
        {
            total++;
        }

        return total;
    }

    /// <summary>Reads the whole cluster beginning at an index.</summary>
    /// <param name="text">The text to read from.</param>
    /// <param name="index">A UTF-16 index; snapped to a boundary first.</param>
    /// <returns>The cluster, or an empty string at or past the end.</returns>
    public static string At(string? text, int index)
    {
        var value = text ?? string.Empty;
        var start = Snap(value, index);

        return start >= value.Length ? string.Empty : value[start..Next(value, start)];
    }
}
