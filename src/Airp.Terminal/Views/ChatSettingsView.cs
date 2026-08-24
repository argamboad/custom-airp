using Airp.Application.Abstractions;
using Airp.Application.Dials;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// The dials that shape how a chat replies, for the open conversation — whatever dials the
/// pack in force declares.
/// </summary>
/// <remarks>
/// <para>
/// The rows are not hardcoded: the pack decides what exists, what it is called, and what each
/// level means, and this view renders whatever it finds enabled. Every level is shown with
/// its name and explanation rather than a bare number, because the difference between one
/// level and the next is the whole point of the setting and "3" says nothing about it.
/// </para>
/// <para>
/// Changes are staged and applied together on Enter. Each keystroke writing straight through
/// would be simpler, but these settings change the chat's behaviour for every reply
/// afterwards — arrowing past a level should not be a decision.
/// </para>
/// </remarks>
internal sealed class ChatSettingsView : ViewBase
{
    private readonly IDialService _dials;
    private readonly string _conversationId;
    private readonly string _title;

    private IReadOnlyList<DialDefinition> _rows = [];
    private Dictionary<string, string> _applied = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _staged = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private int _selected;

    /// <summary>Initialises the view.</summary>
    /// <param name="dials">The pack and the conversation's choices.</param>
    /// <param name="conversationId">The conversation being adjusted.</param>
    /// <param name="title">The conversation's name, shown in the header.</param>
    public ChatSettingsView(IDialService dials, string conversationId, string title)
    {
        _dials = dials;
        _conversationId = conversationId;
        _title = title;
    }

    /// <inheritdoc />
    public override string Title => "Settings";

    /// <summary>Whether anything has been changed but not yet sent.</summary>
    private bool IsDirty =>
        _staged.Count != _applied.Count
        || _staged.Any(pair => !_applied.TryGetValue(pair.Key, out var was) || was != pair.Value);

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => IsDirty
        ?
        [
            new("← →", "Change"),
            new("↑ ↓", "Choose setting"),
            new("Del", "Clear"),
            new("Enter", "Apply"),
            new("Esc", "Discard"),
        ]
        :
        [
            new("← →", "Change"),
            new("↑ ↓", "Choose setting"),
            new("Del", "Clear"),
            new("R", "Reload"),
            new("Esc", "Back"),
        ];

