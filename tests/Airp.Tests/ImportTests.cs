using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "airp-import-tests", Guid.NewGuid().ToString("N"));

    public ImportTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    /// <summary>Writes a transcript in the shape the exporter produces, byte-order mark and all.</summary>
    private string WriteTranscript(string id, string title, params (string Role, string Text)[] turns)
    {
        var payload = new
        {
            ConversationId = id,
            Title = title,
            Speaker = "Blake",
            ExportedAtUtc = DateTimeOffset.UnixEpoch,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            MessageCount = turns.Length,
            Messages = turns.Select((t, i) => new
            {
                Index = i + 1,
                t.Role,
                Speaker = t.Role == "User" ? "You" : "Blake",
                SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
                WordCount = t.Text.Split(' ').Length,
                t.Text,
            }),
        };

        var path = Path.Combine(_directory, $"transcript-{title}.json");

        // The exporter writes UTF-8 with a BOM. Reading it back has to cope with that.
        File.WriteAllText(path, JsonSerializer.Serialize(payload), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    [Fact]
    public async Task A_transcript_becomes_a_conversation()
    {
        WriteTranscript("conv-1", "Harbor", ("Assistant", "He leans back."), ("User", "So what happened?"));

        var result = await Provider().ImportAsync(_directory);

        result.Imported.ShouldBe(1);
        result.Messages.ShouldBe(2);

        var chats = await Provider().ListAsync();
        chats.Count.ShouldBe(1);
        chats[0].Id.ShouldBe("conv-1");
        chats[0].Name.ShouldBe("Harbor");
        chats[0].Speaker.ShouldBe("Blake");
    }

    [Fact]
    public async Task Roles_and_order_survive_the_trip()
    {
        WriteTranscript(
            "conv-1",
            "Harbor",
            ("Assistant", "One."),
            ("User", "Two."),
            ("Assistant", "Tres."));

        await Provider().ImportAsync(_directory);

        var transcript = await Provider().GetMessagesAsync("conv-1");

        transcript.Select(m => m.Role)
            .ShouldBe([ChatRole.Assistant, ChatRole.User, ChatRole.Assistant]);
        transcript.Select(m => m.Text).ShouldBe(["One.", "Two.", "Tres."]);
    }

    [Fact]
    public async Task Importing_twice_does_not_duplicate()
    {
        // The identifier comes from the site, so a second run recognises what it already has.
        // That matters because a half-finished import has no undo — append-only sees to that.
        WriteTranscript("conv-1", "Harbor", ("User", "Hello."));

        await Provider().ImportAsync(_directory);
        var second = await Provider().ImportAsync(_directory);

        second.Imported.ShouldBe(0);
        second.Skipped.ShouldBe(1);
        (await Provider().ListAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_json_file_that_is_not_a_transcript_is_ignored_not_fatal()
    {
        // The export directory also holds prompt captures, which are JSON and are not this.
        WriteTranscript("conv-1", "Harbor", ("User", "Hello."));
        File.WriteAllText(
            Path.Combine(_directory, "prompt-abc.json"),
            """{ "CharacterId": "abc", "Text": "…", "LineCount": 3 }""");

        var result = await Provider().ImportAsync(_directory);

        result.Imported.ShouldBe(1);
        result.Ignored.ShouldBe(1);
    }

    [Fact]
    public async Task A_file_that_is_not_json_at_all_is_ignored()
    {
        WriteTranscript("conv-1", "Harbor", ("User", "Hello."));
        File.WriteAllText(Path.Combine(_directory, "notes.json"), "this is not json");

        var result = await Provider().ImportAsync(_directory);

        result.Imported.ShouldBe(1);
        result.Ignored.ShouldBe(1);
    }

    [Fact]
    public async Task A_single_file_can_be_imported_on_its_own()
    {
        var path = WriteTranscript("conv-1", "Harbor", ("User", "Hello."));

        var result = await Provider().ImportAsync(path);

        result.Imported.ShouldBe(1);
    }

    [Fact]
    public async Task A_character_definition_can_be_attached_on_the_way_in()
    {
        // The export never carried one, so this is the only chance to supply it without
        // editing the row by hand afterwards.
        WriteTranscript("conv-1", "Harbor", ("User", "Hello."));

        await Provider().ImportAsync(_directory, "You are Blake.");

        await using var store = _factory.CreateDbContext();
        var conversation = await store.Conversations.SingleAsync();
        conversation.CharacterDefinition.ShouldBe("You are Blake.");
    }

    [Fact]
    public async Task An_imported_conversation_can_be_continued()
    {
        WriteTranscript("conv-1", "Harbor", ("Assistant", "He leans back."), ("User", "So?"));
        await Provider().ImportAsync(_directory, "You are Blake.");
        _model.Says("He shrugs.");

        var added = await Provider().SendAsync("conv-1", "Tell me.");

        added.Count.ShouldBe(2);

        // The whole point of importing: the old history is in front of the model.
        var sent = _model.Calls[^1];
        sent[0].Content.ShouldBe("You are Blake.");
        sent.Any(m => m.Content == "He leans back.").ShouldBeTrue();
        sent[^1].Content.ShouldBe("Tell me.");
    }

    [Fact]
    public async Task An_out_of_order_index_does_not_take_the_import_down()
    {
        // Sequence is renumbered rather than trusted: a duplicate Index in the file would
        // otherwise collide with the unique index and lose every conversation in the run.
        var payload = new
        {
            ConversationId = "conv-1",
            Title = "Harbor",
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            Messages = new[]
            {
                new { Index = 5, Role = "User", Text = "Segunda." },
                new { Index = 5, Role = "Assistant", Text = "Primera." },
            },
        };

        File.WriteAllText(Path.Combine(_directory, "odd.json"), JsonSerializer.Serialize(payload));

        var result = await Provider().ImportAsync(_directory);

        result.Imported.ShouldBe(1);
        result.Messages.ShouldBe(2);
    }

    [Fact]
    public async Task Importing_from_nowhere_says_where_to_get_transcripts()
    {
        var thrown = await Should.ThrowAsync<ContractException>(
            async () => await Provider().ImportAsync(Path.Combine(_directory, "empty-subdir")));

        thrown.RecoveryHint.ShouldContain("airp export");
    }
}
