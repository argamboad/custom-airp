using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Every reason a reader can pick, driven through the real regenerate path.
/// </summary>
/// <remarks>
/// <para>
/// A theory rather than a handful of spot checks, because the failure this guards against was
/// found in play and not in a test: a directive sent bare — <c>Use at least 30 words</c> —
/// came back as the character's reply, word for word. Some backends read a trailing imperative
/// as the latest thing said rather than as a note about what to write.
/// </para>
/// <para>
/// Covering the whole of <see cref="RegenerateReasons.All"/> is the point. The first fix
/// framed the reason the reader happened to be using; five others were still phrased the old
/// way, and nothing would have caught that. A reason added later fails here until it is
/// framed too.
/// </para>
/// </remarks>
public sealed class RegenerateDirectiveTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    public static TheoryData<RegenerateReason> EveryReason
    {
        get
        {
            var data = new TheoryData<RegenerateReason>();

            foreach (var reason in RegenerateReasons.All)
            {
                data.Add(reason);
            }

            return data;
        }
    }

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    /// <summary>Starts a story and gets one reply into it, so there is something to reroll.</summary>
    private async Task<string> ReadyAsync()
    {
        var id = (await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterDefinition = "Elena teaches composition.",
            Opening = "She is already at the piano.",
        })).Id;

        _model.Says("She looks up from the keys.");
        await Provider().SendAsync(id, "I come in.");

        return id;
    }

    /// <summary>The directive as it actually reaches the model: the last turn of the prompt.</summary>
    private string LastDirective() => _model.Calls[^1][^1].Content;

    [Theory]
    [MemberData(nameof(EveryReason))]
    public async Task Every_reason_says_the_note_is_not_something_to_answer(RegenerateReason reason)
    {
        var id = await ReadyAsync();

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, reason);

        var directive = LastDirective();

        directive.ShouldContain("withdrawn");
        directive.ShouldContain("not something to answer");
        directive.ShouldContain("the scene itself");
        directive.ShouldContain("never write the user's words");
    }

    [Theory]
    [MemberData(nameof(EveryReason))]
    public async Task Every_reason_keeps_the_readers_own_note_apart_from_its_own_words(RegenerateReason reason)
    {
        // Run together with the canned sentence, a bare constraint reads as one more line of
        // prose to reply to. Under a label it reads as a constraint.
        var id = await ReadyAsync();

        _model.Says("She does not look up, and the room stays quiet for a while.");
        await Provider().RegenerateAsync(id, reason, "Use at least 30 words");

        LastDirective().ShouldContain("Also, from the reader: Use at least 30 words");
    }

    [Theory]
    [MemberData(nameof(EveryReason))]
    public async Task No_reason_points_at_the_reply_it_is_replacing(RegenerateReason reason)
    {
        // The superseded reply is hidden before the call, so the model cannot see it. A note
        // about "the previous reply" asks it to guess what it is avoiding.
        var id = await ReadyAsync();

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, reason);

        var sent = _model.Calls[^1];

        sent[^1].Content.ShouldNotContain("previous reply");
        sent.ShouldNotContain(m => m.Content.Contains("She looks up from the keys.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_reason_is_the_only_thing_that_makes_the_second_attempt_differ()
    {
        // So two reasons producing the same words would make one of them a lie to the reader,
        // who picked it expecting something to change.
        var directives = new List<string>();

        foreach (var reason in RegenerateReasons.All)
        {
            var id = await ReadyAsync();

            _model.Says("She does not look up.");
            await Provider().RegenerateAsync(id, reason);

            directives.Add(LastDirective());
        }

        directives.Distinct(StringComparer.Ordinal).Count().ShouldBe(RegenerateReasons.All.Count);
    }

    [Fact]
    public async Task A_reason_with_no_note_of_your_own_carries_no_empty_label()
    {
        var id = await ReadyAsync();

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, RegenerateReason.Looping);

        LastDirective().ShouldNotContain("Also, from the reader");
    }
}
