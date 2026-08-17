namespace Airp.Domain.Conversations;

/// <summary>Who wrote a message.</summary>
public enum ChatRole
{
    /// <summary>The role could not be determined.</summary>
    Unknown = 0,

    /// <summary>The account holder — what you typed.</summary>
    User,

    /// <summary>The chat's reply.</summary>
    Assistant,

    /// <summary>Scene-setting or instructions rather than dialogue.</summary>
    System,

    /// <summary>
    /// Out-of-band payload the site attaches to a conversation, such as tracker state.
    /// Carried so the transcript stays faithful, but not dialogue.
    /// </summary>
    Data,
}

/// <summary>A single message in a conversation.</summary>
public sealed record ChatMessage
{
    /// <summary>Identifier assigned by the site.</summary>
    public required string Id { get; init; }

    /// <summary>Identifier of the conversation this belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Who wrote it.</summary>
    public required ChatRole Role { get; init; }

    /// <summary>The message body, with <c>\n</c> line endings.</summary>
    public required string Text { get; init; }

    /// <summary>When it was sent, in UTC.</summary>
    public DateTimeOffset? SentAtUtc { get; init; }

    /// <summary>Identifier of the character that wrote it, for assistant messages.</summary>
    public string? CharacterId { get; init; }

    /// <summary>Whether the site flagged the content, and why.</summary>
    public string? FlaggedReason { get; init; }

    /// <summary>Number of whitespace-separated words.</summary>
    public int WordCount
    {
        get
        {
            var count = 0;
            var inWord = false;

            foreach (var c in Text)
            {
                if (char.IsWhiteSpace(c))
                {
                    inWord = false;
                }
                else if (!inWord)
                {
                    inWord = true;
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether this message is dialogue rather than an out-of-band payload.</summary>
    public bool IsDialogue => Role is ChatRole.User or ChatRole.Assistant or ChatRole.System;
}

/// <summary>Everything a new conversation starts from.</summary>
/// <remarks>
/// A record rather than a parameter list, because the list reached seven entries — five of
/// them optional strings — and this project has already paid for positional-argument bugs
/// three times in one day. Names are now structural, not a calling convention.
/// </remarks>
public sealed record NewConversation
{
    /// <summary>What the chat list shows.</summary>
    public required string Name { get; init; }

    /// <summary>The name of whoever replies, or null when the story does not say.</summary>
    public string? Speaker { get; init; }

    /// <summary>A character definition written for this story alone; wins over any name.</summary>
    public string? CharacterDefinition { get; init; }

    /// <summary>The first message, written by the reader.</summary>
    public string? Opening { get; init; }

    /// <summary>A character in the library, by name.</summary>
    public string? CharacterName { get; init; }

    /// <summary>A persona in the library, by name.</summary>
    public string? PersonaName { get; init; }

    /// <summary>A persona written for this story alone; wins over the named one.</summary>
    public string? Persona { get; init; }
}
