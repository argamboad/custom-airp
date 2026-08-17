namespace Airp.Application.Text;

/// <summary>The outcome of matching a query against a candidate string.</summary>
/// <param name="IsMatch">Whether every query character was found in order.</param>
/// <param name="Score">Relevance; higher is better. Zero when <paramref name="IsMatch"/> is false.</param>
/// <param name="Positions">Indices in the candidate that the query matched, for highlighting.</param>
public readonly record struct FuzzyMatch(bool IsMatch, int Score, IReadOnlyList<int> Positions)
{
    /// <summary>A result representing "no match".</summary>
    public static FuzzyMatch None { get; } = new(false, 0, []);
}

/// <summary>
/// Subsequence matcher used by the chat filter, global search and command palette.
/// </summary>
/// <remarks>
/// <para>
/// Scoring is deliberately simple and predictable rather than clever: a character scores
/// higher when it is adjacent to the previous match, when it starts a word, and when it
/// appears early in the candidate. Exact substring matches are boosted so that typing a
/// full word always ranks the literal hit first — the behaviour users expect from a filter
/// box even when a scattered subsequence technically matches better.
/// </para>
/// <para>All members are pure and thread-safe.</para>
/// </remarks>
public static class FuzzyMatcher
{
    private const int AdjacencyBonus = 8;
    private const int WordStartBonus = 12;
    private const int CaseMatchBonus = 2;
    private const int LeadingPenaltyPerChar = -1;
    private const int MaxLeadingPenalty = -12;
    private const int UnmatchedPenaltyPerChar = -1;
    private const int SubstringBonus = 40;
    private const int PrefixBonus = 30;

    /// <summary>Matches <paramref name="query"/> against <paramref name="candidate"/>.</summary>
    /// <param name="query">What the user typed. Whitespace is significant.</param>
    /// <param name="candidate">The string being ranked.</param>
    /// <returns>The match result.</returns>
    public static FuzzyMatch Match(string? query, string? candidate)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new FuzzyMatch(true, 0, []);
        }

        if (string.IsNullOrEmpty(candidate))
        {
            return FuzzyMatch.None;
        }

        var positions = new List<int>(query.Length);
        var score = 0;
        var candidateIndex = 0;
        var previousMatchIndex = -2;

        for (var queryIndex = 0; queryIndex < query.Length; queryIndex++)
        {
            var wanted = query[queryIndex];
            var found = -1;

            for (var i = candidateIndex; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(wanted))
                {
                    continue;
                }

                found = i;
                break;
            }

            if (found < 0)
            {
                return FuzzyMatch.None;
            }

            if (found == previousMatchIndex + 1)
            {
                score += AdjacencyBonus;
            }

            if (IsWordStart(candidate, found))
            {
                score += WordStartBonus;
            }

            if (candidate[found] == wanted)
            {
                score += CaseMatchBonus;
            }

            if (queryIndex == 0)
            {
                score += Math.Max(MaxLeadingPenalty, found * LeadingPenaltyPerChar);
            }

            positions.Add(found);
            previousMatchIndex = found;
            candidateIndex = found + 1;
        }

        score += Math.Max(-40, (candidate.Length - query.Length) * UnmatchedPenaltyPerChar);

        var substringIndex = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (substringIndex >= 0)
        {
            score += SubstringBonus;
            if (substringIndex == 0)
            {
                score += PrefixBonus;
            }
        }

        return new FuzzyMatch(true, score, positions);
    }

    /// <summary>
    /// Matches a multi-word query where each whitespace-separated term must match somewhere
    /// in the candidate, in any order. This is what the global search box uses.
    /// </summary>
    /// <param name="query">The query; terms are separated by whitespace.</param>
    /// <param name="candidate">The string being ranked.</param>
    /// <returns>The combined match result.</returns>
    public static FuzzyMatch MatchAllTerms(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new FuzzyMatch(true, 0, []);
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 1)
        {
            return Match(terms[0], candidate);
        }

        var total = 0;
        var positions = new SortedSet<int>();

        foreach (var term in terms)
        {
            var match = Match(term, candidate);
            if (!match.IsMatch)
            {
                return FuzzyMatch.None;
            }

            total += match.Score;
            foreach (var position in match.Positions)
            {
                positions.Add(position);
            }
        }

        return new FuzzyMatch(true, total / terms.Length, [.. positions]);
    }

    /// <summary>Ranks a sequence by fuzzy relevance, dropping non-matches.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="items">Items to rank.</param>
    /// <param name="query">The query; empty preserves the original order.</param>
    /// <param name="selector">Projects the text to match against.</param>
    /// <returns>Matching items, best first. Ties keep their original relative order.</returns>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, string? query, Func<T, string> selector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selector);

        if (string.IsNullOrWhiteSpace(query))
        {
            return [.. items];
        }

        return items
            .Select((item, index) => (item, index, match: MatchAllTerms(query, selector(item))))
            .Where(static x => x.match.IsMatch)
            .OrderByDescending(static x => x.match.Score)
            .ThenBy(static x => x.index)
            .Select(static x => x.item)
            .ToList();
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        var current = text[index];

        if (!char.IsLetterOrDigit(previous))
        {
            return true;
        }

        // camelCase boundary.
        return char.IsLower(previous) && char.IsUpper(current);
    }
}
