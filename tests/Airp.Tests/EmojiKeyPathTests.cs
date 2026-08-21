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
/// Drives the composer with real <see cref="ConsoleKeyInfo"/> values through
/// <see cref="KeyMap.Resolve"/>, the way the shell does.
/// </summary>
/// <remarks>
/// The other emoji tests build a <see cref="KeyStroke"/> directly, which assumes the key map
/// produces the command the view is waiting for. That assumption is exactly the kind that
/// holds in a test and fails at a keyboard — <c>:</c> is a shortcut in one context and a
/// character in another, and Tab could have been claimed by anything between the two.
/// </remarks>
public class EmojiKeyPathTests
{
    private const string Tada = "\U0001F389";

    [Fact]
    public void Colon_IsACharacterInsideAComposerAndAShortcutOutsideOne()
    {
        var typed = new ConsoleKeyInfo(':', default, shift: true, alt: false, control: false);

        KeyMap.Resolve(typed, KeyboardMode.Standard, KeyContext.Text).Command
            .ShouldBe(AppCommand.Character, "a colon has to reach the composer to open a shortcode");

        KeyMap.Resolve(typed, KeyboardMode.Standard, KeyContext.Navigation).Command
            .ShouldBe(AppCommand.CommandPalette);
    }

    [Fact]
    public void TabAndArrows_ReachAComposerIntact()
    {
        var text = KeyContext.Text;

        KeyMap.Resolve(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), KeyboardMode.Standard, text)
            .Command.ShouldBe(AppCommand.Tab);

        KeyMap.Resolve(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false), KeyboardMode.Standard, text)
            .Command.ShouldBe(AppCommand.MoveDown);

        KeyMap.Resolve(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false), KeyboardMode.Standard, text)
            .Command.ShouldBe(AppCommand.Back);
    }

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public async Task ShortcodeCompletion_WorksThroughTheRealKeyMap(KeyboardMode mode)
    {
        var view = Open(out var sent, mode);

        await TypeAsync(view, "ship :tada", mode);
        Render(view).Contains("Tab inserts", StringComparison.Ordinal)
            .ShouldBeTrue("the strip should be open after typing a shortcode");

        await PressAsync(view, new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false), mode);
        await SendAsync(view, mode);

        sent.ShouldHaveSingleItem().ShouldBe($"ship {Tada}");
    }

    [Fact]
    public async Task ClosingColon_SubstitutesThroughTheRealKeyMap()
    {
        var view = Open(out var sent, KeyboardMode.Standard);

        await TypeAsync(view, "yes :tada:", KeyboardMode.Standard);
        await SendAsync(view, KeyboardMode.Standard);

        sent.ShouldHaveSingleItem().ShouldBe($"yes {Tada}");
    }

    // ---------------------------------------------------------------- helpers

    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static ConversationView Open(out List<string> sent, KeyboardMode mode)
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

        PressAsync(view, new ConsoleKeyInfo('i', default, false, false, false), mode)
            .GetAwaiter()
            .GetResult();

        return view;
    }

    /// <summary>Presses a key exactly as the shell does: resolve against the view, then dispatch.</summary>
    private static async Task<ViewAction> PressAsync(ConversationView view, ConsoleKeyInfo key, KeyboardMode mode)
    {
        var stroke = KeyMap.Resolve(key, mode, view.KeyContext);
        return await view.HandleKeyAsync(stroke, Context(), CancellationToken.None);
    }

    private static async Task TypeAsync(ConversationView view, string text, KeyboardMode mode)
    {
        foreach (var character in text)
        {
            await PressAsync(
                view,
                new ConsoleKeyInfo(character, default, char.IsUpper(character) || character == ':', false, false),
                mode);
        }
    }

    private static async Task SendAsync(ConversationView view, KeyboardMode mode)
    {
        var action = await PressAsync(
            view,
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            mode);

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
