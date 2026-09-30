using System.Text.RegularExpressions;
using Airp.Domain.Conversations;

namespace Airp.Proxy;

/// <summary>What a resolution attempt concluded.</summary>
/// <param name="ConversationId">The conversation, or null when none was identified.</param>
/// <param name="Tagged">
/// Whether the request carried a tag at all — so a refusal can say whether one is missing or
/// names nothing, which are two different things to fix.
/// </param>
public readonly record struct SessionResolution(string? ConversationId, bool Tagged);

/// <summary>
/// Works out which stored conversation an incoming request belongs to: the one its tag names,
/// and no other.
/// </summary>
/// <remarks>
/// <para>
/// The problem that exists only on this side. In the terminal the client owns the session and
/// there is nothing to resolve; here a request arrives from someone else's front end carrying
/// no identifier of ours, and getting it wrong means writing one conversation's turn into
/// another — which append-only storage makes permanent, and which is billed.
/// </para>
/// <para>
/// It used to infer as well: a character's name that only one conversation had, or an opening
/// turn that matched one. Both were refusal-first, and both could still write into a real story
/// from a chat that was never meant for it — a test chat in the front end that happened to
/// mention a name was enough. So the tag the reader puts in the front end's custom prompt is now
/// the only way in, and a request without one writes nothing (ADR 0017).
/// </para>
/// </remarks>
public static partial class SessionResolver
{
    /// <summary>
    /// Matches the tag a reader can put in the front end's own custom-prompt field.
    /// </summary>
    [GeneratedRegex(@"\[\[\s*rp\s*:\s*([A-Za-z0-9\-]{1,64})\s*\]\]", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern { get; }

    /// <summary>Resolves a request to the conversation its tag names.</summary>
    /// <param name="prompt">Everything the front end sent, concatenated.</param>
    /// <param name="candidates">The conversations that exist.</param>
    /// <returns>What was concluded.</returns>
    public static SessionResolution Resolve(string prompt, IReadOnlyList<Chat> candidates)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(candidates);

        if (TagPattern.Match(prompt) is not { Success: true } tagged)
        {
            return new SessionResolution(null, Tagged: false);
        }

        var id = tagged.Groups[1].Value;

        // A tag that names nothing is a mistake worth surfacing, not something to work around.
        return new SessionResolution(
            candidates.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Id,
            Tagged: true);
    }
}
