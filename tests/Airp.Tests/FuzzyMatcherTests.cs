using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("prof", "Professor")]
    [InlineData("psr", "Professor")]
    [InlineData("PROF", "professor")]
    [InlineData("", "anything")]
    public void Match_FindsSubsequences(string query, string candidate)
        => FuzzyMatcher.Match(query, candidate).IsMatch.ShouldBeTrue();

    [Theory]
    [InlineData("xyz", "Professor")]
    [InlineData("rp", "Professor")]
    [InlineData("professorx", "Professor")]
    public void Match_RejectsNonSubsequences(string query, string candidate)
        => FuzzyMatcher.Match(query, candidate).IsMatch.ShouldBeFalse();

    [Fact]
    public void Match_AgainstAnEmptyCandidate_Fails()
        => FuzzyMatcher.Match("a", string.Empty).IsMatch.ShouldBeFalse();

    [Fact]
    public void Match_ReportsThePositionsThatMatched()
    {
        var match = FuzzyMatcher.Match("pf", "Professor");

        match.Positions.ShouldBe([0, 3]);
    }

    [Fact]
    public void Match_ScoresAContiguousHitAboveAScatteredOne()
    {
        var contiguous = FuzzyMatcher.Match("prof", "Professor");
        var scattered = FuzzyMatcher.Match("prof", "Purple Roof Of Fame");

        contiguous.Score.ShouldBeGreaterThan(scattered.Score);
    }

    [Fact]
    public void Match_ScoresAPrefixAboveAMidWordHit()
    {
        var prefix = FuzzyMatcher.Match("cap", "Captain");
        var inner = FuzzyMatcher.Match("cap", "Escapade");

        prefix.Score.ShouldBeGreaterThan(inner.Score);
    }

    [Fact]
    public void Match_RewardsWordStarts()
    {
        // Neither candidate contains "ab" literally, so this isolates the word-start bonus
        // from the substring bonus that would otherwise dominate the comparison.
        var wordStarts = FuzzyMatcher.Match("ab", "Alpha Bravo");
        var midWord = FuzzyMatcher.Match("ab", "Xaxbx");

        wordStarts.IsMatch.ShouldBeTrue();
        midWord.IsMatch.ShouldBeTrue();
        wordStarts.Score.ShouldBeGreaterThan(midWord.Score);
    }

    [Fact]
    public void Match_RanksALiteralSubstringAboveAScatteredSubsequence()
    {
        // Typing a whole word should surface the literal hit, which is what a filter box
        // user expects even when a scattered match scores well structurally.
        var literal = FuzzyMatcher.Match("cap", "Escapade");
        var scattered = FuzzyMatcher.Match("cap", "Cold And Precise");

        literal.Score.ShouldBeGreaterThan(scattered.Score);
    }

    [Fact]
    public void MatchAllTerms_RequiresEveryTermButIgnoresOrder()
    {
        FuzzyMatcher.MatchAllTerms("writer story", "Story Writer").IsMatch.ShouldBeTrue();
        FuzzyMatcher.MatchAllTerms("writer poet", "Story Writer").IsMatch.ShouldBeFalse();
    }

    [Fact]
    public void MatchAllTerms_WithASingleTerm_BehavesLikeMatch()
    {
        var single = FuzzyMatcher.MatchAllTerms("prof", "Professor");
        var direct = FuzzyMatcher.Match("prof", "Professor");

        single.Score.ShouldBe(direct.Score);
    }

    [Fact]
    public void MatchAllTerms_WithAWhitespaceQuery_MatchesEverything()
        => FuzzyMatcher.MatchAllTerms("   ", "anything").IsMatch.ShouldBeTrue();

    [Fact]
    public void Rank_OrdersByRelevanceAndDropsNonMatches()
    {
        string[] items = ["Assistant", "Professor", "Story Ideas", "Captain"];

        var ranked = FuzzyMatcher.Rank(items, "st", static s => s);

        ranked.ShouldContain("Story Ideas");
        ranked.ShouldContain("Assistant");
        ranked.ShouldNotContain("Captain");
        ranked[0].ShouldBe("Story Ideas", "a word-start prefix outranks a mid-word hit");
    }

    [Fact]
    public void Rank_WithAnEmptyQuery_PreservesTheOriginalOrder()
    {
        string[] items = ["c", "a", "b"];

        FuzzyMatcher.Rank(items, string.Empty, static s => s).ShouldBe(items);
    }

    [Fact]
    public void Rank_IsStableForEquallyScoredItems()
    {
        string[] items = ["abc", "abc", "abc"];

        var ranked = FuzzyMatcher.Rank(items.Select((s, i) => (Text: s, Index: i)), "abc", static x => x.Text);

        ranked.Select(static x => x.Index).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public void Rank_RejectsNullArguments()
    {
        Should.Throw<ArgumentNullException>(() => FuzzyMatcher.Rank<string>(null!, "a", static s => s));
        Should.Throw<ArgumentNullException>(() => FuzzyMatcher.Rank(["a"], "a", null!));
    }
}
