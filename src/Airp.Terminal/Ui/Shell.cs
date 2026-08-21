using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Terminal.Views;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Ui;

/// <summary>
/// The application shell: owns the screen, the view stack and the input loop.
/// </summary>
/// <remarks>
/// <para>
/// Rendering goes through a single Spectre live display that is updated once per frame.
/// Views are pure state machines — they render and they answer key presses — so nothing in a
/// view ever writes to the console directly, which is what keeps redraws flicker-free and
/// makes the views testable in isolation.
/// </para>
/// <para>
/// Long-running work never blocks the loop. A view returns
/// <see cref="ViewAction.RunAction"/>, the shell keeps drawing a spinner while it awaits,
/// and the result is applied when it lands.
/// </para>
/// </remarks>
internal sealed class Shell
{
    private readonly IServiceProvider _services;
    private readonly ISynchronizationService _synchronization;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<Shell> _logger;

    private readonly List<IView> _stack = [];
    private readonly Queue<char> _pending = new();
    private bool _pasting;
    private bool _bracketedPaste;
    private long _lastDrawTicks;

    private LiveDisplayContext? _live;
    private string _status = string.Empty;
    private StatusKind _statusKind = StatusKind.Info;
    private string? _banner;
    private string? _bannerHint;
    private bool _running = true;
    private string _busyLabel = string.Empty;
    private int _spinnerFrame;
    private DateTimeOffset? _lastSyncUtc;

    /// <summary>Initialises the shell.</summary>
    /// <param name="services">Container used to construct views.</param>
    /// <param name="synchronization">Background synchroniser, surfaced in the header.</param>
    /// <param name="options">Live application options.</param>
    /// <param name="logger">Logger.</param>
    public Shell(
        IServiceProvider services,
        ISynchronizationService synchronization,
        IOptionsMonitor<AirpOptions> options,
        ILogger<Shell> logger)
    {
        _services = services;
        _synchronization = synchronization;
        _options = options;
        _logger = logger;
    }

