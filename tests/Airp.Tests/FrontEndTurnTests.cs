using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Text;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A message from a front end is a turn, a command, or a refusal — and a command is never a turn.
/// </summary>
/// <remarks>
/// Every case says three things: what came back, how many turns the story holds afterwards, and
/// how many times the model was called. The last two are the point. Before this, a command
/// typed into a chat box was stored as a turn of the story, permanent and billed.
/// </remarks>
public sealed class FrontEndTurnTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    /// <summary>A story with one exchange in it.</summary>
    private async Task<Chat> StoryAsync()
    {
        var chat = await Provider().CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });

        _model.Says("Elena looks up from the log.");
        await Provider().SendAsync(chat.Id, "I come in from the rain.");

        return chat;
    }

    private async Task<IReadOnlyList<ChatMessage>> TurnsAsync(Chat chat) => await Provider().GetMessagesAsync(chat.Id);

    private Task<FrontEndOutcome> RunAsync(Chat chat, string said)
        => FrontEndTurn.RunAsync(Provider(), chat, said, CancellationToken.None);

    [Fact]
    public async Task A_plain_message_is_a_turn()
    {
        var chat = await StoryAsync();
        _model.Says("She closes the book.");

        var outcome = await RunAsync(chat, "I sit across from her.");

        outcome.ShouldBe(new FrontEndOutcome("She closes the book.", Refused: false, Stored: true));
        (await TurnsAsync(chat)).Select(static m => m.Text).TakeLast(2)
            .ShouldBe(["I sit across from her.", "She closes the book."]);
    }

    [Fact]
    public async Task A_doubled_slash_sends_prose_that_starts_with_one()
    {
        var chat = await StoryAsync();
        _model.Says("She laughs.");

        await RunAsync(chat, "//shrug");

        (await TurnsAsync(chat))[^2].Text.ShouldBe("/shrug");
    }

    [Fact]
    public async Task Recap_shows_the_last_turns_and_stores_and_bills_nothing()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var calls = _model.Calls.Count;

        var outcome = await RunAsync(chat, "/recap");

        outcome.Refused.ShouldBeFalse();
        outcome.Text.ShouldContain("nothing stored, nothing billed");
        outcome.Stored.ShouldBeFalse();
        outcome.Text.ShouldContain("You:\nI come in from the rain.");
        outcome.Text.ShouldContain("Elena:\nElena looks up from the log.");

        (await TurnsAsync(chat)).Count.ShouldBe(turns);
        _model.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task Recap_takes_a_number_of_turns_and_refuses_anything_else()
    {
        var chat = await StoryAsync();

        var one = await RunAsync(chat, "/recap 1");
        one.Text.ShouldContain("Elena looks up from the log.");
        one.Text.ShouldNotContain("I come in from the rain.");
        one.Text.ShouldContain("The last turn:");

        (await RunAsync(chat, "/recap lots")).Refused.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/sumary")]
    [InlineData("/tracker patience high")]
    [InlineData("/search")]
    [InlineData("/ask")]
    [InlineData("/do")]
    public async Task Anything_with_a_slash_that_is_not_run_here_is_refused_and_stores_nothing(string said)
    {
        // A typo, a command only the terminal can show, a command missing what it needs: none of
        // them may become a turn the character then has to answer.
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var calls = _model.Calls.Count;

        var outcome = await RunAsync(chat, said);

        outcome.Refused.ShouldBeTrue();
        outcome.Text.ShouldContain("nothing was stored");
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
        _model.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task Ask_answers_out_of_character_and_stores_nothing()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        _model.Says("She knows you came up the outside stair.");

        var outcome = await RunAsync(chat, "/ask what does Elena know about me?");

        outcome.Refused.ShouldBeFalse();
        outcome.Text.ShouldContain("Out of character");
        outcome.Stored.ShouldBeFalse();
        outcome.Text.ShouldContain("She knows you came up the outside stair.");
        (await TurnsAsync(chat)).Count.ShouldBe(turns);

        // Bare, so a front end can offer to pin it without pinning the frame around it.
        outcome.Answer.ShouldBe("She knows you came up the outside stair.");
    }

    [Theory]
    [InlineData("/card", "no character definition")]
    [InlineData("/persona", "no persona")]
    [InlineData("/facts", "Nothing is being injected as true yet")]
    [InlineData("/trackers", "keeps no meters")]
    [InlineData("/audit", "#2")]
    [InlineData("/cost", "1 billed call(s)")]
    public async Task The_reading_commands_answer_here_and_neither_store_nor_call_anything(string said, string expected)
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var calls = _model.Calls.Count;

        var outcome = await RunAsync(chat, said);

        outcome.Refused.ShouldBeFalse();
        outcome.Stored.ShouldBeFalse();
        outcome.Text.ShouldContain(expected);
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
        _model.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task Search_finds_the_turn_by_its_position_among_the_visible_ones()
    {
        var chat = await StoryAsync();

        var outcome = await RunAsync(chat, "/search RAIN");

        outcome.Matches.ShouldNotBeNull().ShouldHaveSingleItem().Number.ShouldBe(1);
        outcome.Text.ShouldContain("#1 You: I come in from the rain.");
    }

    [Fact]
    public async Task Fact_pins_a_statement_under_the_character_and_tracker_sets_a_meter_by_its_last_word()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;

        (await RunAsync(chat, "/fact The lamp has been out since Tuesday.")).Text.ShouldContain("Pinned under Elena");
        (await RunAsync(chat, "/tracker her patience 40")).Text.ShouldBe("her patience is now 40.");

        var fact = (await Provider().FactsAsync(chat.Id)).ShouldHaveSingleItem();
        fact.Subject.ShouldBe("Elena");
        fact.Text.ShouldBe("The lamp has been out since Tuesday.");
        (await Provider().TrackersAsync(chat.Id)).ShouldHaveSingleItem().Value.ShouldBe(40);

        // Written to the story's state, never to its transcript.
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
    }

    [Fact]
    public async Task Do_alone_writes_the_next_beat_under_the_framed_direction()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        _model.Says("Evening falls over the headland.");

        var outcome = await RunAsync(chat, "/do skip to the evening");

        outcome.ShouldBe(new FrontEndOutcome("Evening falls over the headland.", Refused: false, Stored: true));

        // A reply and nothing of the reader's: the direction is not a turn.
        var after = await TurnsAsync(chat);
        after.Count.ShouldBe(turns + 1);
        after[^1].Role.ShouldBe(ChatRole.Assistant);

        _model.Calls[^1].ShouldContain(m => m.Content.Contains(LocalDirections.Direction("skip to the evening")));
    }

    [Fact]
    public async Task Do_with_a_message_sends_the_message_under_the_direction()
    {
        var chat = await StoryAsync();
        _model.Says("\"Elena,\" she says.");

        await RunAsync(chat, "/do keep it short\n\nI ask her name.");

        (await TurnsAsync(chat))[^2].Text.ShouldBe("I ask her name.");
        _model.Calls[^1].ShouldContain(m => m.Content.Contains(LocalDirections.Direction("keep it short")));
    }

    [Fact]
    public async Task Focus_hands_the_turn_over_through_the_same_framing()
    {
        var chat = await StoryAsync();
        _model.Says("Morwenna steps in.");

        (await RunAsync(chat, "/focus Morwenna")).Refused.ShouldBeFalse();

        _model.Calls[^1].ShouldContain(m => m.Content.Contains(LocalDirections.Focus("Morwenna")));
    }

    [Fact]
    public async Task Help_lists_what_works_here_without_calling_anything()
    {
        var chat = await StoryAsync();
        var calls = _model.Calls.Count;

        var outcome = await RunAsync(chat, "/help");

        outcome.Text.ShouldContain("/recap");
        outcome.Text.ShouldContain("/ask");
        _model.Calls.Count.ShouldBe(calls);
    }
}
