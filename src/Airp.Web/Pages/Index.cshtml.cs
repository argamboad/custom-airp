using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>The stories, newest first; each can be renamed or deleted from here.</summary>
/// <param name="conversations">The store.</param>
public sealed class IndexModel(LocalConversationProvider conversations) : PageModel
{
    /// <summary>The stories to list.</summary>
    public IReadOnlyList<Chat> Chats { get; private set; } = [];

    /// <summary>Now, once, so every row is measured against the same moment.</summary>
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    /// <summary>Why a rename did nothing, when it did nothing.</summary>
    public string? Error { get; private set; }

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
        => await LoadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Renames a story.</summary>
    /// <remarks>
    /// A blank name is refused rather than clearing anything, as in the terminal: the list needs
    /// something to show. Renaming touches the name alone — the story, its memory and its files
    /// are as they were.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect to the list, or the list with an error on it.</returns>
    public async Task<IActionResult> OnPostRenameAsync(string id, string? name, CancellationToken cancellationToken)
    {
        var chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (chat is null)
        {
            return NotFound();
        }

        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            Error = "A story needs a name. Nothing was changed.";
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            return Page();
        }

        if (!string.Equals(trimmed, chat.Name, StringComparison.Ordinal))
        {
            try
            {
                await conversations.RenameConversationAsync(id, trimmed, cancellationToken).ConfigureAwait(false);
            }
            catch (AirpException ex)
            {
                Error = ex.Message;
                await LoadAsync(cancellationToken).ConfigureAwait(false);
                return Page();
            }
        }

        return Redirect("/");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
        => Chats = [.. (await conversations.ListAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(static c => c.LastMessageAtUtc ?? DateTimeOffset.MinValue)];
}
