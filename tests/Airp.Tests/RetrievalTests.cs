using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Embeds by keyword rather than by meaning, so a test can state which turn should come back.
/// </summary>
/// <remarks>
/// A real embedding model would make these tests assertions about someone else's vector space.
/// What is worth testing here is the wiring: that only compressed turns are candidates, that
/// the threshold and the count are honoured, and that a failure is survivable.
/// </remarks>
internal sealed class KeywordEmbedder : IEmbeddingClient
{
    private static readonly string[] Axes = ["ferrin", "dock", "knife", "silver"];

    public int Calls { get; private set; }

    public bool Broken { get; set; }

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        Calls++;

        if (Broken)
        {
            throw new Airp.Domain.ModelUnavailableException("embeddings down");
        }

        return Task.FromResult<IReadOnlyList<float[]>>(
        [
            .. texts.Select(t => Axes
                .Select(a => t.Contains(a, StringComparison.OrdinalIgnoreCase) ? 1f : 0f)
                .ToArray()),
        ]);
    }
}

public sealed class SimilarityTests
{
    [Fact]
    public void Identical_vectors_score_one()
        => Similarity.Cosine([1f, 0f, 1f], [1f, 0f, 1f]).ShouldBe(1f, 0.0001);

    [Fact]
    public void Orthogonal_vectors_score_zero()
        => Similarity.Cosine([1f, 0f], [0f, 1f]).ShouldBe(0f, 0.0001);

    [Fact]
    public void Vectors_that_cannot_be_compared_score_zero_rather_than_throwing()
    {
        // Different lengths, empties and zero vectors are all "not comparable", and a caller
        // ranking by score wants the same answer for all three.
        Similarity.Cosine([1f, 0f], [1f]).ShouldBe(0f);
        Similarity.Cosine([], []).ShouldBe(0f);
        Similarity.Cosine([0f, 0f], [1f, 1f]).ShouldBe(0f);
    }

    [Fact]
    public void A_vector_survives_the_round_trip_through_storage()
    {
        float[] original = [0.5f, -0.25f, 1f, 0f];

        Similarity.FromBytes(Similarity.ToBytes(original)).ShouldBe(original);
    }

    [Fact]
    public void Nothing_stored_reads_back_as_an_empty_vector()
        => Similarity.FromBytes(null).ShouldBeEmpty();
}

public sealed class RetrievalTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly KeywordEmbedder _embedder = new();

    public void Dispose() => _factory.Dispose();

    private static Action<AirpOptions> SmallBudget => o =>
    {
        o.Model.ContextBudget = 2500;
        o.Model.MaxTokens = 200;
    };

    private LocalConversationProvider Provider(Action<AirpOptions>? configure = null) => new(
        _factory,
        _model,
        TestOptions.Default(configure ?? SmallBudget),
        NullLogger<LocalConversationProvider>.Instance,
        _embedder);

    private async Task<string> SeedAsync(int turns, string buried)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var store = _factory.CreateDbContext();

        store.Conversations.Add(new ConversationRecord
        {
            Id = id,
            Name = "Vardhal",
            Speaker = "Elena",
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
                // One early turn carries the keyword; the rest are filler of similar weight.
                Text = i == 3 ? buried : $"Turn {i}. " + string.Join(' ', Enumerable.Repeat("filler", 60)),
                SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
            });
        }

        await store.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task A_buried_turn_comes_back_when_the_new_message_is_about_it()
    {
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver at the dock.");
        _model.Says("Summary.").Says("Fine.");

        await Provider().SendAsync(id, "What happened with Ferrin?");

        var prompt = _model.Calls[^1];
        prompt.Any(m => m.Content.Contains("clipped silver")).ShouldBeTrue();
    }

    [Fact]
    public async Task Recalled_turns_sit_after_the_transcript()
    {
        // The cache contract. Memories change every turn, so everything stable has to precede
        // them or it is reprocessed along with them.
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver at the dock.");
        _model.Says("Summary.").Says("Fine.");

        await Provider().SendAsync(id, "What happened with Ferrin?");

        var prompt = _model.Calls[^1].ToList();
        var recalled = prompt.FindIndex(m => m.Content.Contains("clipped silver"));
        var lastTranscript = prompt.FindLastIndex(m => m.Content.StartsWith("Turn "));

        recalled.ShouldBeGreaterThan(lastTranscript);
    }

    [Fact]
    public async Task Nothing_is_recalled_when_nothing_is_relevant()
    {
        // A threshold, not a top-N. Four near-misses crowd out the recent turns and read as
        // noise; sending none is the better answer when none are about anything.
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver at the dock.");
        _model.Says("Summary.").Says("Fine.");

        await Provider().SendAsync(id, "It's cold today.");

        _model.Calls[^1].Any(m => m.Content.Contains("clipped silver")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_conversation_that_still_fits_is_not_embedded_at_all()
    {
        // Recent turns are sent whole. Embedding them would spend a call to retrieve what the
        // model can already read.
        var id = await SeedAsync(3, "Ferrin paid me in clipped silver.");
        _model.Says("Fine.");

        await Provider(o => o.Model.ContextBudget = 100000).SendAsync(id, "And Ferrin?");

        _embedder.Calls.ShouldBe(0);

        await using var store = _factory.CreateDbContext();
        (await store.Messages.CountAsync(m => m.Embedding != null)).ShouldBe(0);
    }

    [Fact]
    public async Task Only_compressed_turns_are_embedded()
    {
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver.");
        _model.Says("Summary.").Says("Fine.");

        await Provider().SendAsync(id, "And Ferrin?");

        await using var store = _factory.CreateDbContext();
        var embedded = await store.Messages.CountAsync(m => m.Embedding != null);
        var total = await store.Messages.CountAsync(m => m.ConversationId == id);

        embedded.ShouldBeGreaterThan(0);
        embedded.ShouldBeLessThan(total);
    }

    [Fact]
    public async Task An_embedding_endpoint_that_is_down_does_not_cost_the_reader_a_reply()
    {
        // Retrieval improves a prompt. It is never the reason a turn fails.
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver.");

        // Three calls, in order: the summary, the fact extraction, then the reply the reader
        // is actually waiting on.
        _model.Says("Summary.").Says("""{"facts":[],"retired":[]}""").Says("Fine.");
        _embedder.Broken = true;

        var added = await Provider().SendAsync(id, "And Ferrin?");

        added.Count.ShouldBe(2);
        added[1].Text.ShouldBe("Fine.");
    }

    [Fact]
    public async Task Retrieval_is_optional_and_its_absence_changes_nothing_else()
    {
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver.");
        _model.Says("Summary.").Says("Fine.");

        var withoutEmbedder = new LocalConversationProvider(
            _factory,
            _model,
            TestOptions.Default(SmallBudget),
            NullLogger<LocalConversationProvider>.Instance);

        var added = await withoutEmbedder.SendAsync(id, "And Ferrin?");

        added.Count.ShouldBe(2);

        await using var store = _factory.CreateDbContext();
        (await store.Summaries.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_recall_count_is_a_ceiling()
    {
        var id = await SeedAsync(40, "Ferrin paid me in clipped silver at the dock, with a knife.");
        _model.Says("Summary.").Says("Fine.");

        await Provider(o =>
        {
            o.Model.ContextBudget = 2500;
            o.Model.MaxTokens = 200;
            o.Model.RecallCount = 1;
        }).SendAsync(id, "Ferrin dock knife silver");

        var recalled = _model.Calls[^1].Count(m => m.Content.Contains("Earlier in this conversation"));
        recalled.ShouldBeLessThanOrEqualTo(1);
    }
}
