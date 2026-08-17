using Spectre.Console;
using Spectre.Console.Rendering;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;

namespace Airp.Terminal.Views;

/// <summary>
/// The library, managed from inside the terminal: characters, personas, snippets and
/// openings on four shelves, with create, edit and remove on single keys.
/// </summary>
/// <remarks>
/// <para>
/// Editing still belongs to a real editor — this view launches it and waits, exactly like the
/// CLI verb, because the line drawn earlier holds: composers make messages, editors edit
/// files. What the view adds is not an editor but reach: the shelves are visible, and the
/// verbs are one key instead of a command line.
/// </para>
/// <para>
/// Removing checks who still uses the name, the same question the CLI asks, and puts the
/// answer in the confirmation — deleting a page of the reader's own writing deserves to show
/// its consequences first.
/// </para>
/// </remarks>
internal sealed class LibraryView : ViewBase
{
    private static readonly string[] ShelfNames = ["Characters", "Personas", "Snippets", "Openings"];

    /// <summary>Columns between the list and the description.</summary>
    private const int Gutter = 2;

    /// <summary>
    /// A source line at least this long was wrapped by the writer, so it flows into the next
    /// one; anything shorter was short on purpose — dialogue, a list — and keeps its own row.
    /// </summary>
    private const int WrapHint = 60;

    /// <summary>Enough source lines to fill any pane; the view then cuts to what fits.</summary>
    private const int MaxPreviewLines = 400;

    private readonly TextLibrary _library;
    private readonly LocalConversationProvider? _provider;
    private readonly Func<string, CancellationToken, Task> _editor;

    private int _shelf;
    private int _selected;
    private IReadOnlyList<string> _names = [];

    private (int Shelf, string Name, int Width)? _previewKey;
    private IReadOnlyList<string> _preview = [];
    private int _scroll;

    private bool _naming;
    private string _newName = string.Empty;

    /// <summary>Initialises the view over the library as it is on disk.</summary>
    /// <param name="library">The folders.</param>
    /// <param name="provider">Answers "who uses this name"; null when unavailable.</param>
    /// <param name="editor">Opens a file and completes when editing is done. Injectable for tests.</param>
    public LibraryView(
        TextLibrary library,
        LocalConversationProvider? provider = null,
        Func<string, CancellationToken, Task>? editor = null)
    {
        _library = library;
        _library.EnsureCreated();
        _provider = provider;
        _editor = editor ?? EditorLauncher.OpenAsync;
        Refresh();
    }

    /// <inheritdoc />
    public override string Title => "Library";

    /// <inheritdoc />
    public override KeyContext KeyContext => _naming ? KeyContext.Text : KeyContext.Navigation;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => _naming
        ?
        [
            new("Enter", "Create and edit"),
            new("Esc", "Cancel"),
        ]
        :
        [
            new("←→", "Shelf"),
            new("PgUp/PgDn", "Scroll"),
            new("N", "New"),
            new("Enter", "Edit"),
            new("Del", "Remove"),
            new("Esc", "Back"),
        ];

    private string Folder => _shelf switch
    {
        1 => _library.Personas,
        2 => _library.Snippets,
        3 => _library.Openings,
        _ => _library.Characters,
    };

    private string Kind => _shelf switch
    {
        1 => "persona",
        2 => "snippet",
        3 => "opening",
        _ => "character",
    };

    private string Skeleton => _shelf switch
    {
        1 => TextLibrary.PersonaSkeleton,
        2 => TextLibrary.SnippetSkeleton,
        3 => TextLibrary.OpeningSkeleton,
        _ => TextLibrary.CharacterSkeleton,
    };

