using System.Text.Json;
using System.Text.Json.Serialization;
using Airp.Domain.Conversations;

namespace Airp.Infrastructure.Providers;

/// <summary>One turn as <c>airp export</c> writes it.</summary>
public sealed record ExportedMessage
{
    /// <summary>Position in the transcript, starting at one.</summary>
    public int Index { get; init; }

    /// <summary>Who wrote it, as the enum's name.</summary>
    public string? Role { get; init; }

    /// <summary>Display name of the author.</summary>
    public string? Speaker { get; init; }

    /// <summary>When it was sent.</summary>
    public DateTimeOffset? SentAtUtc { get; init; }

    /// <summary>The body.</summary>
    public string? Text { get; init; }
}

/// <summary>A conversation as <c>airp export</c> writes it.</summary>
public sealed record ExportedTranscript
{
    /// <summary>The site's identifier for the conversation.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Display title.</summary>
    public string? Title { get; init; }

    /// <summary>Name of the character replying.</summary>
    public string? Speaker { get; init; }

    /// <summary>When the earliest message was sent.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>The turns, oldest first.</summary>
    public IReadOnlyList<ExportedMessage> Messages { get; init; } = [];
}

/// <summary>Reads the JSON transcripts that <c>airp export</c> produces.</summary>
/// <remarks>
/// Only the JSON format is read back. Markdown and plain text are for people; they drop the
/// role boundaries that make a transcript replayable, and reconstructing those by guessing at
/// headings would import a conversation subtly wrong rather than not at all.
/// </remarks>
public static class TranscriptFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads one transcript file.</summary>
    /// <param name="path">Path to the JSON file.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The transcript, or <see langword="null"/> when the file is not one.</returns>
    public static async Task<ExportedTranscript?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        // ReadAllText strips the byte-order mark the exporter writes; handing that mark to the
        // JSON reader would fail on the first character of an otherwise valid file.
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        try
        {
            var transcript = JsonSerializer.Deserialize<ExportedTranscript>(content, Options);

            // The export directory also holds prompt captures, which are JSON but not
            // transcripts. Recognised by shape rather than by file name.
            return transcript is { Messages.Count: > 0 } ? transcript : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Maps an exported role name onto the domain enum.</summary>
    /// <remarks>
    /// An unrecognised role becomes <see cref="ChatRole.Unknown"/> rather than being guessed at
    /// or dropped. The turn still happened, and a transcript missing one is worse than a
    /// transcript with one whose author is uncertain.
    /// </remarks>
    /// <param name="role">The exported name.</param>
    /// <returns>The role.</returns>
    public static ChatRole ParseRole(string? role)
        => Enum.TryParse<ChatRole>(role, ignoreCase: true, out var parsed) ? parsed : ChatRole.Unknown;
}

/// <summary>What one reply was built from, and what it cost.</summary>
/// <param name="Sequence">Position of the reply in the transcript.</param>
/// <param name="SentAtUtc">When it arrived.</param>
/// <param name="Hidden">Whether it has since been hidden, by a reroll or a delete.</param>
/// <param name="Model">The model that wrote it.</param>
/// <param name="Provider">The host that served it.</param>
/// <param name="EstimatedPromptTokens">What the context builder predicted the prompt would cost.</param>
/// <param name="PromptTokens">What the API reported it actually cost.</param>
/// <param name="CompletionTokens">Tokens generated.</param>
/// <param name="Context">The layer-by-layer accounting of the prompt.</param>
public readonly record struct TurnAudit(
    long Sequence,
    DateTimeOffset SentAtUtc,
    bool Hidden,
    string? Model,
    string? Provider,
    int? EstimatedPromptTokens,
    int? PromptTokens,
    int? CompletionTokens,
    string? Context);

/// <summary>What an import did.</summary>
/// <param name="Imported">Conversations written.</param>
/// <param name="Skipped">Conversations already present, left alone.</param>
/// <param name="Messages">Messages written.</param>
/// <param name="Ignored">Files that were not transcripts.</param>
public readonly record struct ImportResult(int Imported, int Skipped, int Messages, int Ignored);
