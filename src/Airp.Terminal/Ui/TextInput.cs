using Airp.Application.Text;
using Spectre.Console;

namespace Airp.Terminal.Ui;

/// <summary>
/// A single-line editable field rendered inside the shell rather than by a blocking prompt.
/// </summary>
/// <remarks>
/// Spectre's own prompts take over the console and read a whole line before returning, which
/// would freeze the rest of the screen and break the incremental filtering the list and
/// search views depend on. This keeps every keystroke inside the shell's own loop.
/// </remarks>
internal sealed class TextInput
{
    private string _value = string.Empty;
    private int _cursor;

    /// <summary>Placeholder shown when the field is empty.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>The current text.</summary>
    public string Value
    {
        get => _value;
        set
        {
            _value = value ?? string.Empty;
            _cursor = _value.Length;
        }
    }

    /// <summary>Whether the field holds no text.</summary>
    public bool IsEmpty => _value.Length == 0;

    /// <summary>Clears the field.</summary>
    public void Clear()
    {
        _value = string.Empty;
        _cursor = 0;
    }

    /// <summary>
    /// Applies a key press to the field.
    /// </summary>
    /// <param name="stroke">The resolved key.</param>
    /// <returns><see langword="true"/> when the key was consumed by the field.</returns>
    public bool Handle(KeyStroke stroke)
    {
        switch (stroke.Command)
        {
            case AppCommand.Character when stroke.Character >= ' ' && !char.IsControl(stroke.Character):
                _value = _value[.._cursor] + stroke.Character + _value[_cursor..];
                _cursor++;
                return true;

            // Moving and deleting step a whole grapheme cluster, so a pasted emoji behaves
            // like the one character it looks like rather than the two halves it is stored as.
            case AppCommand.DeleteBack when _cursor > 0:
                var previous = Graphemes.Previous(_value, _cursor);
                _value = _value[..previous] + _value[_cursor..];
                _cursor = previous;
                return true;

            case AppCommand.DeleteForward when _cursor < _value.Length:
                _value = _value[.._cursor] + _value[Graphemes.Next(_value, _cursor)..];
                return true;

            case AppCommand.MoveLeft when _cursor > 0:
                _cursor = Graphemes.Previous(_value, _cursor);
                return true;

            case AppCommand.MoveRight when _cursor < _value.Length:
                _cursor = Graphemes.Next(_value, _cursor);
                return true;

            case AppCommand.Home:
                _cursor = 0;
                return true;

            case AppCommand.End:
                _cursor = _value.Length;
                return true;

            default:
                return false;
        }
    }

    /// <summary>Renders the field as Spectre markup, including a block caret.</summary>
    /// <param name="theme">Active palette.</param>
    /// <param name="focused">Whether to draw the caret.</param>
    /// <returns>Markup ready to embed in a line.</returns>
    public string ToMarkup(Theme theme, bool focused = true)
    {
        if (_value.Length == 0)
        {
            var placeholder = Markup.Escape(Placeholder);
            return focused
                ? $"[{theme.Selection.ToMarkup()}] [/][{theme.Muted.ToMarkup()}]{placeholder}[/]"
                : $"[{theme.Muted.ToMarkup()}]{placeholder}[/]";
        }

        if (!focused)
        {
            return $"[{theme.Text.ToMarkup()}]{Markup.Escape(_value)}[/]";
        }

        // The caret highlights the whole cluster it sits on. Highlighting one char of a
        // surrogate pair would split the emoji across two styled runs and draw two tofu boxes.
        var after = Graphemes.Next(_value, _cursor);
        var head = Markup.Escape(_value[.._cursor]);
        var caret = _cursor < _value.Length ? Markup.Escape(_value[_cursor..after]) : " ";
        var tail = _cursor < _value.Length ? Markup.Escape(_value[after..]) : string.Empty;

        return $"[{theme.Text.ToMarkup()}]{head}[/]"
               + $"[{theme.Selection.ToMarkup()}]{caret}[/]"
               + $"[{theme.Text.ToMarkup()}]{tail}[/]";
    }
}
