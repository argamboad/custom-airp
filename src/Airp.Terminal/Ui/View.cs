using Airp.Application.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Ui;

/// <summary>Everything a view needs to lay itself out for the current frame.</summary>
/// <param name="Width">Usable width in columns.</param>
/// <param name="Height">Usable height in rows, excluding the shell's header and footer.</param>
/// <param name="Theme">Active palette.</param>
/// <param name="Options">Live application options.</param>
internal readonly record struct RenderContext(int Width, int Height, Theme Theme, AirpOptions Options)
{
    /// <summary>Columns under which a screen is a phone rather than a desk.</summary>
    /// <remarks>
    /// One number, here, rather than a threshold per view: a screen that is narrow for the
    /// chat list is narrow for the conversation too, and two views disagreeing about it would
    /// switch layouts at different widths as the window was dragged. Sixty is where the chat
    /// list's two panes stop fitting their own minimums (28 and 24 columns, and the rule
    /// between them) — and a phone in a terminal is 54 columns across.
    /// </remarks>
    public const int NarrowWidth = 60;

    /// <summary>
    /// Whether the screen is too narrow for a layout designed at a desk: panes side by side,
    /// a centred reading column, captions that spell everything out.
    /// </summary>
    public bool Narrow => Width < NarrowWidth;
}

/// <summary>One entry in the footer's key legend.</summary>
/// <param name="Key">The key, as the user would press it.</param>
/// <param name="Label">What it does.</param>
internal readonly record struct KeyHint(string Key, string Label);

/// <summary>One button on a phone's bottom bar: what it says, and the key a tap on it presses.</summary>
/// <remarks>
/// A key rather than a command, and dispatched exactly as a real key press would be — through
/// the <see cref="KeyMap"/>, into the view's own handler. A button is then only ever a
/// shortcut to something the keyboard already does, and it cannot drift from it: a
/// hand-built stroke once shipped a binding nobody could press, and a button that bypassed
/// the map would be the same mistake with a better label.
/// </remarks>
/// <param name="Label">What the button says.</param>
/// <param name="Key">The key a tap presses.</param>
/// <param name="Description">What it does, for where there is room to say so: the <c>⋯</c> list.</param>
internal readonly record struct Button(string Label, ConsoleKeyInfo Key, string Description = "")
{
    /// <summary>A button that types a character, the way a shortcut letter is pressed.</summary>
    /// <param name="label">What the button says.</param>
    /// <param name="character">The character.</param>
    /// <returns>The button.</returns>
    public static Button Press(string label, char character)
        => new(label, new ConsoleKeyInfo(character, default, false, false, false));

    /// <summary>A button that presses a named key, with modifiers if it needs them.</summary>
    /// <param name="label">What the button says.</param>
    /// <param name="key">The key.</param>
    /// <param name="character">The character the key carries, if any.</param>
    /// <param name="alt">Whether Alt is held.</param>
    /// <param name="control">Whether Ctrl is held.</param>
    /// <returns>The button.</returns>
    public static Button Press(string label, ConsoleKey key, char character = '\0', bool alt = false, bool control = false)
        => new(label, new ConsoleKeyInfo(character, key, false, alt, control));

    /// <summary>Back one screen: Escape, which a phone's keyboard does not have to hand.</summary>
    public static Button Back { get; } = Press("‹", ConsoleKey.Escape, '\u001b');

    /// <summary>Confirms: Enter.</summary>
    /// <param name="label">What confirming does here.</param>
    /// <returns>The button.</returns>
    public static Button Enter(string label) => Press(label, ConsoleKey.Enter, '\r');

    /// <summary>Gives up on what is being typed: Escape.</summary>
    /// <param name="label">What giving up does here.</param>
    /// <returns>The button.</returns>
    public static Button Escape(string label) => Press(label, ConsoleKey.Escape, '\u001b');

    /// <summary>The same button, saying what it does.</summary>
    /// <param name="description">What it does, in a few words.</param>
    /// <returns>The button.</returns>
    public Button Doing(string description) => this with { Description = description };
}

