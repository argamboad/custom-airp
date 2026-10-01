using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>Deletes a library entry, and refuses while a story still names it.</summary>
/// <remarks>
/// The terminal warns and lets the reader go ahead; this page refuses, and lists the stories.
/// A story stores a character's or a persona's name and reads the file every turn, so deleting
/// one a story names leaves that story with an empty layer — which has happened by accident
/// once already, and nothing on screen says so when it does. Delete those stories first, or
/// keep the file.
/// </remarks>
/// <param name="conversations">The store, for which stories use an entry.</param>
/// <param name="library">The shelves.</param>
public sealed class LibraryDeleteModel(LocalConversationProvider conversations, TextLibrary library) : PageModel
{
    /// <summary>The entry's shelf.</summary>
    public Shelf? Shelf { get; private set; }

    /// <summary>The entry's name, as the file spells it.</summary>
    public string? Entry { get; private set; }

    /// <summary>The stories that name it; while there are any, it is not deleted.</summary>
    public IReadOnlyList<string> UsedBy { get; private set; } = [];

    /// <summary>For a character, whether it has an opening, which goes with it.</summary>
    public bool HasOpening { get; private set; }

    /// <summary>For an opening, the character it belongs to, which this page returns to.</summary>
    public string? Character { get; private set; }

    /// <summary>Where "keep it" and a finished deletion lead.</summary>
    public string BackTo => Character is not null
        ? Shelves.PathOf(Shelves.Find(library, "characters")!, Character) + "#opening"
        : Shelves.PathOf(Shelf!, Entry!);

    /// <summary>Shows what would go, or why it cannot.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string shelf, string name, CancellationToken cancellationToken)
        => await LoadAsync(shelf, name, cancellationToken).ConfigureAwait(false) ? Page() : NotFound();

    /// <summary>Deletes the entry, unless a story names it.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>A redirect to the shelf, or the page saying why not.</returns>
    public async Task<IActionResult> OnPostAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(shelf, name, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        // Asked again at the moment of deleting, not trusted from when the page was drawn: a
        // story could have been started with it in between.
        if (UsedBy.Count > 0)
        {
            return Page();
        }

        TextLibrary.Delete(Shelf!.Folder, Entry!);

        // The two are one entry on these pages: an opening left behind would belong to nothing.
        if (HasOpening)
        {
            TextLibrary.Delete(library.Openings, Entry!);
        }

        return Redirect(Character is not null
            ? Shelves.PathOf(Shelves.Find(library, "characters")!, Character)
            : $"/library/{(Shelf.Tabbed ? Shelf.Slug : "characters")}");
    }

    private async Task<bool> LoadAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        Shelf = Shelves.Find(library, shelf);

        if (Shelf is null || TextLibrary.Find(Shelf.Folder, name) is not { } path)
        {
            return false;
        }

        Entry = Path.GetFileNameWithoutExtension(path);
        HasOpening = Shelf.Slug == "characters" && TextLibrary.Find(library.Openings, Entry) is not null;
        Character = Shelf.Slug == "openings" && TextLibrary.Find(library.Characters, Entry) is { } owner
            ? Path.GetFileNameWithoutExtension(owner)
            : null;

        if (Shelf.StoriesNameIt)
        {
            UsedBy = await conversations.ConversationsUsingAsync(Shelf.Slug == "personas", Entry, cancellationToken)
                .ConfigureAwait(false);
        }

        return true;
    }
}
