using Microsoft.Extensions.DependencyInjection;
using Airp.Application.Abstractions;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// The root screen: the account's chats on the left, a live preview on the right.
/// </summary>
/// <remarks>
/// Filtering happens against the in-memory cache on every keystroke, which is why the slash
/// key opens an inline field rather than a modal prompt — the list narrows as you type
/// without a round trip to the site.
/// </remarks>
internal sealed class ChatListView : ViewBase, IMouseAware
{
    private readonly IChatService _chats;
    private readonly IConversationService _conversations;
    private readonly IServiceProvider _services;
    private readonly ListState _list = new();
    private readonly TextInput _filter = new() { Placeholder = "type to filter…" };
    private readonly TextInput _rename = new() { Placeholder = "new name…" };

    private IReadOnlyList<Chat> _visible = [];
    private bool _filtering;
    private bool _renaming;
    private char _pendingVimKey;

    /// <summary>Initialises the view.</summary>
    /// <param name="chats">Chat cache.</param>
    /// <param name="conversations">Conversation access, for renaming and deleting chats.</param>
    /// <param name="services">Container used to construct child views.</param>
    public ChatListView(
        IChatService chats,
        IConversationService conversations,
        IServiceProvider services)
    {
        _chats = chats;
        _conversations = conversations;
        _services = services;
        _chats.Changed += OnChatsChanged;
        _visible = _chats.Cached;
        _list.SetCount(_visible.Count);
    }

    /// <inheritdoc />
    public override string Title => "Chats";

    /// <summary>Counts rows with a plural that reads like English.</summary>
    /// <param name="count">How many.</param>
    /// <returns>Text such as "1 chat" or "6 chats".</returns>
    private static string Count(int count) => $"{count} chat{(count == 1 ? string.Empty : "s")}";

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => _filtering
        ?
        [
            new("Enter", "Apply"),
            new("Esc", "Clear filter"),
        ]
        : _renaming
            ?
            [
                new("Enter", "Rename"),
                new("Esc", "Cancel"),
            ]
            :
            [
                new("Enter", "Open the chat"),
                new("N", "New chat"),
                new("M", "Library"),
                new("F2", "Rename"),
                new("Del", "Delete chat"),
                new("R", "Refresh"),
                new("/", "Filter"),
                new("Ctrl+F", "Search all"),
                new("Q", "Quit"),
            ];

    /// <inheritdoc />
    public override KeyContext KeyContext =>
        _filtering || _renaming ? KeyContext.Text : KeyContext.Navigation;

    /// <summary>The chat under the cursor, or <see langword="null"/> when the list is empty.</summary>
    public Chat? Selected => _list.Selected >= 0 && _list.Selected < _visible.Count
        ? _visible[_list.Selected]
        : null;

    /// <inheritdoc />
    public override ValueTask<ViewAction> OnActivatedAsync(CancellationToken cancellationToken)
    {
        ApplyFilter();

        if (_chats.Cached.Count > 0)
        {
            return ValueTask.FromResult(ViewAction.None);
        }

        return ValueTask.FromResult(ViewAction.Run("Loading chats", async ct =>
        {
            await _chats.GetAsync(ct).ConfigureAwait(false);
            ApplyFilter();
            return ViewAction.Status($"{Count(_visible.Count)} loaded.", StatusKind.Success);
        }));
    }

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        // Three tenths to the list and the rest to the preview. The list holds names and an
        // age, both short; the preview holds a reply, which is prose and needs the width to be
        // read at all. The old six-tenths spent the room on the column with nothing in it.
        var (leftWidth, rightWidth) = Draw.SplitWidths(context.Width, 0.3, minLeft: 28, minRight: 24);

        var rows = new List<IRenderable>
        {
            new Markup(_renaming
                ? Draw.Literal("Rename: ", theme.Accent) + _rename.ToMarkup(theme)
                : _filtering
                ? Draw.Literal("Filter: ", theme.Accent) + _filter.ToMarkup(theme)
                : Draw.Literal(
                    Draw.Pad(
                        _filter.IsEmpty
                            ? Count(_visible.Count)
                            : $"{_visible.Count} of {_chats.Cached.Count} matching \"{_filter.Value}\"",
                        leftWidth),
                    theme.Muted.Combine(theme.Surface))),

            // The rule is inside the pane, so it is tinted too. Left on the terminal's own
            // ground it cut a bare stripe across the column three rows from the top.
            new Rule { Style = theme.Border.Combine(theme.Surface) },
        };

