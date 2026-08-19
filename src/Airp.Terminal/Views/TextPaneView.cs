using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// A page of text, scrolled and then dismissed.
/// </summary>
/// <remarks>
/// <para>
/// What the free commands all needed and none of them needed differently: the character sheet,
/// the persona, the live facts, the meters, the audit and the command list are six unrelated
/// things that are all, on screen, a body of text too long for the status line and not worth a
/// screen of its own. Six views would have been six places to get the wrapping wrong.
/// </para>
/// <para>
/// It holds a copy of its lines rather than a callback that produces them. These are answers to
/// "what is true right now", and right now was when the command was typed — a pane that quietly
/// re-read its source while being scrolled would show a mixture of two moments.
/// </para>
/// </remarks>
internal sealed class TextPaneView : ViewBase
{
    private readonly string _title;
    private readonly string _subtitle;
    private readonly IReadOnlyList<string> _lines;

    private int _scroll;

    /// <summary>Initialises the view.</summary>
    /// <param name="title">Shown in the header.</param>
    /// <param name="subtitle">One line under the header saying where this came from.</param>
    /// <param name="lines">The body, one paragraph per entry; wrapping happens on render.</param>
    public TextPaneView(string title, string subtitle, IReadOnlyList<string> lines)
    {
        _title = title;
        _subtitle = subtitle;
        _lines = lines;
    }

    /// <inheritdoc />
    public override string Title => _title;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("↑↓ PgUp PgDn", "Scroll"),
        new("Esc", "Close"),
    ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 4);

        // Wrapped here rather than stored wrapped, because the terminal can be resized between
        // one render and the next and the stored copy would keep the old width forever.
        var wrapped = new List<string>();

        foreach (var line in _lines)
        {
            if (line.Length == 0)
            {
                wrapped.Add(string.Empty);
                continue;
            }

            wrapped.AddRange(Draw.Wrap(line, width));
        }

        var pane = Math.Max(1, context.Height - 6);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, wrapped.Count - pane));

        var shown = wrapped.Skip(_scroll).Take(pane).ToList();

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal(_title, theme.Heading) + Draw.Literal("   " + _subtitle, theme.Muted)),
            new Rule { Style = theme.Border },
        };

        foreach (var line in shown)
        {
            rows.Add(new Markup(Draw.Literal("  " + line, theme.Text)));
        }

        if (wrapped.Count > pane)
        {
            rows.Add(new Rule { Style = theme.Border });
            rows.Add(new Markup(Draw.Literal(
                $"  {_scroll + 1}–{_scroll + shown.Count} of {wrapped.Count}   PgUp/PgDn",
                theme.Muted)));
        }

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, context.Height - 8);

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

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }
}
