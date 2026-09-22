using Microsoft.Extensions.Logging.Abstractions;
using Airp.Infrastructure.Clipboard;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The clipboard is reported missing only once a copy has failed.
/// </summary>
/// <remarks>
/// Written after a real WSL session refused every copy. Availability used to be probed by
/// reading the clipboard, which under WSL goes through <c>powershell.exe Get-Clipboard</c> and
/// times out inside the library — while writing worked the whole time.
/// </remarks>
public class ClipboardServiceTests
{
    private static TextCopyClipboardService Service(Func<string, CancellationToken, Task> copy)
        => new(NullLogger<TextCopyClipboardService>.Instance, copy);

    [Fact]
    public void A_clipboard_nothing_has_been_copied_to_yet_is_assumed_to_work()
    {
        // No copy has been attempted, so nothing is known. The view keys off this, and the old
        // answer — the result of reading the clipboard — was false on the one platform where
        // copying worked.
        var service = Service(static (_, _) => Task.CompletedTask);

        service.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_copy_that_succeeds_leaves_the_clipboard_available()
    {
        var copied = string.Empty;
        var service = Service((text, _) =>
        {
            copied = text;
            return Task.CompletedTask;
        });

        (await service.CopyAsync("She does not look up.")).ShouldBeTrue();

        copied.ShouldBe("She does not look up.");
        service.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_copy_that_fails_reports_it_and_marks_the_clipboard_missing()
    {
        var service = Service(static (_, _) => throw new InvalidOperationException("no clipboard"));

        (await service.CopyAsync("She does not look up.")).ShouldBeFalse();

        // The first attempt answers honestly; the view says the copy was rejected. Only after
        // that does the key stop being offered.
        service.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task A_clipboard_that_comes_back_is_available_again()
    {
        var fails = true;
        var service = Service((_, _) => fails
            ? throw new InvalidOperationException("no clipboard")
            : Task.CompletedTask);

        await service.CopyAsync("She does not look up.");
        service.IsAvailable.ShouldBeFalse();

        fails = false;

        (await service.CopyAsync("She does not look up.")).ShouldBeTrue();
        service.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_cancelled_copy_says_nothing_about_the_clipboard()
    {
        // Cancellation is the caller leaving, not the environment refusing. Counting it as a
        // failure would disable the key for the rest of the session.
        var service = Service(static (_, cancellationToken) =>
            Task.FromCanceled(new CancellationToken(canceled: true)));

        await Should.ThrowAsync<OperationCanceledException>(
            () => service.CopyAsync("She does not look up.", new CancellationToken(canceled: true)));

        service.IsAvailable.ShouldBeTrue();
    }
}
