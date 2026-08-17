using Airp.Application.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Ui;

/// <summary>Small formatting helpers shared by every view.</summary>
/// <remarks>
/// <para>
/// Everything here measures in terminal <em>columns</em> rather than in <see cref="char"/>s,
/// and cuts only on grapheme-cluster boundaries. The two are routinely different: an emoji is
/// two chars and two columns, a family emoji is eleven chars and two columns, and a combining
/// accent is one char and no columns at all. Laying out by <c>string.Length</c> therefore pads
/// columns to the wrong width, wraps in the wrong place, and — worst of the three — can slice
/// a surrogate pair in half and emit a lone surrogate the terminal draws as tofu.
/// </para>
/// <para>
/// Width comes from Spectre's own measurement, so this agrees with the component actually
/// drawing the output. Where a terminal disagrees with Spectre about an exotic sequence, being
/// consistent with the renderer still beats being independently wrong.
/// </para>
/// </remarks>
internal static class Draw
{
    /// <summary>Measures text in terminal columns.</summary>
    /// <param name="text">The text to measure.</param>
    /// <returns>How many columns it occupies.</returns>
    public static int Width(string? text) => (text ?? string.Empty).GetCellWidth();

    /// <summary>Truncates text to a column budget, appending an ellipsis when it does not fit.</summary>
    /// <param name="text">The text.</param>
    /// <param name="width">Maximum columns.</param>
    /// <returns>Text no wider than <paramref name="width"/>.</returns>
    public static string Fit(string? text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var value = (text ?? string.Empty).ReplaceLineEndings(" ");
        if (Width(value) <= width)
        {
            return value;
        }

        // One column is reserved for the ellipsis. Clusters are taken whole, so a wide one
        // that would straddle the edge is dropped rather than halved — the result can come in
        // a column under budget, which is the correct way to be wrong here.
        var budget = width - 1;
        var used = 0;
        var end = 0;

        foreach (var (start, length) in Graphemes.Enumerate(value))
        {
            var cells = Width(value.Substring(start, length));
            if (used + cells > budget)
            {
                break;
            }

            used += cells;
            end = start + length;
        }

        return value[..end] + "…";
    }

    /// <summary>Truncates and right-pads to an exact column budget.</summary>
    /// <param name="text">The text.</param>
    /// <param name="width">Exact column count.</param>
    /// <returns>Text occupying exactly <paramref name="width"/> columns.</returns>
    public static string Pad(string? text, int width)
    {
        var fitted = Fit(text, width);
        return fitted + new string(' ', Math.Max(0, width - Width(fitted)));
    }

    /// <summary>
    /// Breaks a line into segments that fit a column budget, preferring word boundaries.
    /// </summary>
    /// <remarks>
    /// Prompts and messages are prose, and prose that runs past the right edge is simply
    /// unreadable — truncating it silently loses content the user came to read. Words are
    /// kept whole where possible; a single token longer than the budget is hard-broken
    /// rather than allowed to overflow.
    /// </remarks>
    /// <param name="text">The logical line. Never wrapped on embedded newlines — split first.</param>
    /// <param name="width">Column budget, at least one.</param>
    /// <returns>One or more segments. An empty line yields a single empty segment.</returns>
    public static IReadOnlyList<string> Wrap(string? text, int width)
        => [.. WrapSegments(text, width).Select(static segment => segment.Text)];

    /// <summary>
    /// Wraps a line, reporting where in the source each segment began.
    /// </summary>
    /// <remarks>
    /// An editable line needs more than the wrapped text: to draw a caret on the right row
    /// and at the right column, the renderer has to map a source offset onto a segment, and
    /// that is only possible if wrapping says where each one started. Offsets are into the
    /// tab-expanded text, so a document holding literal tabs would map imprecisely — the
    /// editors here insert spaces instead.
    /// </remarks>
    /// <param name="text">The logical line.</param>
    /// <param name="width">Column budget, at least one.</param>
    /// <returns>The segments with their start offsets, in order.</returns>
    public static IReadOnlyList<(int Start, string Text)> WrapSegments(string? text, int width)
    {
        var value = (text ?? string.Empty).Replace("\t", "    ", StringComparison.Ordinal);
        var budget = Math.Max(1, width);

        if (Width(value) <= budget)
        {
            return [(0, value)];
        }

        var segments = new List<(int Start, string Text)>();
        var start = 0;

        while (start < value.Length)
        {
            // How far the budget reaches from here, counted in columns and stopping on a
            // cluster boundary. A wide cluster that would straddle the edge goes to the next
            // row whole.
            var limit = start;
            var used = 0;

            foreach (var (clusterStart, length) in Graphemes.Enumerate(value[start..]))
            {
                var cells = Width(value.Substring(start + clusterStart, length));
                if (used + cells > budget)
                {
                    break;
                }

                used += cells;
                limit = start + clusterStart + length;
            }

            if (limit >= value.Length)
            {
                segments.Add((start, value[start..]));
                break;
            }

            // Look back from the budget edge for a space to break on.
            var breakAt = -1;
            for (var i = limit; i > start; i--)
            {
                if (char.IsWhiteSpace(value[i]))
                {
                    breakAt = i;
                    break;
                }
            }

            if (breakAt < 0)
            {
                // One unbroken token wider than the pane; split it rather than overflow. The
                // split lands on the cluster boundary the budget reached, never inside one.
                segments.Add((start, value[start..Math.Max(limit, Graphemes.Next(value, start))]));
                start = Math.Max(limit, Graphemes.Next(value, start));
                continue;
            }

            segments.Add((start, value[start..breakAt]));
            start = breakAt;

            while (start < value.Length && char.IsWhiteSpace(value[start]))
            {
                start++;
            }
        }

        return segments.Count == 0 ? [(0, string.Empty)] : segments;
    }

