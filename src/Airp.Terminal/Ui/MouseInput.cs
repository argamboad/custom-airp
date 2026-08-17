using System.Globalization;

namespace Airp.Terminal.Ui;

/// <summary>A decoded mouse event.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Column">One-based terminal column.</param>
/// <param name="Row">One-based terminal row.</param>
internal readonly record struct MouseEvent(MouseEventKind Kind, int Column, int Row);

/// <summary>The mouse actions the terminal reacts to.</summary>
internal enum MouseEventKind
{
    /// <summary>Left button pressed.</summary>
    LeftClick = 0,

    /// <summary>Wheel rolled away from the user.</summary>
    ScrollUp,

    /// <summary>Wheel rolled towards the user.</summary>
    ScrollDown,
}

/// <summary>
/// Optional mouse support, decoded from SGR tracking sequences on standard input.
/// </summary>
/// <remarks>
/// <para>
/// The keyboard is the primary interface and nothing here is required to drive the
/// application; this exists because clicking a row and spinning the wheel are cheap to
/// support where the terminal already offers them.
/// </para>
/// <para>
/// Tracking is requested with the standard <c>?1000;1006</c> private modes. Terminals that
/// do not forward mouse reports on standard input — which includes the classic Windows
/// console host — simply never deliver a sequence, so the feature is inert rather than
/// broken there. The keyboard path is untouched either way: decoding only begins after an
/// <c>ESC [ &lt;</c> prefix, and any sequence that fails to parse is discarded.
/// </para>
/// </remarks>
internal static class MouseInput
{
    /// <summary>Asks the terminal to start reporting mouse events.</summary>
    public static void Enable() => Console.Out.Write("\u001b[?1000h\u001b[?1006h");

    /// <summary>Asks the terminal to stop reporting mouse events.</summary>
    public static void Disable() => Console.Out.Write("\u001b[?1006l\u001b[?1000l");

    /// <summary>
    /// Attempts to decode a mouse report that begins at an already-consumed escape key.
    /// </summary>
    /// <param name="readNext">
    /// Reads the next available character, or returns <see langword="null"/> when input has
    /// drained — which is how a bare Escape key press is distinguished from a sequence.
    /// </param>
    /// <returns>The decoded event, or <see langword="null"/> when this was not a mouse report.</returns>
    public static MouseEvent? TryDecode(Func<char?> readNext)
    {
        if (readNext() is not '[')
        {
            return null;
        }

        if (readNext() is not '<')
        {
            return null;
        }

        // SGR format: ESC [ < button ; column ; row (M for press, m for release)
        var buffer = new List<char>(16);
        while (true)
        {
            var next = readNext();
            if (next is null)
            {
                return null;
            }

            if (next is 'M' or 'm')
            {
                return Parse(new string([.. buffer]), next.Value == 'M');
            }

            if (buffer.Count > 24)
            {
                return null;
            }

            buffer.Add(next.Value);
        }
    }

    private static MouseEvent? Parse(string payload, bool pressed)
    {
        var parts = payload.Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var button)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var column)
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var row))
        {
            return null;
        }

        return button switch
        {
            64 => new MouseEvent(MouseEventKind.ScrollUp, column, row),
            65 => new MouseEvent(MouseEventKind.ScrollDown, column, row),
            0 when pressed => new MouseEvent(MouseEventKind.LeftClick, column, row),
            _ => null,
        };
    }
}
