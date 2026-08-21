using Airp.Application.Options;

namespace Airp.Terminal.Ui;

/// <summary>Every logical action the shell and its views understand.</summary>
internal enum AppCommand
{
    None = 0,
    MoveUp,
    MoveDown,
    MoveLeft,
    MoveRight,
    PageUp,
    PageDown,
    Home,
    End,
    WordLeft,
    WordRight,
    Accept,
    Back,
    Quit,
    Refresh,
    ClearScreen,
    Search,
    GlobalSearch,
    SearchNext,
    SearchPrevious,
    Edit,
    Save,
    Generate,
    History,
    Diff,
    Favorite,
    Copy,
    Export,
    Help,
    CommandPalette,
    Undo,
    Redo,
    Replace,
    DeleteLine,
    DeleteBack,
    DeleteForward,
    NewLine,
    Tab,
    ToggleLineNumbers,
    Settings,
    Delete,
    Rename,
    Character,
}

/// <summary>A resolved key press.</summary>
/// <param name="Command">The logical action.</param>
/// <param name="Character">The literal character, when <paramref name="Command"/> is <see cref="AppCommand.Character"/>.</param>
/// <param name="Key">The raw key press, for views that need modifiers.</param>
internal readonly record struct KeyStroke(AppCommand Command, char Character, ConsoleKeyInfo Key)
{
    /// <summary>
    /// Whether this key arrived as part of pasted text rather than being typed.
    /// </summary>
    /// <remarks>
    /// A pasted line break is content, not a command. Without this distinction, pasting two
    /// paragraphs into a message composer sends the first one — which on this site spends
    /// the account's credits and posts half a message.
    /// </remarks>
    public bool Pasted { get; init; }
}

/// <summary>Which kind of surface has focus, which changes how letters are interpreted.</summary>
internal enum KeyContext
{
    /// <summary>A list or read-only view: single letters are shortcuts.</summary>
    Navigation = 0,

    /// <summary>A text field or editor: letters are literal input.</summary>
    Text,
}

/// <summary>
/// Translates raw key presses into logical commands.
/// </summary>
/// <remarks>
/// <para>
/// Two dialects share one command vocabulary. In <see cref="KeyboardMode.Standard"/> the
/// arrow keys and single-letter shortcuts drive everything. In
/// <see cref="KeyboardMode.Vim"/> the standard bindings still work — nobody wants an arrow
/// key to stop functioning — and <c>hjkl</c>, <c>G</c>, <c>n</c>/<c>N</c> and <c>u</c> are
/// layered on top. Only where the reader is navigating: <see cref="KeyContext.Text"/> takes
/// every printable key as itself, so there is no mode to be in and none to leave.
/// </para>
/// <para>
/// This used to claim <c>gg</c> and <c>Ctrl+R</c>. There is no <c>gg</c> — <c>g</c> has no
/// binding in either dialect and is typed — and <c>Ctrl+R</c> is Refresh; redo is
/// <c>Ctrl+Shift+Z</c> or <c>Ctrl+Y</c>, the same in both.
/// </para>
/// <para>
/// Control chords are identical in both dialects and are resolved before anything else, so
/// <c>Ctrl+F</c> means search whether or not you are typing.
/// </para>
/// </remarks>
internal static class KeyMap
{
    /// <summary>Resolves a key press.</summary>
    /// <param name="key">The raw key press.</param>
    /// <param name="mode">The configured keyboard dialect.</param>
    /// <param name="context">Whether a text surface has focus.</param>
    /// <returns>The resolved stroke.</returns>
    public static KeyStroke Resolve(ConsoleKeyInfo key, KeyboardMode mode, KeyContext context)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        if (control)
        {
            var command = key.Key switch
            {
                ConsoleKey.R => AppCommand.Refresh,
                ConsoleKey.L => AppCommand.ClearScreen,
                ConsoleKey.F => AppCommand.GlobalSearch,
                ConsoleKey.S => AppCommand.Save,
                ConsoleKey.G => AppCommand.Generate,
                ConsoleKey.E => AppCommand.Export,
                ConsoleKey.H => AppCommand.History,
                ConsoleKey.D => AppCommand.Diff,
                ConsoleKey.P => AppCommand.CommandPalette,
                ConsoleKey.Z => shift ? AppCommand.Redo : AppCommand.Undo,
                ConsoleKey.Y => AppCommand.Redo,
                ConsoleKey.LeftArrow => AppCommand.WordLeft,
                ConsoleKey.RightArrow => AppCommand.WordRight,
                ConsoleKey.Home => AppCommand.Home,
                ConsoleKey.End => AppCommand.End,
                _ => AppCommand.None,
            };

            if (command != AppCommand.None)
            {
                return new KeyStroke(command, '\0', key);
            }
        }

