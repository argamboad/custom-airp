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
/// Keys the footer advertises must actually do something.
/// </summary>
/// <remarks>
/// A hint that names a key nobody wired up is worse than no hint: it tells the user the
/// feature is broken. These tests pin the bindings that were promised in a legend, which is
/// exactly where the mismatch crept in.
/// </remarks>
public class KeyHandlingTests
{
    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static RenderContext Context(int messageChatLimit)
    {
        var options = new AirpOptions();
        options.MessageCharacterLimit = messageChatLimit;
        return new RenderContext(100, 24, Theme.For(ThemeName.Dark), options);
    }

    /// <summary>Builds a conversation view whose send captures what it was given.</summary>
    /// <param name="sent">Receives the text handed to the service.</param>
    /// <returns>The view.</returns>
    private static ConversationView ComposerView(out List<string> sent, string? libraryRoot = null)
    {
        var captured = new List<string>();
        sent = captured;

        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);
        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(call =>
            {
                captured.Add(call.ArgAt<string>(1));
                return [];
            });

        return new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>(),
            libraryRoot is null ? null : new Airp.Infrastructure.TextLibrary(libraryRoot));
    }

    /// <summary>Feeds text through the composer one key at a time, as a paste arrives.</summary>
    /// <param name="view">The view being driven.</param>
    /// <param name="text">The text to paste.</param>
    /// <param name="context">Layout context.</param>
    private static async Task PasteAsync(ConversationView view, string text, RenderContext context)
    {
        foreach (var c in text)
        {
            var stroke = c == '\n' ? PastedEnter() : Typed(c) with { Pasted = true };
            await view.HandleKeyAsync(stroke, context, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Composer_CarriesATwelveThousandChatPasteThroughIntact()
    {
        // The site's message limit is around this size, so it is the largest paste that has
        // to work rather than an arbitrary stress figure. Every chat must survive, and
        // the line breaks must still be line breaks.
        var context = Context(20000);
        var view = ComposerView(out var sent);

        var paste = string.Join(
            '\n',
            Enumerable.Range(0, 133).Select(i => new string((char)('a' + (i % 26)), 89)));

        await view.HandleKeyAsync(Stroke('i'), context, CancellationToken.None);
        await PasteAsync(view, paste, context);
        await RunAsync(await view.HandleKeyAsync(TypedEnter(), context, CancellationToken.None));

        sent.ShouldHaveSingleItem().ShouldBe(paste);
    }

    [Fact]
    public async Task Composer_RefusesToSendPastTheMessageLimit()
    {
        // The composer is filled in one write, so a field that accepts less would take the
        // beginning and drop the rest without a word — and a truncated message costs the
        // same credits as the whole one.
        var context = Context(1000);
        var view = ComposerView(out var sent);

        await view.HandleKeyAsync(Stroke('i'), context, CancellationToken.None);
        await PasteAsync(view, new string('x', 1001), context);

        var action = await view.HandleKeyAsync(TypedEnter(), context, CancellationToken.None);
        await RunAsync(action);

        sent.ShouldBeEmpty();
        action.ShouldBeOfType<ViewAction.StatusAction>().Text.ShouldContain("nothing has been sent");
    }

    [Fact]
    public async Task Composer_ShowsHowMuchOfTheLimitIsUsed()
    {
        var context = Context(12000);
        var view = ComposerView(out _);

        await view.HandleKeyAsync(Stroke('i'), context, CancellationToken.None);
        await PasteAsync(view, new string('x', 500), context);

        Render(view.Render(context)).ShouldContain("500/12,000 characters");
    }

    [Fact]
    public async Task Composer_CountsLineBreaksAgainstTheLimit()
    {
        // They are characters in the message the site receives, and a count that ignored
        // them would wave through a message the site then rejects.
        var context = Context(12000);
        var view = ComposerView(out _);

        await view.HandleKeyAsync(Stroke('i'), context, CancellationToken.None);
        await PasteAsync(view, "a\nb\nc", context);

        Render(view.Render(context)).ShouldContain("5/12,000 characters");
    }

    [Fact]
    public async Task Composer_WithNoConfiguredLimit_JustCounts()
    {
        var context = Context(0);
        var view = ComposerView(out _);

        await view.HandleKeyAsync(Stroke('i'), context, CancellationToken.None);
        await PasteAsync(view, new string('x', 40), context);

        var rendered = Render(view.Render(context));
        rendered.ShouldContain("40 characters");
        rendered.ShouldNotContain("too long");
    }

    private static KeyStroke Stroke(char c, KeyboardMode mode = KeyboardMode.Standard)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), mode, KeyContext.Navigation);

    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 24;
        console.Write(renderable);
        return writer.ToString();
    }

    private static ConversationView BuildConversation()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "a reply" },
            ]);

        return new ConversationView(
            new Chat { Id = "c", Name = "North Dock", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());
    }

    [Theory]
    [InlineData('i')]
    [InlineData('I')]
    public async Task ConversationView_OpensTheComposerOnI(char key)
    {
        // The footer promises "I / Enter — Write a message". It went unmapped once already.
        var view = BuildConversation();

        await view.HandleKeyAsync(Stroke(key), Context(), CancellationToken.None);

        Render(view.Render(Context())).ShouldContain("Enter sends");
    }

    [Fact]
    public async Task ConversationView_OpensTheComposerOnEnter()
    {
        var view = BuildConversation();
        var enter = KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

        await view.HandleKeyAsync(enter, Context(), CancellationToken.None);

        Render(view.Render(Context())).ShouldContain("Enter sends");
    }

    [Fact]
    public async Task ConversationView_EscapeClosesTheComposerWithoutLeavingTheView()
    {
        var view = BuildConversation();
        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        var escape = KeyMap.Resolve(
            new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

        var action = await view.HandleKeyAsync(escape, Context(), CancellationToken.None);

        action.ShouldNotBeOfType<ViewAction.PopAction>("the first Escape closes the composer, not the view");
        Render(view.Render(Context())).ShouldNotContain("Enter sends");
    }

    [Fact]
    public async Task ConversationView_TypingReachesTheComposer()
    {
        var view = BuildConversation();
        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        foreach (var c in "hello")
        {
            var stroke = KeyMap.Resolve(
                new ConsoleKeyInfo(c, default, false, false, false),
                KeyboardMode.Standard,
                KeyContext.Text);

            await view.HandleKeyAsync(stroke, Context(), CancellationToken.None);
        }

        Render(view.Render(Context())).ShouldContain("hello");
    }

    [Fact]
    public async Task ConversationView_LongDraft_StaysVisibleAsItIsTyped()
    {
        // Reported from a live session: past the width of the pane the message typed itself
        // off the right edge. The tail of the draft — the part being written — has to be on
        // screen at every length.
        var view = BuildConversation();
        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        var words = Enumerable.Range(0, 60).Select(i => $"word{i}").ToArray();

        foreach (var c in string.Join(' ', words))
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                Context(),
                CancellationToken.None);
        }

        var rendered = Render(view.Render(Context()));

        // The last word typed is the one that has to be on screen; nothing may be elided.
        rendered.ShouldContain(words[^1]);
        rendered.ShouldNotContain("…");
    }

    [Fact]
    public async Task ConversationView_LongDraft_KeepsTheTranscriptOnScreenToo()
    {
        // The composer grows with the draft, but not without limit: a reply is written while
        // reading what it answers.
        var view = BuildConversation();
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));
        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        foreach (var c in new string('x', 2000))
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                Context(),
                CancellationToken.None);
        }

        var rendered = Render(view.Render(Context()));

        // The conversation is still readable behind the composer, and the pane still fits.
        rendered.ShouldContain("a reply");
        rendered.Split('\n').Length.ShouldBeLessThanOrEqualTo(26, "the pane must not overflow its height");
    }

    [Fact]
    public async Task ConversationView_SendingAMessage_NeverRemovesWhatIsAlreadyOnScreen()
    {
        // Reported from a live session: the reply arrived and took the rest of the
        // conversation with it. The service can only return what it can see, so the view
        // folds the result into the transcript it is displaying instead of trusting it to be
        // complete.
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new()
                {
                    Id = "old",
                    ConversationId = "c",
                    Role = ChatRole.Assistant,
                    Text = "an earlier turn",
                    SentAtUtc = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
                },
            ]);

        // The failure mode exactly: the send reports only the new exchange.
        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new()
                {
                    Id = "sent",
                    ConversationId = "c",
                    Role = ChatRole.User,
                    Text = "what I just typed",
                    SentAtUtc = new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero),
                },
                new()
                {
                    Id = "reply",
                    ConversationId = "c",
                    Role = ChatRole.Assistant,
                    Text = "the brand new reply",
                    SentAtUtc = new DateTimeOffset(2026, 8, 5, 11, 0, 5, TimeSpan.Zero),
                },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "North Dock", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);
        await view.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('h', default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
            Context(),
            CancellationToken.None);

        var enter = KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

        await RunAsync(await view.HandleKeyAsync(enter, Context(), CancellationToken.None));

        // Scrolled to the newest turn, so assert on the header's count rather than on text
        // that may sit above the fold.
        Render(view.Render(Context())).ShouldContain("3/3");
    }

    /// <summary>Executes the deferred work a view returns, if any.</summary>
    /// <param name="action">The action a view returned.</param>
    private static async Task RunAsync(ViewAction action)
    {
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConversationView_StoppingAWaitSaysTheMessageWentAndKeepsTheDraft()
    {
        // Esc during the wait is not an un-send. The site already has the message, so the one
        // thing the reader must not be left thinking is that retyping it is free.
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);
        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(call =>
            {
                // Reaching this phase is the site confirming it took the message; the stop
                // lands after it, exactly as pressing Esc mid-wait would.
                call.ArgAt<IProgress<string>>(3)?.Report(SendPhase.Waiting);
                throw new OperationCanceledException();
            });

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        foreach (var c in "the message")
        {
            await view.HandleKeyAsync(Typed(c), Context(), CancellationToken.None);
        }

        var enter = KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

        var action = await view.HandleKeyAsync(enter, Context(), CancellationToken.None);
        action.ShouldBeOfType<ViewAction.RunAction>();

        var result = await ((ViewAction.RunAction)action).Work(CancellationToken.None);

        var status = result.ShouldBeOfType<ViewAction.StatusAction>();
        status.Kind.ShouldBe(StatusKind.Warning);
        status.Text.ShouldContain("already sent");

        // Kept, because losing typed text is the one outcome here that cannot be undone. The
        // composer closes on send as it always does, so the draft is asserted where it lives
        // rather than on a pane that is not open.
        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);
        Render(view.Render(Context())).ShouldContain("the message");
    }

    [Fact]
    public async Task ConversationView_StoppingBeforeTheMessageGoesIsNotReportedAsSent()
    {
        // The mirror: cancelled while still typing into the composer. Nothing reached the
        // site, so this must not claim otherwise — it stays a plain cancellation.
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);
        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(call =>
            {
                call.ArgAt<IProgress<string>>(3)?.Report(SendPhase.Typing);
                throw new OperationCanceledException();
            });

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Typed('x'), Context(), CancellationToken.None);

        var enter = KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

        var action = await view.HandleKeyAsync(enter, Context(), CancellationToken.None);

        await Should.ThrowAsync<OperationCanceledException>(
            () => ((ViewAction.RunAction)action).Work(CancellationToken.None));
    }

    [Theory]
    [InlineData('l')]
    [InlineData('L')]
    public void ToggleLineNumbers_RespondsToEitherCase(char key)
        => Stroke(key).Command.ShouldBe(AppCommand.ToggleLineNumbers);

    [Fact]
    public void VimMode_KeepsLowercaseLAsMovement()
        => Stroke('l', KeyboardMode.Vim).Command.ShouldBe(AppCommand.MoveRight);

    // AppCommand is internal, so the theory carries its underlying value rather than
    // widening the enum's visibility just to satisfy a test signature.
    [Theory]
    [InlineData('e', (int)AppCommand.Edit)]
    [InlineData('r', (int)AppCommand.Refresh)]
    [InlineData('c', (int)AppCommand.Copy)]
    [InlineData('x', (int)AppCommand.Export)]
    [InlineData('f', (int)AppCommand.Favorite)]
    [InlineData('v', (int)AppCommand.History)]
    [InlineData('q', (int)AppCommand.Quit)]
    [InlineData('/', (int)AppCommand.Search)]
    public void AdvertisedShortcuts_ResolveInLowercase(char key, int expected)
        => ((int)Stroke(key).Command).ShouldBe(expected);

    [Fact]
    public async Task ConversationView_APastedLineBreakIsTextRatherThanSend()
    {
        // Pasting two paragraphs used to send the first one the moment the line break
        // arrived — spending credits and posting half a message that cannot be taken back.
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "a reply" },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);

        foreach (var c in "first")
        {
            await view.HandleKeyAsync(Typed(c), Context(), CancellationToken.None);
        }

        await view.HandleKeyAsync(PastedEnter(), Context(), CancellationToken.None);

        foreach (var c in "second")
        {
            await view.HandleKeyAsync(Typed(c), Context(), CancellationToken.None);
        }

        await conversations.DidNotReceive().SendAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());

        var rendered = Render(view.Render(Context()));
        rendered.ShouldContain("first");
        rendered.ShouldContain("second");
    }

    [Fact]
    public async Task ConversationView_ATypedEnterStillSends()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);
        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await view.HandleKeyAsync(Stroke('i'), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Typed('h'), Context(), CancellationToken.None);

        var action = await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None);
        await RunAsync(action);

        await conversations.Received(1).SendAsync(
            "c",
            "h",
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[200~", true, true)]
    [InlineData("[201~", true, false)]
    [InlineData("[<0;5", false, false)]
    [InlineData("", false, false)]
    public void PasteMarkers_AreRecognisedAndNothingElseIs(string body, bool matched, bool pasting)
    {
        PasteMode.TryReadMarker(body, out var started).ShouldBe(matched);

        if (matched)
        {
            started.ShouldBe(pasting);
        }
    }

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

    private static KeyStroke TypedEnter()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

    private static KeyStroke PastedEnter() => TypedEnter() with { Pasted = true };

    [Fact]
    public async Task ConversationView_CarriesOnFromTheLastReplyOnAngleBracket()
    {
        // The footer promises "> Carry on". A hint naming a key nobody wired up has shipped
        // here before.
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "a reply" },
            ]);
        conversations.ContinueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<IProgress<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "a reply, carried on" },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));
        await RunAsync(await view.HandleKeyAsync(Stroke('>'), Context(), CancellationToken.None));

        await conversations.Received(1).ContinueAsync(
            "c",
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());

        Render(view.Render(Context())).ShouldContain("carried on");
    }

    [Fact]
    public async Task ConversationView_WithNoReplyYet_DoesNotAskToCarryOn()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));
        await RunAsync(await view.HandleKeyAsync(Stroke('>'), Context(), CancellationToken.None));

        await conversations.DidNotReceive().ContinueAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Delete_MeansRemoveAChatWhileTypingAndRemoveAThingWhileReading()
    {
        // Conflating the two would put an irreversible action one stray key away from an
        // editor, and would leave the editor's own Delete key doing nothing.
        var key = new ConsoleKeyInfo('\0', ConsoleKey.Delete, false, false, false);

        KeyMap.Resolve(key, KeyboardMode.Standard, KeyContext.Text).Command
            .ShouldBe(AppCommand.DeleteForward);

        KeyMap.Resolve(key, KeyboardMode.Standard, KeyContext.Navigation).Command
            .ShouldBe(AppCommand.Delete);
    }

    [Fact]
    public void VimMode_KeepsIAsARawCharacterForTheEditor()
    {
        // The composer's normal mode reads `i` as a character to enter insert mode, so
        // the global map must not claim it.
        Stroke('i', KeyboardMode.Vim).Command.ShouldBe(AppCommand.Character);
        Stroke('i').Command.ShouldBe(AppCommand.Character);
    }

    [Fact]
    public async Task ATypedSnippetTriggerExpandsIntoThePageStillEditable()
    {
        var root = Path.Combine(Path.GetTempPath(), "airp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "snippets"));
        await File.WriteAllTextAsync(Path.Combine(root, "snippets", "office.txt"), "First paragraph.\n\nSecond paragraph.");

        try
        {
            var view = ComposerView(out _, root);
            var context = Context();

            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false), KeyboardMode.Standard, KeyContext.Navigation),
                context, CancellationToken.None);

            foreach (var c in ":off")
            {
                await view.HandleKeyAsync(
                    KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                    context, CancellationToken.None);
            }

            var action = await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo('	', ConsoleKey.Tab, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                context, CancellationToken.None);

            // The page replaced the trigger, line breaks intact, and nothing was sent.
            var rendered = Render(view.Render(context));
            rendered.ShouldContain("First paragraph.");
            rendered.ShouldContain("Second paragraph.");
            rendered.ShouldNotContain(":off");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AMissingSnippetWarnsInsteadOfInsertingNothingSilently()
    {
        var root = Path.Combine(Path.GetTempPath(), "airp-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "snippets"));
        await File.WriteAllTextAsync(Path.Combine(root, "snippets", "ghost.txt"), "  ");

        try
        {
            var view = ComposerView(out _, root);
            var context = Context();

            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false), KeyboardMode.Standard, KeyContext.Navigation),
                context, CancellationToken.None);

            foreach (var c in ":gho")
            {
                await view.HandleKeyAsync(
                    KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                    context, CancellationToken.None);
            }

            var action = await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo('	', ConsoleKey.Tab, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                context, CancellationToken.None);

            action.ShouldBeOfType<ViewAction.StatusAction>().Kind.ShouldBe(StatusKind.Warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
