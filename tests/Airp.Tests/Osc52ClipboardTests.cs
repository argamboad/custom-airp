using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Infrastructure.Clipboard;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Copying where there is no system clipboard: a container, an Android tablet's sandbox, or
/// any session reached over SSH.
/// </summary>
public class Osc52ClipboardTests
{
    private static Osc52Clipboard Terminal(TextWriter writer, bool redirected = false)
        => new(NullLogger<Osc52Clipboard>.Instance, writer, redirected);

    [Fact]
    public async Task The_text_reaches_the_terminal_as_an_OSC_52_sequence()
    {
        var written = new StringWriter();

        (await Terminal(written).CopyAsync("She does not look up.")).ShouldBeTrue();

        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("She does not look up."));
        written.ToString().ShouldBe($"]52;c;{expected}");
    }

    [Fact]
    public void The_payload_is_base64_of_the_utf8_bytes()
    {
        // Accents and em dashes are ordinary in what this copies, and a sequence carrying
        // anything but UTF-8 bytes pastes as mojibake.
        var sequence = Osc52Clipboard.Sequence("—ó");

        sequence.ShouldBe("]52;c;4oCUw7M=");
        Convert.FromBase64String("4oCUw7M=").ShouldBe(Encoding.UTF8.GetBytes("—ó"));
    }

    [Fact]
    public async Task Redirected_output_is_not_a_terminal_and_is_refused()
    {
        // Nothing is reading the stream for escape sequences, so writing one would put control
        // characters into somebody's pipe or file and claim a copy that did not happen.
        var written = new StringWriter();
        var clipboard = Terminal(written, redirected: true);

        clipboard.IsAvailable.ShouldBeFalse();
        (await clipboard.CopyAsync("She does not look up.")).ShouldBeFalse();

        written.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_terminal_that_throws_is_reported_rather_than_swallowed()
    {
        var clipboard = Terminal(new ThrowingWriter());

        (await clipboard.CopyAsync("She does not look up.")).ShouldBeFalse();
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("the pipe is gone");
    }
}

/// <summary>
/// The order of the two, and what each is for.
/// </summary>
public class FallbackClipboardTests
{
    private static FallbackClipboard Chain(
        Func<string, CancellationToken, Task> system,
        TextWriter terminal,
        bool redirected = false)
        => new(
            new TextCopyClipboardService(NullLogger<TextCopyClipboardService>.Instance, system),
            new Osc52Clipboard(NullLogger<Osc52Clipboard>.Instance, terminal, redirected),
            NullLogger<FallbackClipboard>.Instance);

    [Fact]
    public async Task A_working_system_clipboard_is_used_and_the_terminal_is_left_alone()
    {
        // The system clipboard says whether it worked; the terminal never answers. Where both
        // are there, the one that can report a failure is worth more.
        var written = new StringWriter();
        var copied = string.Empty;

        var clipboard = Chain(
            (text, _) =>
            {
                copied = text;
                return Task.CompletedTask;
            },
            written);

        (await clipboard.CopyAsync("She does not look up.")).ShouldBeTrue();

        copied.ShouldBe("She does not look up.");
        written.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task No_system_clipboard_falls_through_to_the_terminal()
    {
        // The sandbox case: nothing to copy to on this machine, but a reader at a terminal
        // somewhere with a clipboard of their own.
        var written = new StringWriter();

        var clipboard = Chain(
            static (_, _) => throw new InvalidOperationException("no clipboard"),
            written);

        (await clipboard.CopyAsync("She does not look up.")).ShouldBeTrue();

        written.ToString().ShouldBe(Osc52Clipboard.Sequence("She does not look up."));
        clipboard.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task Both_gone_is_reported_as_no_clipboard_at_all()
    {
        var clipboard = Chain(
            static (_, _) => throw new InvalidOperationException("no clipboard"),
            new StringWriter(),
            redirected: true);

        (await clipboard.CopyAsync("She does not look up.")).ShouldBeFalse();

        clipboard.IsAvailable.ShouldBeFalse();
    }

    [Fact]
    public async Task The_key_stays_offered_while_either_one_answers()
    {
        // A system clipboard that has failed once marks itself missing. That must not take the
        // terminal down with it, or one failed copy in a sandbox disables the key for good.
        IClipboardService clipboard = Chain(
            static (_, _) => throw new InvalidOperationException("no clipboard"),
            new StringWriter());

        await clipboard.CopyAsync("She does not look up.");

        clipboard.IsAvailable.ShouldBeTrue();
    }
}
