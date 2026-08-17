using System.Diagnostics;

namespace Airp.Terminal.Ui;

/// <summary>Opens a file in the reader's own editor and waits for it to close.</summary>
/// <remarks>
/// Git-style: launch, wait, done. Prose deserves a real editor, and a file on disk is already
/// the interface — the terminal's composers exist for messages, not files. Injectable as a
/// delegate so views can be tested without a Notepad window opening mid-suite.
/// </remarks>
internal static class EditorLauncher
{
    /// <summary>The editor command in effect: <c>EDITOR</c>, <c>VISUAL</c>, or the platform default.</summary>
    public static string Editor
        => Environment.GetEnvironmentVariable("EDITOR")
           ?? Environment.GetEnvironmentVariable("VISUAL")
           ?? (OperatingSystem.IsWindows() ? "notepad" : "nano");

    /// <summary>Opens the file and returns when the editor closes.</summary>
    /// <param name="path">File to edit.</param>
    /// <param name="cancellationToken">Token used to stop waiting.</param>
    /// <returns>A task that completes when the editor exits.</returns>
    public static async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        using var process = Process.Start(
            new ProcessStartInfo(Editor, $"\"{path}\"") { UseShellExecute = true });

        if (process is not null)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
