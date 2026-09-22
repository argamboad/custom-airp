using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;

namespace Airp.Infrastructure.Clipboard;

/// <summary>
/// Tries the system clipboard, and asks the terminal when there is not one.
/// </summary>
/// <remarks>
/// <para>
/// The order is deliberate. A system clipboard reports what happened — it either took the text
/// or threw — while OSC 52 is written into silence and hopes. Where both work, the first is
/// worth more.
/// </para>
/// <para>
/// The second is not a lesser copy of the first, though. Over SSH the system clipboard is the
/// remote machine's, which the reader cannot paste from; the terminal's is theirs. So the
/// fallback is also the better answer in the case the two most obviously disagree, and the
/// environments with no system clipboard at all — a container, an Android tablet's sandbox —
/// are the same ones reached over SSH.
/// </para>
/// </remarks>
public sealed class FallbackClipboard : IClipboardService
{
    private readonly IClipboardService _system;
    private readonly IClipboardService _terminal;
    private readonly ILogger<FallbackClipboard> _logger;

    /// <summary>Initialises the clipboard.</summary>
    /// <param name="system">The system clipboard, tried first.</param>
    /// <param name="terminal">The terminal's own, tried when the first will not have it.</param>
    /// <param name="logger">Logger. Never receives the copied text.</param>
    public FallbackClipboard(
        TextCopyClipboardService system,
        Osc52Clipboard terminal,
        ILogger<FallbackClipboard> logger)
    {
        _system = system;
        _terminal = terminal;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Either one being available is enough for the key to be offered. Both being gone means
    /// there is nowhere for the text to go, and the views say so rather than appearing to work.
    /// </remarks>
    public bool IsAvailable => _system.IsAvailable || _terminal.IsAvailable;

    /// <inheritdoc />
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_system.IsAvailable
            && await _system.CopyAsync(text, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Reached on every copy in a sandbox, where the system clipboard is missing rather than
        // broken. Logged at debug: a line per copy at any louder level would be noise.
        _logger.LogDebug("The system clipboard did not take the copy; asking the terminal.");

        return await _terminal.CopyAsync(text, cancellationToken).ConfigureAwait(false);
    }
}
