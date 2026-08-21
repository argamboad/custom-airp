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
/// Drives emoji completion through the same key path the shell uses, because the parts it is
/// built from are each already covered and what is left to get wrong is the wiring: which key
/// the list claims, when it opens, and what reaches the site in the end.
/// </summary>
public class EmojiComposerTests
{
    private const string Tada = "\U0001F389";
    private const string Grin = "\U0001F601";

    [Fact]
    public async Task TypingAClosedShortcode_SubstitutesTheEmoji()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, "nice :tada: work");
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe($"nice {Tada} work");
    }

    [Fact]
    public async Task TabAcceptsTheHighlightedSuggestion()
    {
        var view = Composer(out var sent);

        // ":tada" with no closing colon leaves the list open on an exact name.
        await TypeAsync(view, "ship it :tada");
        await Press(view, AppCommand.Tab);
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe($"ship it {Tada}");
    }

    [Fact]
    public async Task TabStillIndentsWhenNoListIsOpen()
    {
        // Tab only means "accept" while there is something to accept. Losing indentation to a
        // feature that is not on screen would be a bad trade.
        var view = Composer(out var sent);

        await TypeAsync(view, "a");
        await Press(view, AppCommand.Tab);
        await TypeAsync(view, "b");
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("a  b");
    }

    [Fact]
    public async Task EnterAlwaysSends_EvenWithTheListOpen()
    {
        // The one key that spends credits keeps its meaning no matter what is on screen.
        var view = Composer(out var sent);

        await TypeAsync(view, "hello :sm");
        Render(view).Contains("Tab inserts", StringComparison.Ordinal)
            .ShouldBeTrue("the list has to be open for this to prove anything");

        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("hello :sm");
    }

    [Fact]
    public async Task EscapeDismissesTheListBeforeItLeavesTheComposer()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, "draft :sm");

        // First Escape closes the list; the draft and the composer are still there.
        await Press(view, AppCommand.Back);
        Render(view).ShouldContain("draft :sm");

        // Second Escape leaves the composer, which is what Escape does with no list open.
        await Press(view, AppCommand.Back);
        Render(view).ShouldNotContain("Enter sends");
    }

    [Fact]
    public async Task TheListOffersChoicesAndSaysHowToDriveThem()
    {
        var view = Composer(out _);

        await TypeAsync(view, ":tad");

        var rendered = Render(view);
        rendered.ShouldContain("tada");
        rendered.ShouldContain("Tab inserts");
    }

    [Theory]
    [InlineData("call at 10:30")]
    [InlineData("see https://x.com")]
    [InlineData("the ratio was 3:1")]
    public async Task AColonInOrdinaryProse_NeverOffersAnEmoji(string prose)
    {
        // Word completion may well be open here — that is its job. What must not happen is a
        // colon in a time, a URL or a ratio being read as the start of a shortcode.
        var view = Composer(out _);

        await TypeAsync(view, prose);

        var rendered = Render(view);
        rendered.Any(char.IsHighSurrogate)
            .ShouldBeFalse("no emoji should be on offer: " + rendered);
    }

    [Fact]
    public async Task ArrowKeysChooseFromTheListRatherThanMovingTheCaret()
    {
        // Two runs of the same keys, differing only by one MoveDown. If the arrow moved the
        // caret instead of the selection, both would insert the same emoji.
        var top = Composer(out var withoutArrow);
        await TypeAsync(top, ":s");
        await Press(top, AppCommand.Tab);
        await Send(top);

        var second = Composer(out var withArrow);
        await TypeAsync(second, ":s");
        await Press(second, AppCommand.MoveDown);
        await Press(second, AppCommand.Tab);
        await Send(second);

        var first = withoutArrow.ShouldHaveSingleItem();
        var next = withArrow.ShouldHaveSingleItem();

        first.ShouldNotContain(":");
        next.ShouldNotContain(":");
        next.ShouldNotBe(first, "the arrow chose a different entry");
    }

    [Fact]
    public async Task AcceptedEmojiSurvivesBackspaceAsOneCharacter()
    {
        // The whole point of the feature is an emoji that behaves like a character. Deleting
        // it must not leave half a surrogate pair in the draft.
        var view = Composer(out var sent);

        await TypeAsync(view, "oops :grin:");
        await Press(view, AppCommand.DeleteBack);
        await TypeAsync(view, "!");
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("oops !");
    }

    [Fact]
    public async Task UndoRestoresTheTypedShortcode()
    {
        // Accepting the wrong suggestion should cost one press to reverse, and should bring
        // back what was typed rather than swallowing the word.
        var view = Composer(out var sent);

        await TypeAsync(view, "wow :grin:");
        await Press(view, AppCommand.Undo);
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("wow :grin:");
    }

    [Fact]
    public async Task AnEmojiTypedByNameIsSentAsRealText()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, ":grin:");
        await Send(view);

        var inserted = sent.ShouldHaveSingleItem();
        inserted.ShouldBe(Grin);
        char.IsHighSurrogate(inserted[0]).ShouldBeTrue();
        char.IsLowSurrogate(inserted[1]).ShouldBeTrue("a lone surrogate would draw as tofu");
    }

    // ---------------------------------------------------------------- helpers

    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static ConversationView Composer(out List<string> sent)
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

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Blake" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        // Open the composer, as pressing I does.
        view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo('i', default, false, false, false),
                    KeyboardMode.Standard,
                    KeyContext.Navigation),
                Context(),
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return view;
    }

    private static async Task TypeAsync(ConversationView view, string text)
    {
        foreach (var character in text)
        {
            await view.HandleKeyAsync(
                new KeyStroke(AppCommand.Character, character, default),
                Context(),
                CancellationToken.None);
        }
    }

    private static async Task Press(ConversationView view, AppCommand command)
        => await view.HandleKeyAsync(new KeyStroke(command, '\0', default), Context(), CancellationToken.None);

    private static async Task Send(ConversationView view)
    {
        var action = await view.HandleKeyAsync(
            new KeyStroke(AppCommand.Accept, '\r', new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)),
            Context(),
            CancellationToken.None);

        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

    private static string Render(ConversationView view)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            // Spectre's CI enrichers turn ANSI back on under GITHUB_ACTIONS; opt out so No means no.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 24;
        console.Write(view.Render(Context()));
        return writer.ToString();
    }
}
