using Airp.Application.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// A conversation transcript, navigated message by message.
/// </summary>
/// <remarks>
/// <para>
/// The organising idea is that a chat is a sequence of turns, not a wall of prose. The
/// cursor moves between <em>messages</em> rather than lines, each turn is labelled and
/// coloured by who wrote it, and the selected turn is marked down its left edge so you can
/// always see where you are in a long scroll.
/// </para>
/// <para>
/// Everything is wrapped to the pane, because these messages run to thousands of characters
/// and truncating them loses exactly the content the reader came for.
/// </para>
/// </remarks>
internal sealed partial class ConversationView : ViewBase
{
    private readonly Chat _conversation;
    private readonly IConversationService _conversations;
    private readonly IClipboardService _clipboard;
    private readonly IExportService _export;
    private readonly TextInput _search = new() { Placeholder = "search this conversation…" };

    private readonly Application.Text.TextDocument _composer = Application.Text.TextDocument.FromText(string.Empty);

    /// <summary>
    /// Scroll sentinel meaning "show the selected message from its first line".
    /// </summary>
    /// <remarks>
    /// Set when the view lands somewhere deliberately — opening a conversation, or a reply
    /// arriving — and resolved on the next render, once the wrapped rows are known and the
    /// message's first row can actually be located.
    /// </remarks>
    private const int PinToSelection = int.MaxValue / 2;

    private IReadOnlyList<ChatMessage> _messages = [];
    private int _selected;
    private int _scroll;
    private bool _searching;
    private bool _showData;
    private bool _composing;
    private string _activeQuery = string.Empty;
    private readonly PendingStatus _pending = new();

    /// <summary>
    /// What this story has cost so far, or null before it has been read.
    /// </summary>
    /// <remarks>
    /// Read when the conversation opens and again after anything that spends, rather than on
    /// every render: it is a query, and the transcript redraws on every keystroke.
    /// </remarks>
    private Airp.Infrastructure.Providers.ConversationSpend? _spent;

    /// <summary>How many completions the composer offers at once.</summary>
    private const int SuggestionLimit = 7;

    /// <summary>One offer in the composer's completion strip.</summary>
    /// <param name="Display">What the strip shows.</param>
    /// <param name="Insert">What replaces the typed token when it is accepted.</param>
    private readonly record struct Completion(string Display, string Insert);

    private IReadOnlyList<Completion> _suggestions = [];
    private int _suggestion;

    // The span the suggestions would replace, captured when they were computed. Held rather
    // than re-derived on accept: the two would have to agree exactly, and a disagreement
    // would rewrite the wrong run of the user's message.
    private int _tokenLine;
    private int _tokenStart;
    private int _tokenLength;

    /// <summary>Initialises the view.</summary>
    /// <param name="conversation">The conversation row this was opened from.</param>
    /// <param name="conversations">Transcript access.</param>
    /// <param name="clipboard">Clipboard access.</param>
    /// <param name="export">Export renderer.</param>
    /// <param name="library">The four shelves, for snippets and for showing a card.</param>
    /// <param name="provider">
    /// The local store, when this conversation is on it. Optional because the terminal is
    /// written against the provider seam and a future backend would have none of this; the
    /// commands that need it say so rather than failing.
    /// </param>
    /// <param name="options">Application options, for the default persona a pane has to resolve.</param>
    public ConversationView(
        Chat conversation,
        IConversationService conversations,
        IClipboardService clipboard,
        IExportService export,
        Airp.Infrastructure.TextLibrary? library = null,
        Airp.Infrastructure.Providers.LocalConversationProvider? provider = null,
        Microsoft.Extensions.Options.IOptionsMonitor<Application.Options.AirpOptions>? options = null)
    {
        _library = library ?? new Airp.Infrastructure.TextLibrary();
        _conversation = conversation;
        _conversations = conversations;
        _clipboard = clipboard;
        _export = export;
        _provider = provider;
        _options = options;
    }

    /// <inheritdoc />
    public override string Title => _conversation.Name;

    /// <inheritdoc />
    public override KeyContext KeyContext =>
        _searching || _composing ? KeyContext.Text : KeyContext.Navigation;

    /// <inheritdoc />
    public override bool Reserves(AppCommand command) => command == AppCommand.GlobalSearch;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => _searching
        ?
        [
            new("Enter", "Find"),
            new("Esc", "Cancel"),
        ]
        : _composing
            ?
            [
                new("Enter", "Send"),
                new("Alt+Enter", "New line"),
                new("Tab", "Complete word"),
                new(":name", "Emoji"),
                new("Esc", "Cancel"),
            ]
            :
            [
                new("I / Enter", "Write a message"),
                new("↑↓", "Previous / next"),
                new("PgUp/PgDn", "Scroll"),
                new("/", "Search"),
                new(">", "Carry on"),
                new("G", "Regenerate reply"),
                new("S", "Settings"),
                new("Del", "Delete from here"),
                new("C", "Copy"),
                new("X", "Export"),
                new("R", "Refresh"),
                new("Esc", "Back"),
            ];

    /// <summary>The message under the cursor.</summary>
    private ChatMessage? Selected =>
        _selected >= 0 && _selected < Visible.Count ? Visible[_selected] : null;

    private IReadOnlyList<ChatMessage> Visible =>
        _showData ? _messages : [.. _messages.Where(static m => m.IsDialogue)];

    /// <inheritdoc />
    public override ValueTask<ViewAction> OnActivatedAsync(CancellationToken cancellationToken)
    {
        if (_messages.Count > 0)
        {
            return ValueTask.FromResult(ViewAction.None);
        }

        return ValueTask.FromResult(Load(forceRefresh: false));
    }

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var visible = Visible;

        var rows = new List<IRenderable> { new Markup(BuildHeader(context, visible)), new Rule { Style = theme.Border } };

        // The composer occupies the bottom of the pane while it is open, so the transcript
        // shrinks rather than being covered. It is laid out before the early exits below,
        // because an empty conversation is exactly when you most need to write the first
        // message.
        //
        // Its height follows the wrapped text rather than the line count, so a long message
        // grows the pane instead of running off the edge of it — up to half the window,
        // beyond which the composer would crowd out the conversation being replied to.
        List<ComposerRow> composer = _composing ? BuildComposerRows(ComposerWidth(context)) : [];
        var composerRows = _composing
            ? Math.Clamp(composer.Count + 2, 3, Math.Max(3, Math.Min(12, context.Height / 2)))
            : 0;