        if (_visible.Count == 0)
        {
            rows.Add(new Markup(Draw.Literal(
                _chats.Cached.Count == 0
                    ? "No chats yet. Press R to refresh."
                    : "Nothing matches this filter. Press Esc to clear it.",
                theme.Muted)));
        }
        else
        {
            var viewportHeight = Math.Max(1, context.Height - 2);
            var (start, length) = _list.Viewport(viewportHeight);

            for (var i = start; i < Math.Min(_visible.Count, start + length); i++)
            {
                rows.Add(new Markup(RenderRow(_visible[i], i == _list.Selected, leftWidth, theme)));
            }
        }

        // The list is a pane and the preview is the page beside it, so the list carries the
        // surface tone down its whole height and the preview does not.
        return Draw.Split(
            Draw.Pane(rows, leftWidth, context.Height, theme),
            RenderPreview(context, rightWidth),
            leftWidth,
            rightWidth,
            theme.Border);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        if (_filtering)
        {
            return ValueTask.FromResult(HandleFilterKey(stroke));
        }

        if (_renaming)
        {
            return ValueTask.FromResult(HandleRenameKey(stroke));
        }

        // Vim's gg needs a pending-key state; everything else is a single press.
        if (_pendingVimKey == 'g')
        {
            _pendingVimKey = '\0';
            if (stroke is { Command: AppCommand.Character, Character: 'g' })
            {
                _list.SelectFirst();
                return ValueTask.FromResult(ViewAction.None);
            }
        }

