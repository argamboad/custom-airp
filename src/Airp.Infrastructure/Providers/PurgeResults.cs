namespace Airp.Infrastructure.Providers;

/// <summary>
/// What a purge left in the spend ledger, and what it was worth.
/// </summary>
/// <remarks>
/// Erasing a conversation does not un-spend what it cost. The ledger holds no story text —
/// model names, token counts and money — so keeping it takes nothing back from the erasure,
/// and dropping it would quietly make every report covering that month wrong.
/// </remarks>
/// <param name="Rows">Ledger rows kept.</param>
/// <param name="Cost">What they recorded.</param>
public readonly record struct LedgerKept(int Rows, decimal Cost);

/// <summary>A conversation the reader deleted, still on disk until it is purged.</summary>
/// <param name="Id">The conversation's identifier.</param>
/// <param name="Name">What it was called.</param>
/// <param name="DeletedAtUtc">When it was hidden.</param>
/// <param name="Messages">How many messages it still holds.</param>
public readonly record struct PurgeCandidate(
    string Id,
    string Name,
    DateTimeOffset DeletedAtUtc,
    int Messages);

/// <summary>What a purge erased.</summary>
/// <param name="Conversations">Conversations removed.</param>
/// <param name="Messages">Messages removed with them.</param>
/// <param name="Summaries">Summaries removed.</param>
/// <param name="Facts">Facts removed.</param>
/// <param name="Trackers">Trackers removed.</param>
/// <param name="Asides">Questions asked out of character, removed with their answers.</param>
/// <param name="LedgerKept">
/// Ledger rows deliberately left behind, and what they came to.
/// </param>
public readonly record struct PurgeReport(
    int Conversations,
    int Messages,
    int Summaries,
    int Facts,
    int Trackers,
    int Asides,
    LedgerKept LedgerKept)
{
    /// <summary>Whether anything was there to erase.</summary>
    public bool Empty => Conversations == 0;
}