        // The suggestion strip takes its row from the transcript rather than from the
        // composer: it appears mid-sentence, and stealing a row from the text being written
        // would make the draft jump under the caret as the list opened and closed.
        var suggestionRows = _composing && _suggestions.Count > 0 ? 1 : 0;
        var available = Math.Max(1, context.Height - 2 - composerRows - suggestionRows);

        if (visible.Count == 0)
        {
            rows.Add(new Markup(Draw.Literal(
                _composing
                    ? "No messages yet — this will start the conversation."
                    : "This conversation has no messages yet. Press I to write one.",
                theme.Muted)));
        }
        else
        {
            var display = BuildDisplayRows(context, visible);

            KeepSelectionVisible(display, available);
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, display.Count - available));

            foreach (var row in display.Skip(_scroll).Take(available))
            {
                rows.Add(new Markup(row.Markup));
            }
        }

        if (_composing)
        {
            rows.Add(new Rule { Style = theme.Border });
            rows.AddRange(RenderComposer(context, composer, composerRows - 1));

            if (suggestionRows > 0)
            {
                rows.Add(new Markup(BuildSuggestionStrip(context)) { Overflow = Overflow.Ellipsis });
            }
        }

        return new Rows(rows);
    }

    /// <summary>
    /// Draws the emoji suggestions as a single strip beneath the composer.
    /// </summary>
    /// <remarks>
    /// One row rather than a stacked list, because the composer is already competing with the
    /// conversation for a small pane and seven names cost seven rows of the transcript the
    /// message is a reply to. A strip fits the same choices into the space of one.
    /// </remarks>
    /// <param name="context">Layout context.</param>
    /// <returns>The strip as markup.</returns>
    private string BuildSuggestionStrip(RenderContext context)
    {
        const string Hint = "   Tab inserts · ↑↓ chooses · Esc dismisses";

        var theme = context.Theme;
        var parts = new List<string> { Draw.Literal("  ", theme.Muted) };

        // Entries are measured in columns as they are added and dropped once the row is full.
        // The hint keeps its space throughout: the list is discoverable only if the keys that
        // drive it stay on screen, so it is the suggestions that give way, not the legend.
        var budget = Math.Max(0, context.Width - 2 - Draw.Width(Hint));
        var used = 0;

        for (var i = 0; i < _suggestions.Count; i++)
        {
            var label = $" {_suggestions[i].Display} ";
            var cells = Draw.Width(label);

            // The highlighted entry is never dropped — Tab has to be aimed at something the
            // user can see.
            if (used + cells > budget && i != _suggestion)
            {
                continue;
            }

            used += cells;
            parts.Add(Draw.Literal(label, i == _suggestion ? theme.Selection : theme.Muted));
        }

        parts.Add(Draw.Literal(Hint, theme.Muted));
        return string.Concat(parts);
    }

    /// <summary>Columns available to the composer's text, leaving room for the caret.</summary>
    /// <param name="context">Layout context.</param>
    /// <returns>The wrapping width.</returns>
    private static int ComposerWidth(RenderContext context) => Math.Max(20, context.Width - 4);

    /// <summary>
    /// Length of the draft as the site will receive it, counted without building the string.
    /// </summary>
    /// <remarks>
    /// The document holds lines, so its own character count omits the separators between
    /// them. Those are real chats in the message and are counted here, since this
    /// number is what a limit is judged against.
    /// </remarks>
    /// <returns>The chat count including line breaks.</returns>
    private int ComposedLength() => _composer.CharacterCount + Math.Max(0, _composer.LineCount - 1);

    /// <summary>One drawn row of the composer.</summary>
    /// <param name="Text">The row's text.</param>
    /// <param name="Caret">Column the caret sits at, or -1 when it is on another row.</param>
    private readonly record struct ComposerRow(string Text, int Caret);

    /// <summary>
    /// Wraps the draft into the rows that will be drawn, marking where the caret falls.
    /// </summary>
    /// <remarks>
    /// The composer used to draw one row per logical line and truncate it to the pane, so a
    /// message longer than the window width typed itself off the right edge and out of
    /// sight. Wrapping here — rather than letting the console do it — is what allows the
    /// caret to be placed on the correct visual row.
    /// </remarks>
    /// <param name="width">Columns available for text.</param>
    /// <returns>The rows, in order.</returns>
    private List<ComposerRow> BuildComposerRows(int width)
    {
        var rows = new List<ComposerRow>();
        var lastRowOfCursorLine = 0;
        var caretPlaced = false;

        for (var line = 0; line < _composer.LineCount; line++)
        {
            var segments = Draw.WrapSegments(_composer.Lines[line], width);

            for (var s = 0; s < segments.Count; s++)
            {
                var (start, text) = segments[s];
                var caret = -1;

                if (line == _composer.CursorLine)
                {
                    lastRowOfCursorLine = rows.Count;
                    var column = _composer.CursorColumn - start;

                    // One past the end of a segment belongs to the next row, except on the
                    // last one, where it is where the next character will go.
                    if (column >= 0 && (column < text.Length || (s == segments.Count - 1 && column == text.Length)))
                    {
                        caret = column;
                        caretPlaced = true;
                    }
                }

                rows.Add(new ComposerRow(text, caret));
            }
        }

        // A caret resting on the whitespace a wrap consumed belongs to no segment at all.
        // Showing it at the end of the line it is on beats not showing it.
        if (!caretPlaced && rows.Count > 0)
        {
            rows[lastRowOfCursorLine] = rows[lastRowOfCursorLine] with { Caret = rows[lastRowOfCursorLine].Text.Length };
        }

        return rows;
    }

    /// <summary>Draws the composer pane with a visible caret.</summary>
    /// <param name="context">Layout context.</param>
    /// <param name="composer">The wrapped composer rows.</param>
    /// <param name="height">Rows available to the composer, including its label.</param>
    /// <returns>The composer's rows.</returns>
    private IEnumerable<IRenderable> RenderComposer(
        RenderContext context,
        IReadOnlyList<ComposerRow> composer,
        int height)
    {
        var theme = context.Theme;

        var limit = context.Options.MessageCharacterLimit;
        var length = ComposedLength();
        var over = limit > 0 && length > limit;

        yield return new Markup(
            Draw.Literal("You  ", theme.Accent)
            + (limit > 0
                ? Draw.Literal($"{length:N0}/{limit:N0} characters", over ? theme.Error : theme.Muted)
                : Draw.Literal($"{length:N0} characters", theme.Muted))
            + Draw.Literal($" · {_composer.WordCount} words · ", theme.Muted)
            + Draw.Literal(
                over ? "too long to send — shorten it first" : "Enter sends · Alt+Enter for a new line",
                over ? theme.Error : theme.Muted));

        var textRows = Math.Max(1, height - 1);

        // Scroll the draft so the caret is always the last thing on screen. Whatever else
        // gets hidden, it is never the words being typed.
        var caretRow = Math.Max(0, IndexOfCaret(composer));
        var start = Math.Clamp(caretRow - textRows + 1, 0, Math.Max(0, composer.Count - textRows));

        for (var i = 0; i < textRows; i++)
        {
            var index = start + i;

            if (index >= composer.Count)
            {
                yield return new Markup(string.Empty);
                continue;
            }

            var row = composer[index];

            if (row.Caret < 0)
            {
                yield return new Markup(Draw.Literal("  " + row.Text, theme.Text));
                continue;
            }

            // The caret covers the whole cluster it rests on, so an emoji under the cursor is
            // highlighted as one character rather than split between two styled runs.
            var column = Application.Text.Graphemes.Snap(row.Text, Math.Clamp(row.Caret, 0, row.Text.Length));
            var after = Application.Text.Graphemes.Next(row.Text, column);
            var head = row.Text[..column];
            var caret = column < row.Text.Length ? row.Text[column..after] : " ";
            var tail = column < row.Text.Length ? row.Text[after..] : string.Empty;

            yield return new Markup(
                Draw.Literal("  " + head, theme.Text)
                + Draw.Literal(caret, theme.Selection)
                + Draw.Literal(tail, theme.Text));
        }
    }

    private static int IndexOfCaret(IReadOnlyList<ComposerRow> composer)
    {
        for (var i = 0; i < composer.Count; i++)
        {
            if (composer[i].Caret >= 0)
            {
                return i;
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        if (_searching)
        {
            return ValueTask.FromResult(HandleSearchKey(stroke));
        }

        if (_composing)
        {
            return ValueTask.FromResult(HandleComposerKey(stroke, context));
        }

        var visible = Visible;

        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            // `i` is handled here rather than in the key map because the map is global and
            // the prompt editor's vim mode needs `i` to keep arriving as a raw chat.
            case AppCommand.Edit or AppCommand.Accept:
            case AppCommand.Character when stroke.Character is 'i' or 'I':
                _composing = true;
                return ValueTask.FromResult(ViewAction.Status("Type your message. Enter sends it."));

            case AppCommand.MoveDown:
                Move(1, visible);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveUp:
                Move(-1, visible);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageDown:
                _scroll += Math.Max(1, context.Height - 3);
                SyncSelectionToScroll(context, visible);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageUp:
                _scroll = Math.Max(0, _scroll - Math.Max(1, context.Height - 3));
                SyncSelectionToScroll(context, visible);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Home:
                _selected = 0;
                _scroll = 0;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.End:
                _selected = Math.Max(0, visible.Count - 1);
                _scroll = PinToSelection;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Search or AppCommand.GlobalSearch:
                _searching = true;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.SearchNext:
                return ValueTask.FromResult(StepMatch(1, visible));

            case AppCommand.SearchPrevious:
                return ValueTask.FromResult(StepMatch(-1, visible));

            case AppCommand.ToggleLineNumbers:
                _showData = !_showData;
                _selected = 0;
                _scroll = 0;
                return ValueTask.FromResult(ViewAction.Status(
                    _showData ? "Showing non-dialogue payloads." : "Hiding non-dialogue payloads."));

            case AppCommand.Refresh:
                return ValueTask.FromResult(Load(forceRefresh: true));

            case AppCommand.Settings:
                return ValueTask.FromResult(ViewAction.Push(
                    new ChatSettingsView(_conversations, _conversation.Id, _conversation.Name)));

            // The site offers this on the newest reply only, so the cursor's position does
            // not choose the target — the view says which reply it will replace.
            case AppCommand.Generate when _messages.LastOrDefault(static m => m.Role == ChatRole.Assistant) is { } reply:
                return ValueTask.FromResult(ViewAction.Push(new RegenerateView(
                    _conversations,
                    _conversation.Id,
                    reply,
                    transcript =>
                    {
                        // Replaced, not merged: the reply that was there is gone, and merging
                        // would keep both wordings side by side.
                        _messages = transcript;
                        _selected = Math.Max(0, Visible.Count - 1);
                        _scroll = PinToSelection;

                        // The one action that both spends and throws something away, so it is
                        // the one after which the header would be most misleading if left.
                        return ViewAction.Run("Refreshing", async ct =>
                        {
                            await RefreshSpendAsync(ct).ConfigureAwait(false);
                            return ViewAction.Status("A new reply arrived.", StatusKind.Success);
                        });
                    })));

            case AppCommand.Generate:
                return ValueTask.FromResult(ViewAction.Status(
                    "There is no reply to regenerate yet.",
                    StatusKind.Warning));

            // `>` rather than a letter, because that is the icon the site puts on it and the
            // shift makes it hard to hit by accident — this spends credits.
            case AppCommand.Character when stroke.Character is '>':
                return ValueTask.FromResult(Continue());

            case AppCommand.Delete when Selected is { } doomed:
                return ValueTask.FromResult(ConfirmDeleteFrom(doomed));

            case AppCommand.Delete:
                return ValueTask.FromResult(
                    ViewAction.Status("Select a message first.", StatusKind.Warning));

            case AppCommand.Copy when Selected is { } message && _clipboard.IsAvailable:
                return ValueTask.FromResult(ViewAction.Run("Copying", async ct =>
                    await _clipboard.CopyAsync(message.Text, ct).ConfigureAwait(false)
                        ? ViewAction.Status("Message copied to the clipboard.", StatusKind.Success)
                        : ViewAction.Status("The clipboard rejected the copy.", StatusKind.Warning)));

            case AppCommand.Copy:
                return ValueTask.FromResult(
                    ViewAction.Status("No clipboard is available in this environment.", StatusKind.Warning));

            case AppCommand.Export:
                return ValueTask.FromResult(ViewAction.Push(new ExportView(
                    _export,
                    _clipboard,
                    new ConversationTranscript
                    {
                        ConversationId = _conversation.Id,
                        Title = _conversation.Name,
                        Speaker = _conversation.Speaker,
                        Messages = visible,
                    },
                    $"transcript-{_conversation.Name}")));

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    private ViewAction Load(bool forceRefresh)
        => ViewAction.Run("Loading conversation", async ct =>
        {
            _messages = await _conversations
                .GetMessagesAsync(_conversation.Id, forceRefresh, ct)
                .ConfigureAwait(false);

            // Open on the newest turn: that is where the conversation actually is.
            _selected = Math.Max(0, Visible.Count - 1);
            _scroll = PinToSelection;

            await RefreshSpendAsync(ct).ConfigureAwait(false);

            var replies = _messages.Count(static m => m.Role == ChatRole.Assistant);
            var yours = _messages.Count(static m => m.Role == ChatRole.User);

            return ViewAction.Status($"{yours} from you, {replies} replies.", StatusKind.Success);
        });

    /// <summary>
    /// Re-reads what this conversation has cost.
    /// </summary>
    /// <remarks>
    /// Never allowed to break the screen it decorates. A ledger that cannot be read is a header
    /// without a figure on it, not a conversation the reader cannot open — which is why the
    /// failure is swallowed rather than reported.
    /// </remarks>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>A task that completes once the figure is current, or given up on.</returns>
    private async Task RefreshSpendAsync(CancellationToken cancellationToken)
    {
        if (_provider is null)
        {
            return;
        }

        try
        {
            var report = await _provider
                .SpendAsync(conversationId: _conversation.Id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _spent = report.Conversations.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _spent = null;
        }
    }

    private void Move(int delta, IReadOnlyList<ChatMessage> visible)
    {
        if (visible.Count == 0)
        {
            return;
        }

        _selected = Math.Clamp(_selected + delta, 0, visible.Count - 1);
    }

    /// <summary>
    /// Handles a key while the composer has focus.
    /// </summary>
    /// <remarks>
    /// Enter sends and Alt+Enter inserts a newline, which is the convention every chat
    /// client uses — the common case should be one keystroke.
    /// </remarks>
    /// <param name="stroke">The resolved key.</param>
    /// <returns>What the shell should do next.</returns>
    private ViewAction HandleComposerKey(KeyStroke stroke, RenderContext context)
    {
        var alt = (stroke.Key.Modifiers & ConsoleModifiers.Alt) != 0;

        // Keys the suggestion list claims while it is open. Enter is deliberately not among
        // them: it sends, it spends credits, and a key that usually sends must not quietly
        // mean something else because a popup happens to be showing.
        if (_suggestions.Count > 0)
        {
            switch (stroke.Command)
            {
                case AppCommand.Tab:
                    return AcceptSuggestion();

                case AppCommand.MoveUp:
                    _suggestion = (_suggestion - 1 + _suggestions.Count) % _suggestions.Count;
                    return ViewAction.None;

                case AppCommand.MoveDown:
                    _suggestion = (_suggestion + 1) % _suggestions.Count;
                    return ViewAction.None;

                // Esc dismisses the list first and leaves the composer only on a second press,
                // so escaping a popup never also discards the writing behind it.
                case AppCommand.Back:
                    _suggestions = [];
                    return ViewAction.None;
            }
        }

        var action = ApplyComposerKey(stroke, context, alt);

        // Recomputed after every key rather than only after the ones that obviously matter,
        // because what opens and closes the list is where the caret ended up, and that changes
        // on movement and undo just as much as on typing.
        RefreshSuggestions();
        return action;
    }

    /// <summary>Applies a composer key that the suggestion list did not claim.</summary>
    /// <param name="stroke">The resolved key.</param>
    /// <param name="context">Layout context.</param>
    /// <param name="alt">Whether Alt was held.</param>
    /// <returns>What the shell should do next.</returns>
    private ViewAction ApplyComposerKey(KeyStroke stroke, RenderContext context, bool alt)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _composing = false;
                return _composer.Text.Length > 0
                    ? ViewAction.Status("Draft kept. Press I to carry on writing.")
                    : ViewAction.None;

            // A line break that arrived inside pasted text is part of the message, not an
            // instruction to send it. Getting this wrong sends half of whatever was pasted,
            // which costs credits and cannot be taken back.
            case AppCommand.NewLine or AppCommand.Accept when alt || stroke.Pasted:
                _composer.InsertNewLine();
                return ViewAction.None;

            case AppCommand.NewLine or AppCommand.Accept:
                return Send(context);

            case AppCommand.Character:
                _composer.InsertText(stroke.Character.ToString());
                return stroke.Character == ':' ? SubstituteClosedShortcode() : ViewAction.None;

            case AppCommand.DeleteBack:
                _composer.Backspace();
                return ViewAction.None;

            case AppCommand.DeleteForward:
                _composer.DeleteForward();
                return ViewAction.None;

            case AppCommand.MoveLeft:
                _composer.MoveLeft();
                return ViewAction.None;

            case AppCommand.MoveRight:
                _composer.MoveRight();
                return ViewAction.None;

            case AppCommand.MoveUp:
                _composer.MoveVertical(-1);
                return ViewAction.None;

            case AppCommand.MoveDown:
                _composer.MoveVertical(1);
                return ViewAction.None;

            case AppCommand.Home:
                _composer.MoveToLineStart();
                return ViewAction.None;

            case AppCommand.End:
                _composer.MoveToLineEnd();
                return ViewAction.None;

            case AppCommand.Undo:
                _composer.Undo();
                return ViewAction.None;

            case AppCommand.Tab:
                _composer.InsertText("  ");
                return ViewAction.None;

            default:
                return ViewAction.None;
        }
    }

    // ------------------------------------------------------------ emoji completion

    /// <summary>
    /// Recomputes the completions for wherever the caret now is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds share one strip: emoji names after a colon, and dictionary words while
    /// writing prose. A colon token wins outright — the letters after one are a shortcode
    /// name, and offering English words for them would complete <c>:smi</c> to <c>smile</c>
    /// as text rather than as the emoji the colon asked for.
    /// </para>
    /// <para>
    /// The selected index resets to the top on every recompute. Keeping it would mean a
    /// keystroke that reorders the list silently re-aims Tab at something else.
    /// </para>
    /// </remarks>
    private void RefreshSuggestions()
    {
        _suggestions = [];

        if (!_composing)
        {
            return;
        }

        var line = _composer.Lines[_composer.CursorLine];
        var column = _composer.CursorColumn;

        // A command name being typed wins over everything, and only in the one place a command
        // can start: the very first character of the message. Anywhere else a slash is
        // punctuation — and/or, a date, a closing tag — and a rail that opened on those would
        // be in the way constantly.
        if (CommandBeingTyped(line, column) is { } typed)
        {
            Offer(
                0,
                typed.Length + 1,
                [.. Application.Text.SlashCommands
                    .Matching(typed)
                    .Take(SuggestionLimit)
                    .Select(static c => new Completion($"/{c.Name}", $"/{c.Name} "))]);

            return;
        }

        if (Application.Text.ShortcodeScanner.At(line, column) is { } shortcode)
        {
            var emoji = Application.Text.EmojiShortcodes.Suggest(shortcode.Query, SuggestionLimit);

            // Snippets share the colon trigger with emoji on purpose: one rail, one muscle
            // memory. A snippet inserts a page where an emoji inserts a glyph, so snippets
            // are offered first when the query matches one.
            var snippets = SnippetNames()
                .Where(n => n.StartsWith(shortcode.Query, StringComparison.OrdinalIgnoreCase))
                .Take(SuggestionLimit)
                .Select(static n => new Completion($"» {n}", $"snippet:{n}"))
                .ToList();

            Offer(
                shortcode.Start,
                shortcode.Length,
                [.. snippets, .. emoji.Select(static e => new Completion($"{e.Emoji} {e.Name}", e.Emoji))]);

            return;
        }

        if (Application.Text.WordList.TokenAt(line, column) is { } word)
        {
            var words = Application.Text.WordList.Suggest(word.Prefix, SuggestionLimit);

            Offer(
                word.Start,
                word.Length,
                [.. words.Select(w => new Completion(w, Application.Text.WordList.MatchCase(word.Prefix, w)))]);
        }
    }

    /// <summary>
    /// The command name being typed, when the caret is inside one.
    /// </summary>
    /// <remarks>
    /// Only on the first line, only from the first column, and only while no space has been
    /// typed yet: past the space the reader is writing the argument, and offering command names
    /// for the words of a question would put a Tab away from rewriting them.
    /// </remarks>
    /// <param name="line">The line the caret is on.</param>
    /// <param name="column">The caret's column.</param>
    /// <returns>What has been typed after the slash, or <see langword="null"/>.</returns>
    private string? CommandBeingTyped(string line, int column)
    {
        if (_composer.CursorLine != 0 || line.Length == 0 || line[0] != '/' || column < 1)
        {
            return null;
        }

        // A doubled slash is the escape for prose, not the start of a command.
        if (line.Length > 1 && line[1] == '/')
        {
            return null;
        }

        var typed = line[1..Math.Min(column, line.Length)];

        return typed.Any(char.IsWhiteSpace) ? null : typed;
    }

    /// <summary>Puts a set of completions on offer for a span of the current line.</summary>
    /// <param name="start">Where the token being completed begins.</param>
    /// <param name="length">How long it is.</param>
    /// <param name="completions">What to offer; an empty set closes the strip.</param>
    private void Offer(int start, int length, IReadOnlyList<Completion> completions)
    {
        if (completions.Count == 0)
        {
            _suggestions = [];
            return;
        }

        _tokenLine = _composer.CursorLine;
        _tokenStart = start;
        _tokenLength = length;
        _suggestions = completions;
        _suggestion = 0;
    }

    /// <summary>Replaces the typed token with the highlighted completion.</summary>
    /// <returns>What the shell should do next.</returns>
    private ViewAction AcceptSuggestion()
    {
        if (_suggestions.Count == 0)
        {
            return ViewAction.None;
        }

        var chosen = _suggestions[Math.Clamp(_suggestion, 0, _suggestions.Count - 1)];

        if (chosen.Insert.StartsWith("snippet:", StringComparison.Ordinal))
        {
            return ExpandSnippet(chosen.Insert["snippet:".Length..]);
        }

        _composer.ReplaceRange(_tokenLine, _tokenStart, _tokenLength, chosen.Insert);
        _suggestions = [];

        return ViewAction.None;
    }

    /// <summary>Names on the snippet shelf, read once per view.</summary>
    /// <remarks>
    /// Cached because this runs on every keystroke of an open token. A snippet added while a
    /// conversation is open appears after reopening it, which is the same freshness the
    /// character picker has.
    /// </remarks>
    private IReadOnlyList<string> SnippetNames()
        => _snippetNames ??= Airp.Infrastructure.TextLibrary.Names(SnippetsFolder);

    private IReadOnlyList<string>? _snippetNames;

    private readonly Airp.Infrastructure.TextLibrary _library;
    private readonly Airp.Infrastructure.Providers.LocalConversationProvider? _provider;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Application.Options.AirpOptions>? _options;

    private string SnippetsFolder => _library.Snippets;

    /// <summary>
    /// Replaces the typed trigger with the snippet's full text, still editable in the
    /// composer.
    /// </summary>
    /// <remarks>
    /// The token is removed first and the text inserted at the cursor, because the text is a
    /// page with its own line breaks and a range replacement is a one-line operation. Nothing
    /// downstream knows an expansion happened: what is sent and stored is whatever the
    /// composer holds when Enter lands, same as typing it by hand.
    /// </remarks>
    private ViewAction ExpandSnippet(string name)
    {
        // A synchronous read on purpose, not a blocked async one: this runs on the input
        // loop for a file measured in kilobytes, and Find + ReadAllText is the honest way to
        // say that.
        var text = Airp.Infrastructure.TextLibrary.Find(SnippetsFolder, name) is { } path
            ? File.ReadAllText(path).TrimEnd()
            : null;

        _suggestions = [];

        if (string.IsNullOrEmpty(text))
        {
            return ViewAction.Status($"The snippet '{name}' is empty or gone.", StatusKind.Warning);
        }

        _composer.ReplaceRange(_tokenLine, _tokenStart, _tokenLength, string.Empty);
        _composer.InsertText(text);

        return ViewAction.Status($"Expanded :{name} — still editable before sending.");
    }

    /// <summary>
    /// Swaps a fully typed <c>:name:</c> for its emoji the moment the closing colon lands.
    /// </summary>
    /// <returns>What the shell should do next.</returns>
    private ViewAction SubstituteClosedShortcode()
    {
        var line = _composer.Lines[_composer.CursorLine];

        if (Application.Text.ShortcodeScanner.Closed(line, _composer.CursorColumn) is not { } closed)
        {
            return ViewAction.None;
        }

        var (token, emoji) = closed;
        _composer.ReplaceRange(_composer.CursorLine, token.Start, token.Length, emoji);
        _suggestions = [];

        return ViewAction.Status($"Inserted {emoji}  :{token.Query}:");
    }

    /// <summary>
    /// Sends the composed message and waits for the reply.
    /// </summary>
    /// <remarks>
    /// The draft is cleared only once the send succeeds. A failure keeps what was typed,
    /// because losing a written message to a transient network error is unforgivable.
    /// </remarks>
    /// <returns>The action that performs the send.</returns>
    private ViewAction Send(RenderContext context)
    {
        var parsed = Application.Text.SlashCommands.Parse(_composer.Text);

        if (parsed.Kind != Application.Text.SlashParseKind.Message)
        {
            return Dispatch(parsed, context);
        }

        return Send(context, parsed.Text, instruction: null);
    }

    /// <summary>
    /// Sends a message, optionally under a direction for the reply it asks for.
    /// </summary>
    /// <param name="context">The render context, for the limits.</param>
    /// <param name="text">The message. Already parsed, so a leading double slash is gone.</param>
    /// <param name="instruction">
    /// A direction for the reply, routed to the prompt's instruction layer. It steers the turn
    /// without becoming part of it — nothing of it is stored as something the reader said.
    /// </param>
    /// <returns>The action that performs the send.</returns>
    private ViewAction Send(RenderContext context, string text, string? instruction)
    {
        if (text.Length == 0)
        {
            return ViewAction.Status("Nothing to send.", StatusKind.Warning);
        }

        // Checked here rather than downstream, so nothing ever takes the beginning of a
        // message and drops the rest without complaint — a truncated message costs exactly
        // what the whole one would have.
        var limit = context.Options.MessageCharacterLimit;
        if (limit > 0 && text.Length > limit)
        {
            return ViewAction.Status(
                $"That message is {text.Length:N0} characters and the limit is {limit:N0}. "
                + $"Remove {text.Length - limit:N0} and send again — nothing has been sent.",
                StatusKind.Warning);
        }

        _composing = false;

        return ViewAction.Run("Sending", async ct =>
        {
            _pending.Begin();

            // Replies, not messages. The transcript gains what was just typed as well, so
            // counting everything new would greet a failed generation with "reply received"
            // and the reader's own words as the thing received.
            var before = Visible.Count(static m => m.Role == ChatRole.Assistant);

            // Cleared in a finally: a send that throws leaves the shell showing an error, and
            // a progress line still counting up behind it would claim the work is going on.
            IReadOnlyList<ChatMessage> updated;
            try
            {
                updated = await _conversations
                    .SendAsync(
                        _conversation.Id,
                        text,
                        instruction: instruction,
                        progress: _pending,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SendPhase.IsSubmitted(_pending.Phase))
            {
                // Stopped after the site confirmed it took the message, so this is not an
                // un-send: the reply is still being written somewhere and will be there on the
                // next refresh. The draft stays put — typed text lost is unrecoverable where a
                // duplicate needs a deliberate second Enter — but saying nothing here is how
                // that second Enter gets pressed.
                return ViewAction.Status(
                    "Stopped waiting. Your message was already sent — press R to refresh for the "
                    + "reply rather than sending it again.",
                    StatusKind.Warning);
            }
            catch (ReplyMissingException ex)
            {
                // Handled here rather than left to the shell's error banner, because this is
                // not a send that failed: the message went. So it is treated as one that
                // went — merged in, draft cleared — and only the missing reply is reported.
                // Leaving the text in the composer after the site has already accepted it is
                // how the same message gets sent, and paid for, twice.
                Accept(ex.Partial);

                return ViewAction.Status(
                    "Sent, but the chat generated no reply. Your message is on the site — press R to "
                    + "refresh in case the reply lands late rather than sending it again.",
                    StatusKind.Warning);
            }
            finally
            {
                _pending.Clear();
            }

            Accept(updated);
            await RefreshSpendAsync(ct).ConfigureAwait(false);

            var added = Visible.Count(static m => m.Role == ChatRole.Assistant) - before;
            return ViewAction.Status(
                added > 0 ? $"Reply received ({added} new message(s))." : "Sent, but no reply came back.",
                added > 0 ? StatusKind.Success : StatusKind.Warning);
        });
    }

    /// <summary>
    /// Takes what a send observed into the transcript on screen and clears the draft.
    /// </summary>
    /// <remarks>
    /// Merged rather than assigned. What is on screen is the conversation the reader has been
    /// reading, and sending a message can only ever add to it — so however little the send
    /// managed to see, it cannot take the transcript away.
    /// </remarks>
    /// <param name="observed">The turns the send saw, which may be only the sent message.</param>
    private void Accept(IReadOnlyList<ChatMessage> observed)
    {
        _messages = ChatTranscript.Merge(_messages, observed);

        _composer.SetText(string.Empty);
        _composer.MarkSaved();

        // Land on the newest turn, which is the reply just received — or, when none was, the
        // message that is waiting for one.
        _selected = Math.Max(0, Visible.Count - 1);
        _scroll = PinToSelection;
    }

    /// <summary>
    /// Lets the chat carry on from its last reply, with nothing from you.
    /// </summary>
    /// <remarks>
    /// No confirmation, deliberately: the site does this on a single click too, and a
    /// prompt before every continuation would make the one thing this is for — reading on —
    /// tedious. The status line says plainly that it costs credits.
    /// </remarks>
    /// <returns>The action that continues.</returns>
    private ViewAction Continue()
    {
        if (!_messages.Any(static m => m.Role == ChatRole.Assistant))
        {
            return ViewAction.Status("There is no reply to carry on from yet.", StatusKind.Warning);
        }

        return ViewAction.Run("Continuing", async ct =>
        {
            _pending.Begin();
            var before = _messages.LastOrDefault(static m => m.Role == ChatRole.Assistant)?.Text.Length ?? 0;

            try
            {
                _messages = await _conversations
                    .ContinueAsync(_conversation.Id, instruction: null, progress: _pending, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _pending.Clear();
            }

            _selected = Math.Max(0, Visible.Count - 1);
            _scroll = PinToSelection;

            await RefreshSpendAsync(ct).ConfigureAwait(false);

            var after = _messages.LastOrDefault(static m => m.Role == ChatRole.Assistant)?.Text.Length ?? 0;

            return ViewAction.Status(
                after > before
                    ? $"The chat carried on — {after - before:N0} more characters."
                    : "The chat carried on.",
                StatusKind.Success);
        });
    }

    /// <summary>
    /// Asks before removing the selected message and everything after it.
    /// </summary>
    /// <remarks>
    /// The count is taken over the whole transcript rather than the visible list, because
    /// hidden non-dialogue turns are deleted too and a confirmation that undercounts is
    /// worse than none. The site's own wording — that this cannot be undone — is repeated
    /// here rather than softened.
    /// </remarks>
    /// <param name="target">The first message that would be removed.</param>
    /// <returns>The action that asks.</returns>
    private ViewAction ConfirmDeleteFrom(ChatMessage target)
    {
        var index = IndexOf(target);
        if (index < 0)
        {
            return ViewAction.Status("That message is no longer in the transcript.", StatusKind.Warning);
        }

        var doomed = _messages.Count - index;
        var hidden = doomed - Visible.Skip(_selected).Count();
        var preview = Draw.Fit(target.Text.Replace('\n', ' '), 60);

        return ViewAction.Push(new ConfirmView(
            "Delete",
            $"Delete this message and the {doomed - 1} after it?",
            [
                $"From: {preview}",
                string.Empty,
                $"{doomed} message(s) would be removed from the conversation itself, not just "
                + "from this client."
                + (hidden > 0 ? $" {hidden} of them are not shown in this view." : string.Empty),
                "This cannot be undone.",
                string.Empty,
                $"{index} message(s) would remain.",
            ],
            "Delete",
            async ct =>
            {
                // Replace rather than merge: these messages are meant to be gone, and the
                // merge that protects a short read would put every one of them back.
                _messages = await _conversations
                    .DeleteFromAsync(_conversation.Id, target.Id, ct)
                    .ConfigureAwait(false);

                _selected = Math.Max(0, Visible.Count - 1);
                _scroll = PinToSelection;

                return ViewAction.Status(
                    $"Deleted {doomed} message(s). {_messages.Count} remain.",
                    StatusKind.Success);
            }));
    }

    private int IndexOf(ChatMessage message)
    {
        for (var i = 0; i < _messages.Count; i++)
        {
            if (string.Equals(_messages[i].Id, message.Id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private ViewAction HandleSearchKey(KeyStroke stroke)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _searching = false;
                _search.Clear();
                _activeQuery = string.Empty;
                return ViewAction.None;

            case AppCommand.Accept or AppCommand.NewLine:
            {
                _searching = false;
                _activeQuery = _search.Value;

                var hits = Visible.Count(m => m.Text.Contains(_activeQuery, StringComparison.OrdinalIgnoreCase));
                if (hits == 0)
                {
                    return ViewAction.Status($"\"{_activeQuery}\" is not in this conversation.", StatusKind.Warning);
                }

                StepMatch(1, Visible);
                return ViewAction.Status($"{hits} message(s) match.", StatusKind.Success);
            }
        }

        _search.Handle(stroke);
        return ViewAction.None;
    }

    private ViewAction StepMatch(int delta, IReadOnlyList<ChatMessage> visible)
    {
        if (_activeQuery.Length == 0 || visible.Count == 0)
        {
            return ViewAction.Status("No active search.", StatusKind.Warning);
        }

        for (var offset = 1; offset <= visible.Count; offset++)
        {
            var index = ((_selected + (delta * offset)) % visible.Count + visible.Count) % visible.Count;
            if (visible[index].Text.Contains(_activeQuery, StringComparison.OrdinalIgnoreCase))
            {
                _selected = index;
                return ViewAction.None;
            }
        }

        return ViewAction.Status("No further matches.", StatusKind.Warning);
    }

    private string BuildHeader(RenderContext context, IReadOnlyList<ChatMessage> visible)
    {
        var theme = context.Theme;

        if (_searching)
        {
            return Draw.Literal("Search: ", theme.Accent) + _search.ToMarkup(theme);
        }

        if (_pending.Describe() is { Length: > 0 } pending)
        {
            return Draw.Literal(pending + "…", theme.Warning);
        }

        var position = visible.Count == 0 ? "—" : $"{_selected + 1}/{visible.Count}";
        var words = Selected?.WordCount ?? 0;

        var header = Draw.Literal($"message {position}", theme.Accent)
               + Draw.Literal(
                   $"  ·  {visible.Count(static m => m.Role == ChatRole.User)} yours"
                   + $"  ·  {visible.Count(static m => m.Role == ChatRole.Assistant)} replies"
                   + $"  ·  {words} words in this one"
                   + (_activeQuery.Length > 0 ? $"  ·  filter \"{_activeQuery}\"" : string.Empty),
                   theme.Muted);

        if (_spent is not { Calls: > 0 } spent)
        {
            return header;
        }

        // The money is its own colour, not more grey. It is the one figure here that is about
        // the world outside the story, and the reason for showing it at all is that it should
        // be noticed before it adds up rather than after.
        header += Draw.Literal($"  ·  {spent.Cost:$0.0000}", theme.Warning);

        if (spent.DiscardedCost > 0)
        {
            header += Draw.Literal($" ({spent.DiscardedCost:$0.0000} rerolled away)", theme.Muted);
        }

        return header;
    }

    /// <summary>
    /// Lays every message out into the rows actually drawn: a speaker line, the wrapped
    /// body, and a blank separator.
    /// </summary>
    /// <param name="context">Layout context.</param>
    /// <param name="visible">The messages being shown.</param>
    /// <returns>The display rows, each tagged with the message index it belongs to.</returns>
    private List<(int Message, string Markup)> BuildDisplayRows(
        RenderContext context,
        IReadOnlyList<ChatMessage> visible)
    {
        var theme = context.Theme;
        var rows = new List<(int Message, string Markup)>();
        var width = Math.Max(20, context.Width - 3);

        for (var i = 0; i < visible.Count; i++)
        {
            var message = visible[i];
            var selected = i == _selected;
            var marker = selected ? "▌" : " ";

            var (label, style) = message.Role switch
            {
                ChatRole.User => ("You", theme.Accent),
                ChatRole.Assistant => (SpeakerName(), theme.Success),
                ChatRole.System => ("Scene", theme.Warning),
                ChatRole.Data => ("Data", theme.Muted),
                _ => ("Unknown", theme.Muted),
            };

            var stamp = message.SentAtUtc is { } at
                ? at.LocalDateTime.ToString("ddd HH:mm")
                : string.Empty;

            rows.Add((i, Draw.Literal(marker, selected ? theme.Accent : theme.Border)
                        + Draw.Literal($" {label}", style)
                        + Draw.Literal($"  {stamp}", theme.Muted)
                        + (message.FlaggedReason is null
                            ? string.Empty
                            : Draw.Literal($"  ⚑ {message.FlaggedReason}", theme.Error))));

            foreach (var line in message.Text.Split('\n'))
            {
                // Formatted before wrapping, not after. The markers are removed here, so the
                // widths the wrapper measures are the widths actually drawn — wrapping the raw
                // line would budget columns for asterisks nobody ever sees and leave every
                // action-heavy paragraph short of the margin.
                var formatted = Application.Text.ProseFormat.Format(line);

                foreach (var (start, segment) in Draw.WrapSegments(formatted.Text, width))
                {
                    rows.Add((i, Draw.Literal(marker + " ", selected ? theme.Accent : theme.Border)
                                 + Body(formatted, start, segment, theme)));
                }
            }

            rows.Add((i, string.Empty));
        }

        return rows;
    }

    /// <summary>
    /// Paints one wrapped segment, giving each run of it the style its markers asked for.
    /// </summary>
    /// <remarks>
    /// The runs are offsets into the whole formatted line and a segment is a window onto it, so
    /// each run is clipped to the window before it is drawn. An action that wraps across three
    /// rows is one run and stays one colour, which is the reason the styling is computed on the
    /// line and not on the piece.
    /// </remarks>
    /// <param name="formatted">The whole line, stripped of markers, with its runs.</param>
    /// <param name="start">Where this segment begins within that line.</param>
    /// <param name="segment">The segment's text.</param>
    /// <param name="theme">The palette.</param>
    /// <returns>Markup for the segment.</returns>
    private string Body(Application.Text.FormattedProse formatted, int start, string segment, Theme theme)
    {
        var end = start + segment.Length;
        var markup = new System.Text.StringBuilder();
        var cursor = start;

        foreach (var run in formatted.Runs)
        {
            var from = Math.Max(run.Start, start);
            var to = Math.Min(run.Start + run.Length, end);

            if (to <= from)
            {
                continue;
            }

            // Whatever the runs did not claim is ordinary narration. Drawn rather than skipped:
            // a gap would silently drop the reader's words off the screen.
            if (from > cursor)
            {
                markup.Append(Paint(formatted.Text[cursor..from], theme.Text, theme));
            }

            markup.Append(Paint(
                formatted.Text[from..to],
                run.Kind == Application.Text.ProseKind.Action ? theme.Action : theme.Text,
                theme));

            cursor = to;
        }

        if (cursor < end)
        {
            markup.Append(Paint(formatted.Text[cursor..end], theme.Text, theme));
        }

        return markup.ToString();
    }

    /// <summary>Draws a stretch of text, letting an active search still show through it.</summary>
    /// <param name="text">The stretch.</param>
    /// <param name="style">The style its run asked for.</param>
    /// <param name="theme">The palette.</param>
    /// <returns>Markup for the stretch.</returns>
    private string Paint(string text, Style style, Theme theme)
        => _activeQuery.Length > 0
            ? Draw.Highlight(text, _activeQuery, style, theme.Highlight)
            : Draw.Literal(text, style);

    private string SpeakerName() => _conversation.Speaker ?? "Reply";

    /// <summary>
    /// Moves the viewport only when it has lost the selected message altogether.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The obvious rule — keep the selected message's first row at or below the top of the
    /// viewport — makes a message taller than the pane unreadable. Page down inside one and
    /// the next frame drags the view straight back to its opening line, so everything past
    /// the first screenful can never be reached.
    /// </para>
    /// <para>
    /// So while any part of the selected message is on screen, the scroll position is left
    /// exactly where the reader put it. It is only recovered when the selection has moved
    /// somewhere else entirely.
    /// </para>
    /// </remarks>
    /// <param name="display">Every drawn row, tagged with the message it belongs to.</param>
    /// <param name="available">Rows the transcript can show.</param>
    private void KeepSelectionVisible(List<(int Message, string Markup)> display, int available)
    {
        var first = display.FindIndex(row => row.Message == _selected);
        if (first < 0)
        {
            return;
        }

        var last = display.FindLastIndex(row => row.Message == _selected);

        // A turn is read from its beginning, so opening a conversation and landing on a
        // reply both ask for its first row rather than the foot of the transcript.
        if (_scroll == PinToSelection)
        {
            _scroll = first;
            return;
        }

        if (last < _scroll)
        {
            _scroll = first;
        }
        else if (first >= _scroll + available)
        {
            _scroll = last - first >= available ? first : last - available + 1;
        }
    }

    private void SyncSelectionToScroll(RenderContext context, IReadOnlyList<ChatMessage> visible)
    {
        var display = BuildDisplayRows(context, visible);
        if (display.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_scroll, 0, display.Count - 1);
        _selected = display[index].Message;
    }

}
