using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>A model that answers from a script, and remembers what it was asked.</summary>
internal sealed class ScriptedModel : ILanguageModelClient
{
    private readonly Queue<Func<ModelReply>> _answers = new();

    public List<IReadOnlyList<ModelMessage>> Calls { get; } = [];

    public double? LastTemperature { get; private set; }

    public int? LastMaxTokens { get; private set; }

    public ScriptedModel Says(string text)
    {
        _answers.Enqueue(() => new ModelReply
        {
            Text = text,
            Model = "test-model",
            Provider = "test-host",
            GenerationId = "gen-" + _answers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PromptTokens = 10,
            CompletionTokens = 5,
            CachedTokens = 4,
            Cost = 0.0002,
        });

        return this;
    }

    /// <summary>Answers without the API saying what it charged, as some hosts do.</summary>
    public ScriptedModel SaysUnpriced(string text)
    {
        _answers.Enqueue(() => new ModelReply
        {
            Text = text,
            Model = "test-model",
            PromptTokens = 10,
            CompletionTokens = 5,
        });

        return this;
    }

    public ScriptedModel Fails(string message = "the provider is down")
    {
        _answers.Enqueue(() => throw new ModelUnavailableException(message));
        return this;
    }

    /// <summary>
    /// Answers the way a summariser does: prose, long enough to be an account of something.
    /// </summary>
    /// <remarks>
    /// A summary is refused now if it is too short to be one, because a real host returned
    /// <c>##</c> for ninety-nine messages and it was stored and believed. Scripting
    /// <c>"Summary."</c> for a stretch of forty turns is that same shape, so the tests would be
    /// proving the memory works on data the memory is now right to reject. The gist is kept at
    /// the front, where assertions look for it.
    /// </remarks>
    /// <param name="gist">The words a test wants to find in the summary.</param>
    public ScriptedModel Summarises(string gist)
        => Says(gist + " " + string.Join(
            ' ',
            Enumerable.Repeat("They spoke at length and something was settled between them.", 4)));

    /// <summary>Answers with a few words and reports that it was cut off.</summary>
    /// <remarks>
    /// What a host does when it stops early for its own reasons: <c>finish_reason: length</c>
    /// far below the ceiling that was asked for. The text reads like the start of an answer,
    /// which is what made it survivable — it looked like prose and was stored.
    /// </remarks>
    public ScriptedModel Truncated(string text)
    {
        _answers.Enqueue(() => new ModelReply
        {
            Text = text,
            Model = "test-model",
            Provider = "test-host",
            FinishReason = "length",
            PromptTokens = 4000,
            CompletionTokens = 20,
        });

        return this;
    }

    /// <summary>Fails the way a host does when it answers 200 and sends nothing.</summary>
    public ScriptedModel Empty()
    {
        _answers.Enqueue(() => throw new ModelUnavailableException(
            "The API returned a response with no message content.",
            200));

        return this;
    }

    /// <summary>Fails in a way no second attempt could fix.</summary>
    public ScriptedModel Rejected()
    {
        _answers.Enqueue(() => throw new ModelUnavailableException("the key was rejected", 401));
        return this;
    }

    public Task<ModelReply> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        string? model = null,
        double? temperature = null,
        int? maxTokens = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(messages);
        LastTemperature = temperature;
        LastMaxTokens = maxTokens;

        var next = _answers.Count > 0 ? _answers.Dequeue() : () => new ModelReply { Text = "…" };
        return Task.FromResult(next());
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(["test-model"]);
}

/// <summary>Hands out contexts over one in-memory connection that stays open.</summary>
internal sealed class SharedContextFactory : IDbContextFactory<AirpDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;

    public SharedContextFactory()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var seed = CreateDbContext();
        seed.Database.Migrate();
    }

    public AirpDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<AirpDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}

