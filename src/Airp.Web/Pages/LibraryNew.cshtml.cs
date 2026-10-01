using Airp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>
/// A new library entry as a draft: a name and a text box, started from the shelf's template or
/// from a copy of another entry, and written to the shelf only when it is saved.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is created by opening this page. A name with a template behind it is not an entry
/// yet, and a duplicate abandoned halfway would otherwise sit on the shelf as a second copy of a
/// card, offered in every picker, until someone noticed it.
/// </para>
/// <para>
/// A character's draft carries an opening box too, filled from the original's when duplicating;
/// left empty, no opening is written. Both are checked before either is written, so a refusal
/// never leaves half a character behind.
/// </para>
/// </remarks>
/// <param name="library">The shelves.</param>
public sealed class LibraryNewModel(TextLibrary library) : PageModel
{
    /// <summary>The shelf the entry goes on.</summary>
    public Shelf? Shelf { get; private set; }

    /// <summary>The entry this one is a copy of, as the file spells it; null for a new one.</summary>
    public string? From { get; private set; }

    /// <summary>What it will be called.</summary>
    [BindProperty]
    public string? Name { get; set; }

    /// <summary>Its text.</summary>
    [BindProperty]
    public string? Text { get; set; }

    /// <summary>For a character, its opening; empty for none.</summary>
    [BindProperty]
    public string? OpeningText { get; set; }

    /// <summary>Why it was not saved, when it was not.</summary>
    public string? Error { get; private set; }

    /// <summary>Whether this is a character, which carries an opening box.</summary>
    public bool IsCharacter => Shelf?.Slug == "characters";

    /// <summary>Opens a draft: the template, or a copy of <paramref name="from"/>.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="from">An entry on the same shelf to copy, or null.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The draft, or not found for a shelf or an original that is not there.</returns>
    public async Task<IActionResult> OnGetAsync(string shelf, string? from, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, from))
        {
            return NotFound();
        }

        if (From is null)
        {
            Text = Shelf!.Skeleton;
            OpeningText = IsCharacter ? TextLibrary.OpeningSkeleton : null;
            return Page();
        }

        Name = $"{From} copy";
        Text = await TextLibrary.ReadAsync(Shelf!.Folder, From, cancellationToken).ConfigureAwait(false);
        OpeningText = IsCharacter
            ? await TextLibrary.ReadAsync(library.Openings, From, cancellationToken).ConfigureAwait(false)
            : null;
        return Page();
    }

    /// <summary>Writes the entry, and a character's opening, then opens it.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="from">The original, kept in the address so a refused save still says what it copies.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect to the new entry, or the draft with what stopped it.</returns>
    public async Task<IActionResult> OnPostAsync(string shelf, string? from, CancellationToken cancellationToken)
    {
        if (!Pick(shelf, from))
        {
            return NotFound();
        }

        var name = Name?.Trim();
        var opening = OpeningText?.Trim().Length > 0 ? OpeningText.ReplaceLineEndings("\n") : null;

        if (string.IsNullOrEmpty(name))
        {
            Error = $"A new {Shelf!.Kind} needs a name.";
            return Page();
        }

        // Asked of both files before either is written: an opening refused after its card was
        // created would leave the character half made.
        if (TextLibrary.Find(Shelf!.Folder, name) is { } taken)
        {
            Error = $"'{Path.GetFileNameWithoutExtension(taken)}' already exists. Pick another name — nothing was saved.";
            return Page();
        }

        if (opening is not null && TextLibrary.Find(library.Openings, name) is not null)
        {
            Error = $"An opening called '{name}' is already on the shelf with no character, and would be this one's. "
                + "Pick another name, or empty the opening box to adopt it — nothing was saved.";
            return Page();
        }

        try
        {
            var path = await TextLibrary.CreateAsync(Shelf.Folder, name, (Text ?? string.Empty).ReplaceLineEndings("\n"), cancellationToken)
                .ConfigureAwait(false);
            var created = Path.GetFileNameWithoutExtension(path);

            if (opening is not null)
            {
                await TextLibrary.CreateAsync(library.Openings, created, opening, cancellationToken).ConfigureAwait(false);
            }

            TempData[LibraryEntryModel.NoticeKey] = From is null ? "Created." : $"Created, as a copy of {From}.";
            return Redirect(Shelves.PathOf(Shelf, created));
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message + " Nothing was saved.";
            return Page();
        }
    }

    /// <summary>Settles the shelf and the original; false when either is named and not there.</summary>
    private bool Pick(string shelf, string? from)
    {
        Shelf = Shelves.Find(library, shelf);

        if (Shelf is null || !Shelf.Tabbed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return true;
        }

        From = TextLibrary.Find(Shelf.Folder, from) is { } path ? Path.GetFileNameWithoutExtension(path) : null;
        return From is not null;
    }
}
