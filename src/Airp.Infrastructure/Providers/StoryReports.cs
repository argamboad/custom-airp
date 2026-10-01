using System.Globalization;
using Airp.Application.Text;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>The character and persona a turn would send, and where each came from.</summary>
/// <param name="Character">The character text, or null when nothing resolves.</param>
/// <param name="CharacterSource">Which branch of the resolution rule it came from.</param>
/// <param name="Persona">The persona text, or null when nothing resolves.</param>
/// <param name="PersonaSource">Which branch of the resolution rule it came from.</param>
public sealed record StoryIdentity(string? Character, string CharacterSource, string? Persona, string PersonaSource);

/// <summary>What one of the reading commands found: a titled page of lines, or why there is none.</summary>
/// <param name="Title">What the page is.</param>
/// <param name="Subtitle">A line about it — or, when <see cref="Lines"/> is empty, why there is nothing.</param>
/// <param name="Lines">The page, as plain lines.</param>
public sealed record StoryReport(string Title, string Subtitle, IReadOnlyList<string> Lines)
{
    /// <summary>Whether there was nothing to show; <see cref="Subtitle"/> then says why.</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>A report of nothing, with the reason.</summary>
    /// <param name="title">What the page would have been.</param>
    /// <param name="reason">Why there is nothing.</param>
    /// <returns>The empty report.</returns>
    public static StoryReport Nothing(string title, string reason) => new(title, reason, []);

    /// <summary>The report as one block of text, for a front end that shows text.</summary>
    /// <returns>The title, the subtitle, and the lines.</returns>
    public string ToText()
        => IsEmpty
            ? Subtitle
            : $"{Title} — {Subtitle}\n\n" + string.Join('\n', Lines).TrimEnd();
}

/// <summary>One turn that matched a search.</summary>
/// <param name="Number">Its position among the visible turns, from 1.</param>
/// <param name="Role">Who said it.</param>
/// <param name="Excerpt">The words around the match, on one line.</param>
public sealed record SearchMatch(int Number, ChatRole Role, string Excerpt);

/// <summary>
/// What the composer's reading commands answer with, for every front end.
/// </summary>
/// <remarks>
/// <para>
/// The terminal draws these as a pane and the web pages as a block under the transcript. One
/// place builds the lines, because the alternative is two places deciding what "the facts" or
/// "what this story has cost" means, and a figure that reads one way in the terminal and another
/// on a phone is one of them wrong.
/// </para>
/// <para>Every one only reads. None calls the model or writes anything.</para>
/// </remarks>
public static class StoryReports
{
    /// <summary>How many recent turns <c>/audit</c> lists.</summary>
    public const int AuditTurns = 12;

    /// <summary>How many out-of-character questions <c>/audit</c> lists.</summary>
    public const int AuditAsides = 8;

    /// <summary>The character a turn would send.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> CharacterAsync(
        LocalConversationProvider conversations,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var identity = await conversations.IdentityAsync(conversationId, cancellationToken).ConfigureAwait(false);

