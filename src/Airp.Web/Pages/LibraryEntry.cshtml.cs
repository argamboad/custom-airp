using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>One entry of the library, in a text box: read it, change it, save it.</summary>
/// <remarks>
/// <para>
/// The terminal hands an entry to the system editor; a phone has no such thing, so the page is
/// the editor. A save goes through <see cref="TextLibrary.SaveAsync"/>, which refuses when the
/// file changed after the page was opened — the laptop and the phone edit the same files — and
/// keeps what it replaces as <c>.bak</c>.
/// </para>
/// <para>
/// A refused save keeps what was typed in the box, and shows what the file says now beside it.
/// Saving again replaces the newer text knowingly, and the backup still holds it. Losing a page
/// typed on a phone keyboard to a conflict would be the worse failure.
/// </para>
/// <para>
/// A character's page carries its opening too, since the opening's name matching the
/// character's is the whole association.
/// </para>
/// </remarks>
/// <param name="conversations">The store, for which stories use an entry.</param>
/// <param name="library">The shelves.</param>
public sealed class LibraryEntryModel(LocalConversationProvider conversations, TextLibrary library) : PageModel
{
    /// <summary>Where a saved page's confirmation is carried across the redirect.</summary>
    public const string NoticeKey = "LibraryNotice";

    /// <summary>The entry's shelf.</summary>
    public Shelf? Shelf { get; private set; }

    /// <summary>The entry's name, as the file spells it.</summary>
    public string? Entry { get; private set; }

    /// <summary>The text in the box: the file's, or what was typed when a save was refused.</summary>
    [BindProperty]
    public string? Text { get; set; }

    /// <summary>The version of the file the box started from.</summary>
    [BindProperty]
    public string? Version { get; set; }

    /// <summary>What the file says now, when a save was refused because it changed.</summary>
    public string? Current { get; private set; }

    /// <summary>The stories that name this entry, for a character or a persona.</summary>
    public IReadOnlyList<string> UsedBy { get; private set; } = [];

    /// <summary>The opening's text, for a character that has one.</summary>
    [BindProperty]
    public string? OpeningText { get; set; }

    /// <summary>The version of the opening the box started from.</summary>
    [BindProperty]
    public string? OpeningVersion { get; set; }

    /// <summary>What the opening says now, when its save was refused because it changed.</summary>
    public string? OpeningCurrent { get; private set; }

    /// <summary>Whether this character has an opening on the shelf.</summary>
    public bool HasOpening { get; private set; }

    /// <summary>A confirmation carried from the last save.</summary>
    public string? Notice { get; private set; }

    /// <summary>Why the entry's save did not happen.</summary>
    public string? Error { get; private set; }

    /// <summary>Why the opening's save did not happen.</summary>
    public string? OpeningError { get; private set; }

    /// <summary>Whether this is a character, which carries its opening on the same page.</summary>
    public bool IsCharacter => Shelf?.Slug == "characters";

    /// <summary>Shows the entry.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, name))
        {
            return NotFound();
        }

        // An opening is edited on its character's page, when it has one.
        if (Shelf!.Slug == "openings" && TextLibrary.Find(library.Characters, Entry) is { } character)
        {
            return Redirect(Shelves.PathOf(Shelves.Find(library, "characters")!, Path.GetFileNameWithoutExtension(character)) + "#opening");
        }

