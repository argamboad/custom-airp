using Airp.Application.Text;
using Airp.Terminal.Ui;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers layout that measures in terminal columns rather than in <c>char</c>s. Emoji are the
/// case where the two disagree: two chars wide in storage, two columns on screen, and one
/// character to the reader.
/// </summary>
public class DrawWidthTests
{
    private const string Grin = "\U0001F601";
    private const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";

    [Fact]
    public void Width_CountsColumnsNotChars()
    {
        Draw.Width("abc").ShouldBe(3);
        Draw.Width(Grin).ShouldBe(2, "an emoji is two columns but two chars as well — this one agrees");
        Draw.Width("a" + Grin).ShouldBe(3);
    }

    [Fact]
    public void Pad_PadsToColumnsSoAdjacentColumnsLineUp()
    {
        // The bug this replaces: PadRight counts chars, so a cell holding an emoji came out a
        // column too wide and every column to its right drifted.
        Draw.Width(Draw.Pad("ab", 10)).ShouldBe(10);
        Draw.Width(Draw.Pad(Grin + "ab", 10)).ShouldBe(10);
        Draw.Width(Draw.Pad(Family, 10)).ShouldBe(10);
    }

    [Fact]
    public void Fit_NeverExceedsItsBudget()
    {
        foreach (var width in new[] { 1, 2, 3, 5, 8 })
        {
            Draw.Width(Draw.Fit(Grin + Grin + Grin + Grin, width))
                .ShouldBeLessThanOrEqualTo(width, $"at width {width}");
        }
    }

    [Fact]
    public void Fit_DoesNotCutAnEmojiInHalf()
    {
        // Budget 3 leaves room for one two-column emoji and the ellipsis, but not for half of
        // the second one — which is what a char-counting Fit would have produced.
        var fitted = Draw.Fit(Grin + Grin + Grin, 3);

        fitted.ShouldBe(Grin + "…");
        HasLoneSurrogate(fitted).ShouldBeFalse();
    }

    [Fact]
    public void Wrap_NeverExceedsTheBudgetOnAnyRow()
    {
        var text = string.Join(" ", Enumerable.Repeat(Grin + "word" + Family, 12));

        foreach (var width in new[] { 4, 7, 10, 20, 33 })
        {
            foreach (var segment in Draw.Wrap(text, width))
            {
                Draw.Width(segment).ShouldBeLessThanOrEqualTo(width, $"at width {width}");
            }
        }
    }

    [Fact]
    public void Wrap_NeverSplitsACluster()
    {
        // An unbroken run with no spaces, which is the path that hard-splits a token.
        var text = string.Concat(Enumerable.Repeat(Family, 8));

        for (var width = 1; width <= 12; width++)
        {
            foreach (var segment in Draw.Wrap(text, width))
            {
                HasLoneSurrogate(segment).ShouldBeFalse($"at width {width}");
            }
        }
    }

    [Fact]
    public void Wrap_LosesNothingAndAddsNothing()
    {
        // Wrapping is a display concern; a message must survive it intact. Spaces are the one
        // thing it is allowed to consume, at the breaks it chose.
        var text = string.Join(" ", Enumerable.Repeat(Grin + "hello" + Family, 6));

        var rejoined = string.Concat(Draw.Wrap(text, 9)).Replace(" ", string.Empty);

        rejoined.ShouldBe(text.Replace(" ", string.Empty));
    }

    [Fact]
    public void Wrap_ReportsOffsetsThatLandOnClusterBoundaries()
    {
        // The composer maps the caret onto a row using these offsets, so an offset inside a
        // cluster would put the caret inside an emoji.
        var text = string.Join(" ", Enumerable.Repeat(Family + "abc", 6));

        foreach (var (start, _) in Draw.WrapSegments(text, 11))
        {
            Graphemes.Snap(text, start).ShouldBe(start);
        }
    }

    [Fact]
    public void Wrap_TerminatesOnAClusterWiderThanThePane()
    {
        // A one-column pane cannot fit a two-column emoji at all. The wrapper must still make
        // progress rather than spin forever refusing to place it.
        var segments = Draw.Wrap(Grin + Grin, 1);

        segments.Count.ShouldBe(2);
        segments.ShouldAllBe(s => s == Grin);
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return true;
                }

                i++;
                continue;
            }

            if (char.IsLowSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Guards the one property the shortcode table has to have: every emoji in it is something the
/// layout can measure correctly.
/// </summary>
/// <remarks>
/// Width comes from Spectre, which sums the width of each code point. That is right for a
/// single-code-point emoji and wrong for a zero-width-joiner sequence — Spectre would call a
/// family emoji eight columns where a terminal draws two, and every column to its right would
/// drift. Rather than try to out-guess the renderer, the table is kept to emoji the two agree
/// about, and this test is what keeps it that way when someone adds to it.
/// </remarks>
public class EmojiTableWidthTests
{
    [Fact]
    public void EveryEmojiInTheTable_MeasuresAsOneOrTwoColumns()
    {
        var odd = EmojiShortcodes.All
            .Where(static e => Draw.Width(e.Emoji) is < 1 or > 2)
            .Select(static e => $"{e.Name} measures {Draw.Width(e.Emoji)}")
            .ToList();

        odd.ShouldBeEmpty(
            "these would misalign every column to their right: " + string.Join(", ", odd));
    }
}