    /// <summary>Escapes text so Spectre treats it as literal content.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Escaped markup.</returns>
    public static string Escape(string? text) => Markup.Escape(text ?? string.Empty);

    /// <summary>Wraps text in a style.</summary>
    /// <param name="text">Already-escaped markup or plain text.</param>
    /// <param name="style">The style to apply.</param>
    /// <returns>Styled markup.</returns>
    public static string Styled(string text, Style style) => $"[{style.ToMarkup()}]{text}[/]";

    /// <summary>Escapes text and wraps it in a style.</summary>
    /// <param name="text">Raw text.</param>
    /// <param name="style">The style to apply.</param>
    /// <returns>Styled markup.</returns>
    public static string Literal(string? text, Style style) => Styled(Escape(text), style);

    /// <summary>
    /// Highlights every occurrence of a query inside a line.
    /// </summary>
    /// <param name="text">Raw text.</param>
    /// <param name="query">The literal query; empty returns the plain styled text.</param>
    /// <param name="baseStyle">Style for non-matching runs.</param>
    /// <param name="highlight">Style for matching runs.</param>
    /// <returns>Styled markup with matches emphasised.</returns>
    public static string Highlight(string? text, string? query, Style baseStyle, Style highlight)
    {
        var value = text ?? string.Empty;

        if (string.IsNullOrEmpty(query))
        {
            return Literal(value, baseStyle);
        }

        var builder = new System.Text.StringBuilder();
        var index = 0;

        while (index < value.Length)
        {
            var found = value.IndexOf(query, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                builder.Append(Literal(value[index..], baseStyle));
                break;
            }

            if (found > index)
            {
                builder.Append(Literal(value[index..found], baseStyle));
            }

            builder.Append(Literal(value.Substring(found, query.Length), highlight));
            index = found + query.Length;
        }

        return builder.ToString();
    }

    /// <summary>Formats a timestamp as a short relative age.</summary>
    /// <param name="value">The timestamp, in UTC.</param>
    /// <returns>A short label such as <c>3h ago</c>, or an em dash when unknown.</returns>
    public static string Age(DateTimeOffset? value)
    {
        if (value is not { } at)
        {
            return "—";
        }

        var span = DateTimeOffset.UtcNow - at;

        return span switch
        {
            { TotalSeconds: < 0 } => at.LocalDateTime.ToString("yyyy-MM-dd"),
            { TotalMinutes: < 1 } => "just now",
            { TotalMinutes: < 60 } => $"{span.TotalMinutes:F0}m ago",
            { TotalHours: < 24 } => $"{span.TotalHours:F0}h ago",
            { TotalDays: < 30 } => $"{span.TotalDays:F0}d ago",
            _ => at.LocalDateTime.ToString("yyyy-MM-dd"),
        };
    }

    /// <summary>
    /// Divides a width between two panes and a one-column separator.
    /// </summary>
    /// <remarks>
    /// Naively clamping a preferred width between a minimum and <c>total - minimum</c>
    /// inverts the bounds on a narrow terminal, and <see cref="Math.Clamp(int, int, int)"/>
    /// throws when its minimum exceeds its maximum. Rather than let a small window crash a
    /// view, this degrades: it honours both minimums when they fit and splits evenly when
    /// they do not.
    /// </remarks>
    /// <param name="total">Columns available for both panes and the separator.</param>
    /// <param name="leftRatio">Preferred share for the left pane, between 0 and 1.</param>
    /// <param name="minLeft">Preferred minimum for the left pane.</param>
    /// <param name="minRight">Preferred minimum for the right pane.</param>
    /// <returns>The two pane widths, each at least one column.</returns>
    public static (int Left, int Right) SplitWidths(int total, double leftRatio, int minLeft, int minRight)
    {
        const int separator = 1;
        var usable = Math.Max(2, total - separator);

        var maxLeft = usable - minRight;
        var left = maxLeft < minLeft
            ? usable / 2
            : Math.Clamp((int)Math.Round(usable * leftRatio), minLeft, maxLeft);

        left = Math.Clamp(left, 1, usable - 1);
        return (left, usable - left);
    }

    /// <summary>Builds a two-pane split of the body area.</summary>
    /// <param name="left">Left pane content.</param>
    /// <param name="right">Right pane content.</param>
    /// <param name="leftWidth">Width of the left pane in columns.</param>
    /// <param name="rightWidth">Width of the right pane in columns.</param>
    /// <param name="border">Style of the separator.</param>
    /// <returns>A renderable holding both panes side by side.</returns>
    public static IRenderable Split(
        IRenderable left,
        IRenderable right,
        int leftWidth,
        int rightWidth,
        Style border)
    {
        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(leftWidth).NoWrap());
        grid.AddColumn(new GridColumn().Width(1).NoWrap());
        grid.AddColumn(new GridColumn().Width(Math.Max(1, rightWidth)));
        grid.AddRow(left, new Markup(Styled("│", border)), right);
        return grid;
    }

    /// <summary>Builds a section heading with a rule beneath it.</summary>
    /// <param name="title">The heading text.</param>
    /// <param name="theme">Active palette.</param>
    /// <returns>A renderable heading.</returns>
    public static IRenderable Heading(string title, Theme theme)
        => new Rows(
            new Markup(Literal(title, theme.Heading)),
            new Rule { Style = theme.Border });
}
