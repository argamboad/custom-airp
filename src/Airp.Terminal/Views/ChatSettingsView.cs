using Airp.Application.Options;
using Airp.Application.Abstractions;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// The three dials that shape how a chat replies, for the open conversation.
/// </summary>
/// <remarks>
/// <para>
/// Every level is shown with the site's own name and explanation rather than a bare number,
/// because the difference between one level and the next is the whole point of the setting
/// and "3" says nothing about it.
/// </para>
/// <para>
/// Changes are staged and applied together on Enter. Each keystroke writing straight through
/// would be simpler, but these settings change the chat's behaviour for every reply
/// afterwards — arrowing past a level should not be a decision.
/// </para>
/// </remarks>
internal sealed class ChatSettingsView : ViewBase
{
    private readonly IConversationService _conversations;
    private readonly string _conversationId;
    private readonly string _title;

    /// <summary>Index of the inner-thoughts row, one past the dials.</summary>
    private static int ThoughtsRow => ChatSettingScale.All.Count;

    private ChatSettings _applied = new();
    private ChatSettings _staged = new();
    private bool _loaded;
    private int _selected;

    /// <summary>Initialises the view.</summary>
    /// <param name="conversations">Conversation access.</param>
    /// <param name="conversationId">The conversation being adjusted.</param>
    /// <param name="title">The conversation's name, shown in the header.</param>
    public ChatSettingsView(IConversationService conversations, string conversationId, string title)
    {
        _conversations = conversations;
        _conversationId = conversationId;
        _title = title;
    }

    /// <inheritdoc />
    public override string Title => "Settings";

    /// <summary>Whether anything has been changed but not yet sent.</summary>
    private bool IsDirty => !_staged.ChangesFrom(_applied).IsEmpty;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints => IsDirty
        ?
        [
            new("← →", "Change level"),
            new("↑ ↓", "Choose setting"),
            new("Enter", "Apply"),
            new("Esc", "Discard"),
        ]
        :
        [
            new("← →", "Change level"),
            new("↑ ↓", "Choose setting"),
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

        for (var i = 0; i < ChatSettingScale.All.Count; i++)
        {
            var setting = ChatSettingScale.All[i];
            var selected = i == _selected;
            var level = _staged.Level(setting);
            var described = SettingScales.Describe(setting, level, context.Options);
            var changed = level != _applied.Level(setting);

            rows.Add(new Markup(
                Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border)
                + Draw.Literal(SettingScales.Title(setting, context.Options), selected ? theme.Accent : theme.Text)
                + Draw.Literal("  " + ChatSettingScale.Description(setting), theme.Muted)));

            rows.Add(new Markup(
                Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border)
                + Scale(setting, level, theme, selected)
                + Draw.Literal("  " + described.Label, changed ? theme.Warning : theme.Success)
                + Draw.Literal(changed ? "  (was " + SettingScales.Describe(setting, _applied.Level(setting), context.Options).Label + ")" : string.Empty, theme.Muted)));