    /// <summary>Runs the shell until the user quits or the host shuts down.</summary>
    /// <param name="views">
    /// The initial view stack, outermost first. More than one entry restores a previous
    /// session directly onto the screen the user left.
    /// </param>
    /// <param name="cancellationToken">Token used to stop the loop.</param>
    /// <returns>A task that completes when the loop exits.</returns>
    public async Task RunAsync(IReadOnlyList<IView> views, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(views);

        if (views.Count == 0)
        {
            throw new ArgumentException("The shell needs at least one view.", nameof(views));
        }

        _stack.AddRange(views);
        var root = _stack[^1];
        _synchronization.Completed += OnSyncCompleted;

        if (!IsInteractive(out var reason))
        {
            throw new InvalidOperationException(
                $"The terminal interface needs an interactive console ({reason}). "
                + "Run 'airp' directly in a terminal, or use 'airp diagnose' and "
                + "'airp config' when output is redirected.");
        }

        var mouse = _options.CurrentValue.MouseSupport;

        // Every console interaction below is best-effort. A terminal that refuses one of
        // these is a cosmetic problem; it must never be the reason the application dies,
        // least of all from a finally block on the way out.
        Try(static () => Console.TreatControlCAsInput = true);

        if (mouse)
        {
            Try(MouseInput.Enable);
        }

        // Bracketed paste is asked for unconditionally. It costs nothing where it is not
        // understood, and where it is, it is the only way to know that a line break was
        // pasted rather than typed.
        Try(PasteMode.Enable);

        try
        {
            await AnsiConsole.Live(new Text(string.Empty))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async context =>
                {
                    _live = context;

                    var activation = await root.OnActivatedAsync(cancellationToken).ConfigureAwait(false);
                    await ApplyAsync(activation, cancellationToken).ConfigureAwait(false);

                    await LoopAsync(cancellationToken).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }
        finally
        {
            _synchronization.Completed -= OnSyncCompleted;

            if (mouse)
            {
                Try(MouseInput.Disable);
            }

            Try(PasteMode.Disable);
            Try(static () => AnsiConsole.Cursor.Show());
            Try(static () => Console.TreatControlCAsInput = false);
        }
    }

    /// <summary>
    /// Whether this process has a console it can actually drive.
    /// </summary>
    /// <param name="reason">Why not, when the answer is no.</param>
    /// <returns><see langword="true"/> when the terminal interface can run.</returns>
    private static bool IsInteractive(out string reason)
    {
        if (Console.IsOutputRedirected)
        {
            reason = "standard output is redirected";
            return false;
        }

        if (Console.IsInputRedirected)
        {
            reason = "standard input is redirected, so no keys can be read";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Runs a console call, swallowing the failures terminals are entitled to raise.</summary>
    /// <param name="action">The call.</param>
    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            // Nothing here is load-bearing.
        }
    }

    private IView Current => _stack[^1];

    /// <summary>How long the display may lag behind the input during a burst.</summary>
    private static readonly long StaleAfterTicks = TimeSpan.FromMilliseconds(100).Ticks;

    /// <summary>Whether the frame has been held long enough that it should be drawn.</summary>
    /// <returns><see langword="true"/> when the display is due a redraw.</returns>
    private bool Stale() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond - _lastDrawTicks >= StaleAfterTicks;

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        Draw();

        while (_running && !cancellationToken.IsCancellationRequested)
        {
            var input = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (input is null)
            {
                Draw();
                continue;
            }

            ViewAction action;
            try
            {
                action = input.Value.Mouse is { } mouse
                    ? await HandleMouseAsync(mouse, cancellationToken).ConfigureAwait(false)
                    : await DispatchAsync(input.Value.Key!.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                action = ShowError(ex);
            }

            await ApplyAsync(action, cancellationToken).ConfigureAwait(false);

            // Redrawing between the keys of a paste is what makes pasting a paragraph look
            // like watching it be typed: a frame costs milliseconds and 12,000 characters
            // would ask for thousands of them. The frame is held while input keeps arriving,
            // bounded by time rather than by a key count — what matters is that the display
            // never goes stale for long, not how many keys went by.
            if (!InputWaiting() || Stale())
            {
                Draw();
            }
        }
    }

    private async Task<ViewAction> DispatchAsync(ConsoleKeyInfo key, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var stroke = KeyMap.Resolve(key, options.Keyboard, Current.KeyContext) with
        {
            // Inside bracketed markers this is certain. Without them, a key that arrives
            // with more already queued behind it is a burst, which is what a paste is.
            Pasted = _pasting || (!_bracketedPaste && InputWaiting()),
        };

        // A handful of chords belong to the shell no matter what has focus.
        switch (stroke.Command)
        {
            case AppCommand.ClearScreen:
                AnsiConsole.Clear();
                return ViewAction.Status("Screen cleared.");

            case AppCommand.Help:
                return _stack.Any(static v => v is HelpView)
                    ? ViewAction.None
                    : ViewAction.Push(new HelpView(options.Keyboard));

            case AppCommand.CommandPalette when Current is not CommandPaletteView:
                return ViewAction.Push(new CommandPaletteView(BuildPalette()));

            case AppCommand.GlobalSearch when !Current.Reserves(AppCommand.GlobalSearch):
                return Current is SearchView
                    ? ViewAction.None
                    : ViewAction.Push(ActivatorUtilities.CreateInstance<SearchView>(_services));

        }

        if (key is { Key: ConsoleKey.C, Modifiers: ConsoleModifiers.Control })
        {
            return ViewAction.Quit;
        }

        _banner = null;
        _bannerHint = null;

        return await Current.HandleKeyAsync(stroke, BuildContext(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ViewAction> HandleMouseAsync(MouseEvent mouse, CancellationToken cancellationToken)
    {
        // Translate the wheel into the movement commands views already understand, and map a
        // click onto the row it landed on. Views need no mouse-specific code at all.
        var context = BuildContext();

        switch (mouse.Kind)
        {
            case MouseEventKind.ScrollUp:
            case MouseEventKind.ScrollDown:
            {
                var command = mouse.Kind == MouseEventKind.ScrollUp ? AppCommand.MoveUp : AppCommand.MoveDown;
                return await Current
                    .HandleKeyAsync(new KeyStroke(command, '\0', default), context, cancellationToken)
                    .ConfigureAwait(false);
            }

            default:
                return Current is IMouseAware aware
                    ? aware.OnClick(mouse.Row - HeaderHeight - 1, context)
                    : ViewAction.None;
        }
    }

    private async Task ApplyAsync(ViewAction action, CancellationToken cancellationToken)
    {
        switch (action)
        {
            case ViewAction.NoneAction:
                break;

            case ViewAction.QuitAction:
                _running = false;
                break;

            case ViewAction.StatusAction status:
                _status = status.Text;
                _statusKind = status.Kind;
                break;

            case ViewAction.PushAction push:
                _stack.Add(push.View);
                await ActivateAsync(cancellationToken).ConfigureAwait(false);
                break;

            case ViewAction.ReplaceAction replace:
                _stack[^1] = replace.View;
                await ActivateAsync(cancellationToken).ConfigureAwait(false);
                break;

            case ViewAction.PopAction:
                if (_stack.Count <= 1)
                {
                    _running = false;
                }
                else
                {
                    _stack.RemoveAt(_stack.Count - 1);
                    await ActivateAsync(cancellationToken).ConfigureAwait(false);
                }

                break;

            case ViewAction.RunAction run:
                await RunWithSpinnerAsync(run, cancellationToken).ConfigureAwait(false);
                break;

            case ViewAction.SequenceAction sequence:
                foreach (var step in sequence.Actions)
                {
                    await ApplyAsync(step, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private async Task ActivateAsync(CancellationToken cancellationToken)
    {
        Draw();

        try
        {
            var action = await Current.OnActivatedAsync(cancellationToken).ConfigureAwait(false);
            await ApplyAsync(action, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ApplyAsync(ShowError(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a view's asynchronous work, drawing a spinner and watching for a stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The work gets a token of its own so the keyboard can stop it without stopping the
    /// application. That token is the only way out of a long operation from inside the
    /// program: the input loop is not running while this awaits, so nothing else is reading
    /// keys, and a wait that outlasts the reader's patience would otherwise leave killing the
    /// process as the only exit — which is exactly what a three-minute reply timeout was.
    /// </para>
    /// <para>
    /// Stopping is reported rather than propagated. A reader who pressed Esc has not hit an
    /// error and does not need a red banner; the application shutting down still does, so the
    /// two cancellations are told apart by which token asked for it.
    /// </para>
    /// </remarks>
    /// <param name="run">The work and its label.</param>
    /// <param name="cancellationToken">The application's token; cancels everything.</param>
    private async Task RunWithSpinnerAsync(ViewAction.RunAction run, CancellationToken cancellationToken)
    {
        _busyLabel = run.Label;
        _status = string.Empty;

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            var task = run.Work(stopping.Token);

            while (!task.IsCompleted)
            {
                _spinnerFrame++;
                Draw();

                var finished = await Task.WhenAny(task, Task.Delay(90, cancellationToken)).ConfigureAwait(false);
                if (finished == task)
                {
                    break;
                }

                if (!stopping.IsCancellationRequested && StopRequested())
                {
                    _busyLabel = run.Label + " — stopping";
                    await stopping.CancelAsync().ConfigureAwait(false);
                }
            }

            var result = await task.ConfigureAwait(false);
            _busyLabel = string.Empty;
            await ApplyAsync(result, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stopped from the keyboard, not by the application going down. Views that know
            // what a half-finished operation left behind say so themselves by catching this
            // and returning a status; this is the plain case where nothing needs explaining.
            _busyLabel = string.Empty;
            await ApplyAsync(
                ViewAction.Status($"{run.Label} stopped.", StatusKind.Warning),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _busyLabel = string.Empty;
            throw;
        }
        catch (Exception ex)
        {
            _busyLabel = string.Empty;
            await ApplyAsync(ShowError(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains the keyboard while an action runs and answers whether it asked to stop.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anything that is not a stop is put back rather than swallowed: typing during a wait
    /// should land in the view once the wait ends, not vanish. It is buffered here instead of
    /// re-read, because feeding it back through the reader this loop uses would never
    /// terminate.
    /// </para>
    /// <para>
    /// A bare Escape counts only when it arrives alone. Arrow keys and every other special
    /// key are escape sequences, and a terminal delivers those all at once — so a batch with
    /// anything after the escape is a key press, not a request to give up on a paid send.
    /// Ctrl+C is unambiguous and counts wherever it appears.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the reader asked to stop.</returns>
    private bool StopRequested()
    {
        var batch = new List<ConsoleKeyInfo>();

        try
        {
            while (Console.KeyAvailable)
            {
                batch.Add(Console.ReadKey(intercept: true));
            }
        }
        catch (InvalidOperationException)
        {
            // No interactive keyboard, so nothing can ask.
            return false;
        }

        var stop = batch.Any(static k =>
            k.Key == ConsoleKey.C && (k.Modifiers & ConsoleModifiers.Control) != 0);

        if (batch is [{ Key: ConsoleKey.Escape }])
        {
            stop = true;
        }

        if (!stop)
        {
            foreach (var key in batch)
            {
                _pending.Enqueue(key.KeyChar);
            }
        }

        return stop;
    }

    private ViewAction ShowError(Exception ex)
    {
        _logger.LogError(ex, "An operation failed.");

        if (ex is AirpException known)
        {
            _banner = known.Message;
            _bannerHint = known.RecoveryHint;
            return ViewAction.Status(known.Message, StatusKind.Error);
        }

        _banner = ex.Message;
        _bannerHint = "This was not an error the application expects. See the log file for the full detail.";
        return ViewAction.Status(ex.Message, StatusKind.Error);
    }

    // ------------------------------------------------------------------ input

    private async Task<(ConsoleKeyInfo? Key, MouseEvent? Mouse)?> ReadAsync(CancellationToken cancellationToken)
    {
        // Polling rather than blocking keeps the frame responsive to background events —
        // a finished sync, a browser crash — without a second input thread.
        var idleFrames = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (TryReadChar(out var key))
            {
                if (key.KeyChar == '\u001b')
                {
                    // Paste markers are decoded whatever the mouse setting is, because
                    // asking for bracketed paste is what makes them arrive: leaving them
                    // undecoded would type "[200~" into whatever has focus.
                    if (TryConsumePasteMarker(out var pasting))
                    {
                        _pasting = pasting;
                        _bracketedPaste = true;
                        continue;
                    }

                    if (_options.CurrentValue.MouseSupport)
                    {
                        var mouse = MouseInput.TryDecode(() => TryReadChar(out var next) ? next.KeyChar : null);
                        if (mouse is not null)
                        {
                            return (null, mouse);
                        }
                    }
                }

                return (key, null);
            }

            if (++idleFrames >= 25)
            {
                // Roughly twice a second, let the caller redraw so header clocks tick.
                return null;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Consumes a bracketed-paste marker, if that is what follows an escape.
    /// </summary>
    /// <remarks>
    /// Characters that turn out not to be a marker are put back rather than dropped, so an
    /// escape sequence this does not recognise still reaches whatever does.
    /// </remarks>
    /// <param name="pasting">Whether a paste is starting; only meaningful when this returns true.</param>
    /// <returns><see langword="true"/> when a marker was read and consumed.</returns>
    private bool TryConsumePasteMarker(out bool pasting)
    {
        var buffer = new List<char>(PasteMode.MarkerLength);

        while (buffer.Count < PasteMode.MarkerLength && TryReadChar(out var next))
        {
            buffer.Add(next.KeyChar);
        }

        if (PasteMode.TryReadMarker(new string([.. buffer]), out pasting))
        {
            return true;
        }

        foreach (var character in buffer)
        {
            _pending.Enqueue(character);
        }

        return false;
    }

    /// <summary>
    /// Whether more input is already waiting, which is what a paste looks like from here.
    /// </summary>
    /// <remarks>
    /// Used both to hold the frame until a burst drains and, on terminals without bracketed
    /// paste, to judge whether a line break was pasted or typed. The judgement is
    /// deliberately biased: a typed Enter misread as pasted costs one extra keystroke, while
    /// a pasted line break misread as typed sends half a message and spends real credits.
    /// </remarks>
    /// <returns><see langword="true"/> when a key can be read without waiting.</returns>
    private bool InputWaiting()
    {
        if (_pending.Count > 0)
        {
            return true;
        }

        try
        {
            return Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryReadChar(out ConsoleKeyInfo key)
    {
        if (_pending.Count > 0)
        {
            var buffered = _pending.Dequeue();
            key = new ConsoleKeyInfo(buffered, default, false, false, false);
            return true;
        }

        try
        {
            if (!Console.KeyAvailable)
            {
                key = default;
                return false;
            }

            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // Standard input is redirected; there is no interactive keyboard to read.
            key = default;
            return false;
        }
    }

    // -------------------------------------------------------------- rendering

    private const int HeaderHeight = 3;
    private const int FooterHeight = 4;

    /// <summary>What the terminal's own title bar or tab was last told to say.</summary>
    private string _windowTitle = string.Empty;

    /// <summary>Puts the view you are in on the terminal's tab.</summary>
    /// <remarks>
    /// <para>
    /// Only when it changes. On Unix the setter writes an escape sequence to the same stream
    /// the live display is drawing on, and doing that on every frame is asking for the two to
    /// interleave; the title changes when you move between views, which is a handful of times
    /// a session.
    /// </para>
    /// <para>
    /// A terminal that will not take a title is not a problem worth reporting — the whole
    /// feature is a convenience for someone with several tabs open, and the application has
    /// nothing to do differently if it fails.
    /// </para>
    /// </remarks>
    private void NameTheWindow()
    {
        var wanted = _stack.Count > 0 ? $"airp — {Current.Title}" : "airp";

        if (wanted == _windowTitle)
        {
            return;
        }

        _windowTitle = wanted;

        try
        {
            Console.Title = wanted;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Nothing to do: the title is a convenience, not a feature anything depends on.
        }
    }

    private RenderContext BuildContext()
    {
        var width = Math.Max(40, SafeWidth());
        var height = Math.Max(8, SafeHeight() - HeaderHeight - FooterHeight);
        return new RenderContext(width, height, Theme.For(_options.CurrentValue.Theme), _options.CurrentValue);
    }

    private static int SafeWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 100;
        }
    }

    private static int SafeHeight()
    {
        try
        {
            return Console.WindowHeight;
        }
        catch (IOException)
        {
            return 30;
        }
    }

    private void Draw()
    {
        if (_live is null)
        {
            return;
        }

        _lastDrawTicks = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var context = BuildContext();

        NameTheWindow();

        IRenderable body;
        try
        {
            body = Current.Render(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A view failed to render.");
            body = new Markup($"[{context.Theme.Error.ToMarkup()}]This view could not be drawn: "
                              + $"{Markup.Escape(ex.Message)}[/]");
        }

        var layout = new Layout("root").SplitRows(
            new Layout("header").Update(BuildHeader(context)).Size(HeaderHeight),
            new Layout("body").Update(body),
            new Layout("footer").Update(BuildFooter(context)).Size(FooterHeight));

        _live.UpdateTarget(layout);
        _live.Refresh();
    }

    private IRenderable BuildHeader(RenderContext context)
    {
        var theme = context.Theme;
        var options = _options.CurrentValue;

        // Everything but the last one is where the reader has been; the last one is where they
        // are. Drawn identically, the trail read as four equally live places.
        var breadcrumbLine = string.Join(
            $"[{theme.Muted.ToMarkup()}] › [/]",
            _stack.Select((v, i) =>
            {
                var style = i == _stack.Count - 1 ? theme.Text : theme.Muted;
                return $"[{style.ToMarkup()}]{Markup.Escape(v.Title)}[/]";
            }));

        // A sign-in state, a browser and a sync clock are things a remote site has. This
        // adapter owns its conversations: there is no session to hold and nothing to be out
        // of date with, so the header reports the two things that are true — where the
        // conversations live, and which model is writing.
        return BuildHeaderRows(
            theme,
            $"[{theme.Badge.ToMarkup()}] Local [/]",
            Markup.Escape(options.Model.Name),
            breadcrumbLine);
    }

    /// <summary>Lays out the header: name and state on the left, adapter detail on the right.</summary>
    /// <remarks>
    /// <para>
    /// <c>Expand</c> is what makes "on the right" mean the right of the screen. A grid sizes
    /// its columns to their contents and stops there, so a right-aligned column that is not
    /// asked to expand aligns inside its own width — which is the width of the model's name.
    /// The header therefore drew the model a couple of spaces after the longer of the identity
    /// and the breadcrumb, moving from view to view as the breadcrumb grew, and reading as a
    /// gap that meant something rather than as a column.
    /// </para>
    /// <para>Internal so the alignment can be asserted; nothing else calls it.</para>
    /// </remarks>
    /// <param name="theme">The palette in force.</param>
    /// <param name="identity">Markup describing the adapter's state, already coloured.</param>
    /// <param name="detail">Escaped plain text shown muted on the right.</param>
    /// <param name="breadcrumb">Markup for the view stack.</param>
    /// <returns>The header.</returns>
    internal static IRenderable BuildHeaderRows(Theme theme, string identity, string detail, string breadcrumb)
    {
        var grid = new Grid { Expand = true };
        grid.AddColumn(new GridColumn().NoWrap());
        grid.AddColumn(new GridColumn().RightAligned().NoWrap());

        grid.AddRow(
            new Markup($"[{theme.Heading.ToMarkup()}]airp[/]  {identity}"),
            new Markup($"[{theme.Muted.ToMarkup()}]{detail}[/]"));

        grid.AddRow(new Markup(breadcrumb), new Markup(string.Empty));

        return new Rows(grid, new Rule { Style = theme.Border });
    }

    /// <summary>Columns a string occupies, named around this class's own <c>Draw</c> method.</summary>
    private static int Cells(string text) => Airp.Terminal.Ui.Draw.Width(text);

    /// <summary>What one hint costs in columns: the cap's own two spaces, and the gap after it.</summary>
    /// <remarks>
    /// Two spaces between hints rather than three, because the cap carries a coloured space of
    /// its own on each side. The legend therefore comes out the width it was before the caps
    /// rather than a column per hint wider.
    /// </remarks>
    private const int HintPadding = 4;

    /// <summary>
    /// Builds the footer legend, ending it at a hint boundary rather than mid-word.
    /// </summary>
    /// <remarks>
    /// A conversation offers thirteen strokes and they do not fit on one line of most windows.
    /// Left to the renderer the line wrapped, so the footer's height changed with the view and
    /// the last hint arrived split across two rows. What does not fit is dropped whole and
    /// pointed at instead: <c>?</c> opens the help, which lists every stroke there is.
    /// </remarks>
    /// <param name="hints">The view's hints, most useful first.</param>
    /// <param name="width">Columns the footer has.</param>
    /// <param name="theme">The palette in force.</param>
    /// <returns>Markup for one line.</returns>
    internal static string Legend(IReadOnlyList<KeyHint> hints, int width, Theme theme)
    {
        var more = new KeyHint("?", "All keys");
        var reserve = Cells(more.Key) + Cells(more.Label) + HintPadding;

        var kept = new List<KeyHint>();
        var used = 0;

        foreach (var hint in hints)
        {
            var cost = Cells(hint.Key) + Cells(hint.Label) + HintPadding;

            // The last one only has to fit the line; every earlier one has to leave room for
            // the pointer at the end, or dropping something would go unannounced.
            var room = kept.Count == hints.Count - 1 ? width : width - reserve;

            if (used + cost > room)
            {
                break;
            }

            kept.Add(hint);
            used += cost;
        }

        if (kept.Count < hints.Count)
        {
            kept.Add(more);
        }

        return string.Join(
            "  ",
            kept.Select(h =>
                $"[{theme.Key.ToMarkup()}] {Markup.Escape(h.Key)} [/]"
                + $"[{theme.Muted.ToMarkup()}]{Markup.Escape(h.Label)}[/]"));
    }

    private IRenderable BuildFooter(RenderContext context)
    {
        var theme = context.Theme;
        var rows = new List<IRenderable> { new Rule { Style = theme.Border } };

        // The key on a surface tone and the label off it, so the eye finds the strokes without
        // reading the sentences. Not a second inverted chip like the header's badge: a
        // conversation's footer carries thirteen of these, and thirteen would be a barcode.
        var hints = Current.KeyHints.Count > 0 ? Current.KeyHints : DefaultHints;

        rows.Add(new Markup(Legend(hints, SafeWidth(), theme)) { Overflow = Overflow.Ellipsis });

        if (_banner is not null)
        {
            rows.Add(new Markup(
                $"[{theme.Error.ToMarkup()}]{Markup.Escape(_banner)}[/]  "
                + $"[{theme.Muted.ToMarkup()}]{Markup.Escape(_bannerHint ?? string.Empty)}[/]")
            {
                Overflow = Overflow.Ellipsis,
            });
        }
        else if (_busyLabel.Length > 0)
        {
            var frames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            var frame = frames[_spinnerFrame % frames.Length];

            // The way out is worth spelling out here rather than in the key hints below: this
            // is the one moment when the rest of the interface is not answering, and a reader
            // who does not know Esc works has only the process to kill.
            rows.Add(new Markup(
                $"[{theme.Accent.ToMarkup()}]{frame}[/] "
                + $"[{theme.Text.ToMarkup()}]{Markup.Escape(_busyLabel)}…[/]  "
                + $"[{theme.Muted.ToMarkup()}]Esc to stop[/]"));
        }
        else if (_status.Length > 0)
        {
            var style = _statusKind switch
            {
                StatusKind.Success => theme.Success,
                StatusKind.Warning => theme.Warning,
                StatusKind.Error => theme.Error,
                _ => theme.Muted,
            };

            rows.Add(new Markup($"[{style.ToMarkup()}]{Markup.Escape(_status)}[/]")
            {
                Overflow = Overflow.Ellipsis,
            });
        }

        return new Rows(rows);
    }

    private static IReadOnlyList<KeyHint> DefaultHints { get; } =
    [
        new("Enter", "Open"),
        new("Esc", "Back"),
        new("Ctrl+P", "Commands"),
        new("F1", "Help"),
        new("Q", "Quit"),
    ];

    private static string Describe(TimeSpan span) => span switch
    {
        { TotalSeconds: < 60 } => $"{span.TotalSeconds:F0}s",
        { TotalMinutes: < 60 } => $"{span.TotalMinutes:F0}m",
        _ => $"{span.TotalHours:F0}h",
    };

    private IReadOnlyList<PaletteCommand> BuildPalette() =>
    [
        new("Refresh", "Re-read the conversations from the store",
            _ => Task.FromResult(ViewAction.Run("Refreshing", async ct =>
            {
                var result = await _synchronization.SyncNowAsync(SyncTrigger.Manual, ct).ConfigureAwait(false);
                return result.Succeeded
                    ? ViewAction.Status($"Refreshed {result.ChatCount} chats.", StatusKind.Success)
                    : ViewAction.Status($"Refresh failed: {result.Error?.Message}", StatusKind.Error);
            }))),

        new("Manage the library", "Characters, personas and snippets: create, edit, remove",
            _ => Task.FromResult(ViewAction.Push(
                ActivatorUtilities.CreateInstance<Views.LibraryView>(_services)))),

        new("Help", "Show every key binding",
            _ => Task.FromResult(ViewAction.Push(new HelpView(_options.CurrentValue.Keyboard)))),

        new("Quit", "Close the terminal client", _ => Task.FromResult(ViewAction.Quit)),
    ];

    private void OnSyncCompleted(object? sender, SyncCompleted e)
    {
        _lastSyncUtc = e.AtUtc;

        if (!e.Succeeded && e.Trigger == SyncTrigger.Timer)
        {
            _status = $"Background refresh failed: {e.Error?.Message}";
            _statusKind = StatusKind.Warning;
        }

        Draw();
    }
}

/// <summary>A view that can respond to a click on one of its rows.</summary>
internal interface IMouseAware
{
    /// <summary>Handles a click.</summary>
    /// <param name="row">Zero-based row within the view's body.</param>
    /// <param name="context">Layout context.</param>
    /// <returns>What the shell should do next.</returns>
    ViewAction OnClick(int row, RenderContext context);
}