        var structural = key.Key switch
        {
            ConsoleKey.UpArrow => AppCommand.MoveUp,
            ConsoleKey.DownArrow => AppCommand.MoveDown,
            ConsoleKey.LeftArrow => AppCommand.MoveLeft,
            ConsoleKey.RightArrow => AppCommand.MoveRight,
            ConsoleKey.PageUp => AppCommand.PageUp,
            ConsoleKey.PageDown => AppCommand.PageDown,
            ConsoleKey.Home => AppCommand.Home,
            ConsoleKey.End => AppCommand.End,
            ConsoleKey.Enter => context == KeyContext.Text ? AppCommand.NewLine : AppCommand.Accept,
            ConsoleKey.Escape => AppCommand.Back,
            ConsoleKey.Tab => AppCommand.Tab,
            ConsoleKey.Backspace => AppCommand.DeleteBack,
            // Delete means "remove a character" while typing and "remove this thing" while
            // reading, and conflating the two would put a destructive action one stray key
            // away from an editor.
            ConsoleKey.Delete => context == KeyContext.Text ? AppCommand.DeleteForward : AppCommand.Delete,
            ConsoleKey.F1 => AppCommand.Help,
            ConsoleKey.F2 => AppCommand.Rename,
            ConsoleKey.F5 => AppCommand.Refresh,
            _ => AppCommand.None,
        };

        if (structural != AppCommand.None)
        {
            return new KeyStroke(structural, '\0', key);
        }

        if (context == KeyContext.Text)
        {
            return key.KeyChar >= ' ' && !char.IsControl(key.KeyChar)
                ? new KeyStroke(AppCommand.Character, key.KeyChar, key)
                : new KeyStroke(AppCommand.None, '\0', key);
        }

        var shortcut = key.KeyChar switch
        {
            'q' or 'Q' => AppCommand.Quit,
            'r' or 'R' => AppCommand.Refresh,
            'e' or 'E' => AppCommand.Edit,
            'g' when mode != KeyboardMode.Vim => AppCommand.Generate,
            'G' when mode != KeyboardMode.Vim => AppCommand.Generate,
            'G' when mode == KeyboardMode.Vim => AppCommand.End,
            '/' => AppCommand.Search,
            '?' => AppCommand.Help,
            'f' or 'F' => AppCommand.Favorite,
            'c' or 'C' => AppCommand.Copy,
            'x' or 'X' => AppCommand.Export,
            'h' when mode == KeyboardMode.Vim => AppCommand.MoveLeft,
            'j' when mode == KeyboardMode.Vim => AppCommand.MoveDown,
            'k' when mode == KeyboardMode.Vim => AppCommand.MoveUp,
            'l' when mode == KeyboardMode.Vim => AppCommand.MoveRight,
            'n' when mode == KeyboardMode.Vim => AppCommand.SearchNext,
            'N' when mode == KeyboardMode.Vim => AppCommand.SearchPrevious,
            'u' when mode == KeyboardMode.Vim => AppCommand.Undo,
            'n' or 'N' when mode != KeyboardMode.Vim => AppCommand.SearchNext,
            'p' or 'P' => AppCommand.CommandPalette,
            'v' or 'V' => AppCommand.History,
            'd' or 'D' => AppCommand.Diff,
            's' or 'S' => AppCommand.Settings,
            // Hints are written as capitals for legibility, but nobody holds shift to press
            // them. Outside vim mode — where `l` means "move right" — both cases must work.
            'L' => AppCommand.ToggleLineNumbers,
            'l' when mode != KeyboardMode.Vim => AppCommand.ToggleLineNumbers,
            ':' => AppCommand.CommandPalette,
            _ => AppCommand.None,
        };

        return shortcut != AppCommand.None
            ? new KeyStroke(shortcut, key.KeyChar, key)
            : new KeyStroke(AppCommand.Character, key.KeyChar, key);
    }
}
