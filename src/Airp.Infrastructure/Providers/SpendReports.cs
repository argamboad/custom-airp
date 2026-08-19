using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>What one kind of work cost over a stretch.</summary>
/// <param name="Kind">Replies, questions, compression or extraction.</param>
/// <param name="Calls">How many calls were made.</param>
/// <param name="Cost">What they came to, counting only the calls the API priced.</param>
public readonly record struct SpendByKind(SpendKind Kind, int Calls, decimal Cost);

/// <summary>What one conversation has cost.</summary>
/// <param name="ConversationId">Its identifier.</param>
/// <param name="Name">Its name at the time the report was run.</param>
/// <param name="Speaker">Who replies in it.</param>
/// <param name="Calls">Billed calls of every kind.</param>
/// <param name="Cost">What they came to.</param>
/// <param name="DiscardedCalls">Replies that were rolled back after being paid for.</param>
/// <param name="DiscardedCost">What those came to. Money spent on words nobody kept.</param>
/// <param name="PromptTokens">Prompt tokens the provider reported.</param>
/// <param name="CompletionTokens">Generated tokens the provider reported.</param>
/// <param name="CachedTokens">Prompt tokens served from cache rather than read again.</param>
/// <param name="Unpriced">Calls the API returned no cost for, so the total is a floor.</param>
/// <param name="ByKind">The same money split by what it was doing.</param>
/// <param name="FirstAtUtc">When the earliest counted call was made.</param>
/// <param name="LastAtUtc">When the latest was.</param>
public sealed record ConversationSpend(
    string ConversationId,
    string Name,
    string? Speaker,
    int Calls,
    decimal Cost,
    int DiscardedCalls,
    decimal DiscardedCost,
    long PromptTokens,
    long CompletionTokens,
    long CachedTokens,
    int Unpriced,
    IReadOnlyList<SpendByKind> ByKind,
    DateTimeOffset? FirstAtUtc,
    DateTimeOffset? LastAtUtc)
{
    /// <summary>
    /// The share of prompt tokens the provider did not have to read again, or null when it
    /// never said.
    /// </summary>
    /// <remarks>
    /// The number the prompt's layer order exists to move. Low on a long conversation means
    /// something before the transcript is changing between turns, or the host of the day does
    /// not cache at all — and those are worth telling apart.
    /// </remarks>
    public double? CachedShare => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : null;
}

/// <summary>Everything spent over a stretch of time.</summary>
/// <param name="FromUtc">Start of the window, inclusive. Null means from the beginning.</param>
/// <param name="ToUtc">End of the window, exclusive. Null means up to now.</param>
/// <param name="Conversations">One line each, dearest first.</param>
public sealed record SpendReport(
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    IReadOnlyList<ConversationSpend> Conversations)
{
    /// <summary>What the window came to.</summary>
    public decimal Cost => Conversations.Sum(static c => c.Cost);

    /// <summary>What was spent on replies that were then rolled back.</summary>
    public decimal DiscardedCost => Conversations.Sum(static c => c.DiscardedCost);

    /// <summary>Billed calls in the window.</summary>
    public int Calls => Conversations.Sum(static c => c.Calls);

    /// <summary>Calls the API priced at nothing it reported. The total is a floor by this many.</summary>
    public int Unpriced => Conversations.Sum(static c => c.Unpriced);

    /// <summary>Prompt tokens reported across the window.</summary>
    public long PromptTokens => Conversations.Sum(static c => c.PromptTokens);

    /// <summary>Generated tokens reported across the window.</summary>
    public long CompletionTokens => Conversations.Sum(static c => c.CompletionTokens);

    /// <summary>Prompt tokens served from cache across the window.</summary>
    public long CachedTokens => Conversations.Sum(static c => c.CachedTokens);

    /// <summary>The share of prompt tokens that did not have to be read again.</summary>
    public double? CachedShare => PromptTokens > 0 ? (double)CachedTokens / PromptTokens : null;

    /// <summary>The whole window's money split by what it was doing.</summary>
    public IReadOnlyList<SpendByKind> ByKind =>
    [
        .. Conversations
            .SelectMany(static c => c.ByKind)
            .GroupBy(static k => k.Kind)
            .Select(static g => new SpendByKind(g.Key, g.Sum(static k => k.Calls), g.Sum(static k => k.Cost)))
            .OrderBy(static k => k.Kind),
    ];
}
