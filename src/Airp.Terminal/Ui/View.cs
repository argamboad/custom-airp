using Airp.Application.Options;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Ui;

/// <summary>Everything a view needs to lay itself out for the current frame.</summary>
/// <param name="Width">Usable width in columns.</param>
/// <param name="Height">Usable height in rows, excluding the shell's header and footer.</param>
/// <param name="Theme">Active palette.</param>
/// <param name="Options">Live application options.</param>
internal readonly record struct RenderContext(int Width, int Height, Theme Theme, AirpOptions Options);

/// <summary>One entry in the footer's key legend.</summary>
/// <param name="Key">The key, as the user would press it.</param>
/// <param name="Label">What it does.</param>
internal readonly record struct KeyHint(string Key, string Label);

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

    /// <summary>Shown in the footer legend.</summary>
    IReadOnlyList<KeyHint> KeyHints { get; }

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
    public virtual IReadOnlyList<KeyHint> KeyHints => [];

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
