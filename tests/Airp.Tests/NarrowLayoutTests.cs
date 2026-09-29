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
/// The conversation on a phone: 54 columns, which is what a terminal on one reports.
/// </summary>
/// <remarks>
/// The desk layout at that width clamped the reading column to forty and centred it, so a
/// quarter of the screen was margin, and two captions written to fit a wide window each
/// wrapped onto a second row. Every assertion here is made at the phone's width and again at
/// a desk's, because the point is that one changes and the other does not.
/// </remarks>
public class NarrowLayoutTests
{
    private const int Phone = 54;
    private const int Desk = 100;

    private static RenderContext Context(int width)
        => new(width, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Nav(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
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
        console.Profile.Height = 24;
        console.Write(renderable);

        return writer.ToString();
    }

    private static string[] Lines(string rendered)
        => rendered.Split('\n').Select(static line => line.TrimEnd('\r')).ToArray();

    private static async Task<ConversationView> ViewAsync()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.User, Text = "what I asked" },
                new() { Id = "2", ConversationId = "c", Role = ChatRole.Assistant, Text = "the reply" },
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
        Context(RenderContext.NarrowWidth - 1).Narrow.ShouldBeTrue();
        Context(RenderContext.NarrowWidth).Narrow.ShouldBeFalse();
        Context(Desk).Narrow.ShouldBeFalse();
    }

    [Fact]
    public async Task On_a_phone_the_conversation_takes_the_whole_width()
    {
        var view = await ViewAsync();

        // The counts line is the first thing the view draws, so where it starts is where the
        // column starts. On a desk the sixty-percent column has a margin before it.
        var phone = Lines(Render(view.Render(Context(Phone)), Phone)).First(static l => l.Contains("message 2/2"));
        var desk = Lines(Render(view.Render(Context(Desk)), Desk)).First(static l => l.Contains("message 2/2"));

        phone.ShouldStartWith("message");
        desk.ShouldStartWith(" ");
    }

    [Fact]
    public async Task On_a_phone_the_counts_fit_one_row_and_lose_the_word_count()
    {
        var view = await ViewAsync();

        var phone = Lines(Render(view.Render(Context(Phone)), Phone));
        var desk = Render(view.Render(Context(Desk)), Desk);

        // The cursor opens on the latest turn, so the position reads 2/2.
        phone.ShouldContain(static l => l.Contains("message 2/2") && l.Contains("1 yours") && l.Contains("1 replies"));
        phone.ShouldNotContain(static l => l.Contains("words in this one"));

        // Collapsed, because at a desk's default sixty-percent column this line already folds
        // once; the phrase across the fold is still the phrase.
        System.Text.RegularExpressions.Regex.Replace(desk, @"\s+", " ").ShouldContain("words in this one");
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
}
