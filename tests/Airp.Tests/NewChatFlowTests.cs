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

    private NewChatView View(
        LocalConversationProvider provider,
        Action<AirpOptions>? configure = null)
    {
        // Enough container to construct the ConversationView the flow pushes on success. The
        // substitutes are never spoken to here — what these tests assert is the store.
        var services = new ServiceCollection()
            .AddSingleton(provider)
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IConversationService>())
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IClipboardService>())
            .AddSingleton(Substitute.For<Airp.Application.Abstractions.IExportService>())
            .BuildServiceProvider();

        return new NewChatView(
            services,
            provider,
            TestOptions.Default(configure),
            new Airp.Infrastructure.TextLibrary(_root));
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

    /// <summary>The one rendered row containing a marker, so a layout can be told from a list.</summary>
    private static string Row(NewChatView view, string marker)
        => Lines(view).FirstOrDefault(line => line.Contains(marker, StringComparison.Ordinal))
           ?? string.Empty;

    /// <summary>Renders the view to plain text, escapes stripped.</summary>
    private static string Screen(NewChatView view)
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
        console.Profile.Height = 30;
        console.Write(view.Render(Context()));

        var text = System.Text.RegularExpressions.Regex.Replace(
            writer.ToString(),
            new string((char)27, 1) + @"\[[0-9;]*[A-Za-z]",
            string.Empty);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The rendered screen as its own rows, which is what a side-by-side test needs.</summary>
    private static IReadOnlyList<string> Lines(NewChatView view)
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
        console.Profile.Height = 30;
        console.Write(view.Render(Context()));

        var text = System.Text.RegularExpressions.Regex.Replace(
            writer.ToString(),
            new string((char)27, 1) + @"\[[0-9;]*[A-Za-z]",
            string.Empty);

        return text.Split('\n');
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

        Screen(view).ShouldNotContain("Cornish");
    }

    [Fact]
    public async Task The_world_scrolls_rather_than_stopping_at_the_panel()
    {
        // A real card's world runs to forty lines and the paragraph that separates it from
        // the card next to it is rarely the first one. Showing the top and nothing else is
        // the same as showing the name.
        File.WriteAllText(
            Path.Combine(_root, "characters", "long.txt"),
            "=== THE WORLD ===\n\n"
            + string.Join('\n', Enumerable.Range(1, 60).Select(n => $"paragraph-{n:00}")));

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        var top = Screen(view);
        top.ShouldContain("paragraph-01");
        top.ShouldNotContain("paragraph-40");

        await view.HandleKeyAsync(Pressed(ConsoleKey.PageDown), Context(), CancellationToken.None);

        var next = Screen(view);
        next.ShouldNotContain("paragraph-01");
        next.ShouldContain("paragraph-20");
        next.ShouldContain("of 60");
    }

    [Fact]
    public async Task Scrolling_stops_at_the_end_and_comes_back()
    {
        File.WriteAllText(
            Path.Combine(_root, "characters", "long.txt"),
            "=== THE WORLD ===\n\n"
            + string.Join('\n', Enumerable.Range(1, 60).Select(n => $"paragraph-{n:00}")));

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        for (var page = 0; page < 20; page++)
        {
            await view.HandleKeyAsync(Pressed(ConsoleKey.PageDown), Context(), CancellationToken.None);
        }

        // Twenty pages of a four-page world: the last line is still on screen rather than
        // scrolled past into an empty panel.
        Screen(view).ShouldContain("paragraph-60");

        for (var page = 0; page < 20; page++)
        {
            await view.HandleKeyAsync(Pressed(ConsoleKey.PageUp), Context(), CancellationToken.None);
        }

        Screen(view).ShouldContain("paragraph-01");
    }

    [Fact]
    public async Task Writing_the_opening_takes_the_panel_back()
    {
        // Two things want the same room. The world is what a card is being chosen by; the
        // opening is what is being written. Whichever is being looked at gets the panel.
        File.WriteAllText(
            Path.Combine(_root, "characters", "lighthouse.txt"),
            "=== THE WORLD ===\n\nA lighthouse on the Cornish coast in 1963.");
        Directory.CreateDirectory(Path.Combine(_root, "openings"));
        File.WriteAllText(
            Path.Combine(_root, "openings", "lighthouse.txt"),
            "*The lamp turns, and the rain comes in sideways off the Atlantic.*");

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        Screen(view).ShouldContain("Cornish");

        // Tab past the persona and into the opening.
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);

        var writing = Screen(view);
        writing.ShouldContain("rain comes in sideways");
        writing.ShouldNotContain("Cornish");
    }

    /// <summary>Writes a world of the given number of lines under a character name.</summary>
    private void Card(string name, int lines)
        => File.WriteAllText(
            Path.Combine(_root, "characters", name + ".txt"),
            "=== THE WORLD ===\n\n"
            + string.Join('\n', Enumerable.Range(1, lines).Select(n => $"world-{n:00}")));

    /// <summary>Writes a persona of the given number of lines.</summary>
    private void Persona(string name, int lines)
        => File.WriteAllText(
            Path.Combine(_root, "personas", name + ".txt"),
            string.Join('\n', Enumerable.Range(1, lines).Select(n => $"persona-{n:00}")));

    [Fact]
    public async Task The_world_and_the_persona_stand_side_by_side()
    {
        // A short world used to leave two thirds of the panel blank while the persona — a page
        // written months ago and sent whole on every turn — was offered as a file name and
        // nothing else. The two are read against each other: whether this is the right person
        // to walk into that place is a question about both at once.
        Card("lighthouse", lines: 3);
        Persona("keeper", lines: 4);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");
        await PickFirstCharacterAsync(view);

        var screen = Screen(view);
        screen.ShouldContain("world-03");
        screen.ShouldContain("persona-04");

        // Side by side, not stacked: the first line of each shares one row of the terminal,
        // with the rule between them.
        screen.ShouldContain("world-01");

        var row = Row(view, "world-01");
        row.ShouldContain("│");
        row.ShouldContain("persona-01");
    }

    [Fact]
    public async Task The_default_persona_is_on_screen_before_anything_is_picked()
    {
        // The null slot is not "no persona": it is Airp:DefaultPersona, which is really sent.
        Persona("keeper", lines: 4);

        Screen(View(Provider(), o => o.DefaultPersona = "keeper")).ShouldContain("persona-04");
    }

    [Fact]
    public async Task Picking_a_persona_shows_that_one_instead()
    {
        Persona("keeper", lines: 2);
        Persona("visitor", lines: 2);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");

        // Name, Speaker, Character, Persona — then step onto the first name on the shelf.
        foreach (var _ in Enumerable.Range(0, 3))
        {
            await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        }

        await view.HandleKeyAsync(Pressed(ConsoleKey.RightArrow), Context(), CancellationToken.None);

        Screen(view).ShouldContain("persona-02");
    }

    [Fact]
    public async Task A_world_far_longer_than_the_panel_does_not_push_the_persona_off()
    {
        // Each column has the full height of the panel, so a long world scrolls in its own
        // half rather than costing the persona its place.
        Card("long", lines: 200);
        Persona("keeper", lines: 4);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");
        await PickFirstCharacterAsync(view);

        var screen = Screen(view);
        screen.ShouldContain("persona-04");
        screen.ShouldContain("of 200");
    }

    [Fact]
    public async Task Only_one_column_of_content_takes_the_whole_width()
    {
        // A rule down the middle of nothing is worse than no rule: with no persona to face it,
        // the world gets the width.
        Card("lighthouse", lines: 3);

        var view = View(Provider());
        await PickFirstCharacterAsync(view);

        var screen = Screen(view);
        screen.ShouldContain("world-03");

        // No rule, because there is no second column for it to divide from the first.
        Row(view, "world-03").ShouldNotContain("│");
    }

    [Fact]
    public async Task The_page_keys_scroll_whichever_half_has_the_focus()
    {
        Card("long", lines: 200);
        Persona("keeper", lines: 200);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");
        await PickFirstCharacterAsync(view);

        // On the character, the world has the room and the page keys move it.
        await view.HandleKeyAsync(Pressed(ConsoleKey.PageDown), Context(), CancellationToken.None);
        Screen(view).ShouldNotContain("world-01 ");

        // On the persona, the persona has the room and the page keys move that instead.
        await view.HandleKeyAsync(Pressed(ConsoleKey.Tab), Context(), CancellationToken.None);
        Screen(view).ShouldContain("persona-01");

        await view.HandleKeyAsync(Pressed(ConsoleKey.PageDown), Context(), CancellationToken.None);
        Screen(view).ShouldNotContain("persona-01 ");
    }

    [Fact]
    public async Task The_headings_sit_over_a_rule_rather_than_on_the_text()
    {
        // A caption straight on the paragraph it labels reads as the paragraph's first line.
        Card("lighthouse", lines: 3);
        Persona("keeper", lines: 2);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");
        await PickFirstCharacterAsync(view);

        var lines = Lines(view);
        var heading = lines
            .Select(static (line, index) => (line, index))
            .First(static x => x.line.Contains("Character preview", StringComparison.Ordinal))
            .index;

        // Heading, then a rule that the column divider crosses rather than interrupts, then
        // the first line of both columns.
        lines[heading + 1].ShouldContain("┼");
        lines[heading + 2].ShouldContain("world-01");
        lines[heading + 2].ShouldContain("persona-01");
    }

    [Fact]
    public async Task The_caption_names_the_field_not_the_card_it_repeats()
    {
        // The character's name is spelled out in the form three rows up; a caption repeating it
        // said nothing new and left the two halves of the panel unlabelled.
        Card("lighthouse", lines: 3);
        Persona("keeper", lines: 2);

        var view = View(Provider(), o => o.DefaultPersona = "keeper");
        await PickFirstCharacterAsync(view);

        var screen = Screen(view);
        screen.ShouldContain("Character preview");
        screen.ShouldContain("Persona");
        screen.ShouldNotContain("the world it belongs to");
    }
}
