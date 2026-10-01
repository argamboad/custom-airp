using Airp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Airp.Web.Pages;

/// <summary>One shelf of the library: what is on it, and the way to a new one.</summary>
/// <remarks>
/// The terminal's <c>M</c>. New opens a draft (<see cref="LibraryNewModel"/>) rather than making
/// a file: nothing reaches the shelf until it is saved.
/// </remarks>
/// <param name="library">The shelves.</param>
public sealed class LibraryModel(TextLibrary library) : PageModel
{
    /// <summary>The shelves that have a tab.</summary>
    public IReadOnlyList<Shelf> Tabs { get; } = Shelves.Tabs(library);

    /// <summary>Openings with no character to belong to, listed under the characters.</summary>
    public IReadOnlyList<string> Orphans { get; private set; } = [];

    /// <summary>The shelf being shown.</summary>
    public Shelf? Shelf { get; private set; }

    /// <summary>What is on it, each with its first line to tell one from another.</summary>
    public IReadOnlyList<(string Name, string Teaser)> Entries { get; private set; } = [];

    /// <summary>Shows a shelf; characters when none is named.</summary>
    /// <param name="shelf">The shelf's slug.</param>
    /// <returns>The page, or not found for a shelf that does not exist.</returns>
    public IActionResult OnGet(string? shelf)
    {
        // Openings live on their characters' pages; the shelf of them is not a page of its own.
        if (string.Equals(shelf, "openings", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect("/library/characters");
        }

        return Load(shelf ?? "characters") ? Page() : NotFound();
    }

    private bool Load(string slug)
    {
        Shelf = Shelves.Find(library, slug);

        if (Shelf is null)
        {
            return false;
        }

        Entries = [.. TextLibrary.Names(Shelf.Folder).Select(n => (n, Teaser(n)))];
        Orphans = Shelf.Slug == "characters" ? Shelves.Orphans(library) : [];
        return true;
    }

    private string Teaser(string name)
        => TextLibrary.Find(Shelf!.Folder, name) is { } path
            ? TextLibrary.Preview(path, int.MaxValue).FirstOrDefault(static l => l.Length > 0) ?? string.Empty
            : string.Empty;
}
