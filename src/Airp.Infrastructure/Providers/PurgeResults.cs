namespace Airp.Infrastructure.Providers;

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
public readonly record struct PurgeReport(
    int Conversations,
    int Messages,
    int Summaries,
    int Facts,
    int Trackers)
{
    /// <summary>Whether anything was there to erase.</summary>
    public bool Empty => Conversations == 0;
}
