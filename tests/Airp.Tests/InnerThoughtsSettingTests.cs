using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Spectre.Console;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Inner thoughts as a per-conversation setting, reachable from the S view.
/// </summary>
public sealed class InnerThoughtsSettingTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private static KeyStroke Nav(ConsoleKey key)
        => KeyMap.Resolve(new ConsoleKeyInfo('\0', key, false, false, false), KeyboardMode.Standard, KeyContext.Navigation);

    private static RenderContext Context()
        => new(100, 30, Theme.For(ThemeName.Dark), new AirpOptions());

    [Fact]
    public async Task The_toggle_rides_the_settings_contract_end_to_end()
    {
        var provider = Provider();
        var chat = await provider.CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });

        (await provider.GetSettingsAsync(chat.Id)).InnerThoughts.ShouldBe(false);

        await provider.UpdateSettingsAsync(chat.Id, new ChatSettings { InnerThoughts = true });

        (await provider.GetSettingsAsync(chat.Id)).InnerThoughts.ShouldBe(true);
    }

    [Fact]
    public async Task A_partial_update_leaves_the_toggle_alone()
    {
        // Null means "not stated here" — the same reading the dials rely on. A lust change
        // must not quietly switch thoughts off.
        var provider = Provider();
        var chat = await provider.CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });
        await provider.UpdateSettingsAsync(chat.Id, new ChatSettings { InnerThoughts = true });

        await provider.UpdateSettingsAsync(chat.Id, new ChatSettings { Lust = 2 });

        (await provider.GetSettingsAsync(chat.Id)).InnerThoughts.ShouldBe(true);
    }

    [Fact]
    public async Task The_settings_view_stages_the_toggle_and_apply_persists_it()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetSettingsAsync("c1", Arg.Any<CancellationToken>())
            .Returns(new ChatSettings { Lust = 1, ResponseLength = 1, Creativity = 1, InnerThoughts = false });

        ChatSettings? sent = null;
        conversations.UpdateSettingsAsync("c1", Arg.Any<ChatSettings>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<ChatSettings>(1);
                return new ChatSettings { Lust = 1, ResponseLength = 1, Creativity = 1, InnerThoughts = true };
            });

        var view = new ChatSettingsView(conversations, "c1", "Vardhal");

        // Load, walk down past the three dials to the toggle, flip it, apply.
        var load = (await view.OnActivatedAsync(CancellationToken.None)).ShouldBeOfType<ViewAction.RunAction>();
        await load.Work(CancellationToken.None);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await view.HandleKeyAsync(Nav(ConsoleKey.DownArrow), Context(), CancellationToken.None);
        }

        await view.HandleKeyAsync(Nav(ConsoleKey.RightArrow), Context(), CancellationToken.None);

        var apply = (await view.HandleKeyAsync(Nav(ConsoleKey.Enter), Context(), CancellationToken.None))
            .ShouldBeOfType<ViewAction.RunAction>();
        await apply.Work(CancellationToken.None);

        sent.ShouldNotBeNull();
        sent.InnerThoughts.ShouldBe(true);
        sent.Lust.ShouldBeNull();
    }
}
