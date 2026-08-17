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

    private readonly TextLibrary _library;
    private readonly LocalConversationProvider? _provider;
    private readonly Func<string, CancellationToken, Task> _editor;

    private int _shelf;
    private int _selected;
    private IReadOnlyList<string> _names = [];

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

        var height = Math.Max(3, context.Height - 7);
        var top = Math.Clamp(_selected - height / 2, 0, Math.Max(0, _names.Count - height));

        foreach (var (name, index) in _names.Select(static (n, i) => (n, i)).Skip(top).Take(height))
        {
            rows.Add(new Markup(index == _selected && !_naming
                ? $"[{theme.Selection.ToMarkup()}]{Markup.Escape(" " + name + " ")}[/]"
                : Draw.Literal(" " + name, theme.Text)));
        }

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

            case AppCommand.MoveUp when _selected > 0:
                _selected--;
                return ViewAction.None;

            case AppCommand.MoveDown when _selected < _names.Count - 1:
                _selected++;
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
