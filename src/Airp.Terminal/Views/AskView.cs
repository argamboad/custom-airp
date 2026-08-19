using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// The answer to a question asked about the story, and the one decision it needs.
/// </summary>
/// <remarks>
/// <para>
/// The answer is not in the transcript and never will be, which is what makes it safe to ask
/// anything. But it also makes it dangerous in one specific way: a model asked something the
/// story never settled will answer confidently rather than say so, and that invention lives
/// only on this screen. Read it, play on it, and three turns later the story contradicts a
/// detail nothing ever recorded.
/// </para>
/// <para>
/// So the pane offers exactly one thing to do about it. <c>F</c> writes the answer in as a
/// pinned fact, which the next prompt will carry and the extractor cannot retire; anything else
/// closes the pane and the answer is gone. That turns the trap into the point of the feature:
/// asking is how you find out what the story has implied, and F is how you make it binding.
/// </para>
/// </remarks>
internal sealed class AskView : ViewBase
{
    private readonly LocalConversationProvider _provider;
    private readonly string _conversationId;
    private readonly string _speaker;
    private readonly AskAnswer _answer;

    private int _scroll;
    private bool _pinned;

    /// <summary>Initialises the view.</summary>
    /// <param name="provider">Used to pin the answer as a fact.</param>
    /// <param name="conversationId">The conversation the question was about.</param>
    /// <param name="speaker">Subject a pinned fact is filed under, usually the character.</param>
    /// <param name="answer">The answer and its accounting.</param>
    public AskView(
        LocalConversationProvider provider,
        string conversationId,
        string speaker,
        AskAnswer answer)
    {
        _provider = provider;
        _conversationId = conversationId;
        _speaker = speaker;
        _answer = answer;
    }

    /// <inheritdoc />
    public override string Title => "Asked";

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => _pinned
        ?
        [
            new("↑↓ PgUp PgDn", "Scroll"),
            new("Esc", "Close"),
        ]
        :
        [
            new("F", "Pin as a fact"),
            new("↑↓ PgUp PgDn", "Scroll"),
            new("Esc", "Discard"),
        ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 4);

        var body = new List<(string Text, Style Style)>();

        foreach (var line in Draw.Wrap(_answer.Question, width - 2))
        {
            body.Add((line, theme.Accent));
        }

        body.Add((string.Empty, theme.Text));

        foreach (var paragraph in _answer.Answer.Split('\n'))
        {
            if (paragraph.Trim().Length == 0)
            {
                body.Add((string.Empty, theme.Text));
                continue;
            }

            foreach (var line in Draw.Wrap(paragraph, width))
            {
                body.Add((line, theme.Text));
            }
        }

        var pane = Math.Max(1, context.Height - 7);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, body.Count - pane));

        var rows = new List<IRenderable>
        {
            new Markup(
                Draw.Literal("Out of character", theme.Heading)
                + Draw.Literal("   not in the transcript, not in any later prompt", theme.Muted)),
            new Rule { Style = theme.Border },
        };

        foreach (var (text, style) in body.Skip(_scroll).Take(pane))
        {
            rows.Add(new Markup(Draw.Literal("  " + text, style)));
        }

        rows.Add(new Rule { Style = theme.Border });

        // The cost line is not decoration. This call was billed and left no message behind, so
        // the pane is the only place the reader would ever see what it spent.
        var spent = _answer.PromptTokens is { } prompt
            ? $"{prompt:N0} in, {_answer.CompletionTokens ?? 0:N0} out"
            : $"~{_answer.EstimatedPromptTokens:N0} in";

        rows.Add(new Markup(
            _pinned
                ? Draw.Literal("  Pinned. It is in the world layer from the next turn on.", theme.Success)
                : Draw.Literal($"  {spent}", theme.Muted)
                  + Draw.Literal("   F pins this as a fact · Esc discards it", theme.Muted)));

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, context.Height - 9);

        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveDown:
                _scroll++;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveUp:
                _scroll = Math.Max(0, _scroll - 1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageDown:
                _scroll += page;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageUp:
                _scroll = Math.Max(0, _scroll - page);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Home:
                _scroll = 0;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Favorite:
            case AppCommand.Character when stroke.Character is 'f' or 'F':
                return ValueTask.FromResult(Pin());

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    private ViewAction Pin()
    {
        if (_pinned)
        {
            return ViewAction.Status("Already pinned.", StatusKind.Info);
        }

        var text = _answer.Answer.Trim();

        if (text.Length == 0)
        {
            return ViewAction.Status("There is nothing to pin.", StatusKind.Warning);
        }

        return ViewAction.Run("Pinning", async ct =>
        {
            await _provider
                .AddFactAsync(_conversationId, _speaker, text, ct)
                .ConfigureAwait(false);

            _pinned = true;

            return ViewAction.Status(
                "Pinned. The extractor cannot retire it; airp fact retire can.",
                StatusKind.Success);
        });
    }
}
