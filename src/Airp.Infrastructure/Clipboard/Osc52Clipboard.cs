using System.Text;
using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;

namespace Airp.Infrastructure.Clipboard;

/// <summary>
/// Copies by asking the terminal to do it, with the OSC 52 escape sequence.
/// </summary>
/// <remarks>
/// <para>
/// There is no system clipboard inside a container, inside the <c>proot</c> sandbox an Android
/// tablet runs this in, or on a headless server — and over SSH a system clipboard is the wrong
/// one anyway: it belongs to the machine the application runs on, not to the machine whose
/// keyboard the reader is at. OSC 52 hands the text to the terminal emulator instead, which
/// puts it on the clipboard of the machine the reader is actually sitting in front of.
/// </para>
/// <para>
/// The terminal never answers. A sequence written to a terminal that does not implement OSC 52,
/// or has it turned off — which is the default in a few, since it lets a remote program write to
/// a local clipboard — is discarded in silence. So a copy here reports whether the sequence was
/// <em>written</em>, and nothing more. That is the honest limit of the mechanism, and it is why
/// this is the fallback rather than the first choice: a real clipboard can fail out loud.
/// </para>
/// <para>
/// Terminals also cap how much they will accept, often around 8 KB and sometimes less. A long
/// message can be dropped for that reason alone, and the terminal will not say so either.
/// </para>
/// </remarks>
public sealed class Osc52Clipboard : IClipboardService
{
    private readonly ILogger<Osc52Clipboard> _logger;
    private readonly TextWriter _terminal;
    private readonly bool _redirected;

    /// <summary>Initialises the clipboard against the console.</summary>
    /// <param name="logger">Logger. Never receives the copied text.</param>
    public Osc52Clipboard(ILogger<Osc52Clipboard> logger)
        : this(logger, Console.Out, Console.IsOutputRedirected)
    {
    }

    /// <summary>Initialises the clipboard against an explicit writer.</summary>
    /// <param name="logger">Logger. Never receives the copied text.</param>
    /// <param name="terminal">Where the escape sequence is written.</param>
    /// <param name="redirected">
    /// Whether output is going somewhere other than a terminal. Supplied so tests can assert
    /// both answers; a redirected stream has no emulator reading it, and an escape sequence
    /// written into a pipe or a file is noise in someone's output.
    /// </param>
    internal Osc52Clipboard(ILogger<Osc52Clipboard> logger, TextWriter terminal, bool redirected)
    {
        _logger = logger;
        _terminal = terminal;
        _redirected = redirected;
    }

    /// <inheritdoc />
    /// <remarks>
    /// True whenever there is a terminal to write to. Whether that terminal honours the
    /// sequence cannot be known without an answer it never sends.
    /// </remarks>
    public bool IsAvailable => !_redirected;

    /// <summary>Builds the escape sequence for a piece of text.</summary>
    /// <param name="text">The text to copy.</param>
    /// <returns>The sequence, ready to be written to a terminal.</returns>
    /// <remarks>
    /// <c>ESC ] 52 ; c ; &lt;base64&gt; BEL</c>. The <c>c</c> selects the clipboard proper rather
    /// than a primary selection, and BEL terminates it — accepted more widely than the
    /// <c>ESC \</c> form.
    /// </remarks>
    internal static string Sequence(string text)
        => $"]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}";

    /// <inheritdoc />
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (_redirected)
        {
            return false;
        }

        try
        {
            await _terminal.WriteAsync(Sequence(text).AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await _terminal.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Writing the clipboard escape sequence failed.");
            return false;
        }
    }
}
