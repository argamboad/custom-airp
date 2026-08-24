using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Copying a story as far as one turn, so it can go two ways from there.
/// </summary>
/// <remarks>
/// The point of a branch is a second version of the same story, which means the copy has to
/// carry everything that makes the original read the way it does — the character it names, the
/// dials, the transcript, and the memory built out of those turns — while carrying nothing that
/// belongs to turns it does not have.
/// </remarks>
public sealed class BranchTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    /// <summary>Ten turns, with memory and a ledger built across them.</summary>
    private async Task<(string Id, List<MessageRecord> Messages)> SeedAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        await using var store = _factory.CreateDbContext();

        store.Conversations.Add(new ConversationRecord
        {
            Id = id,
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
            PersonaName = "allan",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });

        // The dials, stored the way the application stores them now: one row per choice.
        foreach (var (key, value) in new[]
                 {
                     ("creativity", "4"), ("lust", "5"), ("response-length", "3"), ("inner-thoughts", "true"),
                 })
        {
            store.DialValues.Add(new DialValueRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = id,
                Key = key,
                Value = value,
                UpdatedAtUtc = DateTimeOffset.UnixEpoch,
            });
        }

        var messages = new List<MessageRecord>();

        for (var i = 1; i <= 10; i++)
        {
            var message = new MessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = id,
                Sequence = i,
                Role = i % 2 == 1 ? ChatRole.User : ChatRole.Assistant,
                Text = $"Turn {i}.",
                SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
                RequestHash = "hash-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Embedding = [1, 2, 3],
            };

            messages.Add(message);
            store.Messages.Add(message);
        }

        store.Summaries.Add(new SummaryRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = id,
            FromSequence = 1,
            ToSequence = 4,
            Text = "They met at the dock.",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            MessageCount = 4,
        });

        // Straddles the branch point at 6: covers turns the copy will not have.
        store.Summaries.Add(new SummaryRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = id,
            FromSequence = 5,
            ToSequence = 8,
            Text = "They argued on the pier.",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            MessageCount = 4,
        });

        store.Facts.AddRange(
            new FactRecord
            {
                Id = "fact-early",
                ConversationId = id,
                Subject = "Elena",
                Text = "Elena keeps the lighthouse.",
                ValidFromSequence = 2,
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            },
            new FactRecord
            {
                Id = "fact-retired-later",
                ConversationId = id,
                Subject = "Elena",
                Text = "Elena is angry with him.",
                ValidFromSequence = 3,
                ValidToSequence = 9,
                SupersededById = "fact-after",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            },
            new FactRecord
            {
                Id = "fact-after",
                ConversationId = id,
                Subject = "Elena",
                Text = "Elena has forgiven him.",
                ValidFromSequence = 9,
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });

        store.Spend.Add(new SpendRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = id,
            Kind = SpendKind.Reply,
            AtUtc = DateTimeOffset.UnixEpoch,
            PromptTokens = 100,
            CompletionTokens = 20,
            Cost = 0.0028m,
        });

        await store.SaveChangesAsync();
        return (id, messages);
    }

    [Fact]
    public async Task The_copy_stops_at_the_turn_it_was_branched_from()
    {
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        var copied = await store.Messages
            .Where(m => m.ConversationId == branch.Id)
            .OrderBy(m => m.Sequence)
            .ToListAsync();

        copied.Count.ShouldBe(6);
        copied[^1].Text.ShouldBe("Turn 6.");
        copied.Select(m => m.Sequence).ShouldBe([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task The_original_is_left_exactly_as_it_was()
    {
        // The whole promise. A branch that touched the story it came from would be a worse
        // version of deleting from here.
        var (id, messages) = await SeedAsync();

        await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        (await store.Messages.CountAsync(m => m.ConversationId == id)).ShouldBe(10);
        (await store.Summaries.CountAsync(s => s.ConversationId == id)).ShouldBe(2);
        (await store.Facts.CountAsync(f => f.ConversationId == id)).ShouldBe(3);
    }

    [Fact]
    public async Task What_the_story_is_played_with_comes_along()
    {
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();
        var copy = await store.Conversations.SingleAsync(c => c.Id == branch.Id);

        copy.Name.ShouldBe("Vardhal (2)");
        copy.CharacterName.ShouldBe("elena");
        copy.PersonaName.ShouldBe("allan");
        copy.Speaker.ShouldBe("Elena");

        // The dials come over as rows, read back through the same contract that wrote them.
        var settings = await Provider().GetSettingsAsync(branch.Id);

        settings.Creativity.ShouldBe(4);
        settings.Lust.ShouldBe(5);
        settings.ResponseLength.ShouldBe(3);
        settings.InnerThoughts.ShouldBe(true);
    }

    [Fact]
    public async Task A_summary_covering_turns_the_copy_does_not_have_is_left_behind()
    {
        // It would describe a scene that has not happened in this version of the story, and the
        // model would be told about it as established fact.
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        var copied = await store.Summaries
            .Where(s => s.ConversationId == branch.Id)
            .ToListAsync();

        copied.ShouldHaveSingleItem().Text.ShouldBe("They met at the dock.");
    }

    [Fact]
    public async Task A_fact_retired_after_the_branch_point_is_still_true_in_the_branch()
    {
        // It was retired by turns the copy does not have. In this version of the story nothing
        // has contradicted it yet, and the fact that superseded it does not exist here to
        // point at.
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        var facts = await store.Facts
            .Where(f => f.ConversationId == branch.Id)
            .OrderBy(f => f.ValidFromSequence)
            .ToListAsync();

        facts.Count.ShouldBe(2);
        facts.ShouldNotContain(f => f.Text == "Elena has forgiven him.");

        var angry = facts.Single(f => f.Text == "Elena is angry with him.");
        angry.ValidToSequence.ShouldBeNull();
        angry.SupersededById.ShouldBeNull();
    }

    [Fact]
    public async Task The_ledger_is_not_copied()
    {
        // Spend is the one table that is not derived: one row per call that was actually
        // charged for. Copying it would invent a second bill for money spent once, and the
        // monthly report would say so.
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        (await store.Spend.CountAsync(s => s.ConversationId == branch.Id)).ShouldBe(0);
        (await store.Spend.CountAsync(s => s.ConversationId == id)).ShouldBe(1);
    }

    [Fact]
    public async Task Idempotency_hashes_are_not_carried_over()
    {
        // The hash is computed over the conversation's own id, so a copied one can never match
        // anything the copy computes. Keeping it would fill a column with values that look
        // meaningful and are not.
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        (await store.Messages.CountAsync(m => m.ConversationId == branch.Id && m.RequestHash != null))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Embeddings_come_across_rather_than_being_bought_again()
    {
        // Identical text, identical vector. Re-embedding would charge for the same lines twice
        // and leave retrieval blind until it finished.
        var (id, messages) = await SeedAsync();

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        (await store.Messages.CountAsync(m => m.ConversationId == branch.Id && m.Embedding != null))
            .ShouldBe(6);
    }

    [Fact]
    public async Task A_hidden_reply_does_not_come_back_in_the_copy()
    {
        // A rerolled reply belongs to the original's audit, where "why did it say that" gets
        // asked. The branch starts from the story as its reader can see it.
        var (id, messages) = await SeedAsync();

        await using (var hide = _factory.CreateDbContext())
        {
            var doomed = await hide.Messages.SingleAsync(m => m.Id == messages[3].Id);
            doomed.DeletedAtUtc = DateTimeOffset.UnixEpoch.AddHours(1);
            await hide.SaveChangesAsync();
        }

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");

        await using var store = _factory.CreateDbContext();

        var copied = await store.Messages
            .Where(m => m.ConversationId == branch.Id)
            .Select(m => m.Text)
            .ToListAsync();

        copied.ShouldNotContain("Turn 4.");
        copied.Count.ShouldBe(5);
    }

    [Fact]
    public async Task The_branch_can_be_played_on_without_touching_the_original()
    {
        // End to end: the copy is a conversation, not a snapshot. Sending into it must leave
        // the story it came from at the length it was.
        var (id, messages) = await SeedAsync();
        _model.Says("She looks up.");

        var branch = await Provider().BranchAsync(id, messages[5].Id, "Vardhal (2)");
        await Provider().SendAsync(branch.Id, "A different question.");

        await using var store = _factory.CreateDbContext();

        (await store.Messages.CountAsync(m => m.ConversationId == branch.Id)).ShouldBe(8);
        (await store.Messages.CountAsync(m => m.ConversationId == id)).ShouldBe(10);
    }
}
