using Airp.Domain;
using Airp.Application.Abstractions;
using Airp.Application.Text;
using Airp.Domain.Conversations;
using Airp.Domain.Search;

namespace Airp.Application.Services;

/// <summary>
/// Default <see cref="ISearchService"/>: a search across the words in every chat.
/// </summary>
/// <remarks>
/// <para>
/// It reads the offline copies rather than the site. One conversation takes tens of seconds
/// to read in full — the site serves a hundred messages at a time and everything older
/// arrives only by scrolling back — so searching every chat live would take minutes. Every
/// chat that has been opened is already on disk, which makes this instant and works with no
/// network at all.
/// </para>
/// <para>
/// The consequence is that a chat never opened has nothing to search, and that is reported
/// rather than hidden. A search that quietly skipped half the conversations would be worse
/// than one that admits what it covered.
/// </para>
/// </remarks>
public sealed class SearchService : ISearchService
{
    private const int SnippetRadius = 48;

    private readonly IChatService _chats;
    private readonly IConversationService _conversations;

    /// <summary>Initialises the service.</summary>
    /// <param name="chats">Source of the chat list.</param>
    /// <param name="conversations">Source of each chat's transcript.</param>
    public SearchService(IChatService chats, IConversationService conversations)
    {
        _chats = chats;
        _conversations = conversations;
    }

    /// <inheritdoc />
    public async Task<SearchResults> SearchAsync(
        string query,
        SearchScope scope = SearchScope.All,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResults([], 0, 0);
        }

        var chats = await _chats.GetAsync(cancellationToken).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        var searched = 0;
        var skipped = 0;

        foreach (var chat in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (scope.HasFlag(SearchScope.Names))
            {
                AddNameHit(hits, chat, query);
            }

            if (!scope.HasFlag(SearchScope.Messages))
            {
                continue;
            }

            IReadOnlyList<ChatMessage> transcript;

            try
            {
                transcript = await _conversations
                    .GetMessagesAsync(chat.Id, forceRefresh: false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AirpException)
            {
                skipped++;
                continue;
            }

            if (transcript is not { Count: > 0 })
            {
                skipped++;
                continue;
            }

            searched++;
            AddMessageHits(hits, chat, transcript, query);
        }

        var ordered = hits
            .OrderByDescending(static h => h.Score)
            .ThenByDescending(static h => h.SentAtUtc ?? DateTimeOffset.MinValue)
            .Take(Math.Max(1, limit))
            .ToList();

        return new SearchResults(ordered, searched, skipped);
    }

    /// <summary>
    /// Builds a short excerpt of a longer text around a match.
    /// </summary>
    /// <param name="text">The text the match was found in.</param>
    /// <param name="matchIndex">Offset of the match.</param>
    /// <param name="radius">How many characters of context to keep on each side.</param>
    /// <returns>The excerpt, with ellipses where it was trimmed.</returns>
    internal static string BuildSnippet(string text, int matchIndex, int radius = SnippetRadius)
    {
        // Messages carry their own line breaks; a result list wants one line per hit.
        var flat = text.ReplaceLineEndings(" ");

        if (flat.Length <= radius * 2)
        {
            return flat.Trim();
        }

        var start = Math.Max(0, matchIndex - radius);
        var end = Math.Min(flat.Length, matchIndex + radius);
        var slice = flat[start..end].Trim();

        if (start > 0)
        {
            slice = "…" + slice;
        }

        if (end < flat.Length)
        {
            slice += "…";
        }

        return slice;
    }

    private static void AddNameHit(List<SearchHit> hits, Chat chat, string query)
    {
        var match = FuzzyMatcher.MatchAllTerms(query, chat.Name);
        if (!match.IsMatch)
        {
            return;
        }

        hits.Add(new SearchHit
        {
            ChatId = chat.Id,
            ChatName = chat.Name,
            Scope = SearchScope.Names,
            Snippet = chat.Name,
            Score = match.Score + 60,
            MatchOffsets = match.Positions,
        });
    }

    /// <summary>
    /// Finds the query inside a transcript, one hit per matching message.
    /// </summary>
    /// <remarks>
    /// A literal match is required here, unlike a name, where the letters need only appear in
    /// order. Fuzzy matching across thousands of characters of prose finds something in
    /// nearly every message, which is indistinguishable from finding nothing.
    /// </remarks>
    /// <param name="hits">Collected hits.</param>
    /// <param name="chat">The chat being searched.</param>
    /// <param name="transcript">Its messages.</param>
    /// <param name="query">The query.</param>
    private static void AddMessageHits(
        List<SearchHit> hits,
        Chat chat,
        IReadOnlyList<ChatMessage> transcript,
        string query)
    {
        foreach (var message in transcript)
        {
            if (!message.IsDialogue || message.Text.Length == 0)
            {
                continue;
            }

            var index = message.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            // A message that mentions the query more than once is the more likely one to
            // have been looking for.
            var occurrences = Occurrences(message.Text, query);

            hits.Add(new SearchHit
            {
                ChatId = chat.Id,
                ChatName = chat.Name,
                Scope = SearchScope.Messages,
                Snippet = BuildSnippet(message.Text, index),
                Speaker = message.Role == ChatRole.User ? "You" : chat.Name,
                SentAtUtc = message.SentAtUtc,
                Score = 40 + Math.Min(20, occurrences * 5),
                MatchOffsets = [Math.Min(index, SnippetRadius)],
            });
        }
    }

    private static int Occurrences(string text, string query)
    {
        var count = 0;
        var at = 0;

        while ((at = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += query.Length;
        }

        return count;
    }
}
