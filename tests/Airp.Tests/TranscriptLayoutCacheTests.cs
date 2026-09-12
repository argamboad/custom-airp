using NSubstitute;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// What the transcript rebuilds between frames, and what it does not.
/// </summary>
/// <remarks>
/// <para>
/// Laying out a long story is the most expensive thing the conversation view does: the real
/// 386-message BJU transcript wraps to 6,804 rows and costs 113ms to format, of which about
/// thirty reach the screen. Doing that on every frame broke the shell's own throttle — a
/// frame held for 100ms while input arrives is stale the moment a 113ms frame lands, so every
/// pasted character drew again and pasting a paragraph looked like watching it be typed.
/// </para>
/// <para>
/// The layout is cached now. These tests pin down which changes are allowed to rebuild it,
/// counting passes rather than timing them.
/// </para>
/// </remarks>
public class TranscriptLayoutCacheTests
{
    private const int Width = 80;
    private const int Height = 24;

    private static RenderContext Context(int width = Width, ThemeName theme = ThemeName.Dark)
        => new(width, Height, Theme.For(theme), new AirpOptions());

    private static ChatMessage Reply(int index) => new()
    {
        Id = $"m{index:D3}",
        ConversationId = "c",
        Role = index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
        Text = string.Join(' ', Enumerable.Repeat($"turn{index:D3}", 60)),
        SentAtUtc = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
    };

    private static async Task<ConversationView> BuildAsync(int turns)
    {
        var messages = Enumerable.Range(0, turns).Select(Reply).ToArray();

        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => messages);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        var action = await view.OnActivatedAsync(CancellationToken.None);
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }

        return view;
    }

    [Fact]
    public async Task A_frame_that_changes_nothing_reuses_the_layout()
    {
        var view = await BuildAsync(turns: 40);

        for (var frame = 0; frame < 50; frame++)
        {
            view.Render(Context());
        }

        view.LayoutPasses.ShouldBe(1);
    }

    /// <summary>
    /// The selection marker is a prefix on the rows actually drawn, not part of the layout.
    /// </summary>
    /// <remarks>
    /// Baking it in was what made arrowing through a long story cost a full pass per key, and
    /// it bought nothing: the marker is one column, and the rows behind it are identical.
    /// </remarks>
    [Fact]
    public async Task Moving_the_cursor_does_not_rebuild_the_transcript()
    {
        var view = await BuildAsync(turns: 40);
        view.Render(Context());

        for (var step = 0; step < 20; step++)
        {
            await view.HandleKeyAsync(
                new KeyStroke(AppCommand.MoveUp, '\0', default),
                Context(),
                CancellationToken.None);

            view.Render(Context());
        }

        view.LayoutPasses.ShouldBe(1);
    }

    [Fact]
    public async Task The_marker_still_follows_the_cursor()
    {
        var view = await BuildAsync(turns: 6);
        var before = TranscriptText(view);

        await view.HandleKeyAsync(
            new KeyStroke(AppCommand.Home, '\0', default),
            Context(),
            CancellationToken.None);

        TranscriptText(view).ShouldNotBe(before);
        TranscriptText(view).ShouldContain("▌");
    }

    [Fact]
    public async Task A_narrower_window_rebuilds_the_transcript()
    {
        var view = await BuildAsync(turns: 40);

        view.Render(Context());
        view.Render(Context(width: 60));
        view.Render(Context(width: 60));

        view.LayoutPasses.ShouldBe(2);
    }

    [Fact]
    public async Task A_new_palette_rebuilds_the_transcript()
    {
        var view = await BuildAsync(turns: 40);

        view.Render(Context());
        view.Render(Context(theme: ThemeName.Light));
        view.Render(Context(theme: ThemeName.Light));

        view.LayoutPasses.ShouldBe(2);
    }

    /// <summary>An active search paints through every row, so it has to be part of the key.</summary>
    [Fact]
    public async Task An_active_search_rebuilds_the_transcript()
    {
        var view = await BuildAsync(turns: 40);
        view.Render(Context());

        await Search(view, "turn003");
        view.Render(Context());
        view.Render(Context());

        view.LayoutPasses.ShouldBe(2);
        TranscriptText(view).ShouldContain("turn003");
    }

    private static async Task Search(ConversationView view, string query)
    {
        await view.HandleKeyAsync(
            new KeyStroke(AppCommand.Search, '\0', default),
            Context(),
            CancellationToken.None);

        foreach (var character in query)
        {
            await view.HandleKeyAsync(
                new KeyStroke(AppCommand.Character, character, default),
                Context(),
                CancellationToken.None);
        }

        await view.HandleKeyAsync(
            new KeyStroke(AppCommand.Accept, '\0', default),
            Context(),
            CancellationToken.None);
    }

    private static string TranscriptText(ConversationView view)
    {
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Enrichment = new Spectre.Console.ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new Spectre.Console.AnsiConsoleOutput(writer),
        });

        console.Profile.Width = Width;
        console.Profile.Height = Height;
        console.Write(view.Render(Context()));
        return writer.ToString();
    }
}
