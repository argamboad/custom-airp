using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers the point of the project: turns that no longer fit are compressed rather than lost.
/// </summary>
public sealed class SummaryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>A budget small enough that a modest transcript overflows it.</summary>
    private static Action<AirpOptions> SmallBudget => o =>
    {
        o.Model.ContextBudget = 2500;
        o.Model.MaxTokens = 200;
    };

    private LocalConversationProvider Provider(Action<AirpOptions>? configure = null) => new(
        _factory,
        _model,
        TestOptions.Default(configure ?? SmallBudget),
        NullLogger<LocalConversationProvider>.Instance);

    /// <summary>Fills a conversation directly, so the model is not spent setting a test up.</summary>
    private async Task<string> SeedAsync(int turns)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var store = _factory.CreateDbContext();

        store.Conversations.Add(new ConversationRecord
        {
            Id = id,
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterDefinition = "You are Elena.",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });

        for (var i = 1; i <= turns; i++)
        {
            store.Messages.Add(new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = id,
                Sequence = i,
                Role = i % 2 == 1 ? ChatRole.User : ChatRole.Assistant,
                Text = $"Turn {i}. " + string.Join(' ', Enumerable.Repeat("word", 60)),
                SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
            });
        }

        await store.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task A_conversation_that_fits_is_never_summarised()
    {
        // Nothing is spent and nothing is compressed until compression is the alternative to
        // losing something.
        var id = await SeedAsync(3);
        _model.Says("Fine.");

        await Provider(o => o.Model.ContextBudget = 100000).SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        (await store.Summaries.CountAsync()).ShouldBe(0);
        _model.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Turns_that_no_longer_fit_are_summarised_instead_of_dropped()
    {
        var id = await SeedAsync(40);
        _model.Summarises("A summary of what happened.").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        var summary = await store.Summaries.SingleAsync();

        summary.FromSequence.ShouldBe(1);
        summary.ToSequence.ShouldBeGreaterThan(1);
        summary.MessageCount.ShouldBeGreaterThan(1);
        summary.Text.ShouldStartWith("A summary of what happened.");
    }

    [Fact]
    public async Task The_summary_reaches_the_prompt_ahead_of_the_recent_turns()
    {
        var id = await SeedAsync(40);
        _model.Summarises("They met at the dock.").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        // Second call is the reply; the first was the summarising.
        var prompt = _model.Calls[^1];
        var summaryAt = prompt.ToList().FindIndex(m => m.Content.Contains("dock"));
        var lastUser = prompt.ToList().FindLastIndex(m => m.Content == "Hello.");

        summaryAt.ShouldBeGreaterThan(0);
        summaryAt.ShouldBeLessThan(lastUser);
    }

    [Fact]
    public async Task The_compressed_turns_stay_in_the_store()
    {
        // The whole distinction. They leave the prompt; they do not leave the conversation.
        var id = await SeedAsync(40);
        _model.Summarises("Summary.").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        (await store.Messages.CountAsync(m => m.ConversationId == id && m.DeletedAtUtc == null))
            .ShouldBe(42);

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Count.ShouldBe(42);
    }

    [Fact]
    public async Task Already_summarised_turns_are_not_summarised_again()
    {
        // Covered ground is never revisited: each summary picks up where the last one ended.
        // Paying twice to compress the same turns would be the cheap half of the damage — the
        // expensive half is two accounts of one stretch, disagreeing.
        var id = await SeedAsync(40);
        _model.Summarises("Summary.").Says("One.").Says("Two.").Says("Three.");

        await Provider().SendAsync(id, "Hello.");

        await using (var first = _factory.CreateDbContext())
        {
            (await first.Summaries.CountAsync(s => s.ConversationId == id)).ShouldBe(1);
        }

        await Provider().SendAsync(id, "Again.");

        await using var store = _factory.CreateDbContext();

        var summaries = await store.Summaries
            .Where(s => s.ConversationId == id)
            .OrderBy(s => s.FromSequence)
            .ToListAsync();

        for (var i = 1; i < summaries.Count; i++)
        {
            summaries[i].FromSequence.ShouldBeGreaterThan(summaries[i - 1].ToSequence);
        }
    }

    [Fact]
    public async Task Compression_is_occasional_rather_than_a_toll_on_every_turn()
    {
        // Reserving room honestly leaves the transcript sitting near the budget, which raises
        // the obvious worry: does every send now pay for a summarising call and a fact
        // extraction on top of the reply? Measured, no — a summary frees far more room than it
        // occupies, so compressing buys headroom for several turns at a time. This is the
        // guarantee, and it is the one worth a test: it is what stops the memory from costing
        // three calls a turn.
        var id = await SeedAsync(40);

        for (var i = 0; i < 60; i++)
        {
            _model.Summarises("Summary.");
        }

        for (var send = 1; send <= 8; send++)
        {
            await Provider().SendAsync(id, $"Message {send}.");
        }

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id))
            .ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task A_summariser_that_fails_sends_the_turns_whole_rather_than_forgetting_them()
    {
        // Going over budget is recoverable; a character that has forgotten is not.
        //
        // Two failures, because one is retried now: a host answering with nothing is the lottery
        // rather than the request, and giving up on the first of those lost a real story its
        // most valuable extraction. "Failed" here means failed twice.
        var id = await SeedAsync(40);
        _model.Fails("summariser down").Fails("summariser down").Says("Fine.");

        var added = await Provider().SendAsync(id, "Hello.");

        added.Count.ShouldBe(2);

        await using var store = _factory.CreateDbContext();
        (await store.Summaries.CountAsync()).ShouldBe(0);

        var prompt = _model.Calls[^1];
        prompt.Any(m => m.Content.StartsWith("Turn 1.")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_audit_says_what_each_reply_was_built_from()
    {
        // The success criterion of the project, made reachable: after the fact, explain why the
        // model said what it said.
        var id = await SeedAsync(40);
        _model.Summarises("Summary.").Says("Fine.");
        await Provider().SendAsync(id, "Hello.");

        var audit = await Provider().AuditAsync(id);
        var reply = audit[^1];

        reply.Context.ShouldNotBeNullOrWhiteSpace();
        reply.Context.ShouldContain("history");
        reply.Context.ShouldContain("summaries");
        reply.EstimatedPromptTokens.ShouldNotBeNull();
        reply.Hidden.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rerolled_reply_stays_in_the_audit_marked_hidden()
    {
        // It is gone from the transcript and still on the record. "Why did it say that" is
        // asked most often about a reply that was thrown away.
        var id = await SeedAsync(3);
        _model.Says("Primera.").Says("Segunda.");
        await Provider(o => o.Model.ContextBudget = 100000).SendAsync(id, "Hello.");
        await Provider(o => o.Model.ContextBudget = 100000).RegenerateAsync(id, RegenerateReason.TooShort);

        var audit = await Provider().AuditAsync(id);

        // Three replies on the record: the seeded one, the rerolled one, and its replacement.
        // Only the rerolled one is gone from the transcript.
        audit.Count.ShouldBe(3);
        audit.Count(static a => a.Hidden).ShouldBe(1);
        audit.Single(static a => a.Hidden).Sequence
            .ShouldBeLessThan(audit[^1].Sequence);

        (await Provider().GetMessagesAsync(id)).Count(static m => m.Role == ChatRole.Assistant)
            .ShouldBe(2);
    }

    [Fact]
    public async Task Summaries_are_derived_and_deleting_them_loses_nothing()
    {
        // Invariant 1: the summary table can go entirely and the conversation is intact,
        // because everything it stood for is still in Messages.
        var id = await SeedAsync(40);
        _model.Summarises("Summary.").Says("Fine.");
        await Provider().SendAsync(id, "Hello.");

        await using (var store = _factory.CreateDbContext())
        {
            store.Summaries.RemoveRange(store.Summaries);
            await store.SaveChangesAsync();
        }

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Count.ShouldBe(42);
        transcript[0].Text.ShouldStartWith("Turn 1.");
    }
}
