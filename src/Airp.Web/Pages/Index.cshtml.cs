using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>The stories, newest first.</summary>
/// <param name="conversations">The store.</param>
public sealed class IndexModel(LocalConversationProvider conversations) : PageModel
{
    /// <summary>The stories to list.</summary>
    public IReadOnlyList<Chat> Chats { get; private set; } = [];

    /// <summary>Now, once, so every row is measured against the same moment.</summary>
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    /// <summary>A story's latest message as one line to recognise it by.</summary>
    /// <remarks>
    /// The first line that says anything, with the markers the story page never shows taken
    /// out — the same preview the terminal's phone layout gives each chat.
    /// </remarks>
    /// <param name="latest">The latest message, if any.</param>
    /// <returns>The line, or a note that nothing has been said.</returns>
    public static string Preview(string? latest)
        => (latest ?? string.Empty)
            .Split('\n')
            .Select(static line => Application.Text.ProseFormat.Format(line).Text.Trim())
            .FirstOrDefault(static line => line.Length > 0) ?? "Nothing said yet.";

    /// <summary>Reads the list.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>A task.</returns>
    public async Task OnGetAsync(CancellationToken cancellationToken)
        => Chats = [.. (await conversations.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(static c => c.LastMessageAtUtc ?? DateTimeOffset.MinValue)];
}
