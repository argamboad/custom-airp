using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Exercises the store against a real SQLite engine, in memory.
/// </summary>
/// <remarks>
/// The schema is built by running the migration rather than by <c>EnsureCreated</c>, so these
/// tests fail if the generated migration and the model ever drift apart — which is the failure
/// that otherwise surfaces on someone's machine at start-up instead.
/// </remarks>
public sealed class LocalStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AirpDbContext _store;

    public LocalStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _store = new AirpDbContext(
            new DbContextOptionsBuilder<AirpDbContext>().UseSqlite(_connection).Options);

        _store.Database.Migrate();
    }

    public void Dispose()
    {
        _store.Dispose();
        _connection.Dispose();
    }

    private async Task<ConversationRecord> SeedAsync(string id = "c1")
    {
        var conversation = new ConversationRecord
        {
            Id = id,
            Name = "Vardhal",
            Speaker = "Elena",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        _store.Conversations.Add(conversation);
        await _store.SaveChangesAsync();
        return conversation;
    }

    private MessageRecord Message(string id, string conversationId = "c1", long sequence = 1, string? hash = null) => new()
    {
        Id = id,
        ConversationId = conversationId,
        Sequence = sequence,
        Role = ChatRole.User,
        Text = "I wasn't expecting to find anyone here.",
        SentAtUtc = DateTimeOffset.UnixEpoch,
        RequestHash = hash,
    };

    [Fact]
    public async Task The_migration_produces_a_working_schema()
    {
        await SeedAsync();
        _store.Messages.Add(Message("m1"));
        await _store.SaveChangesAsync();

        (await _store.Messages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_a_message_is_refused()
    {
        await SeedAsync();
        var message = Message("m1");
        _store.Messages.Add(message);
        await _store.SaveChangesAsync();

        _store.Messages.Remove(message);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _store.SaveChangesAsync());

        thrown.Message.ShouldContain("append-only");
        thrown.Message.ShouldContain("DeletedAtUtc");
    }

    [Fact]
    public async Task Rewriting_the_text_of_a_message_is_refused()
    {
        // The rule is not "do not lose rows", it is "do not lose what was said". An edit in
        // place loses it just as completely as a delete, and looks far more innocent.
        await SeedAsync();
        var message = Message("m1");
        _store.Messages.Add(message);
        await _store.SaveChangesAsync();

        message.Text = "Something else entirely.";

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _store.SaveChangesAsync());

        thrown.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task Deleting_a_conversation_is_refused_because_it_would_take_the_messages()
    {
        var conversation = await SeedAsync();

        _store.Conversations.Remove(conversation);

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _store.SaveChangesAsync());

        thrown.Message.ShouldContain("DeletedAtUtc");
    }

    [Fact]
    public async Task Hiding_a_message_is_allowed_and_keeps_the_row()
    {
        await SeedAsync();
        var message = Message("m1");
        _store.Messages.Add(message);
        await _store.SaveChangesAsync();

        message.DeletedAtUtc = DateTimeOffset.UnixEpoch.AddDays(1);
        await _store.SaveChangesAsync();

        var stored = await _store.Messages.SingleAsync(m => m.Id == "m1");
        stored.DeletedAtUtc.ShouldNotBeNull();
        stored.Text.ShouldBe("I wasn't expecting to find anyone here.");
    }

    [Fact]
    public async Task The_same_request_hash_cannot_land_twice_in_one_conversation()
    {
        await SeedAsync();
        _store.Messages.Add(Message("m1", sequence: 1, hash: "abc123"));
        await _store.SaveChangesAsync();

        _store.Messages.Add(Message("m2", sequence: 2, hash: "abc123"));

        await Should.ThrowAsync<DbUpdateException>(async () => await _store.SaveChangesAsync());
    }

    [Fact]
    public async Task The_same_request_hash_in_a_different_conversation_is_fine()
    {
        // The hash identifies a request within its conversation. Making it globally unique
        // would mean two chats could never be sent the same sentence, which is nonsense.
        await SeedAsync("c1");
        await SeedAsync("c2");

        _store.Messages.Add(Message("m1", "c1", 1, "abc123"));
        _store.Messages.Add(Message("m2", "c2", 1, "abc123"));

        await _store.SaveChangesAsync();

        (await _store.Messages.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Messages_without_a_hash_do_not_collide_with_each_other()
    {
        // Most turns carry no hash. A unique index that counted nulls as equal would allow
        // exactly one of them per conversation.
        await SeedAsync();

        _store.Messages.Add(Message("m1", sequence: 1));
        _store.Messages.Add(Message("m2", sequence: 2));
        _store.Messages.Add(Message("m3", sequence: 3));

        await _store.SaveChangesAsync();

        (await _store.Messages.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task A_sequence_number_cannot_be_reused_within_a_conversation()
    {
        await SeedAsync();
        _store.Messages.Add(Message("m1", sequence: 7));
        await _store.SaveChangesAsync();

        _store.Messages.Add(Message("m2", sequence: 7));

        await Should.ThrowAsync<DbUpdateException>(async () => await _store.SaveChangesAsync());
    }

    [Fact]
    public async Task A_stored_message_projects_onto_the_domain_model()
    {
        await SeedAsync();
        var message = Message("m1");
        message.Role = ChatRole.Assistant;
        _store.Messages.Add(message);
        await _store.SaveChangesAsync();

        var domain = message.ToDomain();

        domain.Id.ShouldBe("m1");
        domain.ConversationId.ShouldBe("c1");
        domain.Role.ShouldBe(ChatRole.Assistant);
        domain.Text.ShouldBe("I wasn't expecting to find anyone here.");
        domain.SentAtUtc.ShouldBe(DateTimeOffset.UnixEpoch);
    }
}
