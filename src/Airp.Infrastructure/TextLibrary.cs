namespace Airp.Infrastructure;

/// <summary>
/// The character and persona descriptions kept as files on disk.
/// </summary>
/// <remarks>
/// <para>
/// A description is a page of prose that gets rewritten as a character is played in. That
/// belongs in a text file opened in a real editor, not escaped into a line of JSON — and it
/// makes the pair of them something that can be copied, backed up and read without this
/// application.
/// </para>
/// <para>
/// A conversation stores the <em>name</em> of what it uses, not a copy of the text. Editing a
/// character therefore reaches every conversation played with them, which is the point of
/// keeping them somewhere editable. A conversation that wants to stand apart holds its own
/// text instead, and nothing here touches it.
/// </para>
/// <para>
/// These files are the reader's own writing about their own scenes. They live under the
/// application directory rather than the repository, and <c>.gitignore</c> is not what should
/// be standing between them and a commit.
/// </para>
/// <para>
/// The API has a deliberate shape: the <em>instance</em> names the folders (so a root can be
/// injected, and tests never touch the real one), while the <em>operations</em> are static
/// functions over an explicit folder. An operation never guesses where it is working; the
/// caller always says, and the same operation serves all four shelves.
/// </para>
/// </remarks>
public sealed class TextLibrary
{
    private readonly string _root;

    /// <summary>Initialises the library.</summary>
    /// <param name="root">
    /// Directory holding the folders. Defaults to the application data directory; supplied
    /// explicitly by tests, which must not read or write the real one.
    /// </param>
    public TextLibrary(string? root = null) => _root = root ?? AppPaths.Root;

    /// <summary>
    /// Characters no entry may be named with, on any operating system.
    /// </summary>
    /// <remarks>
    /// The Windows set, applied everywhere, rather than
    /// <see cref="Path.GetInvalidFileNameChars"/> — which on Unix is only the separator and
    /// NUL. Taking the platform's answer would let a Linux machine create <c>con:trol.txt</c>
    /// that a Windows one then cannot open, and this library is a folder of text files people
    /// copy between machines and put in sync services. The stricter rule is the portable one,
    /// and being refused a colon is a smaller surprise than a file that only exists on one
    /// computer.
    /// </remarks>
    private static readonly char[] ReservedInNames = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    /// <summary>Folder holding character descriptions.</summary>
    public string Characters => Path.Combine(_root, "characters");

    /// <summary>Folder holding persona descriptions.</summary>
    public string Personas => Path.Combine(_root, "personas");

    /// <summary>Folder holding snippets — authored prose expanded into the composer.</summary>
    public string Snippets => Path.Combine(_root, "snippets");

    /// <summary>Folder holding opening messages, named after the character they belong to.</summary>
    public string Openings => Path.Combine(_root, "openings");

    /// <summary>
    /// Writes the worked example into the four shelves, skipping anything already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The skeletons a new file starts from say what the shape is; they cannot say what a good
    /// one reads like, because they are square brackets. This installs one that can be played
    /// immediately — a character, the persona facing it, an opening named to match, and a
    /// snippet — so the first question a new reader has is answered by something working rather
    /// than by prose about it.
    /// </para>
    /// <para>
    /// <strong>It never overwrites.</strong> A file the reader has since edited is theirs, and
    /// a command that quietly restored the shipped version would be a command nobody could run
    /// twice safely. Running it again after editing is a no-op, which is what makes it safe to
    /// suggest in a manual.
    /// </para>
    /// <para>
    /// Carried as embedded resources rather than as files beside the executable: a global tool
    /// is installed as a package, and anything next to the binary is a thing that can go
    /// missing.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Token used to abort the writes.</param>
    /// <returns>What was installed, as shelf-relative names, in the order written.</returns>
    public async Task<IReadOnlyList<string>> InstallSamplesAsync(CancellationToken cancellationToken = default)
    {
        EnsureCreated();

        var assembly = typeof(TextLibrary).Assembly;
        const string prefix = "Airp.Infrastructure.Samples.";
        var written = new List<string>();

        foreach (var resource in assembly.GetManifestResourceNames().Order(StringComparer.Ordinal))
        {
            if (!resource.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // "…Samples.characters.lighthouse.txt" — the shelf, then the file. The extension
            // carries a dot of its own, so the split is from the left and bounded.
            var parts = resource[prefix.Length..].Split('.');

            if (parts.Length != 3)
            {
                continue;
            }

            var folder = parts[0] switch
            {
                "characters" => Characters,
                "personas" => Personas,
                "openings" => Openings,
                "snippets" => Snippets,
                _ => null,
            };

            if (folder is null)
            {
                continue;
            }

            var name = parts[1] + "." + parts[2];
            var path = Path.Combine(folder, name);

            if (File.Exists(path))
            {
                continue;
            }

            await using var stream = assembly.GetManifestResourceStream(resource);

            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            written.Add(parts[0] + "/" + name);
        }

        return written;
    }

    /// <summary>Creates the folders if they are not there.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Characters);
        Directory.CreateDirectory(Personas);
        Directory.CreateDirectory(Snippets);
        Directory.CreateDirectory(Openings);
    }