    /// <inheritdoc />
    public override ValueTask<ViewAction> OnActivatedAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(Load());

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 2);

        var rows = new List<IRenderable>
        {
            Draw.Heading(_title, theme, "these apply to every reply from now on"),
        };

        if (!_loaded)
        {
            rows.Add(new Markup(Draw.Literal("Reading the conversation's settings…", theme.Muted)));
            return new Rows(rows);
        }

        if (_rows.Count == 0)
        {
            rows.Add(new Markup(Draw.Literal(
                "The dial pack declares nothing to adjust. Edit dials.json, or delete it to "
                + "return to the shipped pack.",
                theme.Muted)));
            return new Rows(rows);
        }

        for (var i = 0; i < _rows.Count; i++)
        {
            var dial = _rows[i];
            var selected = i == _selected;
            var value = _staged.GetValueOrDefault(dial.Key);
            var was = _applied.GetValueOrDefault(dial.Key);
            var changed = value != was;

            var gutter = Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border);

            rows.Add(new Markup(
                gutter
                + Draw.Literal(dial.Title, selected ? theme.Accent : theme.Text)
                + Draw.Literal("  " + Shorten(dial.Help), theme.Muted)));

            var (label, meaning) = Describe(dial, value);

            rows.Add(new Markup(
                gutter
                + Control(dial, value, theme, selected)
                + Draw.Literal("  " + label, changed ? theme.Warning : theme.Success)
                + Draw.Literal(changed ? "  (was " + Describe(dial, was).Label + ")" : string.Empty, theme.Muted)));

            foreach (var line in Draw.Wrap(meaning, width - 6))
            {
                rows.Add(new Markup(gutter + Draw.Literal("  " + line, theme.Muted)));
            }

            rows.Add(Draw.Blank);
        }

        rows.Add(new Rule { Style = theme.Border });
        rows.Add(new Markup(IsDirty
            ? Draw.Literal("Press Enter to apply these changes to the conversation.", theme.Warning)
            : Draw.Literal("Nothing changed.", theme.Muted)));

        return new Rows(rows);
    }

    /// <summary>The first sentence of a dial's help, for the line beside its name.</summary>
    private static string Shorten(string help)
    {
        var stop = help.IndexOf(". ", StringComparison.Ordinal);
        return stop > 0 ? help[..(stop + 1)] : help;
    }

    /// <summary>Names a stored value the way the reader chose it.</summary>
    /// <param name="dial">The dial.</param>
    /// <param name="value">The stored value, or null when unset.</param>
    /// <returns>The label and what it means.</returns>
    private static (string Label, string Meaning) Describe(DialDefinition dial, string? value)
    {
        if (value is null)
        {
            return ("Not set", "nothing chosen, so the pack's default applies");
        }

        return dial.Kind switch
        {
            DialKind.Scale when DialEngine.LevelIndex(dial, value) is { } index =>
                (dial.Levels[index].Label,
                 dial.Levels[index].Text ?? dial.Levels[index].Description ?? string.Empty),

            DialKind.Toggle => DialEngine.IsOn(value) ? ("On", dial.Help) : ("Off", dial.Help),

            DialKind.Choice when dial.Options.FirstOrDefault(
                o => string.Equals(o.Key, value, StringComparison.OrdinalIgnoreCase)) is { } option =>
                (option.Label, option.Text),

            DialKind.List => (string.Join(", ", DialEngine.Items(value)), dial.Accepts ?? string.Empty),

            DialKind.Text => (value, dial.Accepts ?? string.Empty),

            _ => (value, string.Empty),
        };
    }

    /// <summary>Draws the adjustable part of a row: dots for a scale, a state for the rest.</summary>
    private static string Control(DialDefinition dial, string? value, Theme theme, bool selected)
    {
        switch (dial.Kind)
        {
            case DialKind.Scale:
            {
                var level = DialEngine.LevelIndex(dial, value);
                var markup = new System.Text.StringBuilder();

                for (var i = 0; i < dial.Levels.Count; i++)
                {
                    markup.Append(Draw.Literal(
                        i == level ? "●" : "○",
                        i == level ? (selected ? theme.Accent : theme.Text) : theme.Border));

                    if (i < dial.Levels.Count - 1)
                    {
                        markup.Append(Draw.Literal("─", theme.Border));
                    }
                }

                return markup.ToString();
            }

            case DialKind.Toggle:
            {
                var on = DialEngine.IsOn(value);
                return Draw.Literal(on ? "● On " : "○ Off", on ? theme.Text : theme.Border);
            }

            case DialKind.Choice:
                return Draw.Literal("‹ ›", theme.Border);

            default:
                // A list or a text is typed, not stepped; the value itself is the display.
                return Draw.Literal("[…]", theme.Border);
        }
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back when IsDirty:
                _staged = new Dictionary<string, string>(_applied, StringComparer.OrdinalIgnoreCase);
                return ValueTask.FromResult(ViewAction.Status("Changes discarded.", StatusKind.Warning));

            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveUp when _rows.Count > 0:
                _selected = (_selected - 1 + _rows.Count) % _rows.Count;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown or AppCommand.Tab when _rows.Count > 0:
                _selected = (_selected + 1) % _rows.Count;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveLeft:
                return ValueTask.FromResult(Step(-1));

            case AppCommand.MoveRight:
                return ValueTask.FromResult(Step(1));

            case AppCommand.Delete or AppCommand.DeleteBack when _loaded && _rows.Count > 0:
                // Back to "Not set": the pack's default applies again, exactly as if the dial
                // had never been touched.
                _staged.Remove(_rows[_selected].Key);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Refresh when !IsDirty:
                return ValueTask.FromResult(Load());

            case AppCommand.Accept when IsDirty:
                return ValueTask.FromResult(Apply());

            case AppCommand.Accept:
                return ValueTask.FromResult(ViewAction.Status("Nothing to apply.", StatusKind.Warning));

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    /// <summary>Moves the selected dial one step in either direction, per its kind.</summary>
    /// <param name="delta">Direction to move.</param>
    /// <returns>The resulting action.</returns>
    private ViewAction Step(int delta)
    {
        if (!_loaded || _rows.Count == 0)
        {
            return ViewAction.None;
        }

        var dial = _rows[_selected];
        var value = _staged.GetValueOrDefault(dial.Key);

        switch (dial.Kind)
        {
            case DialKind.Scale:
            {
                // An unset level has no position to move from; starting at the middle matches
                // where a dial that has never been touched reads as sitting.
                var current = DialEngine.LevelIndex(dial, value) ?? dial.Levels.Count / 2;
                var next = Math.Clamp(current + delta, 0, dial.Levels.Count - 1);
                _staged[dial.Key] = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ViewAction.None;
            }

            case DialKind.Toggle:
                // Both directions flip it, staged like everything else.
                _staged[dial.Key] = DialEngine.IsOn(value) ? "false" : "true";
                return ViewAction.None;

            case DialKind.Choice:
            {
                var keys = dial.Options.Select(static o => o.Key).ToArray();
                var at = Array.FindIndex(
                    keys,
                    k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
                var next = at < 0
                    ? (delta > 0 ? 0 : keys.Length - 1)
                    : (at + delta + keys.Length) % keys.Length;
                _staged[dial.Key] = keys[next];
                return ViewAction.None;
            }

            default:
                return ViewAction.Status(
                    $"'{dial.Title}' is typed rather than stepped: airp dials --chat {_conversationId} "
                    + $"--set {dial.Key}=…",
                    StatusKind.Info);
        }
    }

    private ViewAction Load() => ViewAction.Run("Reading the settings", async ct =>
    {
        var pack = await _dials.PackAsync(ct).ConfigureAwait(false);
        var values = await _dials.ValuesAsync(_conversationId, ct).ConfigureAwait(false);

        _rows = [.. pack.Dials.Where(static d => d.Enabled)];
        _applied = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _staged = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _rows.Count - 1));
        _loaded = true;

        return _applied.Count == 0
            ? ViewAction.Status(
                "This conversation has no settings of its own yet; the pack's defaults apply.",
                StatusKind.Info)
            : ViewAction.None;
    });

    private ViewAction Apply()
    {
        var changed = _rows
            .Select(static d => d.Key)
            .Where(key => _staged.GetValueOrDefault(key) != _applied.GetValueOrDefault(key))
            .ToArray();

        return ViewAction.Run("Applying the settings", async ct =>
        {
            foreach (var key in changed)
            {
                await _dials.SetAsync(_conversationId, key, _staged.GetValueOrDefault(key), ct)
                    .ConfigureAwait(false);
            }

            _applied = new Dictionary<string, string>(_staged, StringComparer.OrdinalIgnoreCase);

            var parts = changed
                .Select(key => _rows.First(d => d.Key == key) is var dial
                    ? dial.Title + " → " + Describe(dial, _staged.GetValueOrDefault(key)).Label
                    : key)
                .ToList();

            return ViewAction.Status("Applied: " + string.Join(", ", parts) + ".", StatusKind.Success);
        });
    }
}
