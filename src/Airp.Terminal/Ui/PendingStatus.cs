using System.Diagnostics;

namespace Airp.Terminal.Ui;

/// <summary>
/// The "still working" line a view shows while a long operation runs, and the clock beside it.
/// </summary>
/// <remarks>
/// <para>
/// The elapsed time is kept here and read at draw time rather than being sent along with each
/// progress message. A worker can only report its own elapsed seconds while it is not busy, and
/// the things these operations wait on block for whole seconds at a time — a page reload waits
/// for network idle on a site whose websocket traffic never lets it go idle. A transmitted
/// number therefore freezes on the last thing the worker managed to say and then jumps when it
/// comes back, which reads as a hung client at exactly the moment the reader is watching for
/// signs of life.
/// </para>
/// <para>
/// So the worker reports only what it is doing, and the clock runs here, in the shell's frame.
/// The shell already redraws roughly ten times a second behind a spinner, so the count is
/// smooth and cannot go backwards however long a single step blocks.
/// </para>
/// <para>
/// This is deliberately not a <see cref="Progress{T}"/>. With no synchronisation context — and
/// a console host has none — that class posts every message to the thread pool as a separate
/// work item, so two messages can be delivered in the order opposite to the one they were sent
/// in. <see cref="Report"/> instead writes the field on the caller's own thread, which keeps
/// the last thing said the last thing shown.
/// </para>
/// </remarks>
internal sealed class PendingStatus : IProgress<string>
{
    private string? _text;
    private long _startedAt;

    /// <summary>Starts the clock, discarding anything left over from a previous run.</summary>
    public void Begin()
    {
        Volatile.Write(ref _text, null);
        Volatile.Write(ref _startedAt, Stopwatch.GetTimestamp());
    }

    /// <summary>Stops the clock and clears the line.</summary>
    public void Clear()
    {
        Volatile.Write(ref _text, null);
        Volatile.Write(ref _startedAt, 0);
    }

    /// <inheritdoc />
    public void Report(string value) => Volatile.Write(ref _text, value);

    /// <summary>
    /// The phase last reported, or <see langword="null"/> when nothing has been.
    /// </summary>
    /// <remarks>
    /// Read as well as drawn. Which phase a send reached is what says whether stopping it
    /// left a message on the site — see <see cref="Abstractions.SendPhase.IsSubmitted"/>.
    /// </remarks>
    public string? Phase => Volatile.Read(ref _text);

    /// <summary>
    /// The line to draw this frame, with the elapsed time appended once there is one worth
    /// showing.
    /// </summary>
    /// <returns>The line, or <see langword="null"/> when nothing is running.</returns>
    public string? Describe()
    {
        if (Volatile.Read(ref _text) is not { Length: > 0 } text)
        {
            return null;
        }

        var startedAt = Volatile.Read(ref _startedAt);
        if (startedAt == 0)
        {
            return text;
        }

        // Below a second there is no number to show, and showing "0s" for the first frame of
        // every operation makes short ones look like they stalled before they began.
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        return elapsed.TotalSeconds < 1 ? text : $"{text} — {elapsed.TotalSeconds:F0}s";
    }
}
