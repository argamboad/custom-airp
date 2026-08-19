using System.Text;

namespace Airp.Application.Text;

/// <summary>What a stretch of a reply is doing.</summary>
public enum ProseKind
{
    /// <summary>Neither marked as action nor quoted. Drawn plainly.</summary>
    Narration,

    /// <summary>Was wrapped in asterisks. Action, or the narrator's voice.</summary>
    Action,

    /// <summary>Was in quotation marks. Words said aloud.</summary>
    Dialogue,
}

/// <summary>A stretch of formatted text, as offsets into the stripped result.</summary>
/// <param name="Start">Where it begins, in the stripped text.</param>
/// <param name="Length">How many characters it covers.</param>
/// <param name="Kind">What it is.</param>
public readonly record struct ProseRun(int Start, int Length, ProseKind Kind);

/// <summary>The markers taken out, and what was under them.</summary>
/// <param name="Text">The line with its markers removed. This is what gets wrapped and drawn.</param>
/// <param name="Runs">Contiguous stretches covering the whole of <paramref name="Text"/>, in order.</param>
public readonly record struct FormattedProse(string Text, IReadOnlyList<ProseRun> Runs);

/// <summary>
/// Reads the conventions a roleplay reply is written in, so the terminal can draw them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is display only.</strong> Nothing here touches what is stored, exported, sent
/// to the model, embedded, summarised or hashed — the message keeps its asterisks and its
/// quotation marks for good, because those are what the model wrote and what the next prompt
/// has to send back unchanged for the prefix cache to hold.
/// </para>
/// <para>
/// The convention is a single pair of asterisks. Doubled ones are read the same way rather
/// than as a second convention: counted across this project's own transcripts they turn up
/// about once for every ten single pairs — a model reaching for Markdown, not the reader
/// meaning something else — and a line that did it would otherwise show its markers.
/// </para>
/// <para>
/// An unclosed marker is left exactly as it was typed. The alternative is worse than useless:
/// a reply cut off mid-action would dim everything after it, and a stray asterisk in the middle
/// of a sentence would swallow the rest of the paragraph.
/// </para>
/// <para>All members are pure and thread-safe.</para>
/// </remarks>
public static class ProseFormat
{
    /// <summary>
    /// Splits one line into its runs, removing the markers that delimited them.
    /// </summary>
    /// <remarks>
    /// One line at a time, because the renderer wraps line by line and a run that crossed a
    /// newline could not be drawn anyway. A marker left open at the end of a line therefore
    /// stays literal, which is the same rule an unclosed marker gets anywhere else.
    /// </remarks>
    /// <param name="line">The line, with its markers still in it. Must not contain newlines.</param>
    /// <returns>The stripped text and the runs covering it.</returns>
    public static FormattedProse Format(string? line)
    {
        var text = line ?? string.Empty;

        if (text.Length == 0)
        {
            return new FormattedProse(string.Empty, []);
        }

        var output = new StringBuilder(text.Length);
        var runs = new List<ProseRun>();
        var plainFrom = 0;
        var i = 0;

        void ClosePlain()
        {
            if (output.Length > plainFrom)
            {
                runs.Add(new ProseRun(plainFrom, output.Length - plainFrom, ProseKind.Narration));
            }
        }

        void Emit(string inner, ProseKind kind)
        {
            ClosePlain();
            runs.Add(new ProseRun(output.Length, inner.Length, kind));
            output.Append(inner);
            plainFrom = output.Length;
        }

        while (i < text.Length)
        {
            var here = text[i];

            if (here == '*')
            {
                var doubled = i + 1 < text.Length && text[i + 1] == '*';
                var marker = doubled ? 2 : 1;

                if (Closing(text, i + marker, doubled) is { } close)
                {
                    Emit(text[(i + marker)..close], ProseKind.Action);
                    i = close + marker;
                    continue;
                }
            }
            else if (Opener(here) is { } closer)
            {
                if (Quoted(text, i + 1, closer) is { } close)
                {
                    Emit(text[(i + 1)..close], ProseKind.Dialogue);
                    i = close + 1;
                    continue;
                }
            }

            output.Append(here);
            i++;
        }

        ClosePlain();

        return new FormattedProse(output.ToString(), runs);
    }

    /// <summary>The closing mark that matches an opening quotation mark, if it is one.</summary>
    private static char? Opener(char c) => c switch
    {
        '"' => '"',
        '“' => '”',
        _ => null,
    };

    /// <summary>
    /// Finds the asterisk run that closes one opened at <paramref name="from"/>.
    /// </summary>
    /// <remarks>
    /// The content has to start and end on something other than a space. Without that,
    /// arithmetic and stray punctuation — <c>2 * 3 * 4</c>, a bullet at the head of a line —
    /// read as actions and half the message goes grey.
    /// </remarks>
    /// <param name="text">The line.</param>
    /// <param name="from">First character after the opening marker.</param>
    /// <param name="doubled">Whether the marker was two asterisks rather than one.</param>
    /// <returns>Index of the closing marker, or <see langword="null"/> when it never closes.</returns>
    private static int? Closing(string text, int from, bool doubled)
    {
        if (from >= text.Length || char.IsWhiteSpace(text[from]))
        {
            return null;
        }

        for (var i = from; i < text.Length; i++)
        {
            if (text[i] != '*')
            {
                continue;
            }

            var isDouble = i + 1 < text.Length && text[i + 1] == '*';

            // A single-asterisk run must not be closed by the first half of a double one, and
            // a double run must not be closed by a lone asterisk inside it.
            if (isDouble != doubled)
            {
                continue;
            }

            return i > from && !char.IsWhiteSpace(text[i - 1]) ? i : null;
        }

        return null;
    }

    /// <summary>Finds the mark closing a quotation opened at <paramref name="from"/>.</summary>
    /// <param name="text">The line.</param>
    /// <param name="from">First character after the opening mark.</param>
    /// <param name="closer">The mark that closes it.</param>
    /// <returns>Index of the closing mark, or <see langword="null"/> when it never closes.</returns>
    private static int? Quoted(string text, int from, char closer)
    {
        if (from >= text.Length || char.IsWhiteSpace(text[from]))
        {
            return null;
        }

        var close = text.IndexOf(closer, from);

        return close > from ? close : null;
    }
}
