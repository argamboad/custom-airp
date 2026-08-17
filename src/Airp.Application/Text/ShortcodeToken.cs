namespace Airp.Application.Text;

/// <summary>A partially typed <c>:shortcode</c> sitting under the caret.</summary>
/// <param name="Start">Index of the opening colon within the line.</param>
/// <param name="Length">How many characters the token occupies, colon included.</param>
/// <param name="Query">What has been typed after the colon.</param>
public readonly record struct ShortcodeToken(int Start, int Length, string Query);

/// <summary>
/// Recognises the <c>:shortcode</c> the caret is currently inside.
/// </summary>
/// <remarks>
/// <para>
/// The hard part is not finding a colon, it is knowing when a colon is <em>not</em> an emoji.
/// Ordinary prose is full of them: <c>10:30</c>, <c>https://…</c>, <c>note: this</c>. A
/// completion popup that opened on any of those would be in the way constantly, and worse, a
/// stray Tab would rewrite the user's text.
/// </para>
/// <para>
/// So three rules narrow it down, each drawn from a way the false positives differ from the
/// real thing. The colon must open a word — preceded by whitespace or nothing, which excludes
/// <c>10:30</c> and <c>https://</c>. Only letters, digits, <c>_</c>, <c>+</c> and <c>-</c> may
/// follow it, which excludes <c>note: this</c> the moment the space arrives. And the caret has
/// to be within the token, so moving away closes the popup without needing to be told.
/// </para>
/// <para>All members are pure and thread-safe.</para>
/// </remarks>
public static class ShortcodeScanner
{
    /// <summary>How long a shortcode name may get before this stops believing in it.</summary>
    private const int MaxNameLength = 32;

    /// <summary>
    /// Finds the shortcode being typed at a caret position.
    /// </summary>
    /// <param name="line">The line the caret is on.</param>
    /// <param name="column">The caret's column within that line.</param>
    /// <returns>The token, or <see langword="null"/> when the caret is not inside one.</returns>
    public static ShortcodeToken? At(string? line, int column)
    {
        var text = line ?? string.Empty;
        var caret = Math.Clamp(column, 0, text.Length);

        // Walk back over the name to find the colon that opens it.
        var start = caret;
        while (start > 0 && IsNameCharacter(text[start - 1]))
        {
            start--;
        }

        if (start == 0 || text[start - 1] != ':')
        {
            return null;
        }

        var colon = start - 1;

        // The colon has to open a word. Without this, every clock time and every URL in a
        // message would pop the list open.
        if (colon > 0 && !char.IsWhiteSpace(text[colon - 1]))
        {
            return null;
        }

        var query = text[start..caret];
        if (query.Length > MaxNameLength)
        {
            return null;
        }

        return new ShortcodeToken(colon, caret - colon, query);
    }

    /// <summary>
    /// Recognises a complete <c>:name:</c> that the caret has just closed.
    /// </summary>
    /// <remarks>
    /// Typing the second colon is the user spelling the emoji out in full, so it substitutes
    /// immediately rather than waiting to be picked from a list — the behaviour every chat
    /// client has trained people to expect.
    /// </remarks>
    /// <param name="line">The line the caret is on.</param>
    /// <param name="column">The caret's column, just after the closing colon.</param>
    /// <returns>
    /// The token spanning <c>:name:</c> and the emoji it names, or <see langword="null"/> when
    /// what precedes the caret is not a closed, known shortcode.
    /// </returns>
    public static (ShortcodeToken Token, string Emoji)? Closed(string? line, int column)
    {
        var text = line ?? string.Empty;
        var caret = Math.Clamp(column, 0, text.Length);

        if (caret == 0 || text[caret - 1] != ':')
        {
            return null;
        }

        // Everything before the closing colon has to be a token in its own right.
        if (At(text, caret - 1) is not { } open || open.Query.Length == 0)
        {
            return null;
        }

        var emoji = EmojiShortcodes.Find(open.Query);
        return emoji is null
            ? null
            : (new ShortcodeToken(open.Start, caret - open.Start, open.Query), emoji);
    }

    private static bool IsNameCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '_' or '+' or '-';
}
