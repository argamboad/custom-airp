using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>Asks before removing a turn and everything after it, then does it.</summary>
/// <remarks>
/// A page of its own rather than a button with a question in a dialog, because the pages carry
/// no script and this is the one action on a story that cannot be taken back from here. The
/// count is taken over the whole transcript, hidden turns included, because they go too and a
/// confirmation that undercounts is worse than none — the terminal's rule, and its wording.
/// </remarks>
/// <param name="conversations">The store.</param>
public sealed class DeleteFromModel(LocalConversationProvider conversations) : PageModel
{
    /// <summary>The story.</summary>
    public Chat? Chat { get; private set; }

    /// <summary>The first turn that would go.</summary>
    public ChatMessage? Target { get; private set; }

    /// <summary>How many messages would go, this one included.</summary>
    public int Doomed { get; private set; }

    /// <summary>How many would remain.</summary>
    public int Remaining { get; private set; }

    /// <summary>The start of the first turn that would go, on one line.</summary>
    public string Preview
    {
        get
        {
            var flat = (Target?.Text ?? string.Empty).ReplaceLineEndings(" ").Trim();
            return flat.Length <= 120 ? flat : flat[..120].TrimEnd() + "…";
        }
    }

    /// <summary>Shows what would go.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="messageId">The first turn that would go.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string id, string messageId, CancellationToken cancellationToken)
        => await LoadAsync(id, messageId, cancellationToken).ConfigureAwait(false) ? Page() : NotFound();

    /// <summary>Removes the turn and everything after it, and goes back to the story.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="messageId">The first turn to go.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect to what is now the newest turn, or not found.</returns>
    public async Task<IActionResult> OnPostAsync(string id, string messageId, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, messageId, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        await conversations.DeleteFromAsync(id, messageId, cancellationToken).ConfigureAwait(false);
        return Redirect(StoryModel.Latest(id));
    }

    private async Task<bool> LoadAsync(string id, string messageId, CancellationToken cancellationToken)
    {
        Chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (Chat is null)
        {
            return false;
        }

        var messages = await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false);
        var index = messages.ToList().FindIndex(m => string.Equals(m.Id, messageId, StringComparison.Ordinal));

        if (index < 0)
        {
            return false;
        }

        Target = messages[index];
        Doomed = messages.Count - index;
        Remaining = index;
        return true;
    }
}
