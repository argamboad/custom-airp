using System.Text.RegularExpressions;
using Airp.Domain.Conversations;

namespace Airp.Proxy;

/// <summary>How a request was matched to a conversation, for the log and for diagnostics.</summary>
public enum SessionMatch
{
    /// <summary>No conversation could be identified.</summary>
    None = 0,

    /// <summary>An explicit tag in the prompt named it.</summary>
    Tag,

    /// <summary>The character's name matched exactly one conversation.</summary>
    Speaker,

    /// <summary>The opening of the transcript matched exactly one conversation.</summary>
    Opening,
}

/// <summary>What a resolution attempt concluded.</summary>
/// <param name="ConversationId">The conversation, or null when none was identified.</param>
/// <param name="How">Which strategy succeeded.</param>
/// <param name="Ambiguous">Whether more than one conversation fitted.</param>
public readonly record struct SessionResolution(string? ConversationId, SessionMatch How, bool Ambiguous = false);

/// <summary>
/// Works out which stored conversation an incoming request belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The problem that exists only on this side. In the terminal the client owns the session and
/// there is nothing to resolve; here a request arrives from someone else's front end carrying
/// no identifier of ours, and getting it wrong means writing one conversation's turn into
/// another — which append-only storage makes permanent.
/// </para>
/// <para>
/// Three strategies, in descending order of how much they can be trusted, and none of them
/// guesses: when nothing identifies the conversation the answer is "none", not "probably this
/// one". A caller that cannot resolve should say so rather than write somewhere.
/// </para>
/// </remarks>
public static partial class SessionResolver
{
    /// <summary>
    /// Matches the tag a reader can put in the front end's own custom-prompt field.
    /// </summary>
    /// <remarks>
    /// The happy path, and the only one that is exact. Everything else infers.
    /// </remarks>
    [GeneratedRegex(@"\[\[\s*rp\s*:\s*([A-Za-z0-9\-]{1,64})\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern { get; }

    /// <summary>Resolves a request to a conversation.</summary>
    /// <param name="prompt">Everything the front end sent, concatenated.</param>
    /// <param name="firstUserText">The earliest user turn the front end sent, if any.</param>
    /// <param name="candidates">The conversations available to match against.</param>
    /// <param name="openings">
    /// The first stored turn of each conversation, keyed by conversation id. Used to recognise
    /// a transcript by how it starts, which survives truncation of everything after it.
    /// </param>
    /// <returns>What was concluded.</returns>
    public static SessionResolution Resolve(
        string prompt,
        string? firstUserText,
        IReadOnlyList<Chat> candidates,
        IReadOnlyDictionary<string, string> openings)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(openings);

        // 1. An explicit tag. Exact, and the only strategy that cannot be wrong.
        if (TagPattern.Match(prompt) is { Success: true } tagged)
        {
            var id = tagged.Groups[1].Value;

            if (candidates.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                return new SessionResolution(
                    candidates.First(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Id,
                    SessionMatch.Tag);
            }

            // A tag that names nothing is a mistake worth surfacing, not a reason to fall back
            // and silently write somewhere else.
            return new SessionResolution(null, SessionMatch.None);
        }

        // 2. The character's name, when exactly one conversation has it. A second conversation
        //    with the same character makes this ambiguous rather than merely uncertain.
        var bySpeaker = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Speaker)
                        && prompt.Contains(c.Speaker!, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (bySpeaker.Length == 1)
        {
            return new SessionResolution(bySpeaker[0].Id, SessionMatch.Speaker);
        }

        if (bySpeaker.Length > 1)
        {
            return new SessionResolution(null, SessionMatch.None, Ambiguous: true);
        }

        // 3. How the transcript opens. A front end that truncates history keeps the start of
        //    the scene far longer than the middle of it.
        if (!string.IsNullOrWhiteSpace(firstUserText))
        {
            var byOpening = openings
                .Where(o => Alike(o.Value, firstUserText))
                .Select(static o => o.Key)
                .ToArray();

            if (byOpening.Length == 1)
            {
                return new SessionResolution(byOpening[0], SessionMatch.Opening);
            }

            if (byOpening.Length > 1)
            {
                return new SessionResolution(null, SessionMatch.None, Ambiguous: true);
            }
        }

        return new SessionResolution(null, SessionMatch.None);
    }

    /// <summary>
    /// Whether two openings are the same turn, allowing for reformatting in transit.
    /// </summary>
    /// <remarks>
    /// Compared on a normalised prefix rather than exactly. A front end may re-wrap lines,
    /// swap quote characters or append its own framing, none of which make it a different
    /// message — and an exact comparison would fail on all of them.
    /// </remarks>
    private static bool Alike(string stored, string incoming)
    {
        var a = Normalise(stored);
        var b = Normalise(incoming);

        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var length = Math.Min(80, Math.Min(a.Length, b.Length));
        return a.AsSpan(0, length).SequenceEqual(b.AsSpan(0, length));
    }

    private static string Normalise(string text)
        => new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
