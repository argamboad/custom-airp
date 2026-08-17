using Microsoft.Extensions.DependencyInjection;
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
/// Renders every view to a string.
/// </summary>
/// <remarks>
/// The views compose Spectre markup by hand, so a name containing <c>[</c>, an empty list,
/// or a narrow window are all live crash risks that the type system cannot catch — and a
/// view that throws at render time shows the user an error box instead of their data. These
/// tests drive each view through a recording console so those faults surface here instead.
/// </remarks>
public class ViewRenderingTests
{
    private static string RenderToText(IRenderable renderable, int width = 100, int height = 30)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Write(renderable);

        return writer.ToString();
    }

    /// <summary>
    /// Drives a view's activation to completion, including the background work it defers.
    /// </summary>
    /// <param name="view">The view to activate.</param>
    private static async Task ActivateAsync(IView view)
    {
        var action = await view.OnActivatedAsync(CancellationToken.None);

        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

    private static RenderContext Context(int width = 100, int height = 24)
        => new(width, height, Theme.For(ThemeName.Dark), new AirpOptions());

    private static Chat Chat(string name = "Professor", string id = "1") => new()
    {
        Id = id,
        Name = name,
        Speaker = "Professor",
        LatestMessage = "I settle into my office…",
        LastMessageAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
    };

    private static ServiceProvider BuildServices(params Chat[] chats)
    {
        var services = new ServiceCollection();

        var chatService = Substitute.For<IChatService>();
        chatService.Cached.Returns(chats);
        chatService.GetAsync(Arg.Any<CancellationToken>()).Returns(chats);
        chatService.Filter(Arg.Any<string>()).Returns(chats);

        services.AddSingleton(chatService);
        services.AddSingleton(Substitute.For<IClipboardService>());
        services.AddSingleton(Substitute.For<IExportService>());
        services.AddSingleton(Substitute.For<ISearchService>());
        services.AddSingleton(Substitute.For<IConversationService>());
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<AirpOptions>>(
            TestOptions.Default());

        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(100, 0.6, 28, 24)]
    [InlineData(40, 0.6, 28, 24)]
    [InlineData(20, 0.6, 28, 24)]
    [InlineData(3, 0.5, 10, 10)]
    public void SplitWidths_NeverInvertsOrReturnsAnEmptyPane(int total, double ratio, int minLeft, int minRight)
    {
        var (left, right) = Draw.SplitWidths(total, ratio, minLeft, minRight);

        left.ShouldBeGreaterThan(0);
        right.ShouldBeGreaterThan(0);
        (left + right).ShouldBeLessThanOrEqualTo(Math.Max(2, total));
    }

    [Fact]
    public void SplitWidths_HonoursTheRatioWhenThereIsRoom()
    {
        var (left, right) = Draw.SplitWidths(101, 0.6, 20, 20);

        left.ShouldBe(60);
        right.ShouldBe(40);
    }

    [Fact]
    public void Wrap_KeepsShortLinesWhole()
        => Draw.Wrap("short", 40).ShouldBe(["short"]);

    [Fact]
    public void Wrap_BreaksOnWordBoundaries()
    {
        var segments = Draw.Wrap("the quick brown fox jumps over the lazy dog", 12);

        segments.ShouldAllBe(s => s.Length <= 12);
        string.Join(' ', segments).ShouldBe("the quick brown fox jumps over the lazy dog");
    }

    [Fact]
    public void Wrap_HardBreaksATokenLongerThanThePane()
    {
        var segments = Draw.Wrap(new string('x', 25), 10);

        segments.Count.ShouldBe(3);
        segments.ShouldAllBe(s => s.Length <= 10);
        string.Concat(segments).ShouldBe(new string('x', 25));
    }

    [Fact]
    public void Wrap_LosesNoChatsOfProse()
    {
        const string prose = "Lacie's breath hitches as your lips find her neck, her head tilting "
                             + "back against the wall with a soft, involuntary sound.";

        var rejoined = string.Join(' ', Draw.Wrap(prose, 30));

        rejoined.ShouldBe(prose, "wrapping must not drop content the way truncation did");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Wrap_YieldsASingleEmptySegmentForBlankInput(string? text)
        => Draw.Wrap(text, 20).ShouldBe([string.Empty]);

    [Fact]
    public void Wrap_ToleratesAZeroWidthPane()
        => Should.NotThrow(() => Draw.Wrap("some text here", 0));

    [Fact]
    public void WrapSegments_ReportsOffsetsThatIndexBackIntoTheSource()
    {
        // The composer places its caret by these offsets, so a segment claiming the wrong
        // start would draw the caret on the wrong row or the wrong chat.
        const string prose = "the quick brown fox jumps over the lazy dog";

        foreach (var (start, text) in Draw.WrapSegments(prose, 12))
        {
            prose.Substring(start, text.Length).ShouldBe(text);
        }
    }

    [Fact]
    public void WrapSegments_ReportsOffsetsForAHardBrokenToken()
    {
        var token = new string('x', 25);

        Draw.WrapSegments(token, 10).Select(static s => s.Start).ShouldBe([0, 10, 20]);
    }

    [Fact]
    public void ChatListView_RendersRowsAndPreview()
    {
        using var services = BuildServices(Chat(), Chat("Captain", "2"));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("Professor");
        text.ShouldContain("Captain");
        text.ShouldContain("Latest message");
    }

    [Fact]
    public void ChatListView_NamesItsRowsChats()
    {
        // The list once showed "1 character(s)" over a row that was a conversation, under a
        // heading that said Characters. It is the reader's chat; it is named as one.
        using var services = BuildServices(Chat("North Dock", "c1"));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        view.Title.ShouldBe("Chats");

        var text = RenderToText(view.Render(Context()));
        text.ShouldContain("1 chat");
        text.ShouldNotContain("character");
    }

    [Fact]
    public void ChatListView_CallsThePreviewItsLatestMessage()
    {
        using var services = BuildServices(Chat("North Dock", "c1"));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("Latest message");
        text.ShouldNotContain("Prompt preview");
    }

    [Fact]
    public void ChatListView_CountsMoreThanOneChatWithoutABracketedS()
    {
        using var services = BuildServices(Chat("North Dock", "c1"), Chat("Another", "c2"));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        RenderToText(view.Render(Context())).ShouldContain("2 chats");
    }

    [Fact]
    public void ChatListView_OffersOnlyTheKeysThatApplyToAChat()
    {
        using var services = BuildServices(Chat("North Dock", "c1"));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        var keys = view.KeyHints.Select(static h => h.Key).ToList();

        keys.ShouldContain("Enter");
        keys.ShouldContain("R");

        // A chat has no definition to edit, no favourite of its own, and nothing to do with
        // image generation.
        keys.ShouldNotContain("E");
        keys.ShouldNotContain("G");
        keys.ShouldNotContain("F");
        keys.ShouldNotContain("Ctrl+H");
    }

    [Fact]
    public void ChatListView_RendersAnEmptyAccountWithoutThrowing()
    {
        using var services = BuildServices();
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        RenderToText(view.Render(Context())).ShouldContain("No chats");
    }

    [Theory]
    [InlineData("Doctor [Who]")]
    [InlineData("100% [bold]red[/] alert")]
    [InlineData("brackets ][ everywhere")]
    public void ChatListView_SurvivesNamesThatLookLikeMarkup(string name)
    {
        // A chat name is remote data. If it reaches Spectre unescaped the whole view
        // throws, so the escaping has to hold for anything the site can return.
        using var services = BuildServices(Chat(name));
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        var text = Should.NotThrow(() => RenderToText(view.Render(Context())));

        text.ShouldContain("[", Case.Sensitive);
    }

    [Theory]
    [InlineData(40, 8)]
    [InlineData(45, 10)]
    [InlineData(60, 12)]
    [InlineData(200, 60)]
    public void ChatListView_SurvivesUnusualWindowSizes(int width, int height)
    {
        using var services = BuildServices(Chat());
        var view = ActivatorUtilities.CreateInstance<ChatListView>(services);

        Should.NotThrow(() => RenderToText(view.Render(Context(width, height)), width, height));
    }

    [Fact]
    public void SearchView_RendersThePromptBeforeAnySearch()
    {
        using var services = BuildServices();
        var view = ActivatorUtilities.CreateInstance<SearchView>(services);

        RenderToText(view.Render(Context())).ShouldContain("Type a query");
    }

    [Fact]
    public void CommandPaletteView_RendersItsCommands()
    {
        var view = new CommandPaletteView(
        [
            new PaletteCommand("Refresh", "Re-read from the site", _ => Task.FromResult(ViewAction.None)),
            new PaletteCommand("Quit", "Close the client", _ => Task.FromResult(ViewAction.Quit)),
        ]);

        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("Refresh");
        text.ShouldContain("Quit");
    }

    [Fact]
    public async Task ConversationView_LabelsEachTurnBySpeaker()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.Conversations.ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = Domain.Conversations.ChatRole.User, Text = "what I asked" },
                new() { Id = "2", ConversationId = "c", Role = Domain.Conversations.ChatRole.Assistant, Text = "the reply" },
            ]);

        var chat = Chat("North Dock") with { Speaker = "Blake" };
        var view = new ConversationView(chat, conversations, Substitute.For<IClipboardService>(), Substitute.For<IExportService>());

        // Drive activation so the transcript loads, then render.
        await ActivateAsync(view);

        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("You", Case.Sensitive);
        text.ShouldContain("Blake");
        text.ShouldContain("what I asked");
        text.ShouldContain("the reply");
    }

    [Fact]
    public void ConversationView_RendersItsEmptyState()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.Conversations.ChatMessage>>(_ => []);

        var view = new ConversationView(
            Chat(), conversations, Substitute.For<IClipboardService>(), Substitute.For<IExportService>());

        RenderToText(view.Render(Context())).ShouldContain("no messages");
    }

    [Theory]
    [InlineData(40, 8)]
    [InlineData(200, 60)]
    public async Task ConversationView_SurvivesUnusualWindowSizes(int width, int height)
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.Conversations.ChatMessage>>(_ =>
            [
                new()
                {
                    Id = "1", ConversationId = "c", Role = Domain.Conversations.ChatRole.Assistant,
                    Text = string.Join(' ', Enumerable.Repeat("prose", 400)),
                },
            ]);

        var view = new ConversationView(
            Chat(), conversations, Substitute.For<IClipboardService>(), Substitute.For<IExportService>());

        await ActivateAsync(view);

        Should.NotThrow(() => RenderToText(view.Render(Context(width, height)), width, height));
    }

    [Fact]
    public void CommandPaletteView_RendersWithNoCommands()
        => Should.NotThrow(() => RenderToText(new CommandPaletteView([]).Render(Context())));

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void HelpView_RendersForBothKeyboardDialects(KeyboardMode mode)
    {
        var text = RenderToText(new HelpView(mode).Render(Context()));

        text.ShouldContain("Anywhere");
    }

    [Fact]
    public void ExportView_RendersAPreviewOfTheSelectedFormat()
    {
        var export = Substitute.For<IExportService>();
        export.Render(Arg.Any<object>(), Arg.Any<ExportFormat>()).Returns("# rendered output");

        var view = new ExportView(export, Substitute.For<IClipboardService>(), Chat(), "chat-1");

        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("Markdown");
        text.ShouldContain("rendered output");
    }

    [Fact]
    public void ExportView_ReportsARenderFailureInsteadOfThrowing()
    {
        var export = Substitute.For<IExportService>();
        export.Render(Arg.Any<object>(), Arg.Any<ExportFormat>())
            .Returns(_ => throw new InvalidOperationException("unsupported"));

        var view = new ExportView(export, Substitute.For<IClipboardService>(), Chat(), "x");

        RenderToText(view.Render(Context())).ShouldContain("cannot be rendered");
    }

    private static IConversationService SettingsService(Domain.Conversations.ChatSettings settings)
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetSettingsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(settings);
        return conversations;
    }

    [Fact]
    public async Task ChatSettingsView_NamesEachLevelRatherThanNumberingIt()
    {
        var view = new ChatSettingsView(
            SettingsService(new Domain.Conversations.ChatSettings { Lust = 3, ResponseLength = 1, Creativity = 4 }),
            "chat-1",
            "North Dock");

        await ActivateAsync(view);
        var text = RenderToText(view.Render(Context()));

        text.ShouldContain("Explicit");
        text.ShouldContain("Concise");
        text.ShouldContain("Wild");
    }

    [Fact]
    public async Task ChatSettingsView_SaysWhenALevelHasNeverBeenSet()
    {
        var view = new ChatSettingsView(
            SettingsService(new Domain.Conversations.ChatSettings { Lust = 2 }),
            "chat-1",
            "North Dock");

        await ActivateAsync(view);

        RenderToText(view.Render(Context())).ShouldContain("Not set");
    }

    [Fact]
    public async Task ChatSettingsView_ShowsWhatAChangeWouldReplaceAndDoesNotWriteUntilAccepted()
    {
        // Arrowing across a scale must not commit anything: these settings change every
        // reply that follows, so the write waits for Enter.
        var conversations = SettingsService(new Domain.Conversations.ChatSettings { Creativity = 2 });
        var view = new ChatSettingsView(conversations, "chat-1", "North Dock");

        await ActivateAsync(view);
        await view.HandleKeyAsync(Right(), Context(), CancellationToken.None);

        var text = RenderToText(view.Render(Context()));
        text.ShouldContain("Creative");
        text.ShouldContain("was Balanced");

        await conversations.DidNotReceive().UpdateSettingsAsync(
            Arg.Any<string>(),
            Arg.Any<Domain.Conversations.ChatSettings>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChatSettingsView_AppliesOnlyTheSettingThatMoved()
    {
        var conversations = SettingsService(
            new Domain.Conversations.ChatSettings { Lust = 3, ResponseLength = 3, Creativity = 2 });

        conversations.UpdateSettingsAsync(
                Arg.Any<string>(),
                Arg.Any<Domain.Conversations.ChatSettings>(),
                Arg.Any<CancellationToken>())
            .Returns(new Domain.Conversations.ChatSettings { Lust = 3, ResponseLength = 3, Creativity = 3 });

        var view = new ChatSettingsView(conversations, "chat-1", "North Dock");

        await ActivateAsync(view);
        await view.HandleKeyAsync(Right(), Context(), CancellationToken.None);
        await RunAsync(await view.HandleKeyAsync(Enter(), Context(), CancellationToken.None));

        await conversations.Received(1).UpdateSettingsAsync(
            "chat-1",
            Arg.Is<Domain.Conversations.ChatSettings>(s =>
                s != null && s.Creativity == 3 && s.Lust == null && s.ResponseLength == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChatSettingsView_CannotBeMovedOutsideTheSitesRange()
    {
        var view = new ChatSettingsView(
            SettingsService(new Domain.Conversations.ChatSettings { Creativity = 4 }),
            "chat-1",
            "North Dock");

        await ActivateAsync(view);

        for (var i = 0; i < 5; i++)
        {
            await view.HandleKeyAsync(Right(), Context(), CancellationToken.None);
        }

        // Level 4 is the top; the site rejects anything above it.
        RenderToText(view.Render(Context())).ShouldContain("Wild");
        RenderToText(view.Render(Context())).ShouldNotContain("was ");
    }

    private static (ConversationView View, IConversationService Conversations) BuildDeletable()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.Conversations.ChatMessage>>(_ =>
            [
                new() { Id = "m1", ConversationId = "c", Role = Domain.Conversations.ChatRole.Assistant, Text = "the opening line" },
                new() { Id = "m2", ConversationId = "c", Role = Domain.Conversations.ChatRole.User, Text = "something I regret" },
                new() { Id = "m3", ConversationId = "c", Role = Domain.Conversations.ChatRole.Assistant, Text = "the reply to it" },
            ]);

        var view = new ConversationView(
            Chat("North Dock") with { Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        return (view, conversations);
    }

    [Fact]
    public async Task ConversationView_DeleteAsksBeforeItRemovesAnything()
    {
        var (view, conversations) = BuildDeletable();
        await ActivateAsync(view);

        // A conversation opens on its newest turn, so step back to the middle message: it
        // and the one after it would go.
        await view.HandleKeyAsync(Up(), Context(), CancellationToken.None);
        var action = await view.HandleKeyAsync(Delete(), Context(), CancellationToken.None);

        var confirm = action.ShouldBeOfType<ViewAction.PushAction>().View;
        var text = RenderToText(confirm.Render(Context()));

        text.ShouldContain("cannot be undone");
        text.ShouldContain("2 message(s) would be removed");
        text.ShouldContain("1 message(s) would remain");

        await conversations.DidNotReceive().DeleteFromAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationView_DeleteDoesNothingUntilItIsConfirmed()
    {
        var (view, conversations) = BuildDeletable();
        await ActivateAsync(view);

        var action = await view.HandleKeyAsync(Delete(), Context(), CancellationToken.None);
        var confirm = action.ShouldBeOfType<ViewAction.PushAction>().View;

        // Any key that is not the confirmation backs out.
        await confirm.HandleKeyAsync(Down(), Context(), CancellationToken.None);

        await conversations.DidNotReceive().DeleteFromAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConversationView_DeleteRemovesFromTheSelectedMessageOnceConfirmed()
    {
        var (view, conversations) = BuildDeletable();
        conversations.DeleteFromAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Domain.Conversations.ChatMessage>>(_ =>
            [
                new() { Id = "m1", ConversationId = "c", Role = Domain.Conversations.ChatRole.Assistant, Text = "the opening line" },
            ]);

        await ActivateAsync(view);
        await view.HandleKeyAsync(Up(), Context(), CancellationToken.None);

        var confirm = (await view.HandleKeyAsync(Delete(), Context(), CancellationToken.None))
            .ShouldBeOfType<ViewAction.PushAction>().View;

        await RunAsync(await confirm.HandleKeyAsync(Enter(), Context(), CancellationToken.None));

        await conversations.Received(1).DeleteFromAsync("1", "m2", Arg.Any<CancellationToken>());

        // The transcript is replaced by what survived, not merged with what was there.
        var text = RenderToText(view.Render(Context()));
        text.ShouldContain("the opening line");
        text.ShouldNotContain("something I regret");
        text.ShouldNotContain("the reply to it");
    }

    private static KeyStroke Delete()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Down()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Up()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Right()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Enter()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static async Task RunAsync(ViewAction action)
    {
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }
}
