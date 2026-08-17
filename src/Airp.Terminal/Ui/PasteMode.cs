namespace Airp.Terminal.Ui;

/// <summary>
/// Tells pasted text apart from typed text, using the terminal's bracketed-paste mode.
/// </summary>
/// <remarks>
/// <para>
/// A paste reaches a console application as an ordinary burst of key presses, so a line
/// break inside it is indistinguishable from someone pressing Enter. In a message composer
/// that difference matters a great deal: one inserts a line, the other sends.
/// </para>
/// <para>
/// Bracketed paste is the terminal's answer. Once mode <c>?2004</c> is enabled, a paste is
/// wrapped in <c>ESC [ 200 ~</c> and <c>ESC [ 201 ~</c>, so everything between them is known
/// to be content. Terminals that do not support it simply never send the markers, leaving
/// the caller's own heuristics to cope — the same silent degradation the mouse support
/// relies on.
/// </para>
/// </remarks>
internal static class PasteMode
{
    /// <summary>Asks the terminal to bracket pasted text.</summary>
    public static void Enable() => Console.Out.Write("\u001b[?2004h");

    /// <summary>Asks the terminal to stop bracketing pasted text.</summary>
    public static void Disable() => Console.Out.Write("\u001b[?2004l");

    /// <summary>The two markers, without their leading escape.</summary>
    private const string Start = "[200~";
    private const string End = "[201~";

    /// <summary>Longest marker body, so a caller knows how far to look ahead.</summary>
    public const int MarkerLength = 5;

    /// <summary>
    /// Decides whether the characters following an escape are a paste marker.
    /// </summary>
    /// <param name="body">Up to <see cref="MarkerLength"/> characters read after the escape.</param>
    /// <param name="pasting">
    /// When this returns <see langword="true"/>: whether a paste is starting or ending.
    /// </param>
    /// <returns><see langword="true"/> when the characters were a marker and are consumed.</returns>
    public static bool TryReadMarker(string body, out bool pasting)
    {
        if (string.Equals(body, Start, StringComparison.Ordinal))
        {
            pasting = true;
            return true;
        }

        if (string.Equals(body, End, StringComparison.Ordinal))
        {
            pasting = false;
            return true;
        }

        pasting = false;
        return false;
    }
}
