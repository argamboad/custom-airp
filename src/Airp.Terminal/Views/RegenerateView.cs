using Airp.Application.Abstractions;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// Asks for the last reply to be written again, optionally saying what was wrong with it.
/// </summary>
/// <remarks>
/// <para>
/// The reasons are the site's own list rather than free text, because the site records the
/// one you pick and only accepts values it knows. Instructions are optional and go alongside.
/// </para>
/// <para>
/// Nothing is asked for until Enter. This spends credits and the reply it replaces is not
/// kept, so the reply about to be discarded is shown in full first.
/// </para>
/// </remarks>
internal sealed class RegenerateView : ViewBase, IMouseAware
{
    private readonly IConversationService _conversations;
    private readonly string _conversationId;
    private readonly ChatMessage _replacing;
    private readonly Func<IReadOnlyList<ChatMessage>, ViewAction> _onDone;

    private readonly Application.Text.TextDocument _instructions =
        Application.Text.TextDocument.FromText(string.Empty);

    private readonly PendingStatus _pending = new();

    private int _selected = 1;
    private bool _writing;

    // Which reason each drawn row belongs to, or -1, recorded as the frame is drawn so a tap
    // is tested against exactly what was on screen.
    private List<int> _rowReasons = [];

    /// <summary>Initialises the view.</summary>
    /// <param name="conversations">Conversation access.</param>
    /// <param name="conversationId">The conversation being changed.</param>
    /// <param name="replacing">The reply that will be replaced.</param>
    /// <param name="onDone">Applied with the new transcript once the reply settles.</param>
    public RegenerateView(
        IConversationService conversations,
        string conversationId,
        ChatMessage replacing,
        Func<IReadOnlyList<ChatMessage>, ViewAction> onDone)
    {
        _conversations = conversations;
        _conversationId = conversationId;
        _replacing = replacing;
        _onDone = onDone;
    }

    /// <inheritdoc />
    public override string Title => "Regenerate";

    /// <inheritdoc />
    public override KeyContext KeyContext => _writing ? KeyContext.Text : KeyContext.Navigation;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => _writing
        ?
        [
            new("Esc", "Done writing"),
            new("Alt+Enter", "New line"),
        ]
        :
        [
            new("↑ ↓", "Choose a reason"),
            new("I", "Add instructions"),
            new("Enter", "Regenerate"),
            new("Esc", "Cancel"),
        ];

    /// <inheritdoc />
    /// <remarks>
    /// Reroll is Enter, which is what spends. Reason steps to the next one the way Tab does,
    /// for a thumb that would rather not reach for the arrows; tapping a reason picks it too.
    /// </remarks>
    public override IReadOnlyList<Button> Buttons => _writing
        ? [Button.Escape("Done")]
        :
        [
            Button.Back,
            Button.Enter("Reroll"),
            Button.Press("Reason", ConsoleKey.Tab, '\t'),
            Button.Press("Write", 'i'),
        ];

    private RegenerateReason Reason => RegenerateReasons.All[_selected];