        switch (stroke.Command)
        {
            case AppCommand.MoveUp:
                _list.Move(-1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown:
                _list.Move(1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageUp:
                _list.Move(-Math.Max(1, context.Height - 3));
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageDown:
                _list.Move(Math.Max(1, context.Height - 3));
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Home:
                _list.SelectFirst();
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.End:
                _list.SelectLast();
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Search:
                _filtering = true;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Accept or AppCommand.MoveRight when Selected is { } open:
                return ValueTask.FromResult(ViewAction.Push(Detail(open)));

            // Seeded with the current name so a small correction does not mean retyping it.
            case AppCommand.Rename when Selected is { } toRename:
                _renaming = true;
                _rename.Value = toRename.Name;
                return ValueTask.FromResult(ViewAction.Status("Edit the name, then press Enter."));

            case AppCommand.Delete when Selected is { } toDelete:
                return ValueTask.FromResult(ConfirmDelete(toDelete));

            case AppCommand.Refresh:
                return ValueTask.FromResult(ViewAction.Run("Refreshing", async ct =>
                {
                    var chats = await _chats.RefreshAsync(ct).ConfigureAwait(false);
                    ApplyFilter();
                    return ViewAction.Status($"{Count(chats.Count)} loaded.", StatusKind.Success);
                }));

            case AppCommand.Back:
                if (_filter.IsEmpty)
                {
                    return ValueTask.FromResult(ViewAction.Quit);
                }

                _filter.Clear();
                ApplyFilter();
                return ValueTask.FromResult(ViewAction.Status("Filter cleared."));

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            // The keymap resolves N to SearchNext everywhere; this list has no search to
            // advance, so here the key means what the footer says: a new chat. The first
            // wiring matched on Character and never fired once — pinned by a test now.
            case AppCommand.SearchNext:
                return ValueTask.FromResult(ViewAction.Push(
                    ActivatorUtilities.CreateInstance<NewChatView>(_services)));

            case AppCommand.Character when stroke.Character is 'm' or 'M':
                return ValueTask.FromResult(ViewAction.Push(
                    ActivatorUtilities.CreateInstance<LibraryView>(_services)));

            case AppCommand.Character when stroke.Character == 'g':
                _pendingVimKey = 'g';
                return ValueTask.FromResult(ViewAction.None);
        }

        return ValueTask.FromResult(ViewAction.None);
    }

    /// <inheritdoc />
    public ViewAction OnClick(int row, RenderContext context)
    {
        // Two rows of chrome sit above the list inside this view's own body.
        var index = _list.IndexAtRow(row - 2, Math.Max(1, context.Height - 2));
        if (index < 0)
        {
            return ViewAction.None;
        }

        if (index == _list.Selected && Selected is { } selected)
        {
            return ViewAction.Push(Detail(selected));
        }

        _list.Select(index);
        return ViewAction.None;
    }

    /// <summary>
    /// Handles keys while a chat is being renamed in place.
    /// </summary>
    /// <remarks>
    /// The name is edited inline rather than in a dialog, for the same reason the filter is:
    /// it is one short string and a whole screen for it would be ceremony.
    /// </remarks>
    /// <param name="stroke">The key.</param>
    /// <returns>The resulting action.</returns>
    private ViewAction HandleRenameKey(KeyStroke stroke)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _renaming = false;
                _rename.Clear();
                return ViewAction.Status("Rename cancelled.");

            case AppCommand.Accept or AppCommand.NewLine:
            {
                var name = _rename.Value.Trim();
                var target = Selected;

                _renaming = false;
                _rename.Clear();

                if (target is null)
                {
                    return ViewAction.None;
                }

                if (name.Length == 0)
                {
                    // The site refuses a blank name rather than clearing the custom one, so
                    // sending it would only produce an error.
                    return ViewAction.Status("A chat needs a name. Nothing was changed.", StatusKind.Warning);
                }

                if (string.Equals(name, target.Name, StringComparison.Ordinal))
                {
                    return ViewAction.Status("That is already its name.");
                }

                return ViewAction.Run("Renaming", async ct =>
                {
                    await _conversations.RenameConversationAsync(target.Id, name, ct).ConfigureAwait(false);

                    // Re-read so the row shows the name the site actually stored, and so the
                    // cached list stops carrying the old one.
                    await _chats.RefreshAsync(ct).ConfigureAwait(false);
                    ApplyFilter();

                    return ViewAction.Status($"Renamed to \"{name}\".", StatusKind.Success);
                });
            }
        }