            foreach (var line in Draw.Wrap(described.Description, width - 6))
            {
                rows.Add(new Markup(
                    Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border)
                    + Draw.Literal("  " + line, theme.Muted)));
            }

            rows.Add(Draw.Blank);
        }

        {
            var selected = _selected == ThoughtsRow;
            var on = _staged.InnerThoughts ?? false;
            var changed = _staged.InnerThoughts != _applied.InnerThoughts;

            rows.Add(new Markup(
                Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border)
                + Draw.Literal("Inner thoughts", selected ? theme.Accent : theme.Text)
                + Draw.Literal("  one line of what they did not say — never for you", theme.Muted)));

            rows.Add(new Markup(
                Draw.Literal(selected ? "▌ " : "  ", selected ? theme.Accent : theme.Border)
                + Draw.Literal(on ? "● On " : "○ Off", changed ? theme.Warning : (on ? theme.Success : theme.Muted))
                + Draw.Literal(changed ? "  (was " + ((_applied.InnerThoughts ?? false) ? "On" : "Off") + ")" : string.Empty, theme.Muted)));

            rows.Add(Draw.Blank);
        }

        rows.Add(new Rule { Style = theme.Border });
        rows.Add(new Markup(IsDirty
            ? Draw.Literal("Press Enter to apply these changes to the conversation.", theme.Warning)
            : Draw.Literal("Nothing changed.", theme.Muted)));

        return new Rows(rows);
    }

    /// <summary>Draws the level as a row of steps, so the range is visible at a glance.</summary>
    /// <param name="setting">The setting being drawn.</param>
    /// <param name="level">The current level, or null when unset.</param>
    /// <param name="theme">Active palette.</param>
    /// <param name="selected">Whether this row has the cursor.</param>
    /// <returns>Markup for the scale.</returns>
    private static string Scale(ChatSetting setting, int? level, Theme theme, bool selected)
    {
        var steps = ChatSettingScale.Levels(setting).Count;
        var markup = new System.Text.StringBuilder();

        for (var i = 0; i < steps; i++)
        {
            markup.Append(Draw.Literal(
                i == level ? "●" : "○",
                i == level ? (selected ? theme.Accent : theme.Text) : theme.Border));

            if (i < steps - 1)
            {
                markup.Append(Draw.Literal("─", theme.Border));
            }
        }

        return markup.ToString();
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
                _staged = _applied;
                return ValueTask.FromResult(ViewAction.Status("Changes discarded.", StatusKind.Warning));

            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveUp:
                _selected = (_selected - 1 + ThoughtsRow + 1) % (ThoughtsRow + 1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown or AppCommand.Tab:
                _selected = (_selected + 1) % (ThoughtsRow + 1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveLeft or AppCommand.MoveRight when _selected == ThoughtsRow:
                // A toggle, not a dial: both directions flip it, staged like everything else.
                _staged = _staged with { InnerThoughts = !(_staged.InnerThoughts ?? false) };
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveLeft:
                return ValueTask.FromResult(Step(-1));

            case AppCommand.MoveRight:
                return ValueTask.FromResult(Step(1));

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

    /// <summary>Moves the selected setting one level, staying inside the site's range.</summary>
    /// <param name="delta">Direction to move.</param>
    /// <returns>The resulting action.</returns>
    private ViewAction Step(int delta)
    {
        if (!_loaded)
        {
            return ViewAction.None;
        }

        var setting = ChatSettingScale.All[_selected];
        var steps = ChatSettingScale.Levels(setting).Count;

        // An unset level has no position to move from. Starting at the middle matches where
        // the site's own control sits when it has never been touched.
        var current = _staged.Level(setting) ?? steps / 2;
        var next = Math.Clamp(current + delta, 0, steps - 1);

        _staged = _staged.With(setting, next);
        return ViewAction.None;
    }

    private ViewAction Load() => ViewAction.Run("Reading the settings", async ct =>
    {
        _applied = await _conversations.GetSettingsAsync(_conversationId, ct).ConfigureAwait(false);
        _staged = _applied;
        _loaded = true;

        return _applied.IsEmpty
            ? ViewAction.Status(
                "This conversation has no settings of its own yet; the site's defaults apply.",
                StatusKind.Info)
            : ViewAction.None;
    });

    private ViewAction Apply()
    {
        var changes = _staged.ChangesFrom(_applied);

        return ViewAction.Run("Applying the settings", async ct =>
        {
            _applied = await _conversations
                .UpdateSettingsAsync(_conversationId, changes, ct)
                .ConfigureAwait(false);

            _staged = _applied;

            var parts = changes.Assigned()
                .Select(s => ChatSettingScale.Title(s)
                             + " → "
                             + ChatSettingScale.Describe(s, _applied.Level(s)).Label)
                .ToList();

            if (changes.InnerThoughts is { } thoughts)
            {
                parts.Add("Inner thoughts → " + (thoughts ? "On" : "Off"));
            }

            return ViewAction.Status($"Applied: {string.Join(", ", parts)}.", StatusKind.Success);
        });
    }
}