    /// <inheritdoc />
    public ViewAction OnClick(int row, RenderContext context)
    {
        if (row >= 0 && row < _rowReasons.Count && _rowReasons[row] >= 0 && !_writing)
        {
            _selected = _rowReasons[row];
        }

        return ViewAction.None;
    }

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 4);
        var phone = context.Narrow;
        var reasons = new List<int>();

        // On a phone the warning is a line of its own rather than a hint beside the heading,
        // which is wider than the screen on its own. Every line there is fitted: one that
        // wraps pushes the reasons down a row the tap mapping below does not know about.
        var rows = new List<IRenderable>
        {
            new Markup(_pending.Describe() is { Length: > 0 } pending
                ? Draw.Literal(Draw.Fit(pending + "…", context.Width), theme.Warning)
                : phone
                ? Draw.Literal(Draw.Fit("Replace this reply", context.Width), theme.Heading)
                : Draw.Literal("Replace this reply with a new one", theme.Heading)
                  + Draw.Literal("   this costs credits and the current wording is not kept", theme.Muted)),
        };

        if (phone)
        {
            rows.Add(new Markup(Draw.Literal(Draw.Fit("Costs credits; this wording is lost.", context.Width), theme.Muted)));
        }

        rows.Add(new Rule { Style = theme.Border });

        // Show what is about to be discarded. Two lines is enough to recognise it without
        // burying the choice below the fold.
        foreach (var line in Draw.Wrap(_replacing.Text.Replace('\n', ' '), width).Take(2))
        {
            rows.Add(new Markup(Draw.Literal("  " + line, theme.Muted)));
        }

        rows.Add(Draw.Blank);
        rows.Add(Draw.Heading("What was wrong with it?", theme, phone ? null : "the reason is sent with the request"));

        while (reasons.Count < rows.Count + 1)
        {
            reasons.Add(-1);
        }

        for (var i = 0; i < RegenerateReasons.All.Count; i++)
        {
            var reason = RegenerateReasons.All[i];
            var selected = i == _selected;
            var marker = Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border);

            // On a phone the label alone, and the description only for the reason picked,
            // wrapped under it: ten descriptions beside ten labels were ten lines folded back
            // to the left edge, and the one that matters is the one about to be sent.
            if (phone)
            {
                rows.Add(new Markup(
                    marker
                    + Draw.Literal(selected ? "● " : "○ ", selected ? theme.Accent : theme.Border)
                    + Draw.Literal(Draw.Fit(RegenerateReasons.Label(reason), context.Width - 4), selected ? theme.Accent : theme.Text)));
                reasons.Add(i);

                if (selected)
                {
                    foreach (var line in Draw.Wrap(RegenerateReasons.Describe(reason), Math.Max(1, context.Width - 6)))
                    {
                        rows.Add(new Markup(marker + Draw.Literal("    " + line, theme.Muted)));
                        reasons.Add(i);
                    }
                }

                continue;
            }

            rows.Add(new Markup(
                marker
                + Draw.Literal(selected ? "● " : "○ ", selected ? theme.Accent : theme.Border)
                + Draw.Literal(Draw.Pad(RegenerateReasons.Label(reason), 20), selected ? theme.Accent : theme.Text)
                + Draw.Literal(RegenerateReasons.Describe(reason), theme.Muted)));
            reasons.Add(i);
        }

        _rowReasons = reasons;

        rows.Add(Draw.Blank);
        rows.Add(new Rule { Style = theme.Border });

        var limit = context.Options.InstructionCharacterLimit;
        var length = _instructions.CharacterCount + Math.Max(0, _instructions.LineCount - 1);
        var over = limit > 0 && length > limit;

        rows.Add(new Markup(
            Draw.Literal("Instructions ", theme.Accent)
            + Draw.Literal(
                limit > 0 ? $"{length:N0}/{limit:N0}" : $"{length:N0}",
                over ? theme.Error : theme.Muted)
            + Draw.Literal(
                _writing ? "  Esc when done" : phone ? "  optional" : "  optional — press I to write some",
                theme.Muted)));

        var text = _instructions.Text;
        if (text.Length > 0)
        {
            foreach (var line in Draw.Wrap(text.Replace('\n', ' '), width).Take(3))
            {
                rows.Add(new Markup(Draw.Literal("  " + line, _writing ? theme.Text : theme.Muted)));
            }
        }
        else if (_writing)
        {
            rows.Add(new Markup(Draw.Literal("  ▌", theme.Selection)));
        }

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        if (_writing)
        {
            return ValueTask.FromResult(HandleWritingKey(stroke));
        }

        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveUp:
                _selected = (_selected - 1 + RegenerateReasons.All.Count) % RegenerateReasons.All.Count;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown or AppCommand.Tab:
                _selected = (_selected + 1) % RegenerateReasons.All.Count;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Edit:
            case AppCommand.Character when stroke.Character is 'i' or 'I':
                _writing = true;
                return ValueTask.FromResult(ViewAction.Status("Type your instructions. Esc when done."));

            case AppCommand.Accept:
                return ValueTask.FromResult(Submit(context));

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    private ViewAction HandleWritingKey(KeyStroke stroke)
    {
        var alt = (stroke.Key.Modifiers & ConsoleModifiers.Alt) != 0;

        switch (stroke.Command)
        {
            case AppCommand.Back:
                _writing = false;
                return ViewAction.None;

            // Enter is a line break here rather than a submit: this field is prose, and the
            // button that spends credits is a deliberate step away.
            case AppCommand.NewLine or AppCommand.Accept:
                _instructions.InsertNewLine();
                return ViewAction.None;

            case AppCommand.Character:
                _instructions.InsertText(stroke.Character.ToString());
                return ViewAction.None;

            case AppCommand.DeleteBack:
                _instructions.Backspace();
                return ViewAction.None;

            case AppCommand.DeleteForward or AppCommand.Delete:
                _instructions.DeleteForward();
                return ViewAction.None;

            case AppCommand.MoveLeft:
                _instructions.MoveLeft();
                return ViewAction.None;

            case AppCommand.MoveRight:
                _instructions.MoveRight();
                return ViewAction.None;

            case AppCommand.Undo:
                _instructions.Undo();
                return ViewAction.None;

            default:
                _ = alt;
                return ViewAction.None;
        }
    }

    private ViewAction Submit(RenderContext context)
    {
        var instructions = _instructions.Text.Trim();
        var limit = context.Options.InstructionCharacterLimit;

        if (limit > 0 && instructions.Length > limit)
        {
            return ViewAction.Status(
                $"The instructions are {instructions.Length:N0} characters and the limit is "
                + $"{limit:N0}. Nothing has been asked for.",
                StatusKind.Warning);
        }

        var reason = Reason;

        return ViewAction.Run("Regenerating", async ct =>
        {
            _pending.Begin();

            IReadOnlyList<ChatMessage> transcript;
            try
            {
                transcript = await _conversations
                    .RegenerateAsync(_conversationId, reason, instructions, _pending, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _pending.Clear();
            }

            return ViewAction.Sequence(ViewAction.Pop, _onDone(transcript));
        });
    }
}
