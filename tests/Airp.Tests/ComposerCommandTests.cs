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
/// The commands as they behave from the composer, driven by real key presses.
/// </summary>
/// <remarks>
/// Every stroke here is resolved through <see cref="KeyMap"/> rather than hand-built. A
/// hand-built stroke once shipped a key nobody could actually press, and the whole value of a
/// command surface is that typing it does something.
/// </remarks>
public class ComposerCommandTests
{
    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text);

    private static KeyStroke TypedEnter()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

    private static KeyStroke Nav(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static async Task RunAsync(ViewAction action)
    {
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

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

    private static (ConversationView View, IConversationService Conversations) Build()
    {
        var conversations = Substitute.For<IConversationService>();

        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "a reply" },
            ]);

        conversations.SendAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<IProgress<string>>(),
                Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);

        conversations.ContinueAsync(
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

        return (view, conversations);
    }

    /// <summary>Opens the composer and types, one real key at a time.</summary>
    private static async Task<ConversationView> ComposeAsync(ConversationView view, string text)
    {
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));
        await view.HandleKeyAsync(Nav('i'), Context(), CancellationToken.None);

        foreach (var c in text)
        {
            var stroke = c == '\n'
                ? TypedEnter() with { Pasted = true }
                : Typed(c) with { Pasted = true };

            await view.HandleKeyAsync(stroke, Context(), CancellationToken.None);
        }

        return view;
    }

    [Fact]
    public async Task A_direction_alone_asks_for_a_turn_and_stores_no_message()
    {
        var (view, conversations) = Build();

        await ComposeAsync(view, "/do have Mariana leave before he answers");
        await RunAsync(await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None));

        await conversations.Received(1).ContinueAsync(
            "c",
            Arg.Is<string?>(i => i != null && i.Contains("have Mariana leave", StringComparison.Ordinal)),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());

        await conversations.DidNotReceive().SendAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_direction_above_prose_steers_that_message_without_joining_it()
    {
        var (view, conversations) = Build();

        await ComposeAsync(view, "/do keep this one short\n\nHe sits down without a word.");
        await RunAsync(await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None));

        await conversations.Received(1).SendAsync(
            "c",
            "He sits down without a word.",
            Arg.Is<string?>(i => i != null && i.Contains("keep this one short", StringComparison.Ordinal)),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_command_is_refused_rather_than_sent()
    {
        // The expensive direction. A typo sent as prose is billed, lands in an append-only
        // transcript as a line the character has to react to, and cannot be taken back.
        var (view, conversations) = Build();

        await ComposeAsync(view, "/sak what has she not said");
        var action = await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None);
        await RunAsync(action);

        action.ShouldBeOfType<ViewAction.StatusAction>().Text.ShouldContain("//sak");

        await conversations.DidNotReceive().SendAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_doubled_slash_sends_the_line_with_one_slash_left()
    {
        var (view, conversations) = Build();

        await ComposeAsync(view, "//ask is what I would type on the other site");
        await RunAsync(await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None));

        await conversations.Received(1).SendAsync(
            "c",
            "/ask is what I would type on the other site",
            null,
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_command_missing_its_argument_sends_nothing_and_says_how_to_type_it()
    {
        var (view, conversations) = Build();

        await ComposeAsync(view, "/ask");
        var action = await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.StatusAction>().Text.ShouldContain("/ask <question>");

        await conversations.DidNotReceive().ContinueAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Prose_is_still_prose_when_it_happens_to_contain_a_slash()
    {
        var (view, conversations) = Build();

        await ComposeAsync(view, "She looks up and/or laughs.");
        await RunAsync(await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None));

        await conversations.Received(1).SendAsync(
            "c",
            "She looks up and/or laughs.",
            null,
            Arg.Any<IProgress<string>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Typing_a_slash_offers_the_commands()
    {
        var (view, _) = Build();

        await ComposeAsync(view, "/as");

        Render(view.Render(Context())).ShouldContain("/ask");
    }

    [Fact]
    public async Task A_slash_part_way_through_a_line_offers_nothing()
    {
        // A rail that opened on every "and/or" would be in the way constantly, and a stray Tab
        // would rewrite the reader's own words.
        var (view, _) = Build();

        await ComposeAsync(view, "and/as");

        Render(view.Render(Context())).ShouldNotContain("/ask");
    }

    [Fact]
    public async Task The_commands_a_conversation_cannot_run_say_so_rather_than_failing()
    {
        // Built with no local store, which is what a future backend would look like.
        var (view, _) = Build();

        await ComposeAsync(view, "/facts");
        var action = await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.StatusAction>().Text.ShouldContain("local store");
    }

    [Fact]
    public async Task Help_lists_every_command_and_needs_no_store()
    {
        var (view, _) = Build();

        await ComposeAsync(view, "/help");
        var action = await view.HandleKeyAsync(TypedEnter(), Context(), CancellationToken.None);

        var pushed = action.ShouldBeOfType<ViewAction.PushAction>().View;
        var rendered = Render(pushed.Render(new RenderContext(100, 60, Theme.For(ThemeName.Dark), new AirpOptions())));

        foreach (var command in Airp.Application.Text.SlashCommands.All)
        {
            rendered.ShouldContain($"/{command.Name}");
        }
    }
}
