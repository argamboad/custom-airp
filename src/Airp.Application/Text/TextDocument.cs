namespace Airp.Application.Text;

/// <summary>A zero-based caret position inside a document.</summary>
/// <param name="Line">Zero-based line index.</param>
/// <param name="Column">Zero-based column index; may equal the line length (caret past the last character).</param>
public readonly record struct TextPosition(int Line, int Column) : IComparable<TextPosition>
{
    /// <inheritdoc />
    public int CompareTo(TextPosition other)
        => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    /// <summary>Whether the left position precedes the right one.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> comes first.</returns>
    public static bool operator <(TextPosition left, TextPosition right) => left.CompareTo(right) < 0;

    /// <summary>Whether the left position follows the right one.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> comes last.</returns>
    public static bool operator >(TextPosition left, TextPosition right) => left.CompareTo(right) > 0;

    /// <summary>Whether the left position precedes or equals the right one.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not come after.</returns>
    public static bool operator <=(TextPosition left, TextPosition right) => left.CompareTo(right) <= 0;

    /// <summary>Whether the left position follows or equals the right one.</summary>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> does not come before.</returns>
    public static bool operator >=(TextPosition left, TextPosition right) => left.CompareTo(right) >= 0;
}

/// <summary>A single search result inside a document.</summary>
/// <param name="Position">Where the match starts.</param>
/// <param name="Length">How many characters matched.</param>
public readonly record struct TextMatch(TextPosition Position, int Length);

/// <summary>
/// The editable buffer behind every multi-line box in the app — the message composer and
/// the regenerate instructions: a line list, a caret, and an undo history.
/// </summary>
/// <remarks>
/// <para>
/// This type is pure model with no console or browser dependency, which is what makes the
/// editor testable. The view renders it and forwards key presses; every mutation goes
/// through a method here.
/// </para>
/// <para>
/// Undo works on whole-document snapshots. A message is capped at 12,000 characters, so the
/// simplicity is worth far more than the memory a delta-based history would save.
/// Consecutive keystrokes of the same kind coalesce into one undo step, so a burst of
/// typing is undone as a unit rather than one character at a time.
/// </para>
/// <para>Instances are not thread-safe; the UI owns one per open editor.</para>
/// </remarks>
public sealed class TextDocument
{
    private const int DefaultUndoDepth = 200;

    private readonly List<string> _lines;
    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private readonly int _undoDepth;

    private string _savedText;
    private EditKind _lastEditKind = EditKind.None;
    private int _desiredColumn;

    private TextDocument(IEnumerable<string> lines, int undoDepth)
    {
        _lines = [.. lines];
        if (_lines.Count == 0)
        {
            _lines.Add(string.Empty);
        }

        _undoDepth = undoDepth;
        _savedText = BuildText(_lines);
    }

    private enum EditKind
    {
        None = 0,
        Typing,
        Deleting,
        Structural,
    }

    /// <summary>Creates a document from raw text. <c>\r\n</c> and <c>\r</c> are normalised to <c>\n</c>.</summary>
    /// <param name="text">Initial contents.</param>
    /// <param name="undoDepth">How many undo steps to retain.</param>
    /// <returns>A new document with the caret at the start.</returns>
    public static TextDocument FromText(string? text, int undoDepth = DefaultUndoDepth)
        => new(Split(text ?? string.Empty), Math.Max(1, undoDepth));

    /// <summary>The current lines. Never empty: an empty document has one empty line.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Number of lines.</summary>
    public int LineCount => _lines.Count;

    /// <summary>Zero-based caret line.</summary>
    public int CursorLine { get; private set; }

    /// <summary>Zero-based caret column.</summary>
    public int CursorColumn { get; private set; }

    /// <summary>The caret position.</summary>
    public TextPosition Cursor => new(CursorLine, CursorColumn);

    /// <summary>The full document text with <c>\n</c> line endings.</summary>
    public string Text => BuildText(_lines);

    /// <summary>Whether the text differs from the last <see cref="MarkSaved"/> point.</summary>
    public bool IsDirty => !string.Equals(Text, _savedText, StringComparison.Ordinal);

    /// <summary>Whether <see cref="Undo"/> would do anything.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether <see cref="Redo"/> would do anything.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Number of whitespace-separated words.</summary>
    public int WordCount => CountWords(Text);