        Notice = TempData[NoticeKey] as string;
        (Text, Version) = await ReadAsync(Shelf!.Folder, Entry!, cancellationToken).ConfigureAwait(false);
        await LoadOpeningAsync(cancellationToken).ConfigureAwait(false);
        await LoadUsageAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    /// <summary>Saves the entry.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="name">The entry's name.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect back to the entry, or the page with what went wrong.</returns>
    public async Task<IActionResult> OnPostAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, name))
        {
            return NotFound();
        }

        var outcome = await TextLibrary.SaveAsync(Shelf!.Folder, Entry!, Text ?? string.Empty, Version, cancellationToken)
            .ConfigureAwait(false);

        if (outcome == LibrarySave.Saved)
        {
            TempData[NoticeKey] = $"Saved. What it replaced is kept as {Entry}.txt.bak.";
            return Redirect(Shelves.PathOf(Shelf, Entry!));
        }

        (Error, Current, Version) = await RefusedAsync(Shelf.Folder, Entry!, outcome, cancellationToken).ConfigureAwait(false);
        await LoadOpeningAsync(cancellationToken).ConfigureAwait(false);
        await LoadUsageAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    /// <summary>Saves a character's opening.</summary>
    /// <param name="shelf">The shelf's slug; only characters have one here.</param>
    /// <param name="name">The character's name.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect back to the character, or the page with what went wrong.</returns>
    public async Task<IActionResult> OnPostOpeningAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, name) || !IsCharacter)
        {
            return NotFound();
        }

        var outcome = await TextLibrary.SaveAsync(library.Openings, Entry!, OpeningText ?? string.Empty, OpeningVersion, cancellationToken)
            .ConfigureAwait(false);

        if (outcome == LibrarySave.Saved)
        {
            TempData[NoticeKey] = $"Opening saved. What it replaced is kept as {Entry}.txt.bak on the openings shelf.";
            return Redirect(Shelves.PathOf(Shelf!, Entry!) + "#opening");
        }

        HasOpening = outcome != LibrarySave.Missing;
        (OpeningError, OpeningCurrent, OpeningVersion) = await RefusedAsync(library.Openings, Entry!, outcome, cancellationToken)
            .ConfigureAwait(false);
        (Text, Version) = await ReadAsync(Shelf!.Folder, Entry!, cancellationToken).ConfigureAwait(false);
        await LoadUsageAsync(cancellationToken).ConfigureAwait(false);
        return Page();
    }

    /// <summary>Gives a character an opening, from the template, named to match it.</summary>
    /// <param name="shelf">The shelf's slug; only characters have one here.</param>
    /// <param name="name">The character's name.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect back to the character, at its opening.</returns>
    public async Task<IActionResult> OnPostAddOpeningAsync(string shelf, string name, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, name) || !IsCharacter)
        {
            return NotFound();
        }

        // An opening already there is left alone: this only ever starts one.
        if (TextLibrary.Find(library.Openings, Entry) is null)
        {
            await TextLibrary.CreateAsync(library.Openings, Entry!, TextLibrary.OpeningSkeleton, cancellationToken)
                .ConfigureAwait(false);
        }

        return Redirect(Shelves.PathOf(Shelf!, Entry!) + "#opening");
    }

    /// <summary>Settles the shelf and the entry from the address; false when either is not there.</summary>
    private bool Pick(string shelf, string name)
    {
        Shelf = Shelves.Find(library, shelf);

        if (Shelf is null || TextLibrary.Find(Shelf.Folder, name) is not { } path)
        {
            return false;
        }

        Entry = Path.GetFileNameWithoutExtension(path);
        return true;
    }

    private static async Task<(string? Text, string? Version)> ReadAsync(string folder, string name, CancellationToken cancellationToken)
        => await TextLibrary.ReadAsync(folder, name, cancellationToken).ConfigureAwait(false) is { } text
            ? (text, TextLibrary.VersionOf(text))
            : (null, null);

    /// <summary>
    /// Why a save was refused, what the file holds now, and the version a second save would
    /// replace — so saving again is a decision made with both texts on the page.
    /// </summary>
    private static async Task<(string Error, string? Current, string? Version)> RefusedAsync(
        string folder,
        string name,
        LibrarySave outcome,
        CancellationToken cancellationToken)
    {
        if (outcome == LibrarySave.Missing)
        {
            return ("It is no longer on the shelf — deleted somewhere else. Your text is below, not saved; copy it before leaving.", null, null);
        }

        var (current, version) = await ReadAsync(folder, name, cancellationToken).ConfigureAwait(false);

        return (
            "It was changed somewhere else after you opened it, so nothing was saved. Your text is still in the box; "
            + "what the file says now is under it. Save again to replace that with yours — it will be kept as the .bak.",
            current,
            version);
    }

    private async Task LoadOpeningAsync(CancellationToken cancellationToken)
    {
        if (!IsCharacter)
        {
            return;
        }

        var (text, version) = await ReadAsync(library.Openings, Entry!, cancellationToken).ConfigureAwait(false);
        HasOpening = text is not null;
        OpeningText ??= text;
        OpeningVersion ??= version;
    }

    private async Task LoadUsageAsync(CancellationToken cancellationToken)
    {
        if (Shelf!.StoriesNameIt)
        {
            UsedBy = await conversations.ConversationsUsingAsync(Shelf.Slug == "personas", Entry!, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
