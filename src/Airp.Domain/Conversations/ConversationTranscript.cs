namespace Airp.Domain.Conversations;

/// <summary>
/// A conversation packaged for export.
/// </summary>
/// <remarks>
/// Deliberately a distinct type rather than one concatenated string. Flattening the turns
/// into a single body loses the role, the timing and the boundaries between them — exactly
/// the structure that makes a transcript readable and machine-processable afterwards.
/// </remarks>
public sealed record ConversationTranscript
{
    /// <summary>Identifier of the conversation.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Display title, as the chat list shows it.</summary>
    public required string Title { get; init; }

    /// <summary>Name of the chat replying, when known.</summary>
    public string? Speaker { get; init; }

    /// <summary>The messages, oldest first.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>When the export was taken, in UTC.</summary>
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>How many turns the account holder wrote.</summary>
    public int UserMessageCount => Messages.Count(static m => m.Role == ChatRole.User);

    /// <summary>How many turns the chat wrote.</summary>
    public int ReplyCount => Messages.Count(static m => m.Role == ChatRole.Assistant);

    /// <summary>Timestamp of the earliest message, when any carry one.</summary>
    public DateTimeOffset? StartedAtUtc => Messages.Min(static m => m.SentAtUtc);

    /// <summary>Timestamp of the latest message, when any carry one.</summary>
    public DateTimeOffset? EndedAtUtc => Messages.Max(static m => m.SentAtUtc);

    /// <summary>Resolves the display name for a message's author.</summary>
    /// <param name="message">The message.</param>
    /// <returns>A human-readable speaker label.</returns>
    public string SpeakerFor(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Role switch
        {
            ChatRole.User => "You",
            ChatRole.Assistant => Speaker ?? "Reply",
            ChatRole.System => "Scene",
            _ => message.Role.ToString(),
        };
    }
}
