using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Asking about the story out of character, and the directions a turn can be given.
/// </summary>
/// <remarks>
/// One property carries most of the weight here: an aside adds nothing to <c>Messages</c>. If
/// it ever did, retrieval would embed it, the summariser would compress it as something that
/// happened and the extractor would pull facts from it — and append-only means none of that
/// could be undone. So the transcript is asserted on directly rather than trusted.
/// </remarks>
public sealed class AsideTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<string> StartAsync()
        => (await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterDefinition = "Elena teaches composition and never says the quiet part.",
            Opening = "She is already at the piano when you come in.",
        })).Id;

    [Fact]
    public async Task Asking_adds_nothing_to_the_transcript()
    {
        var id = await StartAsync();
        var before = await Provider().GetMessagesAsync(id);

        _model.Says("The story does not say where she lives.");
        var answer = await Provider().AskAsync(id, "Where does Elena live?");

        answer.Answer.ShouldBe("The story does not say where she lives.");

        var after = await Provider().GetMessagesAsync(id);
        after.Count.ShouldBe(before.Count);
    }

    [Fact]
    public async Task Asking_records_what_it_cost()
    {
        // A billed call that left no trace would make the audit quietly stop adding up, which
        // is exactly the failure invariant 4 exists to prevent.
        var id = await StartAsync();
        _model.Says("She has not said what she thinks of him.");

        await Provider().AskAsync(id, "What has Elena not said out loud?");

        var asides = await Provider().AsidesAsync(id);

        asides.Count.ShouldBe(1);
        asides[0].Question.ShouldBe("What has Elena not said out loud?");
        asides[0].PromptTokens.ShouldBe(10);
        asides[0].CompletionTokens.ShouldBe(5);
        asides[0].ContextAudit.ShouldNotBeNullOrWhiteSpace();
        asides[0].EstimatedPromptTokens.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Asking_sends_the_same_layers_a_turn_would()
    {
        // The prompt has to match a real turn up to the instruction, or the answer is grounded
        // in a different story than the one the character is playing — and on a caching
        // provider it would also pay full price for a prefix the next turn is about to reuse.
        var id = await StartAsync();

        _model.Says("She sits down.");
        await Provider().SendAsync(id, "I sit next to her.");

        _model.Says("The story does not say.");
        await Provider().AskAsync(id, "How old is she?");

        var turn = _model.Calls[0];
        var aside = _model.Calls[1];

        // A prefix, exactly. The aside appends the reply that has landed since and its own
        // directive; everything before that is byte-for-byte what the turn sent, which is what
        // a provider's cache is keyed on.
        aside.Count.ShouldBeGreaterThan(turn.Count);

        foreach (var (sent, asked) in turn.Zip(aside))
        {
            asked.Content.ShouldBe(sent.Content);
            asked.Role.ShouldBe(sent.Role);
        }
    }

    [Fact]
    public async Task Asking_frames_the_question_as_the_author_rather_than_the_character()
    {
        var id = await StartAsync();
        _model.Says("Nothing in the story says.");

        await Provider().AskAsync(id, "Who else lives in that house?");

        var last = _model.Calls[^1][^1];

        last.Content.ShouldContain("Step out of the scene");
        last.Content.ShouldContain("Who else lives in that house?");
    }

    [Fact]
    public async Task Asking_runs_cold_so_an_answer_that_gets_pinned_is_not_invented()
    {
        var id = await StartAsync();
        _model.Says("Nothing says.");

        await Provider().AskAsync(id, "How far is the rehearsal room?");

        _model.LastTemperature.ShouldBe(0.4);
    }

    [Fact]
    public async Task A_failed_question_hands_back_no_partial_message()
    {
        // Unlike a send: nothing was stored, because asking is not a turn.
        var id = await StartAsync();
        _model.Fails();

        var thrown = await Should.ThrowAsync<ReplyMissingException>(
            async () => await Provider().AskAsync(id, "What is she thinking?"));

        thrown.Partial.ShouldBeEmpty();

        var asides = await Provider().AsidesAsync(id);
        asides.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_direction_replaces_the_carry_on_wording_rather_than_joining_it()
    {
        // Two answers to "what should this turn be" in one prompt is how a reply comes back
        // satisfying neither.
        var id = await StartAsync();
        _model.Says("Mariana gathers her coat and goes.");

        await Provider().ContinueAsync(id, instruction: "A direction: have Mariana leave.");

        var last = _model.Calls[^1][^1];

        last.Content.ShouldBe("A direction: have Mariana leave.");
        last.Content.ShouldNotContain("Carry the scene forward");
    }

    [Fact]
    public async Task Carrying_on_with_no_direction_still_says_not_to_wait()
    {
        var id = await StartAsync();
        _model.Says("The rehearsal runs on without him.");

        await Provider().ContinueAsync(id);

        _model.Calls[^1][^1].Content.ShouldContain("does not wait");
    }

    [Fact]
    public async Task A_message_can_carry_a_direction_without_storing_it()
    {
        // The direction steers the reply and is not part of what the reader said. Stored as
        // part of the message it would be in every later prompt, and in the transcript for good.
        var id = await StartAsync();
        _model.Says("She answers in three words.");

        await Provider().SendAsync(id, "What did you think of it?", instruction: "Keep this reply short.");

        var transcript = await Provider().GetMessagesAsync(id);
        var said = transcript.Last(static m => m.Role == ChatRole.User);

        said.Text.ShouldBe("What did you think of it?");

        var sent = _model.Calls[^1];
        sent[^1].Content.ShouldBe("Keep this reply short.");
        sent.ShouldNotContain(m => m.Content.Contains("What did you think of it?", StringComparison.Ordinal)
                                   && m.Content.Contains("Keep this reply short.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_same_words_under_two_directions_are_two_different_sends()
    {
        // Idempotency is anchored on the request, and the direction is part of what is being
        // asked for. Without it the second would be handed the first one's reply back.
        var id = await StartAsync();

        _model.Says("She smiles.");
        await Provider().SendAsync(id, "Say something.", instruction: "Keep it short.");

        _model.Says("She talks for a while about the second movement, and then about the rain.");
        await Provider().SendAsync(id, "Say something.", instruction: "Take your time.");

        var transcript = await Provider().GetMessagesAsync(id);

        transcript.Count(static m => m.Role == ChatRole.User && m.Text == "Say something.").ShouldBe(2);
    }

    [Fact]
    public async Task Purging_takes_the_questions_with_the_conversation()
    {
        var id = await StartAsync();
        _model.Says("Nothing says.");
        await Provider().AskAsync(id, "Where is she from?");

        await Provider().DeleteConversationAsync(id);
        await Provider().PurgeDeletedAsync();

        await using var store = _factory.CreateDbContext();
        (await store.Asides.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_regenerate_note_is_framed_so_it_cannot_be_read_as_something_to_answer()
    {
        // Observed in play: a bare trailing imperative — "Use at least 30 words" — came back
        // as the reply, verbatim. A directive with nothing around it reads as the latest thing
        // said rather than as a note about what to write.
        var id = await StartAsync();

        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        _model.Says("She does not look up, and the room stays quiet for a long moment.");
        await Provider().RegenerateAsync(id, RegenerateReason.Steer, "Use at least 30 words");

        var directive = _model.Calls[^1][^1].Content;

        directive.ShouldContain("Use at least 30 words");
        directive.ShouldContain("not something to answer");
        directive.ShouldContain("the scene itself");

        // The reader's words stand apart from the canned reason rather than running on from it.
        directive.ShouldContain("Also, from the reader: Use at least 30 words");
    }

    [Fact]
    public async Task A_regenerate_directive_never_points_at_the_reply_it_is_replacing()
    {
        // The previous attempt is hidden before the call, so the model cannot see it. Telling
        // it to write something "differently" from text it was never shown is asking it to
        // guess what it is avoiding.
        var id = await StartAsync();

        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, RegenerateReason.Steer);

        var sent = _model.Calls[^1];

        sent[^1].Content.ShouldNotContain("previous reply differently");
        sent.ShouldNotContain(m => m.Content.Contains("She looks up.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_regenerate_with_no_note_of_your_own_is_still_framed()
    {
        var id = await StartAsync();

        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, RegenerateReason.TooShort);

        var directive = _model.Calls[^1][^1].Content;

        directive.ShouldContain("withdrawn");
        directive.ShouldContain("never write the user's words");
        directive.ShouldNotContain("Also, from the reader");
    }
}