    /// <summary>Number of characters, excluding the synthetic line separators.</summary>
    public int CharacterCount => _lines.Sum(static line => line.Length);

    /// <summary>Marks the current text as the saved baseline, clearing <see cref="IsDirty"/>.</summary>
    public void MarkSaved() => _savedText = Text;

    /// <summary>Replaces the whole document, keeping the caret in range. Undoable.</summary>
    /// <param name="text">The new contents.</param>
    public void SetText(string? text)
    {
        PushUndo(EditKind.Structural);
        _lines.Clear();
        _lines.AddRange(Split(text ?? string.Empty));
        if (_lines.Count == 0)
        {
            _lines.Add(string.Empty);
        }

        ClampCursor();
    }

    // ---------------------------------------------------------------- editing

    /// <summary>
    /// Inserts text at the caret. Embedded newlines split the line, so this doubles as paste.
    /// </summary>
    /// <param name="text">Text to insert.</param>
    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var multiline = text.Contains('\n') || text.Contains('\r');
        PushUndo(multiline ? EditKind.Structural : EditKind.Typing);

        var segments = Split(text);
        var line = _lines[CursorLine];
        var head = line[..CursorColumn];
        var tail = line[CursorColumn..];

        if (segments.Count == 1)
        {
            _lines[CursorLine] = head + segments[0] + tail;
            CursorColumn += segments[0].Length;
        }
        else
        {
            _lines[CursorLine] = head + segments[0];
            for (var i = 1; i < segments.Count; i++)
            {
                _lines.Insert(CursorLine + i, segments[i]);
            }

            CursorLine += segments.Count - 1;
            CursorColumn = segments[^1].Length;
            _lines[CursorLine] += tail;
        }

