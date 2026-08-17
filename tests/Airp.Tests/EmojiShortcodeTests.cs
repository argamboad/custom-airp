using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

public class EmojiShortcodeTests
{
    [Fact]
    public void EveryShortcode_HasAUniqueNameAndAnEmoji()
    {
        EmojiShortcodes.All.Select(static e => e.Name)
            .ShouldBeUnique("a duplicate name would shadow whichever entry came second");

        EmojiShortcodes.All.ShouldAllBe(e => e.Name.Length > 0 && e.Emoji.Length > 0);
    }

    [Fact]
    public void EveryShortcodeName_IsTypeableOnAnyKeyboard()
    {
        // Names are matched against ASCII key presses, so a name holding anything else could
        // be listed but never reached.
        EmojiShortcodes.All.ShouldAllBe(
            e => e.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '+' || c == '-'));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        EmojiShortcodes.Find("tada").ShouldBe("\U0001F389");
        EmojiShortcodes.Find("TADA").ShouldBe("\U0001F389");
        EmojiShortcodes.Find("not_an_emoji").ShouldBeNull();
    }

    [Fact]
    public void Suggest_PutsAnExactNameFirst()
    {
        // "star" also appears inside star_struck, which fuzzy scoring could otherwise rank
        // above the thing the user has typed in full.
        var suggestions = EmojiShortcodes.Suggest("star");

        suggestions[0].Name.ShouldBe("star");
    }

    [Fact]
    public void Suggest_FindsByKeywordAsWellAsName()
    {
        EmojiShortcodes.Suggest("lol").ShouldContain(static e => e.Name == "joy");
        EmojiShortcodes.Suggest("+1").ShouldContain(static e => e.Name == "thumbsup");
    }

    [Fact]
    public void Suggest_HonoursTheLimit()
    {
        EmojiShortcodes.Suggest("a", 3).Count.ShouldBeLessThanOrEqualTo(3);
        EmojiShortcodes.Suggest("smile", 0).ShouldBeEmpty();
    }
}

/// <summary>
/// Covers when a colon is an emoji and — mostly — when it is not. The false positives matter
/// more than the true ones: a popup that opens on ordinary prose is in the way all day.
/// </summary>
public class ShortcodeScannerTests
{
    [Theory]
    [InlineData("hello :sm", 9, "sm")]
    [InlineData(":sm", 3, "sm")]
    [InlineData("a :", 3, "")]
    [InlineData("one :two three", 8, "two")]
    public void At_RecognisesAShortcodeBeingTyped(string line, int column, string expected)
        => ShortcodeScanner.At(line, column)!.Value.Query.ShouldBe(expected);

    [Theory]
    [InlineData("10:30", 5)]           // a time
    [InlineData("https://x.com", 13)]  // a URL
    [InlineData("note: this", 10)]     // prose — the space ends the token
    [InlineData("no colon here", 8)]
    [InlineData("ratio 3:1", 9)]
    public void At_LeavesOrdinaryProseAlone(string line, int column)
        => ShortcodeScanner.At(line, column).ShouldBeNull();

    [Fact]
    public void At_ReportsTheSpanToReplace()
    {
        var token = ShortcodeScanner.At("hi :smi", 7)!.Value;

        token.Start.ShouldBe(3, "the colon opens the token");
        token.Length.ShouldBe(4, "the colon and the three letters after it");
    }

    [Fact]
    public void At_ClosesWhenTheCaretLeavesTheToken()
    {
        // Caret before the colon: there is nothing being typed here any more.
        ShortcodeScanner.At("hi :smile", 2).ShouldBeNull();
    }

    [Fact]
    public void Closed_SubstitutesAFullyTypedShortcode()
    {
        var (token, emoji) = ShortcodeScanner.Closed("nice :tada:", 11)!.Value;

        emoji.ShouldBe("\U0001F389");
        token.Start.ShouldBe(5);
        token.Length.ShouldBe(6, "both colons and the name between them");
    }

    [Fact]
    public void Closed_IgnoresANameThatIsNotInTheTable()
        => ShortcodeScanner.Closed("what :nonsense:", 15).ShouldBeNull();

    [Fact]
    public void Closed_IgnoresAnEmptyPairOfColons()
        => ShortcodeScanner.Closed("a ::", 4).ShouldBeNull();

    [Fact]
    public void Closed_IgnoresAColonThatDoesNotCloseAnything()
        => ShortcodeScanner.Closed("time is 10:30:", 14).ShouldBeNull();
}
