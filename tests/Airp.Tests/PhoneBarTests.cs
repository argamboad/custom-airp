using Microsoft.Extensions.DependencyInjection;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using NSubstitute;
using Shouldly;
using Spectre.Console;

namespace Airp.Tests;

/// <summary>
/// The phone's bottom bar: every button is a key the keyboard already has, and a tap lands on
/// the button it was aimed at.
/// </summary>
/// <remarks>
/// Buttons are dispatched through the real <see cref="KeyMap"/>, so these resolve them the same
/// way — in both dialects, because a button that means one thing in the standard keymap and
/// another under vim is a button that lies to half its readers. A capital N and a bare G were
/// both that, before they were caught here.
/// </remarks>
public class PhoneBarTests
{
    private const int Phone = 38;

    private static KeyStroke Resolve(Button button, KeyboardMode mode, KeyContext context = KeyContext.Navigation)
        => KeyMap.Resolve(button.Key, mode, context);

    private static ConversationView Conversation()
        => new(
            new Chat { Id = "c", Name = "North Dock", Speaker = "Blake" },
            Substitute.For<IConversationService>(),
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

    private static Button Named(IReadOnlyList<Button> buttons, string label)
        => buttons.Single(b => b.Label == label);

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void The_conversation_buttons_mean_what_they_say_in_either_dialect(KeyboardMode mode)
    {
        var buttons = Conversation().Buttons;

        Resolve(Named(buttons, "‹"), mode).Command.ShouldBe(AppCommand.Back);
        Resolve(Named(buttons, "Reroll"), mode).Command.ShouldBe(AppCommand.Generate);

        var write = Resolve(Named(buttons, "Write"), mode);
        write.Command.ShouldBe(AppCommand.Character);
        write.Character.ShouldBe('i');

        var carryOn = Resolve(Named(buttons, "Carry on"), mode);
        carryOn.Command.ShouldBe(AppCommand.Character);
        carryOn.Character.ShouldBe('>');
    }

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void The_chat_list_buttons_mean_what_they_say_in_either_dialect(KeyboardMode mode)
    {
        using var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IChatService>())
            .AddSingleton(Substitute.For<IConversationService>())
            .BuildServiceProvider();

        var buttons = ActivatorUtilities
            .CreateInstance<ChatListView>(services).Buttons;

        // The list answers SearchNext with a new chat, since the map resolves n to it.
        Resolve(Named(buttons, "New"), mode).Command.ShouldBe(AppCommand.SearchNext);
        Resolve(Named(buttons, "Open"), mode).Command.ShouldBe(AppCommand.Accept);

        var library = Resolve(Named(buttons, "Library"), mode);
        library.Command.ShouldBe(AppCommand.Character);
        library.Character.ShouldBe('m');

        buttons.ShouldNotContain(b => b.Label == "‹");
    }

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void The_bar_ends_with_the_command_list(KeyboardMode mode)
    {
        var (_, hits) = Shell.PhoneBar(Conversation().Buttons, Phone, Theme.For(ThemeName.Dark));

        KeyMap.Resolve(hits[^1].Key, mode, KeyContext.Navigation).Command.ShouldBe(AppCommand.CommandPalette);
    }

    [Fact]
    public void The_conversation_bar_fits_a_phone_whole()
    {
        var (markup, hits) = Shell.PhoneBar(Conversation().Buttons, Phone, Theme.For(ThemeName.Dark));

        // ‹, Write, Reroll, Carry on, and ⋯ — nothing dropped at 38 columns.
        hits.Count.ShouldBe(5);
        hits[^1].To.ShouldBeLessThanOrEqualTo(Phone);
        Markup.Remove(markup).ShouldBe(" ‹   Write   Reroll   Carry on   ⋯ ");
    }

