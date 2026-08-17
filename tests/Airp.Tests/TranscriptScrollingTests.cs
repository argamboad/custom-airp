using NSubstitute;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

/// <summary>
/// Reading a message taller than the window.
/// </summary>
/// <remarks>
/// These replies run to thousands of characters — several screens of wrapped text. Keeping
/// the selected message's first row pinned to the top of the viewport on every frame made
/// that unreadable: paging down moved the view, and the very next render dragged it back to
/// the message's opening line, so nothing past the first screenful could be reached at all.
/// </remarks>
public class TranscriptScrollingTests
{
    private const int Width = 80;
    private const int Height = 12;

    private static RenderContext Context()
        => new(Width, Height, Theme.For(ThemeName.Dark), new AirpOptions());

    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = Width;
        console.Profile.Height = Height;
        console.Write(renderable);
        return writer.ToString();
    }

    /// <summary>
    /// A reply of a known height, every row of it individually identifiable.
    /// </summary>
    /// <remarks>
    /// Joined with line breaks rather than spaces so one token really is one drawn row.
    /// Sixty space-separated tokens wrap into about seven rows and would fit the pane
    /// entirely, which tests nothing.
    /// </remarks>
    /// <param name="lines">How many rows the reply should occupy.</param>
    /// <returns>The reply text.</returns>
    private static string LongReply(int lines)
        => string.Join('\n', Enumerable.Range(0, lines).Select(i => $"line{i:D2}"));

    private static async Task<ConversationView> BuildAsync(params ChatMessage[] messages)
    {
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

    private static ChatMessage Message(string id, ChatRole role, string text, int minute) => new()
    {
        Id = id,
        ConversationId = "c",
        Role = role,
        Text = text,
        SentAtUtc = new DateTimeOffset(2026, 8, 5, 12, minute, 0, TimeSpan.Zero),
    };

    private static KeyStroke Key(ConsoleKey key)
        => KeyMap.Resolve(new ConsoleKeyInfo('\0', key, false, false, false), KeyboardMode.Standard, KeyContext.Navigation);

    [Fact]
    public async Task ALongReply_CanBeScrolledPastItsFirstScreenful()
    {
        var view = await BuildAsync(Message("m1", ChatRole.Assistant, LongReply(60), 0));

        // The first render resolves where to land; the reply starts at its own first line.
        Render(view.Render(Context())).ShouldContain("line00");

        await view.HandleKeyAsync(Key(ConsoleKey.PageDown), Context(), CancellationToken.None);
        var after = Render(view.Render(Context()));

        after.ShouldNotContain("line00");
        after.ShouldContain("line1");
    }

    [Fact]
    public async Task ALongReply_CanBeReadRightToItsEnd()
    {
        var view = await BuildAsync(Message("m1", ChatRole.Assistant, LongReply(60), 0));

        for (var i = 0; i < 12; i++)
        {
            await view.HandleKeyAsync(Key(ConsoleKey.PageDown), Context(), CancellationToken.None);
            Render(view.Render(Context()));
        }

        Render(view.Render(Context())).ShouldContain("line59");
    }

    [Fact]
    public async Task ScrollingBackUp_Works()
    {
        var view = await BuildAsync(Message("m1", ChatRole.Assistant, LongReply(60), 0));

        for (var i = 0; i < 6; i++)
        {
            await view.HandleKeyAsync(Key(ConsoleKey.PageDown), Context(), CancellationToken.None);
            Render(view.Render(Context()));
        }

        for (var i = 0; i < 12; i++)
        {
            await view.HandleKeyAsync(Key(ConsoleKey.PageUp), Context(), CancellationToken.None);
            Render(view.Render(Context()));
        }

        Render(view.Render(Context())).ShouldContain("line00");
    }

    [Fact]
    public async Task OpeningAConversation_StartsTheNewestReplyAtItsFirstLine()
    {
        // Not at the foot of the transcript: a turn is read from its beginning.
        var view = await BuildAsync(
            Message("m1", ChatRole.User, "a question", 0),
            Message("m2", ChatRole.Assistant, LongReply(60), 1));

        var text = Render(view.Render(Context()));

        text.ShouldContain("line00");
        text.ShouldNotContain("line59");
    }

    [Fact]
    public async Task MovingToAMessageOffScreen_BringsItIntoView()
    {
        var view = await BuildAsync(
            Message("m1", ChatRole.User, "the first thing said", 0),
            Message("m2", ChatRole.Assistant, LongReply(60), 1));

        Render(view.Render(Context()));

        await view.HandleKeyAsync(Key(ConsoleKey.UpArrow), Context(), CancellationToken.None);

        Render(view.Render(Context())).ShouldContain("the first thing said");
    }

    [Fact]
    public async Task ScrollingWithinAMessage_KeepsItSelected()
    {
        // The header names the selected turn; paging through one reply should not look like
        // walking through the conversation.
        var view = await BuildAsync(
            Message("m1", ChatRole.User, "a question", 0),
            Message("m2", ChatRole.Assistant, LongReply(60), 1));

        Render(view.Render(Context()));

        await view.HandleKeyAsync(Key(ConsoleKey.PageDown), Context(), CancellationToken.None);

        Render(view.Render(Context())).ShouldContain("message 2/2");
    }

    [Fact]
    public async Task AShortTranscript_DoesNotScrollPastItself()
    {
        var view = await BuildAsync(Message("m1", ChatRole.Assistant, "just one short line", 0));

        for (var i = 0; i < 5; i++)
        {
            await view.HandleKeyAsync(Key(ConsoleKey.PageDown), Context(), CancellationToken.None);
            Render(view.Render(Context()));
        }

        Render(view.Render(Context())).ShouldContain("just one short line");
    }
}
