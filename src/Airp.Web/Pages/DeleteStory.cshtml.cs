using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>Asks before deleting a whole story, naming it, then does it.</summary>
/// <remarks>
/// The confirmation names the story rather than asking abstractly, as the terminal's does: a
/// list of similar names is exactly where the wrong row gets tapped.
/// </remarks>
/// <param name="conversations">The store.</param>
public sealed class DeleteStoryModel(LocalConversationProvider conversations) : PageModel
{
    /// <summary>The story.</summary>
    public Chat? Chat { get; private set; }

    /// <summary>How many turns it holds.</summary>
    public int Turns { get; private set; }

    /// <summary>Shows what would go.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
        => await LoadAsync(id, cancellationToken).ConfigureAwait(false) ? Page() : NotFound();

    /// <summary>Deletes the story and goes back to the list.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect to the list, or not found.</returns>
    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        await conversations.DeleteConversationAsync(id, cancellationToken).ConfigureAwait(false);
        return Redirect("/");
    }

    private async Task<bool> LoadAsync(string id, CancellationToken cancellationToken)
    {
        Chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (Chat is null)
        {
            return false;
        }

        Turns = (await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false))
            .Count(static m => m.IsDialogue);
        return true;
    }
}
