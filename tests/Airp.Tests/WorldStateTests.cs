using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

public sealed class WorldStateRenderingTests
{
    private static FactRecord Fact(string subject, string text) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = "c1",
        Subject = subject,
        Text = text,
    };

    [Fact]
    public void Nothing_established_renders_as_nothing()
        => FactExtractor.Render([]).ShouldBeNull();

    [Fact]
    public void Facts_are_grouped_by_who_they_are_about()
    {
        // One line per subject rather than a flat list: the model reads a character sheet more
        // reliably than twenty unrelated sentences in the order they happened to be extracted.
        var rendered = FactExtractor.Render(
        [
            Fact("Elena", "Has a scar on her left forearm"),
            Fact("Ferrin", "Owes Elena money"),
            Fact("Elena", "Avoids the north dock"),
        ]);

        rendered.ShouldNotBeNull();
        rendered.ShouldContain("Elena: ");
        rendered.ShouldContain("Ferrin: ");
        rendered.Split('\n').Count(static l => l.StartsWith("Elena:")).ShouldBe(1);
    }
}

public sealed class WorldStateTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private static Action<AirpOptions> SmallBudget => o =>
    {
        o.Model.ContextBudget = 2500;
        o.Model.MaxTokens = 200;
    };

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(SmallBudget),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<string> SeedAsync(int turns)
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
                Text = $"Turn {i}. " + string.Join(' ', Enumerable.Repeat("word", 60)),
                SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
            });
        }

        await store.SaveChangesAsync();
        return id;
    }

    private static string Extraction(object facts, params string[] retired)
        => JsonSerializer.Serialize(new { facts, retired });

    [Fact]
    public async Task Facts_are_extracted_from_the_stretch_that_gets_compressed()
    {
        var id = await SeedAsync(40);
        _model
            .Summarises("Summary of the stretch.")
            .Says(Extraction(new[] { new { subject = "Elena", text = "Has a scar on her forearm" } }))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        var fact = await store.Facts.SingleAsync();

        fact.Subject.ShouldBe("Elena");
        fact.Text.ShouldContain("scar");
        fact.ValidToSequence.ShouldBeNull();
    }

    [Fact]
    public async Task Live_facts_reach_the_prompt_and_sit_with_the_stable_layers()
    {
        var id = await SeedAsync(40);
        _model
            .Summarises("Summary.")
            .Says(Extraction(new[] { new { subject = "Elena", text = "Distrusts the user" } }))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        var prompt = _model.Calls[^1].ToList();
        var world = prompt.FindIndex(m => m.Content.Contains("Distrusts the user"));
        var transcript = prompt.FindIndex(m => m.Content.StartsWith("Turn "));

        world.ShouldBeGreaterThanOrEqualTo(0);
        world.ShouldBeLessThan(transcript);
    }

    [Fact]
    public async Task A_fact_that_stops_being_true_is_retired_not_deleted()
    {
        // Invariant 5. That she once distrusted you is part of the story; a later scene may
        // turn on it. Only the live set is sent.
        var id = await SeedAsync(40);
        _model
            .Summarises("Summary.")
            .Says(Extraction(new[] { new { subject = "Elena", text = "Distrusts the user" } }))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        string factId;

        await using (var store = _factory.CreateDbContext())
        {
            factId = (await store.Facts.SingleAsync()).Id;
        }

        _model
            .Says("Second summary.")
            .Says(Extraction(
                new[] { new { subject = "Elena", text = "Trusts the user" } },
                factId[..8]))
            .Says("Fine again.");

        // Push far enough past the budget that a second stretch is compressed.
        for (var i = 0; i < 12; i++)
        {
            _model.Says("Filler.");
            await Provider().SendAsync(id, $"Long message {i}. " + string.Join(' ', Enumerable.Repeat("word", 60)));
        }

        await using var after = _factory.CreateDbContext();
        var all = await after.Facts.ToListAsync();

        all.Count.ShouldBeGreaterThanOrEqualTo(1);

        // Whatever was retired is still on the record, with the point it stopped being true.
        foreach (var retired in all.Where(static f => f.ValidToSequence is not null))
        {
            retired.Text.ShouldNotBeNullOrWhiteSpace();
            retired.ValidToSequence!.Value.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public async Task Only_live_facts_are_rendered()
    {
        await using var store = _factory.CreateDbContext();

        store.Conversations.Add(new ConversationRecord
        {
            Id = "c1",
            Name = "x",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        });

        store.Facts.AddRange(
            new FactRecord
            {
                Id = "f1",
                ConversationId = "c1",
                Subject = "Elena",
                Text = "Distrusts the user",
                ValidFromSequence = 1,
                ValidToSequence = 20,
            },
            new FactRecord
            {
                Id = "f2",
                ConversationId = "c1",
                Subject = "Elena",
                Text = "Trusts the user",
                ValidFromSequence = 21,
            });

        await store.SaveChangesAsync();

        var live = await FactExtractor.LiveAsync(store, "c1", default);
        var rendered = FactExtractor.Render(live);

        live.Count.ShouldBe(1);
        rendered.ShouldNotBeNull().ShouldContain("Trusts the user");
        rendered.ShouldNotContain("Distrusts the user");
    }

    [Fact]
    public async Task An_extractor_that_replies_with_prose_changes_nothing()
    {
        // Asked for JSON, models sometimes answer in sentences. That is a reason to leave the
        // world state alone, not a reason to fail the reader's turn.
        var id = await SeedAsync(40);
        _model.Summarises("Summary.").Says("Sure, here are the facts: Elena has a scar.").Says("Fine.");

        var added = await Provider().SendAsync(id, "Hello.");

        added.Count.ShouldBe(2);

        await using var store = _factory.CreateDbContext();
        (await store.Facts.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Json_wrapped_in_a_code_fence_is_still_read()
    {
        // Refusing a correct answer over its packaging would be throwing away the work.
        var id = await SeedAsync(40);
        _model
            .Summarises("Summary.")
            .Says("```json\n" + Extraction(new[] { new { subject = "Elena", text = "Has a knife" } }) + "\n```")
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        (await store.Facts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_fact_can_be_stated_by_hand_and_reaches_the_prompt()
    {
        // What a pinned memory is for: something true that the transcript has simply never
        // mentioned, so no extractor could ever find it.
        var id = await SeedAsync(3);
        _model.Says("Fine.");

        await Provider().AddFactAsync(id, "Elena", "Is allergic to shellfish");
        await Provider().SendAsync(id, "Let's have dinner.");

        _model.Calls[^1].Any(m => m.Content.Contains("allergic to shellfish")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_model_cannot_retire_a_fact_a_person_stated()
    {
        // The difference between a fact the reader controls and one they merely suggested.
        var id = await SeedAsync(40);
        var pinned = await Provider().AddFactAsync(id, "Elena", "Is allergic to shellfish");

        _model
            .Summarises("Summary.")
            .Says(Extraction(Array.Empty<object>(), pinned.Id[..8]))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        (await store.Facts.SingleAsync(f => f.Id == pinned.Id)).ValidToSequence.ShouldBeNull();
    }

    [Fact]
    public async Task A_person_can_retire_their_own_fact()
    {
        var id = await SeedAsync(3);
        var fact = await Provider().AddFactAsync(id, "Elena", "Distrusts the user");

        var retired = await Provider().RetireFactAsync(id, fact.Id[..8]);

        retired.ShouldNotBeNull();

        await using var store = _factory.CreateDbContext();
        var live = await FactExtractor.LiveAsync(store, id, default);
        live.ShouldBeEmpty();

        // Retired, not gone: that the story once held it is part of the story.
        (await store.Facts.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_ambiguous_id_retires_nothing()
    {
        // Rather than whichever one the prefix happened to reach first. Identifiers are shown
        // truncated, so a prefix matching two is a thing a reader can actually type.
        var id = await SeedAsync(3);

        await using (var store = _factory.CreateDbContext())
        {
            store.Facts.AddRange(
                new FactRecord { Id = "abc11111", ConversationId = id, Subject = "Elena", Text = "One" },
                new FactRecord { Id = "abc22222", ConversationId = id, Subject = "Elena", Text = "Two" });

            await store.SaveChangesAsync();
        }

        (await Provider().RetireFactAsync(id, "abc")).ShouldBeNull();

        await using var after = _factory.CreateDbContext();
        (await FactExtractor.LiveAsync(after, id, default)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_id_matching_nothing_retires_nothing()
        => (await Provider().RetireFactAsync(await SeedAsync(3), "nothinghere")).ShouldBeNull();

    [Fact]
    public async Task Facts_are_derived_and_deleting_them_loses_nothing()
    {
        var id = await SeedAsync(40);
        _model
            .Summarises("Summary.")
            .Says(Extraction(new[] { new { subject = "Elena", text = "Has a knife" } }))
            .Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        await using (var store = _factory.CreateDbContext())
        {
            store.Facts.RemoveRange(store.Facts);
            await store.SaveChangesAsync();
        }

        (await Provider().GetMessagesAsync(id)).Count.ShouldBe(42);
    }
}
