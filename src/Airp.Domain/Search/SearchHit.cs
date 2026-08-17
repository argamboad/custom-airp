namespace Airp.Domain.Search;

/// <summary>Which part of a chat produced a search hit.</summary>
[Flags]
public enum SearchScope
{
    /// <summary>No scope.</summary>
    None = 0,

    /// <summary>The chat's name.</summary>
    Names = 1 << 0,

    /// <summary>The text of the messages themselves.</summary>
    Messages = 1 << 1,

    /// <summary>Everything searchable.</summary>
    All = Names | Messages,
}

/// <summary>A single result returned by the global search.</summary>
public sealed record SearchHit
{
    /// <summary>Identifier of the chat the hit belongs to.</summary>
    public required string ChatId { get; init; }

    /// <summary>Display name of the chat, used as the result heading.</summary>
    public required string ChatName { get; init; }

    /// <summary>Which part matched.</summary>
    public required SearchScope Scope { get; init; }

    /// <summary>A short excerpt around the match.</summary>
    public required string Snippet { get; init; }

    /// <summary>Who wrote the matching message, when the hit is in one.</summary>
    public string? Speaker { get; init; }

    /// <summary>When the matching message was sent, when the hit is in one.</summary>
    public DateTimeOffset? SentAtUtc { get; init; }

    /// <summary>Relevance score; higher is better.</summary>
    public int Score { get; init; }

    /// <summary>
    /// Character offsets inside <see cref="Snippet"/> that matched the query, so the UI can
    /// highlight them.
    /// </summary>
    public IReadOnlyList<int> MatchOffsets { get; init; } = [];
}

/// <summary>
/// What a search covered, so a partial answer can say so.
/// </summary>
/// <param name="Hits">The results, best match first.</param>
/// <param name="ChatsSearched">How many chats had a local copy to read.</param>
/// <param name="ChatsSkipped">
/// How many had none. Searching those would mean reading each conversation from the site in
/// full, which takes tens of seconds apiece — so they are reported rather than waited for.
/// </param>
public readonly record struct SearchResults(
    IReadOnlyList<SearchHit> Hits,
    int ChatsSearched,
    int ChatsSkipped)
{
    /// <summary>Whether some chats had no local copy and were not searched.</summary>
    public bool IsPartial => ChatsSkipped > 0;
}
