namespace Airp.Domain.Conversations;

/// <summary>
/// One of the account's conversations, as it appears in the list.
/// </summary>
/// <remarks>
/// <para>
/// A read model, produced by a chat provider and safe to cache: nothing here holds a
/// reference to a live browser page.
/// </para>
/// <para>
/// It carries only what a chat actually has. An earlier version of this type was a
/// <c>Chat</c> with a persona, a prompt preview, a lifecycle status and a favourite
/// flag — fields a conversation does not own, which is what let a chat be opened on a screen
/// built for something else.
/// </para>
/// </remarks>
public sealed record Chat
{
    /// <summary>Identifier the site uses to address the conversation.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// What to call it: the name you gave it, or the scenario's own.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The chat or scenario the conversation is with, when the site says.</summary>
    public string? Speaker { get; init; }

    /// <summary>The most recent message, used for the list preview.</summary>
    public string? LatestMessage { get; init; }

    /// <summary>When the conversation was last active, in UTC.</summary>
    public DateTimeOffset? LastMessageAtUtc { get; init; }

    /// <summary>Whether the site marks the conversation as having unread messages.</summary>
    public bool IsUnread { get; init; }

    /// <summary>Absolute or site-relative URL of the conversation.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Raw provider payload retained for diagnostics and for fields this model does not yet
    /// understand. Keys are provider-defined.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Text the list filter matches against.</summary>
    public string SearchableText =>
        string.Join(
            '\n',
            new[] { Name, Speaker, LatestMessage }.Where(static s => !string.IsNullOrWhiteSpace(s)));
}