/// <summary>What a view wants the shell to do after handling a key.</summary>
internal abstract record ViewAction
{
    /// <summary>Stay where we are and redraw.</summary>
    public static ViewAction None { get; } = new NoneAction();

    /// <summary>Leave the current view and return to the one beneath it.</summary>
    public static ViewAction Pop { get; } = new PopAction();

    /// <summary>Exit the application.</summary>
    public static ViewAction Quit { get; } = new QuitAction();

    /// <summary>Open a view on top of the current one.</summary>
    /// <param name="view">The view to open.</param>
    /// <returns>The action.</returns>
    public static ViewAction Push(IView view) => new PushAction(view);

    /// <summary>Replace the current view.</summary>
    /// <param name="view">The replacement.</param>
    /// <returns>The action.</returns>
    public static ViewAction Replace(IView view) => new ReplaceAction(view);

    /// <summary>Show a transient message in the status bar.</summary>
    /// <param name="text">The message.</param>
    /// <param name="kind">How to colour it.</param>
    /// <returns>The action.</returns>
    public static ViewAction Status(string text, StatusKind kind = StatusKind.Info)
        => new StatusAction(text, kind);

    /// <summary>Press a key on whatever view is current once the actions before it are applied.</summary>
    /// <remarks>
    /// How an entry in the <c>⋯</c> list does what it names: the list closes, and the key is
    /// pressed on the screen underneath through the same dispatch as the keyboard's — so the
    /// entry cannot do anything the key would not.
    /// </remarks>
    /// <param name="key">The key.</param>
    /// <returns>The action.</returns>
    public static ViewAction Press(ConsoleKeyInfo key) => new PressAction(key);

    /// <summary>
    /// Run asynchronous work while the shell shows a spinner, then apply whatever the work
    /// returns.
    /// </summary>
    /// <param name="label">What to show next to the spinner.</param>
    /// <param name="work">The work.</param>
    /// <returns>The action.</returns>
    public static ViewAction Run(string label, Func<CancellationToken, Task<ViewAction>> work)
        => new RunAction(label, work);

    /// <summary>Stay where we are and redraw.</summary>
    public sealed record NoneAction : ViewAction;

    /// <summary>Leave the current view.</summary>
    public sealed record PopAction : ViewAction;

    /// <summary>Exit the application.</summary>
    public sealed record QuitAction : ViewAction;

    /// <summary>Open a view on top of the current one.</summary>
    /// <param name="View">The view to open.</param>
    public sealed record PushAction(IView View) : ViewAction;

    /// <summary>Replace the current view.</summary>
    /// <param name="View">The replacement.</param>
    public sealed record ReplaceAction(IView View) : ViewAction;

    /// <summary>Show a transient message in the status bar.</summary>
    /// <param name="Text">The message.</param>
    /// <param name="Kind">How to colour it.</param>
    public sealed record StatusAction(string Text, StatusKind Kind) : ViewAction;

    /// <summary>Press a key on the current view.</summary>
    /// <param name="Key">The key.</param>
    public sealed record PressAction(ConsoleKeyInfo Key) : ViewAction;

    /// <summary>Run asynchronous work behind a spinner.</summary>
    /// <param name="Label">What to show next to the spinner.</param>
    /// <param name="Work">The work.</param>
    public sealed record RunAction(string Label, Func<CancellationToken, Task<ViewAction>> Work) : ViewAction;

    /// <summary>Apply several actions in order.</summary>
    /// <param name="Actions">The actions.</param>
    public sealed record SequenceAction(IReadOnlyList<ViewAction> Actions) : ViewAction;

    /// <summary>Applies several actions in order.</summary>
    /// <param name="actions">The actions.</param>
    /// <returns>The action.</returns>
    public static ViewAction Sequence(params ViewAction[] actions) => new SequenceAction(actions);
}

/// <summary>How a status message should be coloured.</summary>
internal enum StatusKind
{
    /// <summary>Neutral information.</summary>
    Info = 0,

    /// <summary>A completed action.</summary>
    Success,

