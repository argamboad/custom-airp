using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The memory, exercised on a conversation shaped the way real ones are.
/// </summary>
/// <remarks>
/// <para>
/// Every other test in this suite writes the character into
/// <see cref="ConversationRecord.CharacterDefinition"/>, which no conversation the application
/// creates actually does: a conversation stores the <em>name</em> and the text lives in a file,
/// so that editing the file reaches every story using it. That gap hid the worst bug this
/// project has had.
/// </para>
/// <para>
/// The summariser reserved room for the character by reading the record's own text — empty in
/// every real conversation — so it believed the transcript had the whole budget while the
/// builder, which resolves the file, had less than half of it. Nothing was ever compressed and
/// the builder dropped the oldest turns instead. Found on a 202-message story with a
/// 30,000-token character: twenty-four turns gone, and no summary, fact or embedding written
/// for any of them. The forgetting this project exists to prevent, running undetected behind
/// five hundred passing tests.
/// </para>
/// </remarks>
public sealed class CharacterInAFileTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "airp-library-" + Guid.NewGuid().ToString("N"));

    /// <summary>A character big enough that ignoring it changes every decision about the budget.</summary>
    private const int CharacterWords = 3000;

    public CharacterInAFileTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));
        Directory.CreateDirectory(Path.Combine(_root, "personas"));

        File.WriteAllText(
            Path.Combine(_root, "characters", "elena.txt"),
            "You are Elena. " + string.Join(' ', Enumerable.Repeat("detail", CharacterWords)));
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

    /// <summary>Seeds a conversation that names its character rather than carrying a copy.</summary>
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

    [Fact]
    public async Task The_character_file_is_counted_when_deciding_what_no_longer_fits()
    {
        // The regression test, stated as the failure: turns must not leave the prompt without
        // a summary having been written for them.
        var id = await SeedAsync(60);
        _model.Says("They met at the dock.").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id))
            .ShouldBeGreaterThan(0, "the character file fills most of the budget, so the "
                + "oldest turns cannot fit and have to be compressed rather than dropped");
    }

    [Fact]
    public async Task Nothing_leaves_the_prompt_that_was_not_written_down_first()
    {
        // The stronger claim, and the one that actually matters. Compressing something is not
        // enough: what the builder drops has to be a subset of what the summariser covered.
        var id = await SeedAsync(60);
        _model.Says("They met at the dock.").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        var audited = await store.Messages
            .Where(m => m.ConversationId == id && m.ContextAudit != null)
            .OrderBy(m => m.Sequence)
            .LastAsync();

        // The audit records the layer breakdown for the reply, dropped turns included.
        audited.ContextAudit.ShouldNotBeNull();
        audited.ContextAudit.ShouldNotContain(
            "dropped",
            Case.Sensitive,
            "a dropped turn is one nobody wrote anything down about: it is gone from the "
            + "prompt and there is no summary, fact or embedding standing in for it");
    }

    [Fact]
    public async Task A_transcript_sitting_at_the_budget_edge_is_compressed_in_batches()
    {
        // The shape that only appears with a character in a file: a card big enough to hold the
        // transcript against the ceiling, so every send overflows by exactly the exchange that
        // was just added. Compressing only that overflow means a summarising call per turn over
        // two messages — and a two-message summary is not smaller than the two messages.
        // Observed on a real story before this was fixed: six summaries in forty minutes, one of
        // them longer than the turns it replaced.
        var id = await SeedAsync(60);

        for (var i = 0; i < 40; i++)
        {
            _model.Says("They talked by the water.");
        }

        for (var send = 1; send <= 6; send++)
        {
            await Provider().SendAsync(id, $"Message {send}.");
        }

        await using var store = _factory.CreateDbContext();

        var summaries = await store.Summaries
            .Where(s => s.ConversationId == id)
            .OrderBy(s => s.FromSequence)
            .ToListAsync();

        summaries.ShouldNotBeEmpty("the card fills most of the budget, so the transcript must be compressed");

        summaries.Count.ShouldBeLessThan(
            6,
            "compressing only what overflowed fires again on the very next send");

        foreach (var summary in summaries)
        {
            summary.MessageCount.ShouldBeGreaterThan(
                2,
                "a stretch this short costs a call and yields neither a shorter prompt nor a fact");
        }
    }

    [Fact]
    public async Task A_conversation_that_fits_beside_its_character_is_still_never_compressed()
    {
        // Reserving honestly must not tip into compressing eagerly: the guarantee that a short
        // story costs exactly one call is the other half of the design.
        var id = await SeedAsync(4);
        _model.Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id)).ShouldBe(0);
        _model.Calls.Count.ShouldBe(1);
    }
}
