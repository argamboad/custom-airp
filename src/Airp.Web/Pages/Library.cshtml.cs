using Airp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>One shelf of the library: what is on it, and starting something new.</summary>
/// <remarks>
/// The terminal's <c>M</c>. A new entry starts from the same template the terminal's does and
/// opens straight in the editor, because a name with a template behind it is not finished yet.
/// </remarks>
/// <param name="library">The shelves.</param>
public sealed class LibraryModel(TextLibrary library) : PageModel
{
    /// <summary>All four shelves, for the tabs.</summary>
    public IReadOnlyList<Shelf> All { get; } = Shelves.All(library);

    /// <summary>The shelf being shown.</summary>
    public Shelf? Shelf { get; private set; }

    /// <summary>What is on it, each with its first line to tell one from another.</summary>
    public IReadOnlyList<(string Name, string Teaser)> Entries { get; private set; } = [];

    /// <summary>The name asked for a new entry, kept when it was refused.</summary>
    [BindProperty]
    public string? Name { get; set; }

    /// <summary>Why the new entry was not made, when it was not.</summary>
    public string? Error { get; private set; }

    /// <summary>Shows a shelf; characters when none is named.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <returns>The page, or not found for a shelf that does not exist.</returns>
    public IActionResult OnGet(string? shelf)
        => Load(shelf ?? "characters") ? Page() : NotFound();

    /// <summary>Starts a new entry from the shelf's template and opens it.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect to the new entry, or the shelf with the reason it was refused.</returns>
    public async Task<IActionResult> OnPostAsync(string? shelf, CancellationToken cancellationToken)
    {
        if (!Load(shelf ?? "characters"))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = $"A new {Shelf!.Kind} needs a name.";
            return Page();
        }

        try
        {
            var path = await TextLibrary.CreateAsync(Shelf!.Folder, Name, Shelf.Skeleton, cancellationToken)
                .ConfigureAwait(false);

            return Redirect(Shelves.PathOf(Shelf, Path.GetFileNameWithoutExtension(path)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Refused rather than replaced: these files are the reader's writing, and a reused
            // name must never quietly become an overwrite.
            Error = ex.Message;
            return Page();
        }
    }

    private bool Load(string slug)
    {
        Shelf = Shelves.Find(library, slug);

        if (Shelf is null)
        {
            return false;
        }

        Entries = [.. TextLibrary.Names(Shelf.Folder).Select(n => (n, Teaser(n)))];
        return true;
    }

    private string Teaser(string name)
        => TextLibrary.Find(Shelf!.Folder, name) is { } path
            ? TextLibrary.Preview(path, int.MaxValue).FirstOrDefault(static l => l.Length > 0) ?? string.Empty
            : string.Empty;
}