    /// <summary>The names available in a folder, alphabetically.</summary>
    /// <remarks>
    /// A name starting with <c>_</c> is kept but not listed. A shelf holds working papers as
    /// well as things to play — a template, notes towards a character — and they belong beside
    /// the files they are about rather than in every picker that offers something to start.
    /// Resolution by name still finds them, so nothing is hidden from someone who asks for it.
    /// </remarks>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <returns>The names, without extensions.</returns>
    public static IReadOnlyList<string> Names(string folder)
        => Directory.Exists(folder)
            ? [.. Directory.GetFiles(folder, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(static n => !n.StartsWith('_'))
                .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>
    /// Settles which description a conversation uses, by one rule for both kinds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Characters and personas resolve identically, on purpose. They are the same shape of
    /// thing — a page of prose the reader wrote, kept in a folder, referred to by name — and
    /// an earlier version of this resolved them differently, with personas also readable from
    /// the configuration file. Two sources for one concept meant a name defined in both
    /// silently preferred the wrong one.
    /// </para>
    /// <para>
    /// The order is: text held by the conversation, then the named file, then the default.
    /// Text the conversation holds always wins — it was written for that story and cannot have
    /// been meant for another.
    /// </para>
    /// </remarks>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <param name="inline">Text held by the conversation itself, if any.</param>
    /// <param name="name">The name the conversation refers to, if any.</param>
    /// <param name="fallbackName">A name to fall back on when the conversation gives none.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The text to send, or <see langword="null"/> when there is none.</returns>
    public static async Task<string?> ResolveAsync(
        string folder,
        string? inline,
        string? name,
        string? fallbackName = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(inline))
        {
            return inline;
        }

        if (await ReadAsync(folder, name, cancellationToken).ConfigureAwait(false) is { } named)
        {
            return named;
        }

        // Three steps and no fourth. An earlier version also used the only file in the folder
        // when there was exactly one, which is convenient right up to the day a second file
        // appears and a conversation quietly changes who it is about.
        return await ReadAsync(folder, fallbackName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a description by name.</summary>
    /// <remarks>
    /// Names are matched case-insensitively and without the extension, because they are typed
    /// on a command line by someone who named the file themselves.
    /// </remarks>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <param name="name">The name, with or without <c>.txt</c>.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The text, or <see langword="null"/> when there is no such file.</returns>
    public static async Task<string?> ReadAsync(
        string folder,
        string? name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(folder))
        {
            return null;
        }

        var wanted = Path.GetFileNameWithoutExtension(name.Trim());

        var match = Directory.GetFiles(folder, "*.txt")
            .FirstOrDefault(f => string.Equals(
                Path.GetFileNameWithoutExtension(f),
                wanted,
                StringComparison.OrdinalIgnoreCase));

        return match is null
            ? null
            : await File.ReadAllTextAsync(match, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Finds the file a name refers to, or null when there is none.</summary>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <param name="name">The name, with or without <c>.txt</c>.</param>
    /// <returns>The full path, or <see langword="null"/>.</returns>
    public static string? Find(string folder, string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Directory.Exists(folder))
        {
            return null;
        }

        var wanted = Path.GetFileNameWithoutExtension(name.Trim());

        return Directory.GetFiles(folder, "*.txt")
            .FirstOrDefault(f => string.Equals(
                Path.GetFileNameWithoutExtension(f),
                wanted,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads the lines that tell a reader what an entry is, for a list to show.</summary>
    /// <remarks>
    /// A name alone says nothing to choose by. Template cards all open with the same
    /// boilerplate, so what distinguishes one from another starts under
    /// <c>=== THE WORLD ===</c>; files without that marker — openings, personas, snippets,
    /// hand-made cards — preview from the top. The preview runs to the next section header
    /// or the line budget, whichever arrives first.
    /// </remarks>
    /// <param name="path">The file, typically from <see cref="Find"/>.</param>
    /// <param name="maxLines">Most lines to return; a longer section ends in an ellipsis.</param>
    /// <returns>Up to <paramref name="maxLines"/> trimmed lines; empty when unreadable.</returns>
    public static IReadOnlyList<string> Preview(string path, int maxLines = 4)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var world = Array.FindIndex(lines, static l =>
            l.Trim().Equals("=== THE WORLD ===", StringComparison.OrdinalIgnoreCase));

        // FindIndex returns -1 when the marker is absent, so the skip lands on the top.
        var body = lines
            .Skip(world + 1)
            .SkipWhile(static l => string.IsNullOrWhiteSpace(l))
            .TakeWhile(static l => !l.TrimStart().StartsWith("=== ", StringComparison.Ordinal))
            .Select(static l => l.Trim())
            .ToList();

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        if (body.Count <= maxLines)
        {
            return body;
        }

        var kept = body.Take(maxLines).ToList();

        kept[^1] = kept[^1].Length == 0 ? "…" : kept[^1] + " …";

        return kept;
    }

    /// <summary>Creates a new entry, refusing to overwrite one that exists.</summary>
    /// <remarks>
    /// Refusal matters more than convenience here: these files are the reader's own writing,
    /// and "create" silently becoming "replace" would destroy a page of it over a reused name.
    /// </remarks>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <param name="name">The name; becomes the file name, so path characters are refused.</param>
    /// <param name="content">The initial text.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The full path of the created file.</returns>
    /// <exception cref="ArgumentException">The name is empty or not usable as a file name.</exception>
    /// <exception cref="InvalidOperationException">An entry with that name already exists.</exception>
    public static async Task<string> CreateAsync(
        string folder,
        string name,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Validated before GetFileNameWithoutExtension, not after: that call strips a
        // directory part silently, so "a/b" would sail through a later check and create a file
        // named "b" — an entry under a name nobody asked for.
        var given = name.Trim();

        if (given.IndexOfAny(ReservedInNames) >= 0 || given.Any(char.IsControl))
        {
            throw new ArgumentException($"'{name}' cannot be used as a file name.", nameof(name));
        }

        var trimmed = Path.GetFileNameWithoutExtension(given);

        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"'{name}' cannot be used as a file name.", nameof(name));
        }

        if (Find(folder, trimmed) is { } existing)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileNameWithoutExtension(existing)}' already exists. Edit it, or pick another name.");
        }

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, trimmed + ".txt");
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        return path;
    }

