using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

public class PromptDocumentTests
{
    [Fact]
    public void FromText_NormalisesLineEndings()
    {
        var document = TextDocument.FromText("a\r\nb\rc\nd");

        document.Lines.ShouldBe(["a", "b", "c", "d"]);
        document.Text.ShouldBe("a\nb\nc\nd");
    }

    [Fact]
    public void EmptyDocument_StillHasOneLine()
    {
        var document = TextDocument.FromText(string.Empty);

        document.LineCount.ShouldBe(1);
        document.Lines[0].ShouldBe(string.Empty);
    }

    [Fact]
    public void InsertText_AtCursor_SplitsTheLine()
    {
        var document = TextDocument.FromText("hello world");
        document.MoveTo(0, 5);

        document.InsertText(",");

        document.Text.ShouldBe("hello, world");
        document.CursorColumn.ShouldBe(6);
    }

    [Fact]
    public void InsertText_WithNewlines_BehavesLikePaste()
    {
        var document = TextDocument.FromText("start-end");
        document.MoveTo(0, 6);

        document.InsertText("one\ntwo\n");

        document.Lines.ShouldBe(["start-one", "two", "end"]);
        document.Cursor.ShouldBe(new TextPosition(2, 0));
    }

    [Fact]
    public void Backspace_AtColumnZero_JoinsWithThePreviousLine()
    {
        var document = TextDocument.FromText("one\ntwo");
        document.MoveTo(1, 0);

        document.Backspace().ShouldBeTrue();

        document.Text.ShouldBe("onetwo");
        document.Cursor.ShouldBe(new TextPosition(0, 3));
    }

    [Fact]
    public void Backspace_AtDocumentStart_DoesNothing()
    {
        var document = TextDocument.FromText("abc");
        document.MoveToDocumentStart();

        document.Backspace().ShouldBeFalse();
        document.Text.ShouldBe("abc");
    }

    [Fact]
    public void DeleteForward_AtEndOfLine_JoinsWithTheNextLine()
    {
        var document = TextDocument.FromText("one\ntwo");
        document.MoveTo(0, 3);

        document.DeleteForward().ShouldBeTrue();

        document.Text.ShouldBe("onetwo");
    }

    [Fact]
    public void DeleteForward_AtEndOfDocument_DoesNothing()
    {
        var document = TextDocument.FromText("abc");
        document.MoveToDocumentEnd();

        document.DeleteForward().ShouldBeFalse();
    }

    [Fact]
    public void MoveVertical_RemembersTheDesiredColumn()
    {
        var document = TextDocument.FromText("a long first line\nshort\nanother long line");
        document.MoveTo(0, 15);

        document.MoveVertical(1);
        document.CursorColumn.ShouldBe(5, "the short line clamps the caret");

        document.MoveVertical(1);
        document.CursorColumn.ShouldBe(15, "and the original column is restored on a longer line");
    }

    [Fact]
    public void MoveToLineStart_TogglesBetweenIndentAndColumnZero()
    {
        var document = TextDocument.FromText("    indented");
        document.MoveToLineEnd();

        document.MoveToLineStart();
        document.CursorColumn.ShouldBe(4);

        document.MoveToLineStart();
        document.CursorColumn.ShouldBe(0);
    }

    [Fact]
    public void MoveWordRight_StopsAtTheNextWord()
    {
        var document = TextDocument.FromText("alpha beta gamma");

        document.MoveWordRight();
        document.CursorColumn.ShouldBe(6);

        document.MoveWordRight();
        document.CursorColumn.ShouldBe(11);
    }

    [Fact]
    public void MoveWordLeft_StopsAtTheStartOfThePreviousWord()
    {
        var document = TextDocument.FromText("alpha beta gamma");
        document.MoveTo(0, 11);

        document.MoveWordLeft();
        document.CursorColumn.ShouldBe(6);
    }

