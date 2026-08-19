using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The ledger, and the report over it.
/// </summary>
/// <remarks>
/// One property is load-bearing: a call that was billed is recorded whatever became of what it
/// produced. A reply regenerated away a second later cost exactly what a kept one did, and a
/// total that quietly left it out would disagree with the invoice in the direction that
/// flatters the application.
/// </remarks>
public sealed class SpendTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider(Action<AirpOptions>? configure = null) => new(
        _factory,
        _model,
        TestOptions.Default(configure),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<string> StartAsync()
        => (await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterDefinition = "Elena teaches composition.",
            Opening = "She is already at the piano.",
        })).Id;

    [Fact]
    public async Task A_reply_records_what_the_provider_charged()
    {
        var id = await StartAsync();
        _model.Says("She looks up.");

        await Provider().SendAsync(id, "I come in.");

        var report = await Provider().SpendAsync(conversationId: id);
        var line = report.Conversations.ShouldHaveSingleItem();

        line.Calls.ShouldBe(1);
        line.Cost.ShouldBe(0.0002m);
        line.PromptTokens.ShouldBe(10);
        line.CompletionTokens.ShouldBe(5);
        line.CachedTokens.ShouldBe(4);
        line.Unpriced.ShouldBe(0);
        line.Name.ShouldBe("Vardhal");
    }

    [Fact]
    public async Task The_generation_identifier_is_kept_so_a_charge_can_be_traced_back()
    {
        var id = await StartAsync();
        _model.Says("She looks up.");

        await Provider().SendAsync(id, "I come in.");

        await using var store = _factory.CreateDbContext();
        var row = await store.Spend.SingleAsync();

        row.GenerationId.ShouldNotBeNullOrWhiteSpace();
        row.Provider.ShouldBe("test-host");
        row.Kind.ShouldBe(SpendKind.Reply);
        row.MessageId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_reply_regenerated_away_is_still_counted_and_reported_as_discarded()
    {
        // The question that started this: rerolled replies cost money. They are hidden from the
        // transcript, never from the bill.
        var id = await StartAsync();

        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, RegenerateReason.Steer);

        var line = (await Provider().SpendAsync(conversationId: id)).Conversations.ShouldHaveSingleItem();

        line.Calls.ShouldBe(2);
        line.Cost.ShouldBe(0.0004m);
        line.DiscardedCalls.ShouldBe(1);
        line.DiscardedCost.ShouldBe(0.0002m);
    }

    [Fact]
    public async Task Whether_a_reply_was_discarded_is_read_now_rather_than_frozen_when_it_was_paid_for()
    {
        // The ledger row is written before anyone knows the reply will be rerolled. Reading the
        // tombstone at report time is what lets the answer change afterwards.
        var id = await StartAsync();

        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        (await Provider().SpendAsync(conversationId: id)).DiscardedCost.ShouldBe(0m);

        _model.Says("She does not look up.");
        await Provider().RegenerateAsync(id, RegenerateReason.Steer);

        (await Provider().SpendAsync(conversationId: id)).DiscardedCost.ShouldBe(0.0002m);
    }

    [Fact]
    public async Task Compression_and_extraction_are_recorded_rather_than_invisible()
    {
        // These fire without the reader asking for anything. Before the ledger they spent money
        // that appeared in no accounting anywhere.
        var id = Guid.NewGuid().ToString("N");

        await using (var store = _factory.CreateDbContext())
        {
            store.Conversations.Add(new ConversationRecord
            {
                Id = id,
                Name = "Vardhal",
                Speaker = "Elena",
                CharacterDefinition = "You are Elena.",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });

            // Forty turns of sixty words is what it takes to overflow a 2,500-token budget,
            // matching the shape the summariser's own tests compress.
            for (var i = 1; i <= 40; i++)
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
        }

        _model.Says("They talked for a while.");
        _model.Says("She looks up.");

        await Provider(o =>
        {
            o.Model.ContextBudget = 2500;
            o.Model.MaxTokens = 200;
        }).SendAsync(id, "I come in.");

        var line = (await Provider().SpendAsync(conversationId: id)).Conversations.ShouldHaveSingleItem();

        line.ByKind.ShouldContain(k => k.Kind == SpendKind.Summary && k.Calls > 0);
        line.ByKind.ShouldContain(k => k.Kind == SpendKind.Reply && k.Calls > 0);
        line.Calls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task A_question_asked_out_of_character_is_its_own_kind_of_spending()
    {
        var id = await StartAsync();
        _model.Says("The story does not say.");

        await Provider().AskAsync(id, "How old is she?");

        var line = (await Provider().SpendAsync(conversationId: id)).Conversations.ShouldHaveSingleItem();

        line.ByKind.ShouldHaveSingleItem().Kind.ShouldBe(SpendKind.Aside);
        line.DiscardedCost.ShouldBe(0m);
    }

    [Fact]
    public async Task A_call_the_api_did_not_price_is_reported_as_unpriced_rather_than_free()
    {
        // Zero and "never said" are different facts. Adding them together would make the total
        // confidently wrong instead of honestly incomplete.
        var id = await StartAsync();
        _model.SaysUnpriced("She looks up.");

        await Provider().SendAsync(id, "I come in.");

        var report = await Provider().SpendAsync(conversationId: id);

        report.Unpriced.ShouldBe(1);
        report.Cost.ShouldBe(0m);
    }

    [Fact]
    public async Task The_window_keeps_a_month_to_itself()
    {
        var id = await StartAsync();
        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        var now = DateTimeOffset.UtcNow;

        (await Provider().SpendAsync(
            fromUtc: new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero),
            toUtc: new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1)))
            .Cost.ShouldBe(0.0002m);

        (await Provider().SpendAsync(
            fromUtc: now.AddYears(-1),
            toUtc: now.AddYears(-1).AddMonths(1)))
            .Conversations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Purging_keeps_the_ledger_and_says_what_it_kept()
    {
        // Erasing the story does not un-spend it, and the ledger carries no story text — so it
        // stays, and a report covering that month keeps adding up.
        var id = await StartAsync();
        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        await Provider().DeleteConversationAsync(id);
        var report = await Provider().PurgeDeletedAsync();

        report.LedgerKept.Rows.ShouldBe(1);
        report.LedgerKept.Cost.ShouldBe(0.0002m);

        await using var store = _factory.CreateDbContext();
        (await store.Spend.CountAsync()).ShouldBe(1);
        (await store.Messages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_purged_conversation_still_reports_its_spending_under_a_name_that_says_so()
    {
        var id = await StartAsync();
        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        await Provider().DeleteConversationAsync(id);
        await Provider().PurgeDeletedAsync();

        var line = (await Provider().SpendAsync()).Conversations.ShouldHaveSingleItem();

        line.Name.ShouldBe("(purged)");
        line.Cost.ShouldBe(0.0002m);
    }

    [Fact]
    public async Task The_cached_share_is_the_prompt_the_provider_did_not_read_again()
    {
        var id = await StartAsync();
        _model.Says("She looks up.");
        await Provider().SendAsync(id, "I come in.");

        var line = (await Provider().SpendAsync(conversationId: id)).Conversations.ShouldHaveSingleItem();

        // Four cached out of ten prompt tokens, as the scripted model reports.
        line.CachedShare.ShouldBe(0.4);
    }

    [Fact]
    public async Task Conversations_are_reported_dearest_first()
    {
        var first = await StartAsync();
        var second = await StartAsync();

        _model.Says("a");
        await Provider().SendAsync(first, "one.");

        _model.Says("b");
        _model.Says("c");
        await Provider().SendAsync(second, "two.");
        await Provider().SendAsync(second, "three.");

        var report = await Provider().SpendAsync();

        report.Conversations[0].ConversationId.ShouldBe(second);
        report.Conversations[0].Cost.ShouldBeGreaterThan(report.Conversations[1].Cost);
        report.Cost.ShouldBe(0.0006m);
    }
}
