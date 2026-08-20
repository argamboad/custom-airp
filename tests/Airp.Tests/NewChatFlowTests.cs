using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Spectre.Console;
using Airp.Application.Options;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Starting a conversation from inside the terminal.
/// </summary>
public sealed class NewChatFlowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "airp-tests", Guid.NewGuid().ToString("n"));

    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public NewChatFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));
        Directory.CreateDirectory(Path.Combine(_root, "personas"));
    }

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private NewChatView View(LocalConversationProvider provider)
    {
        // Enough container to construct the ConversationView the flow pushes on success. The
        // substitutes are never spoken to here — what these tests assert is the store.
        var services = new ServiceCollection()
            .AddSingleton(provider)
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IConversationService>())
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IClipboardService>())
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IExportService>())
            .BuildServiceProvider();

        return new NewChatView(services, provider, TestOptions.Default(), new Airp.Infrastructure.TextLibrary(_root));
    }

    private static RenderContext Context()
        => new(100, 30, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text);

    private static KeyStroke Pressed(ConsoleKey key, bool control = false)
        => KeyMap.Resolve(new ConsoleKeyInfo('\0', key, false, false, control), KeyboardMode.Standard, KeyContext.Text);

    private static async Task TypeAsync(NewChatView view, string text)
    {
        foreach (var c in text)
        {
            await view.HandleKeyAsync(Typed(c), Context(), CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_whole_flow_creates_a_conversation_and_opens_it()
    {
        var provider = Provider();
        var view = View(provider);

        await TypeAsync(view, "Vardhal");
        await view.HandleKeyAsync(Pressed(ConsoleKey.Enter), Context(), CancellationToken.None);
        await TypeAsync(view, "Elena");
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await TypeAsync(view, "I am cleaning a knife by the fire.");

        var action = await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        // Created, and the reader lands inside it rather than back at the list.
        var sequence = action.ShouldBeOfType<ViewAction.SequenceAction>();
        sequence.Actions.OfType<ViewAction.PushAction>().ShouldHaveSingleItem();

        var chats = await provider.ListAsync();
        var chat = chats.ShouldHaveSingleItem();
        chat.Name.ShouldBe("Vardhal");
        chat.Speaker.ShouldBe("Elena");

        var messages = await provider.GetMessagesAsync(chat.Id);
        messages.ShouldHaveSingleItem().Text.ShouldBe("I am cleaning a knife by the fire.");
    }

    [Fact]
    public async Task Ctrl_Enter_creates_too_because_a_console_eats_Ctrl_S()
    {
        var provider = Provider();
        var view = View(provider);

        await TypeAsync(view, "Vardhal");

        // Ctrl+S is XOFF in a Windows console and never arrives; this chord does.
        var action = await view.HandleKeyAsync(
            Pressed(ConsoleKey.Enter, control: true), Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.SequenceAction>();
        (await provider.ListAsync()).ShouldHaveSingleItem().Name.ShouldBe("Vardhal");
    }

    [Fact]
    public async Task A_nameless_chat_falls_back_to_the_speaker()
    {
        var provider = Provider();
        var view = View(provider);

        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await TypeAsync(view, "Elena");

        await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        (await provider.ListAsync()).ShouldHaveSingleItem().Name.ShouldBe("Elena");
    }

    [Fact]
    public async Task Nothing_at_all_is_refused_with_a_warning_not_a_crash()
    {
        var provider = Provider();
        var view = View(provider);

        var action = await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.StatusAction>().Kind.ShouldBe(StatusKind.Warning);
        (await provider.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Escape_with_nothing_typed_just_closes()
    {
        var action = await View(Provider())
            .HandleKeyAsync(Pressed(ConsoleKey.Escape), Context(), CancellationToken.None);

        action.ShouldBe(ViewAction.Pop);
    }

    [Fact]
    public async Task Escape_with_something_typed_asks_first()
    {
        var view = View(Provider());
        await TypeAsync(view, "half a name");

        var action = await view.HandleKeyAsync(Pressed(ConsoleKey.Escape), Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.PushAction>().View.ShouldBeOfType<ConfirmView>();
    }

    [Fact]
    public async Task Enter_in_the_opening_is_a_line_break_not_a_submit()
    {
        var provider = Provider();
        var view = View(provider);

        await TypeAsync(view, "Two paragraphs");
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        }

        await TypeAsync(view, "First.");
        await view.HandleKeyAsync(Pressed(ConsoleKey.Enter), Context(), CancellationToken.None);
        await TypeAsync(view, "Second.");
        await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        var chat = (await provider.ListAsync()).ShouldHaveSingleItem();
        var opening = (await provider.GetMessagesAsync(chat.Id)).ShouldHaveSingleItem();

        opening.Text.ShouldBe("First.\nSecond.");
    }

    [Fact]
    public async Task Picking_a_character_offers_their_opening_prefilled()
    {
        Directory.CreateDirectory(Path.Combine(_root, "openings"));
        File.WriteAllText(Path.Combine(_root, "characters", "elena.txt"), "You are Elena.");
        File.WriteAllText(Path.Combine(_root, "openings", "elena.txt"), "I am cleaning a knife by the fire.");

        var provider = Provider();
        var view = View(provider);

        // to the character field, then pick the only entry
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.RightArrow), Context(), CancellationToken.None);
        await TypeAsync(view, "Named");
        await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        var chat = (await provider.ListAsync()).ShouldHaveSingleItem();
        (await provider.GetMessagesAsync(chat.Id))
            .ShouldHaveSingleItem().Text.ShouldBe("I am cleaning a knife by the fire.");
    }

    [Fact]
    public async Task A_typed_opening_is_never_clobbered_by_the_picker()
    {
        Directory.CreateDirectory(Path.Combine(_root, "openings"));
        File.WriteAllText(Path.Combine(_root, "characters", "elena.txt"), "You are Elena.");
        File.WriteAllText(Path.Combine(_root, "openings", "elena.txt"), "Shelf opening.");

        var provider = Provider();
        var view = View(provider);

        await TypeAsync(view, "Mine");
        foreach (var _ in Enumerable.Range(0, 4))
        {
            await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        }

        await TypeAsync(view, "My own words.");

        // back to the character field and cycle — the typed opening must survive
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.RightArrow), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.S, control: true), Context(), CancellationToken.None);

        var chat = (await provider.ListAsync()).ShouldHaveSingleItem();
        (await provider.GetMessagesAsync(chat.Id))
            .ShouldHaveSingleItem().Text.ShouldBe("My own words.");
    }

    /// <summary>Renders the view to plain text, escapes stripped.</summary>
    private static string Screen(NewChatView view)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 30;
        console.Write(view.Render(Context()));

        var text = System.Text.RegularExpressions.Regex.Replace(
            writer.ToString(),
            new string((char)27, 1) + @"\[[0-9;]*[A-Za-z]",
            string.Empty);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Moves focus to the character picker and steps onto the first real card.</summary>
    private static async Task PickFirstCharacterAsync(NewChatView view)
    {
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.RightArrow), Context(), CancellationToken.None);
    }

    [Fact]
    public async Task Picking_a_character_shows_the_world_it_belongs_to()
    {
        // Three of the cards in a real library are resort scenarios whose names differ by one
        // word. The name alone does not say which one is about to be played, and the card is
        // the thing being chosen.
        File.WriteAllText(
            Path.Combine(_root, "characters", "lighthouse.txt"),
            """
            You are the narrator and every character of the world described below.

            === THE WORLD ===

            A lighthouse on the Cornish coast in 1963, three miles of cliff path from the
            nearest village and a fortnight from the next supply run.

            === THE CAST ===

            Morwenna — 34, the keeper. This section must not appear in the preview.
            """);

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        var screen = Screen(view);

        screen.ShouldContain("lighthouse");
        screen.ShouldContain("Cornish coast in 1963");
        screen.ShouldNotContain(
            "must not appear",
            Case.Sensitive,
            "the preview stops at the next section header rather than running into the cast");
    }

    [Fact]
    public async Task Picking_a_character_says_what_it_will_cost_every_turn()
    {
        // The number that decides whether a story compresses at turn twenty or turn two
        // hundred, and it used to be invisible until the reader was already playing.
        File.WriteAllText(
            Path.Combine(_root, "characters", "enormous.txt"),
            "=== THE WORLD ===\n\nA place. " + string.Join(' ', Enumerable.Repeat("detail", 4000)));

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        Screen(view).ShouldContain("tokens every turn");
    }

    [Fact]
    public async Task Stepping_back_to_no_character_leaves_nothing_behind()
    {
        // The null slot is a legal choice, and a stale world under it would describe a card
        // that is not selected.
        File.WriteAllText(
            Path.Combine(_root, "characters", "lighthouse.txt"),
            "=== THE WORLD ===\n\nA lighthouse on the Cornish coast in 1963.");

        var view = View(Provider());
        await PickFirstCharacterAsync(view);
        Screen(view).ShouldContain("Cornish");

        await view.HandleKeyAsync(Pressed(ConsoleKey.LeftArrow), Context(), CancellationToken.None);

        var screen = Screen(view);
        screen.ShouldNotContain("Cornish");
        screen.ShouldNotContain("tokens every turn");
    }
}
