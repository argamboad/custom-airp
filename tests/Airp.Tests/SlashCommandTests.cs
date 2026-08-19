using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The reading of a composed line into a command.
/// </summary>
/// <remarks>
/// The stakes are asymmetric, and every test here is about the expensive direction. Reading a
/// command as prose sends it: it is billed, it lands in an append-only transcript as a line the
/// character has to react to, and nothing can take it back. Reading prose as a command only
/// costs a re-typed message.
/// </remarks>
public class SlashCommandTests
{
    [Fact]
    public void Parse_TreatsOrdinaryProseAsAMessage()
    {
        var parsed = SlashCommands.Parse("She looks up and/or laughs. 10:30 on the dot.");

        parsed.Kind.ShouldBe(SlashParseKind.Message);
        parsed.Text.ShouldBe("She looks up and/or laughs. 10:30 on the dot.");
    }

    [Fact]
    public void Parse_RecognisesACommandAndItsArgument()
    {
        var parsed = SlashCommands.Parse("/ask what has Nicole not said out loud yet?");

        parsed.Kind.ShouldBe(SlashParseKind.Command);
        parsed.Command!.Name.ShouldBe("ask");
        parsed.Text.ShouldBe("what has Nicole not said out loud yet?");
    }

    [Fact]
    public void Parse_IgnoresTheCaseOfTheName()
    {
        SlashCommands.Parse("/ASK who is she").Command!.Name.ShouldBe("ask");
    }

    [Fact]
    public void Parse_RefusesAnUnknownCommandRatherThanSendingIt()
    {
        // The whole reason commands are parsed before anything is sent. A typed /sak would
        // otherwise cost what the message would have cost and stay in the transcript for good.
        var parsed = SlashCommands.Parse("/sak what has she not said");

        parsed.Kind.ShouldBe(SlashParseKind.Unknown);
        parsed.Text.ShouldBe("sak");
    }

    [Fact]
    public void Parse_SendsADoubledSlashAsProseWithOneSlashLeft()
    {
        var parsed = SlashCommands.Parse("//ask is what I would type on the other site");

        parsed.Kind.ShouldBe(SlashParseKind.Message);
        parsed.Text.ShouldBe("/ask is what I would type on the other site");
    }

    [Fact]
    public void Parse_OnlyLooksAtTheFirstColumn()
    {
        // A slash mid-sentence is punctuation. Opening a command there would make half the
        // messages in this application unsendable.
        SlashCommands.Parse("She said /do it and left.").Kind.ShouldBe(SlashParseKind.Message);
    }

    [Fact]
    public void Parse_KeepsACommandWhoseArgumentStartsOnTheNextLine()
    {
        var parsed = SlashCommands.Parse("/do\nkeep this one short");

        parsed.Kind.ShouldBe(SlashParseKind.Command);
        parsed.Command!.Name.ShouldBe("do");
        parsed.Text.ShouldBe("keep this one short");
    }

    [Fact]
    public void Parse_ReadsABareCommandAsHavingNoArgument()
    {
        var parsed = SlashCommands.Parse("/facts");

        parsed.Command!.Name.ShouldBe("facts");
        parsed.Text.ShouldBeEmpty();
    }

    [Fact]
    public void SplitDirection_TakesTheWholeThingAsTheDirectionWhenThereIsNoBlankLine()
    {
        var (direction, message) = SlashCommands.SplitDirection("have Mariana leave\nbefore he answers");

        direction.ShouldBe("have Mariana leave\nbefore he answers");
        message.ShouldBeEmpty();
    }

    [Fact]
    public void SplitDirection_SeparatesTheDirectionFromTheMessageAtABlankLine()
    {
        var (direction, message) = SlashCommands.SplitDirection("keep it short\n\nHe sits down without a word.");

        direction.ShouldBe("keep it short");
        message.ShouldBe("He sits down without a word.");
    }

    [Fact]
    public void Matching_OffersEverythingForABareSlashAndNarrowsAsYouType()
    {
        SlashCommands.Matching(string.Empty).Count.ShouldBe(SlashCommands.All.Count);
        SlashCommands.Matching("fac").Select(static c => c.Name).ShouldBe(["facts", "fact"], ignoreOrder: true);
    }

    [Fact]
    public void All_GivesEveryCommandAUsageLineStartingWithItsOwnName()
    {
        // The usage strings are what /help prints and what a missing argument quotes back. One
        // that named a different command would send the reader to the wrong place.
        foreach (var command in SlashCommands.All)
        {
            command.Usage.ShouldStartWith($"/{command.Name}");
            command.Summary.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void All_HasNoTwoCommandsUnderTheSameName()
    {
        SlashCommands.All
            .Select(static c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ShouldBe(SlashCommands.All.Count);
    }
}
