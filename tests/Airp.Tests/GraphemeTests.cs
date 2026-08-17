using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers the promise that one key press moves or deletes one <em>visible</em> character, not
/// one UTF-16 code unit. The distinction only shows up on text past the basic plane, so every
/// case here uses text that is longer in <c>char</c>s than it looks.
/// </summary>
public class GraphemeTests
{
    // Two chars (a surrogate pair), one cluster, two columns.
    private const string Grin = "\U0001F601";

    // Four chars: a base emoji plus a skin-tone modifier, which is itself a surrogate pair.
    private const string Wave = "\U0001F44B\U0001F3FD";

    // Eleven chars: four emoji joined by three zero-width joiners.
    private const string Family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";

    [Fact]
    public void TheseFixtures_AreLongerInCharsThanTheyLook()
    {
        // Guards the premise of every other test here. If these ever come out as one char
        // each, the tests below would pass without exercising anything.
        Grin.Length.ShouldBe(2);
        Wave.Length.ShouldBe(4);
        Family.Length.ShouldBe(11);

        Graphemes.Count(Grin).ShouldBe(1);
        Graphemes.Count(Wave).ShouldBe(1);
        Graphemes.Count(Family).ShouldBe(1);
    }

    [Fact]
    public void Next_StepsOverAWholeCluster()
    {
        Graphemes.Next(Grin + "a", 0).ShouldBe(2);
        Graphemes.Next(Wave + "a", 0).ShouldBe(4);
        Graphemes.Next(Family + "a", 0).ShouldBe(11);
    }

    [Fact]
    public void Previous_StepsBackOverAWholeCluster()
    {
        Graphemes.Previous("a" + Family, 12).ShouldBe(1);
        Graphemes.Previous(Grin, 2).ShouldBe(0);
    }

    [Fact]
    public void Snap_MovesAnIndexOutOfTheMiddleOfACluster()
    {
        // 1 is between the two halves of the surrogate pair — a place no caret may rest.
        Graphemes.Snap(Grin, 1).ShouldBe(0);
        Graphemes.Snap(Family, 5).ShouldBe(0);

        // Boundaries and the ends are already legal and must not move.
        Graphemes.Snap(Grin, 0).ShouldBe(0);
        Graphemes.Snap(Grin, 2).ShouldBe(2);
    }

    [Fact]
    public void Backspace_RemovesTheWholeEmoji()
    {
        var document = TextDocument.FromText("hi " + Family);
        document.MoveToLineEnd();

        document.Backspace();

        // The whole thing goes, and nothing is left that would render as a broken glyph.
        document.Text.ShouldBe("hi ");
        document.Text.ShouldNotContain("‍");
    }

    [Fact]
    public void Backspace_DoesNotLeaveALoneSurrogate()
    {
        // The failure this exists to catch: deleting one char of a pair leaves the other
        // behind, and a lone surrogate is not valid text at all.
        var document = TextDocument.FromText(Grin);
        document.MoveToLineEnd();

        document.Backspace();

        document.Text.ShouldBe(string.Empty);
    }

    [Fact]
    public void DeleteForward_RemovesTheWholeEmoji()
    {
        var document = TextDocument.FromText(Wave + " there");

        document.DeleteForward();

        document.Text.ShouldBe(" there");
    }

    [Fact]
    public void MoveRight_ThenLeft_ReturnsToWhereItStarted()
    {
        var document = TextDocument.FromText(Family + "x");

        document.MoveRight();
        document.CursorColumn.ShouldBe(11, "the caret clears the cluster in one press");

        document.MoveLeft();
        document.CursorColumn.ShouldBe(0);
    }

    [Fact]
    public void MoveTo_SnapsAColumnThatLandsInsideACluster()
    {
        // A click or a search hit can name any offset; the caret still may not sit inside one.
        var document = TextDocument.FromText(Family);

        document.MoveTo(0, 4);

        document.CursorColumn.ShouldBe(0);
    }

    [Fact]
    public void TypingAnEmojiOneCharAtATime_SurvivesBeingDeletedAsOne()
    {
        // How an emoji actually arrives from the console: one ConsoleKeyInfo per code unit,
        // so the document is fed half a surrogate pair at a time and has to reassemble it.
        var document = TextDocument.FromText(string.Empty);

        foreach (var character in Grin)
        {
            document.InsertText(character.ToString());
        }

        document.Text.ShouldBe(Grin);

        document.Backspace();
        document.Text.ShouldBe(string.Empty);
    }
}