    [Fact]
    public void What_does_not_fit_is_dropped_from_the_right_and_the_command_list_stays()
    {
        Button[] many =
        [
            Button.Press("First thing", 'a'),
            Button.Press("Second thing", 'b'),
            Button.Press("Third thing", 'c'),
            Button.Press("Fourth thing", 'd'),
        ];

        var (markup, hits) = Shell.PhoneBar(many, Phone, Theme.For(ThemeName.Dark));
        var text = Markup.Remove(markup);

        Draw.Width(text).ShouldBeLessThanOrEqualTo(Phone);
        text.ShouldStartWith(" First thing ");
        text.ShouldNotContain("Fourth");
        text.ShouldEndWith(" ⋯ ");
        hits[^1].Key.Key.ShouldBe(ConsoleKey.P);
    }

    [Fact]
    public void A_tap_lands_on_the_button_under_it_and_nowhere_between()
    {
        var buttons = Conversation().Buttons;
        var (_, hits) = Shell.PhoneBar(buttons, Phone, Theme.For(ThemeName.Dark));

        // Both edges of Reroll, which is the third cell: " ‹ " + " " + " Write " + " " is 12
        // columns, so it spans 13 to 20.
        hits[2].From.ShouldBe(13);
        hits[2].To.ShouldBe(20);
        Shell.Hit(hits, 13).ShouldBe(Named(buttons, "Reroll").Key);
        Shell.Hit(hits, 20).ShouldBe(Named(buttons, "Reroll").Key);

        // The gap between Write and Reroll belongs to neither.
        Shell.Hit(hits, 12).ShouldBeNull();
        Shell.Hit(hits, Phone + 1).ShouldBeNull();
    }

    private static RenderContext At(int width) => new(width, 30, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text);