    [Fact]
    public void IsDirty_TracksChangesAgainstTheSavedBaseline()
    {
        var document = TextDocument.FromText("original");
        document.IsDirty.ShouldBeFalse();

        document.MoveToDocumentEnd();
        document.InsertText("!");
        document.IsDirty.ShouldBeTrue();

        document.MarkSaved();
        document.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void Undo_CoalescesARunOfTyping()
    {
        var document = TextDocument.FromText(string.Empty);

        foreach (var c in "hello")
        {
            document.InsertText(c.ToString());
        }

        document.Text.ShouldBe("hello");

        document.Undo().ShouldBeTrue();
        document.Text.ShouldBe(string.Empty, "a burst of typing is one undo step, not five");
    }

    [Fact]
    public void Undo_TreatsAStructuralEditAsItsOwnStep()
    {
        var document = TextDocument.FromText("line");
        document.MoveToDocumentEnd();

        document.InsertNewLine();
        document.InsertText("second");

        document.Undo();
        document.Text.ShouldBe("line\n");

        document.Undo();
        document.Text.ShouldBe("line");
    }

    [Fact]
    public void Redo_ReappliesAnUndoneEdit()
    {
        var document = TextDocument.FromText("a");
        document.MoveToDocumentEnd();
        document.InsertText("bc");

        document.Undo();
        document.Text.ShouldBe("a");

        document.Redo().ShouldBeTrue();
        document.Text.ShouldBe("abc");
    }

    [Fact]
    public void Redo_IsDiscardedByANewEdit()
    {
        var document = TextDocument.FromText("a");
        document.MoveToDocumentEnd();
        document.InsertText("b");
        document.Undo();

        document.InsertText("c");

        document.CanRedo.ShouldBeFalse();
        document.Redo().ShouldBeFalse();
    }

    [Fact]
    public void Undo_OnAFreshDocument_DoesNothing()
    {
        var document = TextDocument.FromText("abc");

        document.CanUndo.ShouldBeFalse();
        document.Undo().ShouldBeFalse();
    }

    [Fact]
    public void Undo_RespectsTheConfiguredDepth()
    {
        var document = TextDocument.FromText(string.Empty, undoDepth: 2);

        document.InsertText("one\n");
        document.InsertText("two\n");
        document.InsertText("three\n");
        document.InsertText("four\n");

        var undone = 0;
        while (document.Undo())
        {
            undone++;
        }

        undone.ShouldBe(2);
    }

    [Fact]
    public void DeleteLine_OnTheOnlyLine_ClearsItInstead()
    {
        var document = TextDocument.FromText("only");

        document.DeleteLine().ShouldBeTrue();

        document.LineCount.ShouldBe(1);
        document.Text.ShouldBe(string.Empty);
    }

    [Fact]
    public void DeleteLine_OnAnEmptySingleLine_ReportsNoChangeAndLeavesUndoAlone()
    {
        var document = TextDocument.FromText(string.Empty);

        document.DeleteLine().ShouldBeFalse();
        document.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void DeleteToEndOfLine_TruncatesAtTheCaret()
    {
        var document = TextDocument.FromText("keep this: drop this");
        document.MoveTo(0, 11);

        document.DeleteToEndOfLine().ShouldBeTrue();
        document.Text.ShouldBe("keep this: ");
    }

    [Fact]
    public void FindAll_ReturnsEveryOccurrenceInDocumentOrder()
    {
        var document = TextDocument.FromText("one two\nthree two\ntwo");

        var matches = document.FindAll("two");

        matches.Count.ShouldBe(3);
        matches[0].Position.ShouldBe(new TextPosition(0, 4));
        matches[1].Position.ShouldBe(new TextPosition(1, 6));
        matches[2].Position.ShouldBe(new TextPosition(2, 0));
    }

    [Fact]
    public void FindAll_IsCaseInsensitiveByDefaultAndCaseSensitiveOnRequest()
    {
        var document = TextDocument.FromText("Alpha alpha ALPHA");

        document.FindAll("alpha").Count.ShouldBe(3);
        document.FindAll("alpha", ignoreCase: false).Count.ShouldBe(1);
    }

    [Fact]
    public void FindAll_WithAnEmptyQuery_ReturnsNothing()
        => TextDocument.FromText("text").FindAll(string.Empty).ShouldBeEmpty();

    [Fact]
    public void Find_WrapsAroundToTheTop()
    {
        var document = TextDocument.FromText("target\nfiller\nfiller");
        document.MoveTo(2, 0);

        var match = document.Find("target");

        match.ShouldNotBeNull();
        match.Value.Position.ShouldBe(new TextPosition(0, 0));
    }

    [Fact]
    public void Find_Backwards_ReturnsThePrecedingMatch()
    {
        var document = TextDocument.FromText("x\nx\nx");
        document.MoveTo(2, 0);

        var match = document.Find("x", backwards: true);

        match.ShouldNotBeNull();
        match.Value.Position.Line.ShouldBe(1);
    }

    [Fact]
    public void ReplaceNext_ReplacesOneOccurrenceAndMovesTheCaret()
    {
        var document = TextDocument.FromText("cat cat cat");

        document.ReplaceNext("cat", "dog").ShouldBeTrue();

        document.Text.ShouldBe("dog cat cat");
        document.CursorColumn.ShouldBe(3);
    }

    [Fact]
    public void ReplaceAll_ReplacesEveryOccurrenceAcrossLines()
    {
        var document = TextDocument.FromText("cat\ncat and cat\nno match here");

        document.ReplaceAll("cat", "dog").ShouldBe(3);

        document.Text.ShouldBe("dog\ndog and dog\nno match here");
    }

    [Fact]
    public void ReplaceAll_WithALongerReplacement_KeepsLaterOffsetsCorrect()
    {
        var document = TextDocument.FromText("a a a");

        document.ReplaceAll("a", "bbb").ShouldBe(3);

        document.Text.ShouldBe("bbb bbb bbb");
    }

    [Fact]
    public void ReplaceAll_IsUndoneAsASingleStep()
    {
        var document = TextDocument.FromText("x x x");
        document.ReplaceAll("x", "y");

        document.Undo();

        document.Text.ShouldBe("x x x");
    }

    [Fact]
    public void ReplaceAll_WithNoMatches_ReportsZeroAndLeavesTheTextAlone()
    {
        var document = TextDocument.FromText("hello");

        document.ReplaceAll("absent", "x").ShouldBe(0);
        document.Text.ShouldBe("hello");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("one two three", 3)]
    [InlineData("  spaced   out  ", 2)]
    [InlineData("line one\nline two", 4)]
    public void WordCount_CountsWhitespaceSeparatedRuns(string text, int expected)
        => TextDocument.FromText(text).WordCount.ShouldBe(expected);

    [Fact]
    public void MoveTo_ClampsOutOfRangePositions()
    {
        var document = TextDocument.FromText("ab\ncd");

        document.MoveTo(99, 99);

        document.Cursor.ShouldBe(new TextPosition(1, 2));
    }

    [Fact]
    public void SetText_ReplacesEverythingAndIsUndoable()
    {
        var document = TextDocument.FromText("before");

        document.SetText("after\nlines");

        document.Text.ShouldBe("after\nlines");
        document.Undo().ShouldBeTrue();
        document.Text.ShouldBe("before");
    }

    [Fact]
    public void MoveRight_WrapsToTheNextLine()
    {
        var document = TextDocument.FromText("ab\ncd");
        document.MoveTo(0, 2);

        document.MoveRight();

        document.Cursor.ShouldBe(new TextPosition(1, 0));
    }

    [Fact]
    public void MoveLeft_WrapsToThePreviousLineEnd()
    {
        var document = TextDocument.FromText("ab\ncd");
        document.MoveTo(1, 0);

        document.MoveLeft();

        document.Cursor.ShouldBe(new TextPosition(0, 2));
    }
}
