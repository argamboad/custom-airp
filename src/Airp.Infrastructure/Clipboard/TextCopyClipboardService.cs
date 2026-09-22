using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;

namespace Airp.Infrastructure.Clipboard;

/// <summary>
/// Cross-platform clipboard access.
/// </summary>
/// <remarks>
/// <para>
/// Headless Linux sessions and locked-down terminals frequently have no clipboard at all, and a
/// missing one is reported to the caller so the UI can say "no clipboard available" instead of
/// silently doing nothing.
/// </para>
/// <para>
/// That answer comes from a copy that actually failed, never from a probe. The earlier version
/// probed by <em>reading</em> the clipboard once, lazily, and under WSL that reads through
/// <c>powershell.exe Get-Clipboard</c>, which times out inside the library every time — while
/// writing works perfectly. So the one environment where the probe disagreed with reality was
/// the one where it refused a copy that would have succeeded. Reading is not what a copy needs,
/// and a question nobody asked cannot be answered wrongly.
/// </para>
/// </remarks>
public sealed class TextCopyClipboardService : IClipboardService
{
    private readonly ILogger<TextCopyClipboardService> _logger;
    private readonly Func<string, CancellationToken, Task> _copy;
    private bool _failed;

    /// <summary>Initialises the service.</summary>
    /// <param name="logger">Logger.</param>
    public TextCopyClipboardService(ILogger<TextCopyClipboardService> logger)
        : this(logger, static (text, cancellationToken) =>
            TextCopy.ClipboardService.SetTextAsync(text, cancellationToken))
    {
    }

    /// <summary>Initialises the service with an explicit copy operation.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="copy">
    /// Places text on the clipboard. Supplied by tests, which have no clipboard to write to and
    /// must be able to fail on purpose.
    /// </param>
    internal TextCopyClipboardService(
        ILogger<TextCopyClipboardService> logger,
        Func<string, CancellationToken, Task> copy)
    {
        _logger = logger;
        _copy = copy;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Optimistic until proven otherwise: true until a copy has actually failed. The cost of
    /// being wrong is one attempt that reports it was rejected; the cost of the opposite is a
    /// clipboard that works being declared missing.
    /// </remarks>
    public bool IsAvailable => !_failed;

    /// <inheritdoc />
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            await _copy(text, cancellationToken).ConfigureAwait(false);
            _failed = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failed = true;
            _logger.LogWarning(ex, "Copying to the clipboard failed.");
            return false;
        }
    }
}