public sealed class LocalProviderTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<string> StartAsync(string? definition = null, string? opening = null)
        => (await Provider().CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Vardhal", Speaker = "Elena", CharacterDefinition = definition, Opening = opening })).Id;

    [Fact]
    public async Task A_new_conversation_shows_up_in_the_list()
    {
        var id = await StartAsync(opening: "Estoy en la playa.");
        var provider = Provider();

        var chats = await provider.ListAsync();

        chats.Count.ShouldBe(1);
        chats[0].Id.ShouldBe(id);
        chats[0].Speaker.ShouldBe("Elena");
        chats[0].LatestMessage.ShouldBe("Estoy en la playa.");
    }

    [Fact]
    public async Task Sending_stores_both_turns_in_order()
    {
        var id = await StartAsync();
        _model.Says("I could say the same about you.");

        var added = await Provider().SendAsync(id, "No esperaba encontrarte.");

        added.Count.ShouldBe(2);
        added[0].Role.ShouldBe(ChatRole.User);
        added[1].Role.ShouldBe(ChatRole.Assistant);
        added[1].Text.ShouldBe("I could say the same about you.");

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Select(m => m.Role).ShouldBe([ChatRole.User, ChatRole.Assistant]);
    }

    [Fact]
    public async Task The_message_survives_a_model_that_fails()
    {
        // Invariant 2. If the send were only stored after a successful reply, a provider
        // outage would silently swallow what the reader typed.
        var id = await StartAsync();
        _model.Fails();

        var thrown = await Should.ThrowAsync<ReplyMissingException>(
            async () => await Provider().SendAsync(id, "No esperaba encontrarte."));

        thrown.Partial.Count.ShouldBe(1);
        thrown.Partial[0].Text.ShouldBe("No esperaba encontrarte.");

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Count.ShouldBe(1);
        transcript[0].Role.ShouldBe(ChatRole.User);
    }

    [Fact]
    public async Task Retrying_a_send_the_model_failed_does_not_store_it_twice()
    {
        // The case idempotency exists for. The first attempt stored the turn and then died at
        // the model; the reader presses send again on the same words. One turn, one retry.
        var id = await StartAsync();
        _model.Fails().Says("I could say the same about you.");

        await Should.ThrowAsync<ReplyMissingException>(
            async () => await Provider().SendAsync(id, "Hello."));

        var added = await Provider().SendAsync(id, "Hello.");

        added.Count.ShouldBe(2);

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Count(m => m.Role == ChatRole.User).ShouldBe(1);
        transcript.Count(m => m.Role == ChatRole.Assistant).ShouldBe(1);
        _model.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_same_words_after_a_reply_landed_are_a_genuinely_new_send()
    {
        // The opposite case, and the reason the hash is anchored on the last reply rather than
        // on the text alone: saying "Hello." twice in one conversation is ordinary, not a retry.
        var id = await StartAsync();
        _model.Says("Una.").Says("Two.");

        await Provider().SendAsync(id, "Hello.");
        await Provider().SendAsync(id, "Hello.");

        var transcript = await Provider().GetMessagesAsync(id);
        transcript.Count(m => m.Role == ChatRole.User).ShouldBe(2);
        transcript.Count(m => m.Role == ChatRole.Assistant).ShouldBe(2);
        _model.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_send_already_answered_is_returned_rather_than_replayed()
    {
        // Belt and braces for a caller that retries after a reply it never saw: the anchor has
        // moved on, so this only triggers on a true replay of the identical request.
        var id = await StartAsync();
        _model.Says("Una.");
        await Provider().SendAsync(id, "Hello.");

        await using var store = _factory.CreateDbContext();
        var stored = await store.Messages.FirstAsync(m => m.RequestHash != null);
        stored.RequestHash.ShouldNotBeNull();

        (await Provider().GetMessagesAsync(id)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Deleting_from_a_message_hides_it_and_everything_after()
    {
        var id = await StartAsync();
        _model.Says("One.").Says("Two.");
        await Provider().SendAsync(id, "Primera.");
        await Provider().SendAsync(id, "Segunda.");

        var transcript = await Provider().GetMessagesAsync(id);
        var surviving = await Provider().DeleteFromAsync(id, transcript[2].Id);

        surviving.Count.ShouldBe(2);

        // Hidden from the terminal, still on disk. That is the whole point of the tombstone.
        await using var store = _factory.CreateDbContext();
        (await store.Messages.CountAsync()).ShouldBe(4);
    }

    [Fact]
    public async Task Regenerating_hides_the_old_reply_and_writes_a_new_one()
    {
        var id = await StartAsync();
        _model.Says("First version.").Says("Second version.");
        await Provider().SendAsync(id, "Hello.");

        var transcript = await Provider().RegenerateAsync(id, RegenerateReason.TooShort);

        transcript.Count.ShouldBe(2);
        transcript[1].Text.ShouldBe("Second version.");

        await using var store = _factory.CreateDbContext();
        (await store.Messages.CountAsync(m => m.DeletedAtUtc != null)).ShouldBe(1);
    }

    [Fact]
    public async Task The_regenerate_reason_reaches_the_model_as_an_instruction()
    {
        // The reason came from ourdream's list, but on the site it was a posted value. Here it
        // is the only thing making the second attempt differ, so it has to be in the prompt.
        var id = await StartAsync();
        _model.Says("Primera.").Says("Segunda.");
        await Provider().SendAsync(id, "Hello.");

        await Provider().RegenerateAsync(id, RegenerateReason.ActingForUser);

        var last = _model.Calls[^1];
        last[^1].Role.ShouldBe(ModelRole.System);

        // The reason's own substance, not a phrase from its wording: the sentences get
        // rewritten, and a test that pinned one of them would fail for saying it better.
        last[^1].Content.ShouldContain("Write only your own character");
    }

    [Fact]
    public async Task The_character_definition_leads_the_prompt_and_the_directive_ends_it()
    {
        // Order is the cache contract: everything before the first volatile section is reused,
        // everything after it is reprocessed.
        var id = await StartAsync(definition: "You are Elena.");
        _model.Says("Primera.").Says("Segunda.");
        await Provider().SendAsync(id, "Hello.");
        await Provider().RegenerateAsync(id, RegenerateReason.Looping);

        var last = _model.Calls[^1];

        last[0].Role.ShouldBe(ModelRole.System);
        last[0].Content.ShouldBe("You are Elena.");
        last[^1].Content.ShouldContain("repeated itself");
    }

    [Fact]
    public async Task The_dials_are_stored_and_shape_the_call()
    {
        var id = await StartAsync();
        _model.Says("Fine.");

        var saved = await Provider().UpdateSettingsAsync(id, new ChatSettings { Creativity = 4, ResponseLength = 0 });

        saved.Creativity.ShouldBe(4);
        saved.ResponseLength.ShouldBe(0);

        await Provider().SendAsync(id, "Hello.");

        _model.LastTemperature.ShouldBe(1.4);
        _model.LastMaxTokens.ShouldBe(200);
    }

    [Fact]
    public async Task An_unset_dial_is_left_alone_rather_than_cleared()
    {
        var id = await StartAsync();
        await Provider().UpdateSettingsAsync(id, new ChatSettings { Lust = 3 });
        await Provider().UpdateSettingsAsync(id, new ChatSettings { Creativity = 1 });

        var settings = await Provider().GetSettingsAsync(id);

        settings.Lust.ShouldBe(3);
        settings.Creativity.ShouldBe(1);
    }

    [Fact]
    public async Task The_lust_dial_reaches_the_prompt_in_the_terminals_own_wording()
    {
        var id = await StartAsync();
        _model.Says("Fine.");
        await Provider().UpdateSettingsAsync(id, new ChatSettings { Lust = 3 });

        await Provider().SendAsync(id, "Hello.");

        var directives = _model.Calls[^1].First(m => m.Content.Contains("Lust Level"));
        directives.Content.ShouldContain("Explicit");
    }

    [Fact]
    public async Task A_deleted_conversation_disappears_from_the_list_but_not_from_disk()
    {
        var id = await StartAsync();
        _model.Says("Fine.");
        await Provider().SendAsync(id, "Hello.");

        await Provider().DeleteConversationAsync(id);

        (await Provider().ListAsync()).ShouldBeEmpty();

        await using var store = _factory.CreateDbContext();
        (await store.Conversations.CountAsync()).ShouldBe(1);
        (await store.Messages.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task A_regenerate_the_model_never_answers_gives_the_old_reply_back()
    {
        var id = await StartAsync();
        _model.Says("The first one.");
        await Provider().SendAsync(id, "Hello.");

        _model.Fails();

        await Should.ThrowAsync<AirpException>(
            async () => await Provider().RegenerateAsync(id, RegenerateReason.Looping));

        // The reply was hidden before the call; a call that brought nothing back must not
        // leave the conversation shorter than it found it.
        var transcript = await Provider().GetMessagesAsync(id);
        transcript[^1].Text.ShouldBe("The first one.");

        // And a second attempt still has something to write again.
        _model.Says("The second one.");
        await Provider().RegenerateAsync(id, RegenerateReason.Looping);

        (await Provider().GetMessagesAsync(id))[^1].Text.ShouldBe("The second one.");
    }

    [Fact]
    public async Task Purging_erases_what_was_deleted_and_leaves_the_rest()
    {
        var doomed = await StartAsync();
        var kept = await StartAsync();
        _model.Says("Fine.").Says("Also fine.");
        await Provider().SendAsync(doomed, "Hello.");
        await Provider().SendAsync(kept, "Hello.");
        await Provider().DeleteConversationAsync(doomed);

        var waiting = await Provider().PurgeableAsync();
        waiting.ShouldHaveSingleItem().Messages.ShouldBe(2);

        var report = await Provider().PurgeDeletedAsync();

        report.Conversations.ShouldBe(1);
        report.Messages.ShouldBe(2);

        await using var store = _factory.CreateDbContext();
        (await store.Conversations.CountAsync()).ShouldBe(1);
        (await store.Messages.CountAsync()).ShouldBe(2);
        (await store.Conversations.SingleAsync()).Id.ShouldBe(kept);
    }

    [Fact]
    public async Task Purging_with_nothing_deleted_touches_nothing()
    {
        var id = await StartAsync();
        _model.Says("Fine.");
        await Provider().SendAsync(id, "Hello.");

        var report = await Provider().PurgeDeletedAsync();

        report.Empty.ShouldBeTrue();
        (await Provider().ListAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_append_only_guard_still_refuses_an_ordinary_delete()
    {
        var id = await StartAsync();
        _model.Says("Fine.");
        await Provider().SendAsync(id, "Hello.");

        // Purging lifts the guard for itself alone; every other caller still meets the wall.
        await using var store = _factory.CreateDbContext();
        store.Messages.RemoveRange(await store.Messages.ToListAsync());

        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveChangesAsync());
    }

    [Fact]
    public async Task Renaming_changes_the_list_entry()
    {
        var id = await StartAsync();

        await Provider().RenameConversationAsync(id, "  Puerto de Vardhal  ");

        (await Provider().GetAsync(id))!.Name.ShouldBe("Puerto de Vardhal");
    }

    [Fact]
    public async Task Sending_to_an_unknown_conversation_says_so_usefully()
    {
        var thrown = await Should.ThrowAsync<ContractException>(
            async () => await Provider().SendAsync("nope", "Hello."));

        thrown.RecoveryHint.ShouldContain("airp new");
    }

    [Fact]
    public async Task Regenerating_with_nothing_to_regenerate_is_refused()
    {
        var id = await StartAsync();

        await Should.ThrowAsync<ContractException>(
            async () => await Provider().RegenerateAsync(id, RegenerateReason.None));
    }

    [Fact]
    public async Task Continuing_asks_the_model_without_adding_a_user_turn()
    {
        var id = await StartAsync();
        _model.Says("One.").Says("Sigo.");
        await Provider().SendAsync(id, "Hello.");

        var transcript = await Provider().ContinueAsync(id);

        transcript.Count.ShouldBe(3);
        transcript.Count(m => m.Role == ChatRole.User).ShouldBe(1);
        transcript[^1].Text.ShouldBe("Sigo.");
    }
}
