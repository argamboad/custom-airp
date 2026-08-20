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
        _model.Summarises("They met at the dock.").Says("Fine.");

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
        _model.Summarises("They met at the dock.").Says("Fine.");

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
            _model.Summarises("They talked by the water.");
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
    public async Task A_reply_too_short_to_be_a_summary_is_refused_rather_than_believed()
    {
        // What actually happened, and the worst outcome this project has produced. A backlog of
        // ninety-nine messages went up in one call and "##" came back — two characters. It was
        // not empty, so it was stored, and it stood in for the first hundred turns of a real
        // story while those turns left the prompt. The forgetting this exists to prevent,
        // arriving through the machinery built to prevent it.
        var id = await SeedAsync(60);
        _model.Says("##").Says("##").Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id))
            .ShouldBe(0, "an answer that cannot be an account of the turns must not stand in for them");

        // And the turns are still whole in the prompt, over budget, which is the branch that
        // already exists for a summary that could not be written at all.
        var audited = await store.Messages
            .Where(m => m.ConversationId == id && m.ContextAudit != null)
            .OrderBy(m => m.Sequence)
            .LastAsync();

        audited.ContextAudit.ShouldNotBeNull();
        audited.ContextAudit.ShouldNotContain("dropped", Case.Sensitive);
    }

    [Fact]
    public async Task A_summary_the_host_cut_short_is_refused_rather_than_stored_half_written()
    {
        // The variant that got through the first guard. Not two characters this time but the
        // opening of a real answer, ending mid-word, reported as cut off at a fraction of the
        // ceiling it was given. Observed on the real story as "…a free practice room (Room",
        // standing in for twenty-seven messages.
        //
        // A summary is written in chronological order, so a clipped one loses its tail — the
        // newest events in the stretch, which are the ones the next turn needs most.
        var id = await SeedAsync(60);

        _model
            .Truncated("Allan ate ramen at the food court and wrote in his journal. He received an email about a free practice room (Room")
            .Truncated("Allan ate ramen at the food court and wrote in his journal. He received an email about a free practice room (Room")
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id))
            .ShouldBe(0, "half an account is not an account of the turns it replaces");
    }

    [Fact]
    public async Task A_summary_that_ran_to_the_ceiling_is_kept()
    {
        // The other side of it, and the reason the check is not simply "was it truncated". A
        // long account clipped at the ceiling is as much as was paid for; refusing that would
        // mean never compressing a busy stretch at all.
        var id = await SeedAsync(60);

        _model
            .Truncated(string.Join(' ', Enumerable.Repeat("They spoke at length and something was settled.", 200)))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        (await store.Summaries.CountAsync(s => s.ConversationId == id)).ShouldBe(1);
    }

    [Fact]
    public async Task No_single_summary_is_asked_to_carry_a_whole_backlog()
    {
        // The other half of the same failure. The summarising call has a fixed output ceiling,
        // so fidelity falls as the stretch grows: sixty-two messages came back as a usable
        // account and ninety-nine came back as punctuation. A backlog is worked down in several
        // passes instead of being handed over in one.
        var id = await SeedAsync(120);

        for (var i = 0; i < 40; i++)
        {
            _model.Summarises("They talked by the water.");
        }

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();

        var summaries = await store.Summaries
            .Where(s => s.ConversationId == id)
            .ToListAsync();

        summaries.ShouldNotBeEmpty();
        summaries.ShouldAllBe(s => s.MessageCount <= 40);
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

/// <summary>
/// Who the transcript says is speaking, when it is handed to a model to be read rather than
/// continued.
/// </summary>
/// <remarks>
/// Both background readers had their own copy of the same labelling expression, and both said
/// <c>User</c>. Observed on a real story: two extracted facts, both filed under the subject
/// "User", sitting under a summary that called the same person by name. The world layer told
/// the character that "User" had named a squirrel, while everything else in the prompt was
/// about Allan.
/// </remarks>
public class TranscriptLabelTests
{
    private static ConversationRecord Conversation(string? persona, string? speaker = "Elena")
        => new()
        {
            Id = "c",
            Name = "Vardhal",
            Speaker = speaker,
            PersonaName = persona,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };

    private static MessageRecord Message(ChatRole role, string text)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = "c",
            Sequence = 1,
            Role = role,
            Text = text,
            SentAtUtc = DateTimeOffset.UnixEpoch,
        };

    [Fact]
    public void The_reader_is_named_by_their_persona_rather_than_called_User()
    {
        Transcript.Reader(Conversation("allan-sanlucar-el-kettani"))
            .ShouldBe("Allan Sanlucar El Kettani");
    }

    [Fact]
    public void Without_a_persona_there_is_no_name_to_use()
    {
        // Inventing one would be worse than the generic label.
        Transcript.Reader(Conversation(null)).ShouldBe("User");
        Transcript.Reader(Conversation("   ")).ShouldBe("User");
    }

    [Fact]
    public void Both_sides_of_a_stretch_are_named()
    {
        var rendered = Transcript.Render(
            Conversation("allan-sanlucar-el-kettani"),
            [
                Message(ChatRole.User, "Where is the lighthouse?"),
                Message(ChatRole.Assistant, "Past the pier."),
            ]);

        rendered.ShouldContain("Allan Sanlucar El Kettani: Where is the lighthouse?");
        rendered.ShouldContain("Elena: Past the pier.");
        rendered.ShouldNotContain("User:");
    }

    [Fact]
    public void A_conversation_with_no_speaker_still_reads_as_two_people()
    {
        var rendered = Transcript.Render(
            Conversation("traveller", speaker: null),
            [Message(ChatRole.Assistant, "Past the pier.")]);

        rendered.ShouldStartWith("Character: ");
    }
}
