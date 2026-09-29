using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>One story, read from its latest turns back.</summary>
/// <param name="conversations">The store.</param>
public sealed class StoryModel(LocalConversationProvider conversations) : PageModel
{
    /// <summary>
    /// How many turns a page shows before offering the rest. A real story runs to several
    /// hundred turns of long prose; a phone loads the end, which is what is read.
    /// </summary>
    public const int Recent = 40;

    /// <summary>The story.</summary>
    public Chat? Chat { get; private set; }

    /// <summary>The turns on the page, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Shown { get; private set; } = [];

    /// <summary>How many earlier turns are not on the page.</summary>
    public int Hidden { get; private set; }

    /// <summary>What to call the replies' author: the speaker, or the story's name.</summary>
    public string Speaker => Chat?.Speaker ?? Chat?.Name ?? "Reply";

    /// <summary>Reads the story.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="all">Whether to show every turn rather than the recent ones.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string id, bool all, CancellationToken cancellationToken)
    {
        Chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (Chat is null)
        {
            return NotFound();
        }

        var turns = (await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false))
            .Where(static m => m.IsDialogue)
            .ToList();

        Shown = all ? turns : [.. turns.TakeLast(Recent)];
        Hidden = turns.Count - Shown.Count;

        return Page();
    }
}