    /// <summary>Something degraded but survivable.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>A screen the shell can display.</summary>
internal interface IView
{
    /// <summary>Shown in the header's breadcrumb.</summary>
    string Title { get; }

    /// <summary>
    /// A few words about where the reader is, for the right-hand end of a phone's one header
    /// row — a conversation's position and cost. Empty when the view has nothing to add.
    /// </summary>
    string Summary { get; }

    /// <summary>Shown in the footer legend.</summary>
    IReadOnlyList<KeyHint> KeyHints { get; }

    /// <summary>
    /// The phone's bottom bar, most useful first. The shell adds <c>⋯</c> for the command
    /// list at the end and drops from the right whatever does not fit.
    /// </summary>
    IReadOnlyList<Button> Buttons { get; }

    /// <summary>
    /// Everything else this screen does, listed first under <c>⋯</c> ahead of the commands that
    /// work anywhere. What the bar has no room for has to be somewhere a thumb can reach.
    /// </summary>
    IReadOnlyList<Button> Actions { get; }

    /// <summary>Whether letters typed here are literal text rather than shortcuts.</summary>
    KeyContext KeyContext { get; }

    /// <summary>
    /// Whether this view claims a command that the shell would otherwise handle globally.
    /// </summary>
    /// <remarks>
    /// The editor and the prompt viewer reserve <see cref="AppCommand.GlobalSearch"/> so that
    /// <c>Ctrl+F</c> searches the document in front of you rather than opening a search over
    /// every character — which is what the same key does everywhere else.
    /// </remarks>
    /// <param name="command">The command about to be handled globally.</param>
    /// <returns><see langword="true"/> to receive it instead.</returns>
    bool Reserves(AppCommand command);

    /// <summary>Called once each time the view becomes the active one.</summary>
    /// <param name="cancellationToken">Token used to abort activation work.</param>
    /// <returns>An action to apply immediately, typically <see cref="ViewAction.None"/>.</returns>
    ValueTask<ViewAction> OnActivatedAsync(CancellationToken cancellationToken);

    /// <summary>Builds this frame's content.</summary>
    /// <param name="context">Layout context.</param>
    /// <returns>The renderable body.</returns>
    IRenderable Render(RenderContext context);

    /// <summary>Handles a key press.</summary>
    /// <param name="stroke">The resolved key.</param>
    /// <param name="context">Layout context, for page-sized movement.</param>
    /// <param name="cancellationToken">Token used to abort the handler.</param>
    /// <returns>What the shell should do next.</returns>
    ValueTask<ViewAction> HandleKeyAsync(KeyStroke stroke, RenderContext context, CancellationToken cancellationToken);
}

/// <summary>Convenience base class with sensible defaults for most views.</summary>
internal abstract class ViewBase : IView
{
    /// <inheritdoc />
    public abstract string Title { get; }

    /// <inheritdoc />
    public virtual string Summary => string.Empty;

    /// <inheritdoc />
    public virtual IReadOnlyList<KeyHint> KeyHints => [];

    /// <inheritdoc />
    /// <remarks>Back, unless the view says otherwise: every screen but the first can be left.</remarks>
    public virtual IReadOnlyList<Button> Buttons => [Button.Back];

    /// <inheritdoc />
    public virtual IReadOnlyList<Button> Actions => [];

    /// <inheritdoc />
    public virtual KeyContext KeyContext => KeyContext.Navigation;

    /// <inheritdoc />
    public virtual bool Reserves(AppCommand command) => false;

    /// <inheritdoc />
    public virtual ValueTask<ViewAction> OnActivatedAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(ViewAction.None);

    /// <inheritdoc />
    public abstract IRenderable Render(RenderContext context);

    /// <inheritdoc />
    public abstract ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken);

    /// <summary>Builds an empty-state placeholder.</summary>
    /// <param name="context">Layout context.</param>
    /// <param name="message">The message to centre.</param>
    /// <returns>A renderable placeholder.</returns>
    protected static IRenderable Empty(RenderContext context, string message)
        => new Markup($"[{context.Theme.Muted.ToMarkup()}]{Markup.Escape(message)}[/]");
}
