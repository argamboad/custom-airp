using NSubstitute;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Application.Text;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

public class WordListTests
{
    [Fact]
    public void TheDictionary_LoadedFromTheEmbeddedResource()
    {
        // A resource that fails to embed leaves an empty list and a feature that silently
        // does nothing, which is the failure mode hardest to notice.
        WordList.All.Count.ShouldBeGreaterThan(1000);
        WordList.All.ShouldContain("because");
        WordList.All.ShouldContain("conversation");
    }

    [Fact]
    public void TheDictionary_IsSortedAndLowercase()
    {
        WordList.All.ShouldBe(WordList.All.OrderBy(static w => w, StringComparer.Ordinal));
        WordList.All.ShouldAllBe(w => w == w.ToLowerInvariant());
    }

    [Fact]
    public void Suggest_ReturnsOnlyPrefixMatches()
    {
        var suggestions = WordList.Suggest("conv");

        suggestions.ShouldNotBeEmpty();
        suggestions.ShouldAllBe(w => w.StartsWith("conv"));
    }

    [Fact]
    public void Suggest_OffersTheShortestCompletionsFirst()
    {
        var suggestions = WordList.Suggest("conv");

        suggestions[0].ShouldBe("convert", "shorter than convince or conversation");
        suggestions.ShouldBe(suggestions.OrderBy(static w => w.Length).ThenBy(static w => w, StringComparer.Ordinal));
    }

    [Fact]
    public void Suggest_DoesNotOfferTheWordAlreadyTypedInFull()
    {
        // "because" is in the list, but a user who has typed it has nothing left to complete.
        WordList.Suggest("because").ShouldNotContain("because");
    }

    [Fact]
    public void Suggest_StaysQuietUntilThereIsEnoughToGoOn()
    {
        WordList.Suggest("b").ShouldBeEmpty();
        WordList.Suggest("be").ShouldBeEmpty();
        WordList.Suggest("bec").ShouldNotBeEmpty();
    }

    [Fact]
    public void Suggest_HonoursTheLimit()
        => WordList.Suggest("con", 4).Count.ShouldBeLessThanOrEqualTo(4);

    [Fact]
    public void Suggest_IsCaseInsensitive()
        => WordList.Suggest("Bec").ShouldBe(WordList.Suggest("bec"));

    [Theory]
    [InlineData("writing a mess", 14, "mess")]
    [InlineData("conve", 5, "conve")]
    public void TokenAt_FindsTheWordBeingFinished(string line, int column, string expected)
        => WordList.TokenAt(line, column)!.Value.Prefix.ShouldBe(expected);

    [Theory]
    [InlineData("hello there", 3)]      // caret mid-word: an edit, not a write
    [InlineData("go", 2)]               // too short to guess from
    [InlineData("done ", 5)]            // caret after a space
    [InlineData("won't", 3)]            // a contraction, not a word to complete
    public void TokenAt_StaysOutOfTheWay(string line, int column)
        => WordList.TokenAt(line, column).ShouldBeNull();

    [Theory]
    [InlineData("Bec", "because", "Because")]
    [InlineData("bec", "because", "because")]
    [InlineData("BEC", "because", "BECAUSE")]
    public void MatchCase_KeepsTheCapitalisationTheUserWasUsing(string typed, string word, string expected)
        => WordList.MatchCase(typed, word).ShouldBe(expected);
}

/// <summary>Word completion driven through the composer, as a user would.</summary>
public class WordCompletionTests
{
    [Fact]
    public async Task TabCompletesTheWordBeingTyped()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, "this is a conversa");
        await Press(view, AppCommand.Tab);
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("this is a conversation");
    }

    [Fact]
    public async Task TheStripOpensOnlyOnceThereIsEnoughOfAWord()
    {
        var view = Composer(out _);

        await TypeAsync(view, "co");
        Render(view).ShouldNotContain("Tab inserts");

        await TypeAsync(view, "n");
        Render(view).ShouldContain("Tab inserts");
    }

    [Fact]
    public async Task ArrowsChooseAmongWords()
    {
        var first = Composer(out var withoutArrow);
        await TypeAsync(first, "con");
        await Press(first, AppCommand.Tab);
        await Send(first);

        var second = Composer(out var withArrow);
        await TypeAsync(second, "con");
        await Press(second, AppCommand.MoveDown);
        await Press(second, AppCommand.Tab);
        await Send(second);

        withArrow.ShouldHaveSingleItem().ShouldNotBe(withoutArrow.ShouldHaveSingleItem());
    }

    [Fact]
    public async Task AcceptingKeepsTheCapitalYouStartedWith()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, "Conversa");
        await Press(view, AppCommand.Tab);
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("Conversation");
    }

    [Fact]
    public async Task EnterStillSendsWithWordsOnOffer()
    {
        // Same rule as the emoji strip: the key that spends credits keeps its meaning.
        var view = Composer(out var sent);

        await TypeAsync(view, "conve");
        Render(view).ShouldContain("Tab inserts");

        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("conve");
    }

    [Fact]
    public async Task TypingPastAWordClosesTheStrip()
    {
        var view = Composer(out _);

        await TypeAsync(view, "conve");
        Render(view).ShouldContain("Tab inserts");

        await TypeAsync(view, " ");
        Render(view).ShouldNotContain("Tab inserts");
    }

    [Fact]
    public async Task AColonTokenBeatsWordCompletion()
    {
        // ":smi" is a shortcode name. Completing it as the English word "smile" would insert
        // text where the colon asked for an emoji.
        var view = Composer(out var sent);

        await TypeAsync(view, "look :smi");
        await Press(view, AppCommand.Tab);
        await Send(view);

        var inserted = sent.ShouldHaveSingleItem();
        inserted.ShouldNotContain("smi");
        inserted.Any(char.IsHighSurrogate).ShouldBeTrue("an emoji, not a word");
    }

    [Fact]
    public async Task UndoRestoresTheTypedPrefix()
    {
        var view = Composer(out var sent);

        await TypeAsync(view, "a conve");
        await Press(view, AppCommand.Tab);
        await Press(view, AppCommand.Undo);
        await Send(view);

        sent.ShouldHaveSingleItem().ShouldBe("a conve");
    }

    [Fact]
    public async Task TheStripNeverCostsMoreThanOneRow()
    {
        // The strip is drawn in space taken from the transcript, and the layout reserves
        // exactly one row for it. A wrap here would push the composer off the bottom.
        var view = Composer(out _);

        await TypeAsync(view, "con");

        var rendered = Render(view);
        var strip = rendered.Split('\n').Where(static l => l.Contains("Tab inserts")).ToList();

        strip.ShouldHaveSingleItem();
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
                KeyMap.Resolve(
                    new ConsoleKeyInfo(character, default, char.IsUpper(character), false, false),
                    KeyboardMode.Standard,
                    KeyContext.Text),
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
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 24;
        console.Write(view.Render(Context()));
        return writer.ToString();
    }
}
