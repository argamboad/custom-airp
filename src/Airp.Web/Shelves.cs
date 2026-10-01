using Airp.Infrastructure;

namespace Airp.Web;

/// <summary>One of the library's four shelves, as the pages address it.</summary>
/// <param name="Slug">How it appears in an address: <c>characters</c>, <c>personas</c>, …</param>
/// <param name="Title">What a tab calls it.</param>
/// <param name="Kind">What one entry on it is called, in a sentence.</param>
/// <param name="Skeleton">What a new entry starts as — the same template the terminal uses.</param>
/// <param name="Folder">Where its files are.</param>
public sealed record Shelf(string Slug, string Title, string Kind, string Skeleton, string Folder)
{
    /// <summary>Whether stories name entries on this shelf, so deleting one can leave a story without it.</summary>
    /// <remarks>
    /// A story stores a character's and a persona's name and reads the file every turn. Snippets
    /// are expanded into a message as it is written, and an opening is copied into the story as
    /// its first turn, so neither is read again once used.
    /// </remarks>
    public bool StoriesNameIt => Slug is "characters" or "personas";
}

/// <summary>The four shelves, by the slug in an address.</summary>
public static class Shelves
{
    /// <summary>The shelves, in the order the terminal's library shows them.</summary>
    /// <param name="library">The library.</param>
    /// <returns>Characters, personas, snippets, openings.</returns>
    public static IReadOnlyList<Shelf> All(TextLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        return
        [
            new("characters", "Characters", "character", TextLibrary.CharacterSkeleton, library.Characters),
            new("personas", "Personas", "persona", TextLibrary.PersonaSkeleton, library.Personas),
            new("snippets", "Snippets", "snippet", TextLibrary.SnippetSkeleton, library.Snippets),
            new("openings", "Openings", "opening", TextLibrary.OpeningSkeleton, library.Openings),
        ];
    }

    /// <summary>The shelf an address names, or null for one that does not exist.</summary>
    /// <param name="library">The library.</param>
    /// <param name="slug">The slug from the address.</param>
    /// <returns>The shelf, or null.</returns>
    public static Shelf? Find(TextLibrary library, string? slug)
        => All(library).FirstOrDefault(s => string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>The address of an entry's page.</summary>
    /// <param name="shelf">Its shelf.</param>
    /// <param name="name">Its name.</param>
    /// <returns>A site-relative address.</returns>
    public static string PathOf(Shelf shelf, string name)
    {
        ArgumentNullException.ThrowIfNull(shelf);
        return $"/library/{shelf.Slug}/{Uri.EscapeDataString(name)}";
    }
}