        _desiredColumn = CursorColumn;
    }

    /// <summary>Inserts a line break at the caret.</summary>
    public void InsertNewLine()
    {
        PushUndo(EditKind.Structural);

        var line = _lines[CursorLine];
        _lines[CursorLine] = line[..CursorColumn];
        _lines.Insert(CursorLine + 1, line[CursorColumn..]);
        CursorLine++;
        CursorColumn = 0;
        _desiredColumn = 0;
    }

    /// <summary>Deletes the character before the caret, joining lines at column zero.</summary>
    /// <remarks>
    /// A "character" here is a whole grapheme cluster. Deleting one <see cref="char"/> would
    /// take half of an emoji and leave a lone surrogate behind, so one press removes what the
    /// user sees as one character however many code units that turns out to be.
    /// </remarks>
    /// <returns><see langword="true"/> when something was deleted.</returns>
    public bool Backspace()
    {
        if (CursorLine == 0 && CursorColumn == 0)
        {
            return false;
        }

        PushUndo(CursorColumn == 0 ? EditKind.Structural : EditKind.Deleting);

        if (CursorColumn > 0)
        {
            var line = _lines[CursorLine];
            var start = Graphemes.Previous(line, CursorColumn);
            _lines[CursorLine] = line[..start] + line[CursorColumn..];
            CursorColumn = start;
        }
        else
        {
            var current = _lines[CursorLine];
            _lines.RemoveAt(CursorLine);
            CursorLine--;
            CursorColumn = _lines[CursorLine].Length;
            _lines[CursorLine] += current;
        }

        _desiredColumn = CursorColumn;
        return true;
    }

    /// <summary>Deletes the character at the caret, joining lines at end of line.</summary>
    /// <returns><see langword="true"/> when something was deleted.</returns>
    public bool DeleteForward()
    {
        var line = _lines[CursorLine];
        var atEnd = CursorColumn >= line.Length;

        if (atEnd && CursorLine == _lines.Count - 1)
        {
            return false;
        }

        PushUndo(atEnd ? EditKind.Structural : EditKind.Deleting);

        if (!atEnd)
        {
            // A whole cluster, for the reason given on Backspace.
            _lines[CursorLine] = line[..CursorColumn] + line[Graphemes.Next(line, CursorColumn)..];
        }
        else
        {
            _lines[CursorLine] = line + _lines[CursorLine + 1];
            _lines.RemoveAt(CursorLine + 1);
        }

        return true;
    }

    /// <summary>
    /// Replaces a run within a single line, leaving the caret after the replacement.
    /// </summary>
    /// <remarks>
    /// Written for completion, where the text being replaced is the token the user typed and
    /// the replacement is what they picked. It is one undo step, so undo restores the typed
    /// token rather than deleting the whole word — accepting the wrong suggestion costs one
    /// press to reverse.
    /// </remarks>
    /// <param name="line">Zero-based line index.</param>
    /// <param name="start">Where the run begins.</param>
    /// <param name="length">How many characters to replace.</param>
    /// <param name="replacement">What to put there; may be empty but not null.</param>
    /// <returns><see langword="true"/> when the range was valid and the text changed.</returns>
    public bool ReplaceRange(int line, int start, int length, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (line < 0 || line >= _lines.Count || start < 0 || length < 0 || start + length > _lines[line].Length)
        {
            return false;
        }

        PushUndo(EditKind.Structural);

        var text = _lines[line];
        _lines[line] = text[..start] + replacement + text[(start + length)..];

        CursorLine = line;
        CursorColumn = start + replacement.Length;
        _desiredColumn = CursorColumn;
        return true;
    }

    /// <summary>Deletes the caret's line.</summary>
    /// <returns><see langword="true"/> when something was deleted.</returns>
    public bool DeleteLine()
    {
        PushUndo(EditKind.Structural);

        if (_lines.Count == 1)
        {
            if (_lines[0].Length == 0)
            {
                _undo.RemoveAt(_undo.Count - 1);
                return false;
            }

            _lines[0] = string.Empty;
        }
        else
        {
            _lines.RemoveAt(CursorLine);
        }

        ClampCursor();
        CursorColumn = 0;
        _desiredColumn = 0;
        return true;
    }

    /// <summary>Deletes from the caret to the end of the line.</summary>
    /// <returns><see langword="true"/> when something was deleted.</returns>
    public bool DeleteToEndOfLine()
    {
        var line = _lines[CursorLine];
        if (CursorColumn >= line.Length)
        {
            return false;
        }

        PushUndo(EditKind.Structural);
        _lines[CursorLine] = line[..CursorColumn];
        return true;
    }

    // ------------------------------------------------------------ undo / redo

    /// <summary>Reverts the last edit.</summary>
    /// <returns><see langword="true"/> when an edit was reverted.</returns>
    public bool Undo()
    {
        if (_undo.Count == 0)
        {
            return false;
        }

        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Capture());
        Restore(snapshot);
        _lastEditKind = EditKind.None;
        return true;
    }

    /// <summary>Reapplies the last reverted edit.</summary>
    /// <returns><see langword="true"/> when an edit was reapplied.</returns>
    public bool Redo()
    {
        if (_redo.Count == 0)
        {
            return false;
        }

        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Capture());
        Restore(snapshot);
        _lastEditKind = EditKind.None;
        return true;
    }

    // ------------------------------------------------------------- navigation

    /// <summary>Moves the caret to an arbitrary position, clamped to the document.</summary>
    /// <param name="line">Target line.</param>
    /// <param name="column">Target column.</param>
    public void MoveTo(int line, int column)
    {
        CursorLine = Math.Clamp(line, 0, _lines.Count - 1);

        // Snapped, not just clamped. Columns reach this method from outside the document — a
        // mouse click, a search hit, a remembered column on another line — and any of them can
        // name an offset that falls inside a cluster.
        CursorColumn = Graphemes.Snap(_lines[CursorLine], column);
        _desiredColumn = CursorColumn;
        _lastEditKind = EditKind.None;
    }

    /// <summary>Moves the caret to a position, clamped to the document.</summary>
    /// <param name="position">Target position.</param>
    public void MoveTo(TextPosition position) => MoveTo(position.Line, position.Column);

    /// <summary>Moves one character left, wrapping to the previous line.</summary>
    /// <remarks>One grapheme cluster, so the caret never lands inside an emoji.</remarks>
    public void MoveLeft()
    {
        if (CursorColumn > 0)
        {
            CursorColumn = Graphemes.Previous(_lines[CursorLine], CursorColumn);
        }
        else if (CursorLine > 0)
        {
            CursorLine--;
            CursorColumn = _lines[CursorLine].Length;
        }

        _desiredColumn = CursorColumn;
        _lastEditKind = EditKind.None;
    }

    /// <summary>Moves one character right, wrapping to the next line.</summary>
    /// <remarks>One grapheme cluster, so the caret never lands inside an emoji.</remarks>
    public void MoveRight()
    {
        if (CursorColumn < _lines[CursorLine].Length)
        {
            CursorColumn = Graphemes.Next(_lines[CursorLine], CursorColumn);
        }
        else if (CursorLine < _lines.Count - 1)
        {
            CursorLine++;
            CursorColumn = 0;
        }

        _desiredColumn = CursorColumn;
        _lastEditKind = EditKind.None;
    }

    /// <summary>
    /// Moves the caret vertically, remembering the column the user last chose horizontally so
    /// travelling through short lines does not lose the position.
    /// </summary>
    /// <param name="delta">Number of lines to move; negative moves up.</param>
    public void MoveVertical(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        CursorLine = Math.Clamp(CursorLine + delta, 0, _lines.Count - 1);
        CursorColumn = Graphemes.Snap(_lines[CursorLine], Math.Min(_desiredColumn, _lines[CursorLine].Length));
        _lastEditKind = EditKind.None;
    }

    /// <summary>Moves to the first non-whitespace character, or to column zero if already there.</summary>
    public void MoveToLineStart()
    {
        var line = _lines[CursorLine];
        var indent = 0;
        while (indent < line.Length && char.IsWhiteSpace(line[indent]))
        {
            indent++;
        }

        CursorColumn = CursorColumn == indent ? 0 : indent;
        _desiredColumn = CursorColumn;
        _lastEditKind = EditKind.None;
    }

    /// <summary>Moves to the end of the current line.</summary>
    public void MoveToLineEnd()
    {
        CursorColumn = _lines[CursorLine].Length;
        _desiredColumn = CursorColumn;
        _lastEditKind = EditKind.None;
    }

    /// <summary>Moves to the very start of the document.</summary>
    public void MoveToDocumentStart() => MoveTo(0, 0);

    /// <summary>Moves to the very end of the document.</summary>
    public void MoveToDocumentEnd() => MoveTo(_lines.Count - 1, _lines[^1].Length);

    /// <summary>Moves to the start of the next word.</summary>
    public void MoveWordRight()
    {
        var (line, column) = (CursorLine, CursorColumn);
        var text = _lines[line];

        while (column < text.Length && char.IsLetterOrDigit(text[column]))
        {
            column++;
        }

        while (column < text.Length && !char.IsLetterOrDigit(text[column]))
        {
            column++;
        }

        if (column >= text.Length && line < _lines.Count - 1)
        {
            MoveTo(line + 1, 0);
            return;
        }

        MoveTo(line, column);
    }

    /// <summary>Moves to the start of the previous word.</summary>
    public void MoveWordLeft()
    {
        var (line, column) = (CursorLine, CursorColumn);
        if (column == 0)
        {
            if (line == 0)
            {
                return;
            }

            MoveTo(line - 1, _lines[line - 1].Length);
            return;
        }

        var text = _lines[line];
        column--;

        while (column > 0 && !char.IsLetterOrDigit(text[column]))
        {
            column--;
        }

        while (column > 0 && char.IsLetterOrDigit(text[column - 1]))
        {
            column--;
        }

        MoveTo(line, column);
    }

    // ----------------------------------------------------------------- search

    /// <summary>Finds every occurrence of <paramref name="query"/>.</summary>
    /// <param name="query">Literal text to find; empty returns nothing.</param>
    /// <param name="ignoreCase">Whether to compare case-insensitively.</param>
    /// <returns>All matches, in document order.</returns>
    public IReadOnlyList<TextMatch> FindAll(string? query, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var results = new List<TextMatch>();

        for (var line = 0; line < _lines.Count; line++)
        {
            var index = 0;
            while (index <= _lines[line].Length - query.Length)
            {
                var found = _lines[line].IndexOf(query, index, comparison);
                if (found < 0)
                {
                    break;
                }

                results.Add(new TextMatch(new TextPosition(line, found), query.Length));
                index = found + Math.Max(1, query.Length);
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the next occurrence at or after <paramref name="from"/>, wrapping to the top.
    /// </summary>
    /// <param name="query">Literal text to find.</param>
    /// <param name="from">Where to start; defaults to just after the caret.</param>
    /// <param name="ignoreCase">Whether to compare case-insensitively.</param>
    /// <param name="backwards">Search towards the start of the document instead.</param>
    /// <returns>The match, or <see langword="null"/> when the query is absent.</returns>
    public TextMatch? Find(
        string? query,
        TextPosition? from = null,
        bool ignoreCase = true,
        bool backwards = false)
    {
        var all = FindAll(query, ignoreCase);
        if (all.Count == 0)
        {
            return null;
        }

        var origin = from ?? new TextPosition(CursorLine, CursorColumn);

        if (backwards)
        {
            for (var i = all.Count - 1; i >= 0; i--)
            {
                if (all[i].Position < origin)
                {
                    return all[i];
                }
            }

            return all[^1];
        }

        foreach (var match in all)
        {
            if (match.Position >= origin)
            {
                return match;
            }
        }

        return all[0];
    }

    /// <summary>
    /// Replaces the next occurrence at or after the caret and leaves the caret after it.
    /// </summary>
    /// <param name="query">Literal text to find.</param>
    /// <param name="replacement">Replacement text.</param>
    /// <param name="ignoreCase">Whether to compare case-insensitively.</param>
    /// <returns><see langword="true"/> when a replacement was made.</returns>
    public bool ReplaceNext(string? query, string replacement, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        var match = Find(query, ignoreCase: ignoreCase);
        if (match is not { } hit)
        {
            return false;
        }

        PushUndo(EditKind.Structural);

        var line = _lines[hit.Position.Line];
        _lines[hit.Position.Line] =
            line[..hit.Position.Column] + replacement + line[(hit.Position.Column + hit.Length)..];

        CursorLine = hit.Position.Line;
        CursorColumn = hit.Position.Column + replacement.Length;
        _desiredColumn = CursorColumn;
        return true;
    }

    /// <summary>Replaces every occurrence.</summary>
    /// <param name="query">Literal text to find.</param>
    /// <param name="replacement">Replacement text.</param>
    /// <param name="ignoreCase">Whether to compare case-insensitively.</param>
    /// <returns>How many replacements were made.</returns>
    public int ReplaceAll(string? query, string replacement, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        var matches = FindAll(query, ignoreCase);
        if (matches.Count == 0)
        {
            return 0;
        }

        PushUndo(EditKind.Structural);

        // Walk backwards so earlier offsets stay valid as later ones are rewritten.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var line = _lines[match.Position.Line];
            _lines[match.Position.Line] =
                line[..match.Position.Column] + replacement + line[(match.Position.Column + match.Length)..];
        }

        ClampCursor();
        return matches.Count;
    }

    // ---------------------------------------------------------------- helpers

    private static List<string> Split(string text)
        => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace('\r', '\n')
                   .Split('\n')];

    private static string BuildText(List<string> lines) => string.Join('\n', lines);

    internal static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    private void ClampCursor()
    {
        CursorLine = Math.Clamp(CursorLine, 0, _lines.Count - 1);
        CursorColumn = Graphemes.Snap(_lines[CursorLine], CursorColumn);
        _desiredColumn = CursorColumn;
    }

    private Snapshot Capture() => new(BuildText(_lines), CursorLine, CursorColumn);

    private void Restore(Snapshot snapshot)
    {
        _lines.Clear();
        _lines.AddRange(Split(snapshot.Text));
        if (_lines.Count == 0)
        {
            _lines.Add(string.Empty);
        }

        CursorLine = snapshot.Line;
        CursorColumn = snapshot.Column;
        ClampCursor();
    }

    private void PushUndo(EditKind kind)
    {
        _redo.Clear();

        // Coalesce a run of same-kind character edits into a single undo step.
        if (kind != EditKind.Structural && kind == _lastEditKind && _undo.Count > 0)
        {
            _lastEditKind = kind;
            return;
        }

        _undo.Add(Capture());
        if (_undo.Count > _undoDepth)
        {
            _undo.RemoveAt(0);
        }

        _lastEditKind = kind;
    }

    private readonly record struct Snapshot(string Text, int Line, int Column);
}
