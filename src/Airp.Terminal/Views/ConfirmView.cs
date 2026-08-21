using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// Stops before something irreversible and says exactly what it will do.
/// </summary>
/// <remarks>
/// <para>
/// This takes the whole pane rather than sitting inline. An action that cannot be undone
/// should not be confirmable by a keystroke aimed at whatever was on screen a moment ago,
/// and a banner in the corner of a familiar screen is exactly that.
/// </para>
/// <para>
/// Nothing here is defaulted to yes: every key that is not the confirmation cancels, so the
/// only way through is the one the reader means.
/// </para>
/// </remarks>
internal sealed class ConfirmView : ViewBase
{
    private readonly string _question;
    private readonly IReadOnlyList<string> _consequences;
    private readonly string _confirmLabel;
    private readonly Func<CancellationToken, Task<ViewAction>> _confirm;

    /// <summary>Initialises the view.</summary>
    /// <param name="title">Shown in the breadcrumb.</param>
    /// <param name="question">The decision, in one line.</param>
    /// <param name="consequences">What will happen, one line each. Say the irreversible part.</param>
    /// <param name="confirmLabel">Verb for the confirming key, such as "Delete".</param>
    /// <param name="confirm">Runs when confirmed. Its result replaces this view's.</param>
    public ConfirmView(
        string title,
        string question,
        IReadOnlyList<string> consequences,
        string confirmLabel,
        Func<CancellationToken, Task<ViewAction>> confirm)
    {
        Title = title;
        _question = question;
        _consequences = consequences;
        _confirmLabel = confirmLabel;
        _confirm = confirm;
    }

    /// <inheritdoc />
    public override string Title { get; }

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("Enter", _confirmLabel),
        new("Esc", "Cancel"),
    ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 4);

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal(_question, theme.Error)),
            new Rule { Style = theme.Border },
        };

        foreach (var consequence in _consequences)
        {
            foreach (var line in Draw.Wrap(consequence, width - 2))
            {
                rows.Add(new Markup(Draw.Literal("  " + line, theme.Text)));
            }
        }

        rows.Add(Draw.Blank);
        rows.Add(new Markup(
            Draw.Literal("Enter", theme.Error)
            + Draw.Literal($" to {_confirmLabel.ToLowerInvariant()}   ", theme.Muted)
            + Draw.Literal("Esc", theme.Accent)
            + Draw.Literal(" to leave everything as it is", theme.Muted)));

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(stroke.Command switch
        {
            AppCommand.Accept => ViewAction.Run(_confirmLabel, async ct =>
                ViewAction.Sequence(ViewAction.Pop, await _confirm(ct).ConfigureAwait(false))),

            // Anything else backs out. Cancelling is always the safe reading of an
            // ambiguous keystroke here.
            _ => ViewAction.Sequence(ViewAction.Pop, ViewAction.Status("Cancelled.", StatusKind.Info)),
        });
}
