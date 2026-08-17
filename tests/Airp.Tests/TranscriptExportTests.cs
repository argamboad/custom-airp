using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain.Conversations;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Transcript export, which has to keep every turn a separate entry. Flattening the
/// conversation into one body loses the role, the timing and the boundaries — the structure
/// that makes an export worth having.
/// </summary>
public class TranscriptExportTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ourdream-tests", Guid.NewGuid().ToString("n"));

    private readonly ExportService _service;

    public TranscriptExportTests()
        => _service = new ExportService(
            TestOptions.Default(o => o.ExportDirectory = _directory),
            NullLogger<ExportService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static ConversationTranscript Sample => new()
    {
        ConversationId = "chat-1",
        Title = "North Dock",
        Speaker = "Blake",
        Messages =
        [
            new()
            {
                Id = "m1", ConversationId = "chat-1", Role = ChatRole.User, Text = "what I asked",
                SentAtUtc = new DateTimeOffset(2026, 8, 4, 21, 0, 0, TimeSpan.Zero),
            },
            new()
            {
                Id = "m2", ConversationId = "chat-1", Role = ChatRole.Assistant, Text = "the reply",
                SentAtUtc = new DateTimeOffset(2026, 8, 4, 21, 1, 0, TimeSpan.Zero),
                FlaggedReason = "policy",
            },
        ],
    };

    [Fact]
    public void Json_EmitsOneEntryPerMessage()
    {
        var json = _service.Render(Sample, ExportFormat.Json);

        using var document = JsonDocument.Parse(json);
        var messages = document.RootElement.GetProperty("Messages");

        messages.GetArrayLength().ShouldBe(2, "each turn must be its own entry, not one blob");
        messages[0].GetProperty("Index").GetInt32().ShouldBe(1);
        messages[0].GetProperty("Text").GetString().ShouldBe("what I asked");
    }

    [Fact]
    public void Json_ResolvesTheSpeakerOnEveryEntry()
    {
        using var document = JsonDocument.Parse(_service.Render(Sample, ExportFormat.Json));
        var messages = document.RootElement.GetProperty("Messages");

        messages[0].GetProperty("Speaker").GetString().ShouldBe("You");
        messages[1].GetProperty("Speaker").GetString().ShouldBe("Blake");
        messages[0].GetProperty("Role").GetString().ShouldBe("User");
    }

    [Fact]
    public void Json_CarriesPerMessageMetadata()
    {
        using var document = JsonDocument.Parse(_service.Render(Sample, ExportFormat.Json));
        var second = document.RootElement.GetProperty("Messages")[1];

        second.GetProperty("WordCount").GetInt32().ShouldBe(2);
        second.GetProperty("FlaggedReason").GetString().ShouldBe("policy");
        second.GetProperty("SentAtUtc").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Json_SummarisesTheConversation()
    {
        using var document = JsonDocument.Parse(_service.Render(Sample, ExportFormat.Json));
        var root = document.RootElement;

        root.GetProperty("Title").GetString().ShouldBe("North Dock");
        root.GetProperty("MessageCount").GetInt32().ShouldBe(2);
        root.GetProperty("UserMessageCount").GetInt32().ShouldBe(1);
        root.GetProperty("ReplyCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Markdown_GivesEachMessageItsOwnNumberedHeading()
    {
        var markdown = _service.Render(Sample, ExportFormat.Markdown);

        markdown.ShouldContain("# North Dock");
        markdown.ShouldContain("## 1. You");
        markdown.ShouldContain("## 2. Blake");
        markdown.ShouldContain("what I asked");
        markdown.ShouldContain("the reply");
    }

    [Fact]
    public void Markdown_CarriesFrontMatterAndFlags()
    {
        var markdown = _service.Render(Sample, ExportFormat.Markdown);

        markdown.ShouldStartWith("---");
        markdown.ShouldContain("speaker: Blake");
        markdown.ShouldContain("fromYou: 1");
        markdown.ShouldContain("replies: 1");
        markdown.ShouldContain("Flagged: policy");
    }

    [Fact]
    public void PlainText_SeparatesEachTurn()
    {
        var text = _service.Render(Sample, ExportFormat.PlainText);

        text.ShouldContain("[001] You");
        text.ShouldContain("[002] Blake");
        text.ShouldContain("what I asked");
        text.ShouldContain("the reply");
    }

    [Fact]
    public async Task ExportAsync_NamesTheFileAfterTheConversation()
    {
        var path = await _service.ExportAsync(Sample, ExportFormat.Markdown);

        Path.GetFileName(path).ShouldStartWith("transcript-north-dock-");
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void SpeakerFor_FallsBackWhenTheChatIsUnknown()
    {
        var anonymous = Sample with { Speaker = null };

        anonymous.SpeakerFor(anonymous.Messages[1]).ShouldBe("Reply");
    }

    [Fact]
    public void Transcript_ReportsItsSpan()
    {
        Sample.StartedAtUtc!.Value.Minute.ShouldBe(0);
        Sample.EndedAtUtc!.Value.Minute.ShouldBe(1);
    }

    [Fact]
    public void EmptyTranscript_StillRendersInEveryFormat()
    {
        var empty = Sample with { Messages = [] };

        Should.NotThrow(() => _service.Render(empty, ExportFormat.Json));
        Should.NotThrow(() => _service.Render(empty, ExportFormat.Markdown));
        Should.NotThrow(() => _service.Render(empty, ExportFormat.PlainText));
    }
}
