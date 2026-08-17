using Airp.Application.Text;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>One entry in the command palette.</summary>
/// <param name="Name">What the command is called.</param>
/// <param name="Description">A one-line explanation.</param>
/// <param name="Invoke">Produces the action to apply when the command is chosen.</param>
internal sealed record PaletteCommand(
    string Name,
    string Description,
    Func<CancellationToken, Task<ViewAction>> Invoke);

/// <summary>A fuzzy-filtered list of every action the shell can perform.</summary>
internal sealed class CommandPaletteView : ViewBase, IMouseAware
{
    private readonly IReadOnlyList<PaletteCommand> _commands;
    private readonly TextInput _query = new() { Placeholder = "type a command…" };
    private readonly ListState _list = new();

    private IReadOnlyList<PaletteCommand> _visible;

    /// <summary>Initialises the view.</summary>
    /// <param name="commands">The commands to offer.</param>
    public CommandPaletteView(IReadOnlyList<PaletteCommand> commands)
    {
        _commands = commands;
        _visible = commands;
        _list.SetCount(_visible.Count);
    }

    /// <inheritdoc />
    public override string Title => "Commands";

    /// <inheritdoc />
    public override KeyContext KeyContext => KeyContext.Text;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("Enter", "Run"),
        new("↑↓", "Move"),
        new("Esc", "Close"),
    ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal("› ", theme.Accent) + _query.ToMarkup(theme)),
            new Rule { Style = theme.Border },
        };

        if (_visible.Count == 0)
        {
            rows.Add(new Markup(Draw.Literal("No command matches.", theme.Muted)));
            return new Rows(rows);
        }

        var available = Math.Max(1, context.Height - 2);
        var (start, length) = _list.Viewport(available);

        for (var i = start; i < Math.Min(_visible.Count, start + length); i++)
        {
            var command = _visible[i];
            var selected = i == _list.Selected;

            rows.Add(new Markup(Draw.Literal(
                $"{(selected ? '>' : ' ')} {Draw.Pad(command.Name, 34)} {Draw.Fit(command.Description, Math.Max(10, context.Width - 40))}",
                selected ? theme.Selection : theme.Text)));
        }

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

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

            case AppCommand.Accept or AppCommand.NewLine:
                return Run(cancellationToken);

            default:
                if (_query.Handle(stroke))
                {
                    Filter();
                }

                return ValueTask.FromResult(ViewAction.None);
        }
    }

    /// <inheritdoc />
    public ViewAction OnClick(int row, RenderContext context)
    {
        var index = _list.IndexAtRow(row - 2, Math.Max(1, context.Height - 2));
        if (index < 0)
        {
            return ViewAction.None;
        }

        _list.Select(index);
        return ViewAction.None;
    }

    private async ValueTask<ViewAction> Run(CancellationToken cancellationToken)
    {
        if (_list.Selected < 0 || _list.Selected >= _visible.Count)
        {
            return ViewAction.None;
        }

        var command = _visible[_list.Selected];
        var action = await command.Invoke(cancellationToken).ConfigureAwait(false);

        // The palette closes itself before the chosen action runs, so a command that pushes a
        // view does not leave the palette buried underneath it.
        return action switch
        {
            ViewAction.PushAction push => ViewAction.Replace(push.View),
            ViewAction.ReplaceAction replace => ViewAction.Replace(replace.View),
            ViewAction.NoneAction => ViewAction.Pop,
            ViewAction.QuitAction => action,
            _ => ViewAction.Sequence(ViewAction.Pop, action),
        };
    }

    private void Filter()
    {
        _visible = FuzzyMatcher.Rank(_commands, _query.Value, static c => c.Name + " " + c.Description);
        _list.SetCount(_visible.Count);
        _list.SelectFirst();
    }
}
