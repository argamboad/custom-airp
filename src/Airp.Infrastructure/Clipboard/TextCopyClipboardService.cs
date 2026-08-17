using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;

namespace Airp.Infrastructure.Clipboard;

/// <summary>
/// Cross-platform clipboard access.
/// </summary>
/// <remarks>
/// Headless Linux sessions and locked-down terminals frequently have no clipboard at all.
/// Availability is probed once, lazily, and a missing clipboard is reported to the caller so
/// the UI can say "no clipboard available" instead of silently doing nothing.
/// </remarks>
public sealed class TextCopyClipboardService : IClipboardService
{
    private readonly ILogger<TextCopyClipboardService> _logger;
    private bool? _available;

    /// <summary>Initialises the service.</summary>
    /// <param name="logger">Logger.</param>
    public TextCopyClipboardService(ILogger<TextCopyClipboardService> logger) => _logger = logger;

    /// <inheritdoc />
    public bool IsAvailable => _available ??= Probe();

    /// <inheritdoc />
    public async Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            await TextCopy.ClipboardService.SetTextAsync(text, cancellationToken).ConfigureAwait(false);
            _available = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.LogWarning(ex, "Copying to the clipboard failed.");
            return false;
        }
    }

    private bool Probe()
    {
        try
        {
            _ = TextCopy.ClipboardService.GetText();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No clipboard is available in this environment.");
            return false;
        }
    }
}