    private void Refresh()
    {
        _names = TextLibrary.Names(Folder);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _names.Count - 1));

        // The file behind the selection may have just been edited; re-read on next render.
        _previewKey = null;
        _scroll = 0;
    }

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var rows = new List<IRenderable>();

        rows.Add(new Markup(string.Join(
            Draw.Literal("   ", theme.Muted),
            ShelfNames.Select((s, i) => i == _shelf
                ? $"[{theme.Accent.ToMarkup()}]{s}[/]"
                : Draw.Literal(s, theme.Muted)))));

        rows.Add(new Rule { Style = theme.Border });

        if (_naming)
        {
            rows.Add(new Markup(
                Draw.Literal($"New {Kind}: ", theme.Accent)
                + Draw.Literal(_newName, theme.Text)
                + Draw.Literal("▌", theme.Selection)));
        }

        if (_names.Count == 0 && !_naming)
        {
            rows.Add(new Markup(Draw.Literal($"No {Kind}s yet — N starts one.", theme.Muted)));
        }

        // Names on the left, what they are on the right: a quarter of the width is enough
        // for any file name, and the description is the part worth reading.
        var listWidth = Math.Clamp(context.Width / 4, 16, 40);
        var textWidth = Math.Max(20, context.Width - listWidth - Gutter);
        var height = Math.Max(3, context.Height - 7);

        if (!_naming && Selected is { } current)
        {
            if (_previewKey != (_shelf, current, textWidth))
            {
                _previewKey = (_shelf, current, textWidth);
                _preview = TextLibrary.Find(Folder, current) is { } path
                    ? Describe(TextLibrary.Preview(path, MaxPreviewLines), textWidth)
                    : [];
            }
        }
        else
        {
            _preview = [];
        }

        // One row goes to saying where you are, but only when there is somewhere else to be.
        var scrollable = _preview.Count > height;
        var pane = scrollable ? height - 1 : height;

        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _preview.Count - pane));

        var shown = _preview.Skip(_scroll).Take(pane).ToList();

        var description = shown
            .Select(p => new Markup(Draw.Literal(p.Length == 0 ? " " : p, theme.Muted)))
            .ToList<IRenderable>();

        if (scrollable)
        {
            description.Add(new Markup(Draw.Literal(
                $"{_scroll + 1}–{_scroll + shown.Count} of {_preview.Count}   PgUp/PgDn",
                theme.Border)));
        }

        var top = Math.Clamp(_selected - height / 2, 0, Math.Max(0, _names.Count - height));

        var list = new List<IRenderable>();

        foreach (var (name, index) in _names.Select(static (n, i) => (n, i)).Skip(top).Take(height))
        {
            list.Add(new Markup(index == _selected && !_naming
                ? $"[{theme.Selection.ToMarkup()}]{Markup.Escape(" " + name + " ")}[/]"
                : Draw.Literal(" " + name, theme.Text)));
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn { Width = listWidth, NoWrap = true, Padding = new Padding(0, 0, Gutter, 0) });
        grid.AddColumn(new GridColumn { Width = textWidth, Padding = new Padding(0, 0, 0, 0) });
        grid.AddRow(new Rows(list), new Rows(description));

        rows.Add(grid);

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override async ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        if (_naming)
        {
            return await HandleNamingAsync(stroke, cancellationToken).ConfigureAwait(false);
        }

        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ViewAction.Pop;

            case AppCommand.MoveLeft:
                _shelf = (_shelf + ShelfNames.Length - 1) % ShelfNames.Length;
                Refresh();
                return ViewAction.None;

            case AppCommand.MoveRight or AppCommand.Tab:
                _shelf = (_shelf + 1) % ShelfNames.Length;
                Refresh();
                return ViewAction.None;

            // A new entry starts at its first line, not where the last one was left.
            case AppCommand.MoveUp when _selected > 0:
                _selected--;
                _scroll = 0;
                return ViewAction.None;

            case AppCommand.MoveDown when _selected < _names.Count - 1:
                _selected++;
                _scroll = 0;
                return ViewAction.None;

            // Arrows move the selection, so paging scrolls what the selection is showing —
            // the same division the conversation makes between its messages and its text.
            case AppCommand.PageDown:
                _scroll += Math.Max(1, context.Height - 9);
                return ViewAction.None;

            case AppCommand.PageUp:
                _scroll = Math.Max(0, _scroll - Math.Max(1, context.Height - 9));
                return ViewAction.None;

            // The keymap resolves N to SearchNext outside a search, and this view has no
            // search to advance — here the key means what the footer says it means.
            case AppCommand.SearchNext:
                _naming = true;
                _newName = string.Empty;
                return ViewAction.None;

            case AppCommand.Accept or AppCommand.Edit when Selected is { } toEdit:
                return EditAction(TextLibrary.Find(Folder, toEdit)!, toEdit);

            case AppCommand.Delete when Selected is { } toRemove:
                return await RemoveAsync(toRemove, cancellationToken).ConfigureAwait(false);
        }

        return ViewAction.None;
    }

    private string? Selected
        => _selected >= 0 && _selected < _names.Count ? _names[_selected] : null;

    /// <summary>Turns the file's own lines into the rows a pane of this width would show.</summary>
    /// <remarks>
    /// The files are hard-wrapped for an editor, which leaves a ragged column of short lines
    /// in a pane three times that wide. Prose is rejoined so it wraps to the space it has;
    /// deliberately short lines are left alone. Wrapping happens here rather than in the
    /// renderer because a scroll position has to mean a row the reader can count.
    /// </remarks>
    private static IReadOnlyList<string> Describe(IReadOnlyList<string> lines, int width)
    {
        var paragraphs = new List<string>();
        var current = new List<string>();

        void Flush()
        {
            if (current.Count > 0)
            {
                paragraphs.Add(string.Join(' ', current));
                current.Clear();
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length == 0)
            {
                Flush();

                if (paragraphs.Count > 0 && paragraphs[^1].Length > 0)
                {
                    paragraphs.Add(string.Empty);
                }

                continue;
            }

            if (current.Count > 0 && lines[i - 1].Length < WrapHint)
            {
                Flush();
            }

            current.Add(lines[i]);
        }

        Flush();

        var rows = new List<string>();

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                rows.Add(string.Empty);
                continue;
            }

            rows.AddRange(Wrap(paragraph, width));
        }

        while (rows.Count > 0 && rows[^1].Length == 0)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return rows;
    }

    /// <summary>Breaks a paragraph on spaces into rows no wider than the pane.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var start = 0;

        while (start < text.Length)
        {
            var take = Math.Min(width, text.Length - start);

            if (start + take < text.Length)
            {
                var space = text.LastIndexOf(' ', start + take, take);

                // No space to break on means a word longer than the pane: cut it rather than
                // overflow, since an overflowing row would push the layout out of shape.
                if (space > start)
                {
                    take = space - start;
                }
            }

            yield return text.Substring(start, take).TrimEnd();

            start += take;

            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }
        }
    }

    private async ValueTask<ViewAction> HandleNamingAsync(KeyStroke stroke, CancellationToken cancellationToken)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _naming = false;
                return ViewAction.None;

            case AppCommand.DeleteBack when _newName.Length > 0:
                _newName = _newName[..^1];
                return ViewAction.None;

            case AppCommand.Accept or AppCommand.NewLine when _newName.Trim().Length > 0:
            {
                _naming = false;

                string path;

                try
                {
                    path = await TextLibrary.CreateAsync(Folder, _newName.Trim(), Skeleton, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return ViewAction.Status(ex.Message, StatusKind.Warning);
                }

                Refresh();
                _selected = _names.ToList().FindIndex(n =>
                    string.Equals(n, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase));

                return EditAction(path, Path.GetFileNameWithoutExtension(path));
            }
        }

        if (stroke.Character is not '\0' && !char.IsControl(stroke.Character))
        {
            _newName += stroke.Character;
        }

        return ViewAction.None;
    }

    private ViewAction EditAction(string path, string name)
        => ViewAction.Run($"Waiting for {EditorLauncher.Editor} — save and close to come back", async ct =>
        {
            await _editor(path, ct).ConfigureAwait(false);
            Refresh();
            return ViewAction.Status(
                $"Done. Every conversation naming '{name}' sees the change from its next turn.",
                StatusKind.Success);
        });

    private async ValueTask<ViewAction> RemoveAsync(string name, CancellationToken cancellationToken)
    {
        var used = _provider is not null && _shelf is not (2 or 3)
            ? await _provider.ConversationsUsingAsync(_shelf == 1, name, cancellationToken).ConfigureAwait(false)
            : [];

        var consequences = new List<string> { "The file is deleted. This cannot be undone." };

        consequences.AddRange(used.Count > 0
            ? [$"{used.Count} conversation(s) still use it and would fall back to the default:",
               .. used.Select(static c => "  " + c)]
            : [$"No live conversation uses this {Kind}."]);

        return ViewAction.Push(new ConfirmView(
            "Remove",
            $"Delete the {Kind} \"{name}\"?",
            consequences,
            "Delete",
            _ =>
            {
                TextLibrary.Delete(Folder, name);
                Refresh();
                return Task.FromResult(ViewAction.Status($"Removed '{name}'.", StatusKind.Success));
            }));
    }
}
