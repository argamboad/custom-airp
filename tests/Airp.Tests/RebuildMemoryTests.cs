using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Spending invariant 6: throwing the derived memory away and making it again.
/// </summary>
/// <remarks>
/// The reason this exists is that a memory can be produced badly — by a version with a bug in
/// it — and a story already played cannot be played again to fix it. Summaries, facts and
/// embeddings are derived from <c>Messages</c>, so they can be produced a second time. A
/// hand-written fact is not derived from anything, and is the one thing here a rebuild must
/// never take.
/// </remarks>
public sealed class RebuildMemoryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "airp-rebuild-" + Guid.NewGuid().ToString("N"));

    public RebuildMemoryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));

        File.WriteAllText(
            Path.Combine(_root, "characters", "elena.txt"),
            "You are Elena. " + string.Join(' ', Enumerable.Repeat("detail", 3000)));
    }

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(o =>
        {
            o.Model.ContextBudget = 6000;
            o.Model.MaxTokens = 200;
        }),
        NullLogger<LocalConversationProvider>.Instance,
        embeddings: null,
        library: new TextLibrary(_root));

    private async Task<string> SeedAsync(int turns)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var store = _factory.CreateDbContext();

        store.Conversations.Add(new ConversationRecord
        {
            Id = id,
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
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

    /// <summary>Puts a badly-shaped memory in place: one summary per message, as the bug made.</summary>
    private async Task DamageAsync(string id)
    {
        await using var store = _factory.CreateDbContext();

        for (var seq = 1; seq <= 20; seq++)
        {
            store.Summaries.Add(new SummaryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = id,
                FromSequence = seq,
                ToSequence = seq,
                Text = $"Something happened in turn {seq}, at length, saying little.",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
                MessageCount = 1,
            });
        }

        store.Facts.Add(new FactRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = id,
            Subject = "User",
            Text = "User has named the squirrel Arnaldo.",
            ValidFromSequence = 5,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });

        await store.SaveChangesAsync();
    }

    private void ScriptEnoughAnswers()
    {
        for (var i = 0; i < 40; i++)
        {
            _model.Says("They talked by the water.");
        }
    }

    [Fact]
    public async Task The_old_summaries_are_replaced_by_ones_covering_the_same_turns()
    {
        var id = await SeedAsync(60);
        await DamageAsync(id);
        ScriptEnoughAnswers();

        var report = await Provider().RebuildMemoryAsync(id);

        report.SummariesRemoved.ShouldBe(20);
        report.SummariesWritten.ShouldBeLessThan(20, "fewer and longer is the point of doing this");
        report.MessagesCovered.ShouldBeGreaterThan(20);

        await using var store = _factory.CreateDbContext();

        var summaries = await store.Summaries
            .Where(s => s.ConversationId == id)
            .ToListAsync();

        summaries.ShouldAllBe(s => s.MessageCount > 2);
        summaries.ShouldNotContain(s => s.Text.StartsWith("Something happened in turn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_hand_written_fact_survives_the_rebuild()
    {
        // The one thing here that is not derived from anything. Deleting it would be the only
        // unrecoverable act this command could perform.
        var id = await SeedAsync(60);
        await DamageAsync(id);

        var provider = Provider();
        await provider.AddFactAsync(id, "Elena", "She is allergic to shellfish.");

        ScriptEnoughAnswers();

        var report = await provider.RebuildMemoryAsync(id);

        report.PinnedKept.ShouldBe(1);

        await using var store = _factory.CreateDbContext();

        var kept = await store.Facts
            .Where(f => f.ConversationId == id && f.Pinned)
            .ToListAsync();

        kept.ShouldHaveSingleItem().Text.ShouldBe("She is allergic to shellfish.");
    }

    [Fact]
    public async Task The_extracted_facts_are_thrown_away_and_asked_for_again()
    {
        var id = await SeedAsync(60);
        await DamageAsync(id);
        ScriptEnoughAnswers();

        var report = await Provider().RebuildMemoryAsync(id);

        report.FactsRemoved.ShouldBe(1);

        await using var store = _factory.CreateDbContext();

        (await store.Facts.AnyAsync(f => f.ConversationId == id && f.Subject == "User"))
            .ShouldBeFalse("the mislabelled fact was the reason for rebuilding");
    }

    [Fact]
    public async Task The_transcript_is_not_touched()
    {
        // Everything else here is derived. The operation is only safe because it never reaches
        // for the one thing that is not.
        var id = await SeedAsync(60);
        await DamageAsync(id);
        ScriptEnoughAnswers();

        await Provider().RebuildMemoryAsync(id);

        await using var store = _factory.CreateDbContext();
        (await store.Messages.CountAsync(m => m.ConversationId == id)).ShouldBe(60);
    }

    [Fact]
    public async Task The_ledger_is_added_to_rather_than_reset()
    {
        // Spend is the other table that is not derived. Those calls happened, and a rebuild
        // that erased them would make the monthly report a work of fiction.
        var id = await SeedAsync(60);
        await DamageAsync(id);

        await using (var store = _factory.CreateDbContext())
        {
            store.Spend.Add(new SpendRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = id,
                Kind = SpendKind.Summary,
                AtUtc = DateTimeOffset.UnixEpoch,
                Cost = 0.0028m,
            });

            await store.SaveChangesAsync();
        }

        ScriptEnoughAnswers();

        await Provider().RebuildMemoryAsync(id);

        await using var check = _factory.CreateDbContext();

        (await check.Spend.CountAsync(s => s.ConversationId == id))
            .ShouldBeGreaterThan(1, "the old row stays and the rebuild's own calls are recorded beside it");
    }
}
