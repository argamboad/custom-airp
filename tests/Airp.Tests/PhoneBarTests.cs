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