        return Page("Character", identity.CharacterSource, identity.Character, "This conversation has no character definition.");
    }

    /// <summary>The persona a turn would send.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> PersonaAsync(
        LocalConversationProvider conversations,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var identity = await conversations.IdentityAsync(conversationId, cancellationToken).ConfigureAwait(false);

        return Page("Persona", identity.PersonaSource, identity.Persona, "This conversation has no persona.");
    }

    /// <summary>What is being injected as true right now, grouped by whom it is about.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> FactsAsync(
        LocalConversationProvider conversations,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var facts = await conversations.FactsAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var live = facts.Where(static f => f.ValidToSequence is null).ToList();

        if (live.Count == 0)
        {
            return StoryReport.Nothing("Facts", "Nothing is being injected as true yet. /fact <statement> writes one.");
        }

        var lines = new List<string>();

        foreach (var group in live.GroupBy(static f => f.Subject).OrderBy(static g => g.Key))
        {
            lines.Add(group.Key);

            foreach (var fact in group.OrderBy(static f => f.ValidFromSequence))
            {
                lines.Add("  · " + fact.Text);
            }

            lines.Add(string.Empty);
        }

        var retired = facts.Count - live.Count;

        return new StoryReport("Facts", retired > 0 ? $"{live.Count} live, {retired} retired" : $"{live.Count} live", lines);
    }

    /// <summary>The meters this story keeps, and their values.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> TrackersAsync(
        LocalConversationProvider conversations,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var meters = await conversations.TrackersAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (meters.Count == 0)
        {
            return StoryReport.Nothing("Trackers", "This conversation keeps no meters. /tracker <name> <value> starts one.");
        }

        var lines = new List<string>();

        foreach (var meter in meters)
        {
            lines.Add($"{meter.Name}  {meter.Value:0.##} / {meter.Max:0.##}"
                + (meter.Delta != 0 ? $"   last moved {meter.Delta:+0.##;-0.##}" : string.Empty));

            if (!string.IsNullOrWhiteSpace(meter.Note))
            {
                lines.Add("  " + meter.Note);
            }

            lines.Add(string.Empty);
        }

        return new StoryReport("Trackers", $"{meters.Count} meter(s)", lines);
    }

    /// <summary>What the recent turns cost, layer by layer, and the questions asked beside them.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> AuditAsync(
        LocalConversationProvider conversations,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var turns = await conversations.AuditAsync(conversationId, cancellationToken).ConfigureAwait(false);
        var asides = await conversations.AsidesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (turns.Count == 0 && asides.Count == 0)
        {
            return StoryReport.Nothing("Audit", "Nothing has been generated in this conversation yet.");
        }

        var lines = new List<string>();

        foreach (var turn in turns.OrderByDescending(static t => t.Sequence).Take(AuditTurns))
        {
            lines.Add($"#{turn.Sequence}{(turn.Hidden ? "  (rolled back)" : string.Empty)}"
                + $"   {Count(turn.PromptTokens)} in, {Count(turn.CompletionTokens)} out"
                + $"   served by {turn.Provider ?? "unknown"}");

            if (!string.IsNullOrWhiteSpace(turn.Context))
            {
                lines.Add("  " + turn.Context);
            }

            lines.Add(string.Empty);
        }

        // Asides are billed and store no message, so they appear nowhere else. Leaving them
        // out is how a per-chat cost quietly stops adding up.
        if (asides.Count > 0)
        {
            lines.Add("Questions asked out of character");

            foreach (var aside in asides.Take(AuditAsides))
            {
                lines.Add($"  · {aside.Question}");
                lines.Add($"    {Count(aside.PromptTokens)} in, {Count(aside.CompletionTokens)} out"
                    + $"   served by {aside.Provider ?? "unknown"}");
            }
        }

        return new StoryReport(
            "Audit",
            asides.Count > 0 ? $"{turns.Count} turn(s), {asides.Count} question(s)" : $"{turns.Count} turn(s)",
            lines);
    }

    /// <summary>
    /// What this story has cost, and what it bought.
    /// </summary>
    /// <remarks>
    /// Which kinds of call it went on, how much of the prompt the provider served from cache,
    /// and what was paid for replies that were then rerolled away — the only line of spending
    /// here that bought nothing at all.
    /// </remarks>
    /// <param name="conversations">The store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="name">What to call the story on the page.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The report.</returns>
    public static async Task<StoryReport> CostAsync(
        LocalConversationProvider conversations,
        string conversationId,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        var report = await conversations
            .SpendAsync(conversationId: conversationId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (report.Conversations.FirstOrDefault() is not { } spend)
        {
            return StoryReport.Nothing("Cost", "Nothing has been spent on this story yet.");
        }

        var lines = new List<string>
        {
            $"{spend.Cost:$0.0000}   over {spend.Calls} billed call(s)",
            string.Empty,
        };

        foreach (var kind in spend.ByKind)
        {
            lines.Add($"  {KindName(kind.Kind),-14}{kind.Cost:$0.0000}   {kind.Calls} call(s)");
        }

        lines.Add(string.Empty);
        lines.Add($"  {"tokens",-14}{spend.PromptTokens:N0} in, {spend.CompletionTokens:N0} out");

        lines.Add(spend.CachedShare is { } share
            ? $"  {"cached",-14}{share:P0} of the prompt was served from cache"
            : $"  {"cached",-14}the provider never said");

        if (spend.DiscardedCost > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"  {spend.DiscardedCost:$0.0000} went on {spend.DiscardedCalls} reply(ies) "
                + "you regenerated away. They are still in the audit.");
        }

        if (spend.Unpriced > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"  {spend.Unpriced} call(s) came back with no price, so this is a floor.");
        }

        lines.Add(string.Empty);
        lines.Add("  Embeddings are not counted; the whole corpus costs under a cent.");

        return new StoryReport("Cost", name, lines);
    }

    /// <summary>The visible turns that contain some words, newest last.</summary>
    /// <param name="turns">The conversation's messages, oldest first.</param>
    /// <param name="query">What to look for; case does not matter.</param>
    /// <returns>Each matching turn with its position and the words around the match.</returns>
    public static IReadOnlyList<SearchMatch> Search(IReadOnlyList<ChatMessage> turns, string query)
    {
        ArgumentNullException.ThrowIfNull(turns);
        var wanted = (query ?? string.Empty).Trim();

        if (wanted.Length == 0)
        {
            return [];
        }

        return [.. turns
            .Where(static m => m.IsDialogue)
            .Select(static (m, i) => (Message: m, Number: i + 1))
            .Where(t => t.Message.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase))
            .Select(t => new SearchMatch(t.Number, t.Message.Role, Excerpt(t.Message.Text, wanted)))];
    }

    /// <summary>A search as a page of text, for a front end that can only show text.</summary>
    /// <param name="matches">What <see cref="Search"/> found.</param>
    /// <param name="query">What was looked for.</param>
    /// <param name="speaker">What to call the replies' author.</param>
    /// <returns>The report.</returns>
    public static StoryReport SearchReport(IReadOnlyList<SearchMatch> matches, string query, string speaker)
    {
        ArgumentNullException.ThrowIfNull(matches);

        if (matches.Count == 0)
        {
            return StoryReport.Nothing("Search", $"\"{query.Trim()}\" is not in this conversation.");
        }

        return new StoryReport(
            "Search",
            $"{matches.Count} turn(s) with \"{query.Trim()}\"",
            [.. matches.Select(m => $"#{m.Number} {(m.Role == ChatRole.User ? "You" : speaker)}: {m.Excerpt}")]);
    }

    /// <summary>Every command, grouped by what it costs, as the help lists them.</summary>
    /// <param name="extra">Lines for commands a particular front end adds, listed after the rest.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<string> HelpLines(IReadOnlyList<string>? extra = null)
    {
        var lines = new List<string>();

        foreach (var group in SlashCommands.All.GroupBy(static c => c.Cost))
        {
            lines.Add(group.Key switch
            {
                CommandCost.Billed => "Billed — these call the model",
                CommandCost.Write => "These write to the conversation",
                _ => "Free — these only read what is already here",
            });

            foreach (var command in group)
            {
                lines.Add($"  {command.Usage}");
                lines.Add($"      {command.Summary}");
            }

            lines.Add(string.Empty);
        }

        if (extra is { Count: > 0 })
        {
            lines.AddRange(extra);
            lines.Add(string.Empty);
        }

        lines.Add("A message that genuinely starts with a slash is sent by doubling it: //like this.");

        return lines;
    }

    /// <summary>What a kind of billed work is called on screen.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Its name.</returns>
    public static string KindName(SpendKind kind) => kind switch
    {
        SpendKind.Reply => "replies",
        SpendKind.Aside => "questions",
        SpendKind.Summary => "compression",
        SpendKind.Facts => "extraction",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static StoryReport Page(string title, string source, string? text, string missing)
        => string.IsNullOrWhiteSpace(text)
            ? StoryReport.Nothing(title, missing)
            : new StoryReport(title, source, [.. text.ReplaceLineEndings("\n").Split('\n')]);

    private static string Count(int? tokens) => tokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "?";

    /// <summary>The words either side of the first match, on one line.</summary>
    private static string Excerpt(string text, string query)
    {
        const int around = 60;
        var flat = text.ReplaceLineEndings(" ");
        var at = flat.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, at - around);
        var end = Math.Min(flat.Length, at + query.Length + around);

        return (start > 0 ? "…" : string.Empty)
            + flat[start..end].Trim()
            + (end < flat.Length ? "…" : string.Empty);
    }
}

/// <summary>What became of a request to change a story's model.</summary>
/// <param name="Saved">Whether the story's model changed.</param>
/// <param name="Model">The story's model now, or null for the default.</param>
/// <param name="Context">That model's window, when known.</param>
/// <param name="Message">What to tell the reader — including, when nothing was saved, why and what the story stays on.</param>
public sealed record ModelChange(bool Saved, string? Model, int? Context, string Message);