        _rename.Handle(stroke);
        return ViewAction.None;
    }

    /// <summary>
    /// Asks before deleting a whole conversation.
    /// </summary>
    /// <remarks>
    /// This removes every message in it from the account, not merely from this client, and
    /// there is no undo — so the confirmation names the chat rather than asking abstractly.
    /// </remarks>
    /// <param name="chat">The conversation that would go.</param>
    /// <returns>The action that asks.</returns>
    private ViewAction ConfirmDelete(Chat chat)
        => ViewAction.Push(new ConfirmView(
            "Delete",
            $"Delete the chat \"{chat.Name}\"?",
            [
                "The whole conversation goes, every message in it, from the account itself.",
                "This cannot be undone.",
            ],
            "Delete",
            async ct =>
            {
                await _conversations.DeleteConversationAsync(chat.Id, ct).ConfigureAwait(false);
                await _chats.RefreshAsync(ct).ConfigureAwait(false);
                ApplyFilter();

                return ViewAction.Status($"Deleted \"{chat.Name}\".", StatusKind.Success);
            }));

    private ViewAction HandleFilterKey(KeyStroke stroke)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _filtering = false;
                _filter.Clear();
                ApplyFilter();
                return ViewAction.Status("Filter cleared.");

            case AppCommand.Accept or AppCommand.NewLine:
                _filtering = false;
                return ViewAction.None;

            case AppCommand.MoveUp:
                _list.Move(-1);
                return ViewAction.None;

            case AppCommand.MoveDown:
                _list.Move(1);
                return ViewAction.None;
        }

        if (_filter.Handle(stroke))
        {
            ApplyFilter();
        }

        return ViewAction.None;
    }

    /// <summary>
    /// Draws one chat: whether it is unread, its name, and how long since it last moved.
    /// </summary>
    /// <remarks>
    /// A chat has no lifecycle to report — no draft, no error, nothing generating — so the
    /// column that once carried one is gone and its width goes to the name instead.
    /// </remarks>
    /// <param name="chat">The chat.</param>
    /// <param name="selected">Whether the cursor is on it.</param>
    /// <param name="width">Columns available.</param>
    /// <param name="theme">Active palette.</param>
    /// <returns>The rendered row.</returns>
    private string RenderRow(Chat chat, bool selected, int width, Theme theme)
    {
        var unread = chat.IsUnread ? "●" : " ";
        var age = Draw.Age(chat.LastMessageAtUtc);
        var nameWidth = Math.Max(8, width - 4 - 10);

        var line = $"{(selected ? '>' : ' ')} {unread} "
                   + Draw.Pad(chat.Name, nameWidth)
                   + " " + Draw.Pad(age, 9);

        if (selected)
        {
            return Draw.Literal(line, theme.Selection);
        }

        // The row is already padded to the column, so tinting its style tints the whole width
        // of it — which is what makes the rows and the blank space under them one surface.
        var style = (chat.IsUnread ? theme.Accent : theme.Text).Combine(theme.Surface);

        return _filter.IsEmpty
            ? Draw.Literal(line, style)
            : Draw.Highlight(line, _filter.Value, style, theme.Highlight);
    }

    private IRenderable RenderPreview(RenderContext context, int width)
    {
        var theme = context.Theme;

        if (Selected is not { } chat)
        {
            return new Markup(Draw.Literal("Nothing selected.", theme.Muted));
        }

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal(chat.Name, theme.Heading)),
            new Rule { Style = theme.Border },
        };

        if (!string.IsNullOrWhiteSpace(chat.Speaker))
        {
            rows.Add(new Markup(Draw.Literal("with " + chat.Speaker, theme.Accent)));
            rows.Add(new Text(string.Empty));
        }

        rows.Add(new Markup(Draw.Literal("Latest message", theme.Muted)));
        rows.Add(new Rule { Style = theme.Border });

        var preview = chat.LatestMessage;
        if (string.IsNullOrWhiteSpace(preview))
        {
            rows.Add(new Markup(Draw.Literal(
                "Nothing said yet — press Enter to open this chat.",
                theme.Muted)));
        }
        else
        {
            // Drawn the way the transcript draws it — actions dimmed, quotation marks gone —
            // because a reader recognising a reply at a glance is the whole point of drawing
            // them, and half of that is lost if the preview shows the raw markers instead.
            var available = Math.Max(1, context.Height - rows.Count - 2);
            var drawn = new List<string>();

            foreach (var line in preview.Split('\n'))
            {
                var formatted = Application.Text.ProseFormat.Format(line);

                foreach (var (start, segment) in Draw.WrapSegments(formatted.Text, Math.Max(10, width)))
                {
                    drawn.Add(Draw.Prose(formatted, start, segment, theme.Text, theme.Action));

                    if (drawn.Count > available)
                    {
                        break;
                    }
                }

                if (drawn.Count > available)
                {
                    break;
                }
            }

            // The preview is for recognising a chat, not for reading it, so it stops where the
            // pane does — but it says so. A reply cut at the pane's edge with nothing to mark
            // it reads as a reply that ended there.
            var cut = drawn.Count > available;
            var shown = cut ? Math.Max(0, available - 1) : available;

            foreach (var markup in drawn.Take(shown))
            {
                rows.Add(new Markup(markup));
            }

            if (cut)
            {
                rows.Add(new Markup(Draw.Literal("…", theme.Muted)));
            }
        }

        return new Rows(rows);
    }

    private void ApplyFilter()
    {
        _visible = _filter.IsEmpty ? _chats.Cached : _chats.Filter(_filter.Value);
        _list.SetCount(_visible.Count);
    }

    private void OnChatsChanged(object? sender, IReadOnlyList<Chat> chats) => ApplyFilter();

    /// <summary>Opens the selected chat.</summary>
    /// <param name="chat">The selected row.</param>
    /// <returns>The view to push.</returns>
    private IView Detail(Chat chat) => RowView.For(chat, _services);
}
