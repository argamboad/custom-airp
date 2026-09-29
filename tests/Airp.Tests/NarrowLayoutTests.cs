using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

/// <summary>
/// The conversation on a phone: 38 columns, which is what a terminal on one reports at a font
/// size that can be read.
/// </summary>
/// <remarks>
/// The desk layout at that width spent ten of thirty-eight rows on chrome, and every header
/// line that wrapped was a row the transcript was not told about, taken back from the bottom
/// of the screen — the last lines of the last reply could not be reached. Most assertions are
/// made at the phone's width and again at a desk's, because the point is that one changes and
/// the other does not.
/// </remarks>
public class NarrowLayoutTests
{
    private const int Phone = 38;
    private const int Desk = 100;
    private const int Height = 30;

    private static RenderContext Context(int width)
        => new(width, Height, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Nav(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Down()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static string Render(IRenderable renderable, int width)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = width;
        console.Profile.Height = 60;
        console.Write(renderable);

        return writer.ToString();
    }

    private static string[] Lines(string rendered)
        => rendered.TrimEnd('\r', '\n').Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

    private static async Task<ConversationView> ViewAsync(string reply = "the reply")
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new()
                {
                    Id = "1", ConversationId = "c", Role = ChatRole.User, Text = "what I asked",
                    SentAtUtc = new DateTimeOffset(2026, 9, 29, 12, 1, 0, TimeSpan.Zero),
                },
                new()
                {
                    Id = "2", ConversationId = "c", Role = ChatRole.Assistant, Text = reply,
                    SentAtUtc = new DateTimeOffset(2026, 9, 29, 12, 2, 0, TimeSpan.Zero),
                },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "North Dock", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        if (await view.OnActivatedAsync(CancellationToken.None) is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }

        return view;
    }

    [Fact]
    public void Sixty_columns_is_where_narrow_begins()
    {
        Context(Phone).Narrow.ShouldBeTrue();
        Context(54).Narrow.ShouldBeTrue();
        Context(RenderContext.NarrowWidth - 1).Narrow.ShouldBeTrue();
        Context(RenderContext.NarrowWidth).Narrow.ShouldBeFalse();
        Context(Desk).Narrow.ShouldBeFalse();
    }

    [Fact]
    public async Task On_a_phone_the_conversation_takes_the_whole_width()
    {
        var view = await ViewAsync();

        // The speaker's chip opens each turn. On a phone it sits against the left edge, after
        // the marker's columns; on a desk the sixty-percent column is centred with a margin.
        var phone = Lines(Render(view.Render(Context(Phone)), Phone)).First(static l => l.Contains("Blake"));
        var desk = Lines(Render(view.Render(Context(Desk)), Desk)).First(static l => l.Contains("Blake"));

        phone.IndexOf("Blake", StringComparison.Ordinal).ShouldBeLessThan(4);
        desk.IndexOf("Blake", StringComparison.Ordinal).ShouldBeGreaterThan(10);
    }

    [Fact]
    public async Task On_a_phone_the_counts_move_to_the_header_row()
    {
        var view = await ViewAsync();

        var phone = Render(view.Render(Context(Phone)), Phone);
        var desk = Render(view.Render(Context(Desk)), Desk);

        // The view's own header is empty on a phone; the position is what the shell's one row
        // shows beside the story's name. The cursor opens on the latest turn.
        phone.ShouldNotContain("message 2/2");
        phone.ShouldNotContain("yours");
        view.Summary.ShouldBe("2/2");

        desk.ShouldContain("message 2/2");
    }

    [Fact]
    public async Task On_a_phone_a_turn_is_its_speaker_its_text_and_a_blank_line()
    {
        var view = await ViewAsync();
        await view.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false), KeyboardMode.Standard, KeyContext.Navigation),
            Context(Phone),
            CancellationToken.None);

        var phone = Lines(Render(view.Render(Context(Phone)), Phone));
        var desk = Lines(Render(view.Render(Context(Desk)), Desk));

        // The time a turn was sent, and the hairline between turns, are the desk's.
        var stamp = new DateTimeOffset(2026, 9, 29, 12, 2, 0, TimeSpan.Zero).LocalDateTime.ToString("HH:mm");
        phone.ShouldNotContain(l => l.Contains(stamp));
        phone.ShouldNotContain(static l => l.Contains('─'));
        desk.ShouldContain(l => l.Contains(stamp));
        desk.ShouldContain(static l => l.Contains("───"));

        // What is left: You, what was asked, a blank, Blake, the reply.
        var you = Array.FindIndex(phone, static l => l.Contains("You"));
        phone[you + 1].ShouldContain("what I asked");

        // The name starts in the same column as the text under it.
        phone[you].IndexOf("You", StringComparison.Ordinal)
            .ShouldBe(phone[you + 1].IndexOf("what", StringComparison.Ordinal));
        phone[you + 2].Trim().ShouldBeEmpty();
        phone[you + 3].ShouldContain("Blake");
        phone[you + 4].ShouldContain("the reply");
    }

    [Fact]
    public async Task On_a_phone_the_transcript_is_never_taller_than_its_height()
    {
        // The bug this whole change began from: a header line that wrapped pushed the bottom
        // of the transcript off the screen. Whatever the view draws has to fit what it was given.
        var view = await ViewAsync(string.Join(' ', Enumerable.Repeat("prose", 600)));

        for (var i = 0; i < 200; i++)
        {
            await view.HandleKeyAsync(Down(), Context(Phone), CancellationToken.None);
        }

        Lines(Render(view.Render(Context(Phone)), Phone)).Length.ShouldBeLessThanOrEqualTo(Height);
    }

    [Fact]
    public async Task On_a_phone_the_composer_caption_keeps_the_words_and_drops_the_key_hints()
    {
        var view = await ViewAsync();
        await view.HandleKeyAsync(Nav('i'), Context(Phone), CancellationToken.None);

        var phone = Lines(Render(view.Render(Context(Phone)), Phone));
        var desk = Render(view.Render(Context(Desk)), Desk);

        // The footer already says Enter sends; on a phone the caption is one row of what only
        // it knows.
        phone.ShouldContain(static l => l.StartsWith("You", StringComparison.Ordinal) && l.Contains("0 words"));
        phone.ShouldNotContain(static l => l.Contains("Enter sends"));
        desk.ShouldContain("Enter sends");
    }

    [Fact]
    public void The_phone_header_is_one_row_and_its_rule_whatever_the_title()
    {
        var header = Lines(Render(
            Shell.BuildPhoneHeader(
                Theme.For(ThemeName.Dark),
                title: "BJU - Student - Soccer, the long version of the name",
                summary: "112/240 · $0.1313",
                width: Phone),
            Phone));

        header.Length.ShouldBe(2);
        header[0].Length.ShouldBeLessThanOrEqualTo(Phone);
        header[0].ShouldStartWith("BJU - Student");
        header[0].ShouldContain("…");
        header[0].ShouldEndWith("112/240 · $0.1313");
    }

    [Fact]
    public void The_phone_header_gives_the_whole_row_to_a_title_with_no_summary()
    {
        var header = Lines(Render(
            Shell.BuildPhoneHeader(Theme.For(ThemeName.Dark), title: "Chats", summary: string.Empty, width: Phone),
            Phone));

        header.Length.ShouldBe(2);
        header[0].TrimEnd().ShouldBe("Chats");
    }
}