    private static KeyStroke Enter(bool alt = false)
        => KeyMap.Resolve(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, alt, false), KeyboardMode.Standard, KeyContext.Text);

    /// <summary>A conversation with the composer open and "a" typed, at the given width.</summary>
    private static async Task<ConversationView> WritingAsync(int width)
    {
        var view = Conversation();
        await view.HandleKeyAsync(Resolve(Button.Press("Write", 'i'), KeyboardMode.Standard), At(width), CancellationToken.None);
        await view.HandleKeyAsync(Typed('a'), At(width), CancellationToken.None);
        return view;
    }

    [Fact]
    public async Task On_a_phone_Enter_is_a_new_line_and_sends_nothing()
    {
        // A touch keyboard's Enter is a new line in every other text box on the device, and a
        // send is permanent and billed.
        var view = await WritingAsync(Phone);

        (await view.HandleKeyAsync(Enter(), At(Phone), CancellationToken.None)).ShouldBe(ViewAction.None);
        await view.HandleKeyAsync(Typed('b'), At(Phone), CancellationToken.None);

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            // Spectre's CI enrichers turn ANSI back on under GITHUB_ACTIONS, and the caret's
            // row then comes back wrapped in escapes that no plain "b" can equal.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(new StringWriter()),
        });
        var writer = new StringWriter();
        console.Profile.Out = new AnsiConsoleOutput(writer);
        console.Profile.Width = Phone;
        console.Write(view.Render(At(Phone)));

        // The draft is two rows now: what was typed before Enter, and after it.
        var lines = writer.ToString().Split('\n').Select(static l => l.Trim()).ToList();
        lines.ShouldContain("a");
        lines.IndexOf("b").ShouldBe(lines.IndexOf("a") + 1);
    }

    [Fact]
    public async Task On_a_phone_the_Send_button_sends()
    {
        var view = await WritingAsync(Phone);
        view.Render(At(Phone));

        var send = Resolve(Named(view.Buttons, "Send"), KeyboardMode.Standard, KeyContext.Text);

        (await view.HandleKeyAsync(send, At(Phone), CancellationToken.None)).ShouldBeOfType<ViewAction.RunAction>();
    }

    [Fact]
    public async Task At_a_desk_Enter_still_sends_and_Alt_Enter_is_the_new_line()
    {
        (await (await WritingAsync(100)).HandleKeyAsync(Enter(), At(100), CancellationToken.None))
            .ShouldBeOfType<ViewAction.RunAction>();

        (await (await WritingAsync(100)).HandleKeyAsync(Enter(alt: true), At(100), CancellationToken.None))
            .ShouldBe(ViewAction.None);
    }

    // ⋯ lists the screen's own actions first. It used to list only refresh, the library, help
    // and quit, so on a phone a conversation's settings, branching, copying and exporting had
    // no way to be reached at all.

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void The_conversation_lists_the_rest_of_what_it_does_under_the_ellipsis(KeyboardMode mode)
    {
        var actions = Conversation().Actions;

        AppCommand Command(string label) => Resolve(Named(actions, label), mode).Command;

        Command("Reply settings").ShouldBe(AppCommand.Settings);
        Command("Search this chat").ShouldBe(AppCommand.Search);
        Command("Copy this message").ShouldBe(AppCommand.Copy);
        Command("Export the transcript").ShouldBe(AppCommand.Export);
        Command("Delete from here").ShouldBe(AppCommand.Delete);
        Command("First message").ShouldBe(AppCommand.Home);
        Command("Last message").ShouldBe(AppCommand.End);

        var branch = Resolve(Named(actions, "Branch from here"), mode);
        branch.Command.ShouldBe(AppCommand.Character);
        branch.Character.ShouldBe('b');

        actions.ShouldAllBe(static a => a.Description.Length > 0);
    }

    [Theory]
    [InlineData(KeyboardMode.Standard)]
    [InlineData(KeyboardMode.Vim)]
    public void The_chat_list_lists_the_rest_of_what_it_does_under_the_ellipsis(KeyboardMode mode)
    {
        using var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IChatService>())
            .AddSingleton(Substitute.For<IConversationService>())
            .BuildServiceProvider();

        var actions = ActivatorUtilities.CreateInstance<ChatListView>(services).Actions;

        Resolve(Named(actions, "Filter the list"), mode).Command.ShouldBe(AppCommand.Search);
        Resolve(Named(actions, "Search every chat"), mode).Command.ShouldBe(AppCommand.GlobalSearch);
        Resolve(Named(actions, "Rename the chat"), mode).Command.ShouldBe(AppCommand.Rename);
        Resolve(Named(actions, "Delete the chat"), mode).Command.ShouldBe(AppCommand.Delete);
    }

    [Fact]
    public async Task The_ellipsis_puts_the_screens_actions_first_and_choosing_one_presses_its_key()
    {
        var view = Conversation();
        PaletteCommand[] general = [new("Help", "Show every key binding", _ => Task.FromResult(ViewAction.None))];

        var list = Shell.PaletteFor(view, general);

        list.Select(static c => c.Name).ShouldBe([.. view.Actions.Select(static a => a.Label), "Help"]);

        // Run through the list itself, the way a tap on it or Enter does: it closes, then the
        // key is pressed on the conversation underneath.
        var palette = new CommandPaletteView(list);
        var action = await palette.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), KeyboardMode.Standard, KeyContext.Navigation),
            At(Phone),
            CancellationToken.None);

        var sequence = action.ShouldBeOfType<ViewAction.SequenceAction>();
        sequence.Actions[0].ShouldBe(ViewAction.Pop);
        sequence.Actions[1].ShouldBe(ViewAction.Press(Named(view.Actions, "Reply settings").Key));
    }

    [Fact]
    public async Task While_writing_there_is_nothing_else_to_list()
    {
        var view = await WritingAsync(Phone);

        view.Actions.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_writing_the_bar_sends_and_closes()
    {
        var view = Conversation();
        await view.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('i', default, false, false, false), KeyboardMode.Standard, KeyContext.Navigation),
            new RenderContext(Phone, 30, Theme.For(ThemeName.Dark), new AirpOptions()),
            CancellationToken.None);

        view.Buttons.Select(static b => b.Label).ShouldBe(["Send", "Close"]);
        Resolve(Named(view.Buttons, "Close"), KeyboardMode.Standard, KeyContext.Text).Command.ShouldBe(AppCommand.Back);
    }
}
