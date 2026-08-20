namespace Airp.Infrastructure.Providers;

/// <summary>
/// What rebuilding a conversation's memory removed and what it produced in its place.
/// </summary>
/// <remarks>
/// Both halves are reported because they are not the same number and the difference is the
/// point. Fewer, longer summaries covering the same turns is the result to want; the same count
/// back again means nothing was wrong with the old ones.
/// </remarks>
/// <param name="SummariesRemoved">Summaries thrown away.</param>
/// <param name="FactsRemoved">Extracted facts thrown away.</param>
/// <param name="PinnedKept">
/// Hand-written facts left untouched. These are the one thing here that is not derived from the
/// transcript, so they are the one thing a rebuild must not take.
/// </param>
/// <param name="SummariesWritten">Summaries produced.</param>
/// <param name="FactsExtracted">Facts extracted.</param>
/// <param name="MessagesCovered">Messages the new summaries stand in for.</param>
public readonly record struct MemoryRebuild(
    int SummariesRemoved,
    int FactsRemoved,
    int PinnedKept,
    int SummariesWritten,
    int FactsExtracted,
    int MessagesCovered);
