using Airp.Application.Options;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>The full key reference, grouped by where each binding applies.</summary>
internal sealed class HelpView : ViewBase
{
    private readonly KeyboardMode _mode;
    private int _scroll;

    /// <summary>Initialises the view.</summary>
    /// <param name="mode">The configured keyboard dialect, so the vim section is only shown when relevant.</param>
    public HelpView(KeyboardMode mode) => _mode = mode;

    /// <inheritdoc />
    public override string Title => "Help";

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => [new("↑↓", "Scroll"), new("Esc", "Close")];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var lines = new List<(string Section, string Key, string Description)>
        {
            ("Anywhere", "Ctrl+P  or  :", "Command palette"),
            ("Anywhere", "Ctrl+F", "Global search across chat names and messages"),
            ("Anywhere", "Ctrl+R  or  R", "Refresh from the site"),
            ("Anywhere", "Ctrl+L", "Clear and redraw the screen"),
            ("Anywhere", "F1  or  ?", "This help"),
            ("Anywhere", "Esc", "Back one screen"),
            ("Anywhere", "Ctrl+C  or  Q", "Quit"),

            ("Navigation", "↑ ↓", "Move the selection"),
            ("Navigation", "PgUp PgDn", "Move a page"),
            ("Navigation", "Home End", "First / last item"),
            ("Navigation", "Enter  or  →", "Open"),
            ("Navigation", "Tab", "Next pane"),

            ("Chat list", "Enter", "Open the chat"),
            ("Chat list", "/", "Filter this list as you type"),
            ("Chat list", "F2", "Rename the chat"),
            ("Chat list", "Del", "Delete the chat, after confirming"),

            ("Conversation", "I  or  Enter", "Write a message"),
            ("Conversation", "PgUp PgDn", "Scroll the message being read"),
            ("Conversation", "Home End", "First / last message"),
            ("Conversation", ">", "Carry on with no prompt from you"),
            ("Conversation", "G", "Regenerate the last reply"),
            ("Conversation", "S", "Reply settings"),
            ("Conversation", "Del", "Delete from the selected message onwards"),
            ("Conversation", "/", "Search inside this chat"),
            ("Conversation", "C  /  X", "Copy the message / export the transcript"),

            ("Composer", "Enter", "Send"),
            ("Composer", "Alt+Enter", "New line"),
            ("Composer", "Ctrl+Z / Ctrl+Y", "Undo / redo"),
            ("Composer", "Esc", "Stop writing, keeping the draft"),

            ("Regenerate", "↑ ↓", "Choose a reason"),
            ("Regenerate", "I", "Add instructions"),
            ("Regenerate", "Enter", "Regenerate"),

            ("Reply settings", "↑ ↓  /  ← →", "Choose a setting / change its level"),
            ("Reply settings", "Enter", "Apply to the site"),
        };

        if (_mode == KeyboardMode.Vim)
        {
            lines.AddRange(
            [
                ("Vim", "h j k l", "Move"),
                ("Vim", "gg  /  G", "Top / bottom"),
                ("Vim", "w  /  b", "Word forward / back"),
                ("Vim", "0  /  $", "Start / end of line"),
                ("Vim", "i a A o", "Enter insert mode"),
                ("Vim", "Esc", "Return to normal mode"),
                ("Vim", "x  /  D  /  dd", "Delete character / to end of line / line"),
                ("Vim", "u", "Undo"),
                ("Vim", "n  /  N", "Next / previous match"),
            ]);
        }

        var rows = new List<IRenderable>();
        var section = string.Empty;

        foreach (var (group, key, description) in lines)
        {
            if (group != section)
            {
                section = group;
                if (rows.Count > 0)
                {
                    rows.Add(new Text(string.Empty));
                }

                rows.Add(new Markup(Draw.Literal(group, theme.Heading)));
                rows.Add(new Rule { Style = theme.Border });
            }

            rows.Add(new Markup(
                Draw.Literal(key.PadRight(18), theme.Accent)
                + Draw.Literal(description, theme.Text)));
        }

        var available = Math.Max(1, context.Height);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, rows.Count - available));

        return new Rows(rows.Skip(_scroll).Take(available));
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken) => ValueTask.FromResult(stroke.Command switch
    {
        AppCommand.MoveUp => Scroll(-1),
        AppCommand.MoveDown => Scroll(1),
        AppCommand.PageUp => Scroll(-Math.Max(1, context.Height - 2)),
        AppCommand.PageDown => Scroll(Math.Max(1, context.Height - 2)),
        AppCommand.Home => Scroll(int.MinValue / 2),
        AppCommand.Back or AppCommand.Accept or AppCommand.Help => ViewAction.Pop,
        AppCommand.Quit => ViewAction.Quit,
        _ => ViewAction.None,
    });

    private ViewAction Scroll(int delta)
    {
        _scroll = Math.Max(0, _scroll + delta);
        return ViewAction.None;
    }
}