    /// <summary>Deletes an entry by name.</summary>
    /// <param name="folder">One of <see cref="Characters"/> or <see cref="Personas"/>.</param>
    /// <param name="name">The name, with or without <c>.txt</c>.</param>
    /// <returns>The path that was deleted, or <see langword="null"/> when nothing matched.</returns>
    public static string? Delete(string folder, string name)
    {
        if (Find(folder, name) is not { } path)
        {
            return null;
        }

        File.Delete(path);
        return path;
    }

    /// <summary>What a new character file starts as.</summary>
    /// <remarks>
    /// The skeleton carries the two lessons that were measured rather than guessed: concrete
    /// prose over ritual language, and the fail-safe written as a procedure — every card that
    /// phrased it as a prohibition still broke it.
    /// </remarks>
    public const string CharacterSkeleton =
        """
        You are the narrator and every character of the world described below. You are not a
        single person: you play the whole setting and everyone in it.

        === THE WORLD ===

        [ Name of the place. One or two paragraphs: what it is, when and where it is set, what
        kind of story happens here, and what the tone is. Write it as a place a reader can
        arrive at, not as a synopsis. ]

        === WHO PLAYS WHOM ===

        You write and act for: [ every character below, by name ]
        You never write or act for: the user.

        If a moment requires assuming what the user wants, does, or says, stop there and hand
        the scene back to them.

        === THE CHARACTERS ===

        [ One block per character who actually appears. Name, a line of voice, what they want,
        what they will not do. Concrete behaviour over adjectives. ]

        """;

    /// <summary>What a new persona file starts as.</summary>
    public const string PersonaSkeleton =
        """
        [ Who you are in the scene: name, age, what you look like, how you carry yourself, and
        whatever the characters would notice first. A few hundred words is plenty — the three
        that worked ran 400 to 560 tokens each. ]

        """;

    /// <summary>What a new snippet file starts as.</summary>
    public const string SnippetSkeleton =
        """
        [ Authored prose, deployed on demand: in the composer, type a colon and the start of
        this file's name, press Tab, and this text replaces the trigger — editable before
        sending. Written once, used at the dramatically right moment. ]

        """;

    /// <summary>What a new opening file starts as.</summary>
    /// <remarks>
    /// Named after the character it belongs to — that filename match is the whole
    /// association the new-chat flow uses to pre-fill it.
    /// </remarks>
    public const string OpeningSkeleton =
        """
        [ The first message of the story, written by you. Name this file exactly like the
        character it opens for, and the new-chat flow offers it when that character is
        picked. A greeting where each character speaks once, in their own voice, establishes
        them better than paragraphs describing them. ]

        """;
}
