using Microsoft.Extensions.DependencyInjection;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

/// <summary>
/// That a conversation opened the way the terminal opens one can actually run the commands.
/// </summary>
/// <remarks>
/// The store-backed commands take the local provider through an optional constructor
/// parameter, and an optional parameter the container declines to fill is not an error — it is
/// a default. So the whole feature can fail closed and silently, with every command politely
/// reporting that this conversation is not on the local store. Asserting on the container
/// rather than on a hand-built view is the only way that shows up.
/// </remarks>
public class CommandWiringTests
{
    [Fact]
    public async Task A_row_opened_through_the_container_can_reach_the_store()
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddOptions<AirpOptions>();
        services.AddSingleton(Substitute.For<IConversationService>());
        services.AddSingleton(Substitute.For<IClipboardService>());
        services.AddSingleton(Substitute.For<IExportService>());
        services.AddSingleton(Substitute.For<ILanguageModelClient>());
        services.AddSingleton<Airp.Infrastructure.TextLibrary>();
        services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<
            Airp.Infrastructure.Storage.Local.AirpDbContext>>(new SharedContextFactory());
        services.AddSingleton<LocalConversationProvider>();

        var provider = services.BuildServiceProvider();

        var conversations = provider.GetRequiredService<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => []);

        var view = RowView.For(new Chat { Id = "c", Name = "Student", Speaker = "Blake" }, provider);

        var context = new RenderContext(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

        await view.OnActivatedAsync(CancellationToken.None);
        await view.HandleKeyAsync(Nav('i'), context, CancellationToken.None);

        foreach (var c in "/facts")
        {
            await view.HandleKeyAsync(Typed(c) with { Pasted = true }, context, CancellationToken.None);
        }

        var action = await view.HandleKeyAsync(TypedEnter(), context, CancellationToken.None);

        // Positively a run, not a refusal. Without the provider this same key press returns a
        // status saying the conversation is not on the local store, so asserting the shape is
        // what separates "wired up" from "politely broken".
        action.ShouldBeOfType<ViewAction.RunAction>();
    }

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text);

    private static KeyStroke Nav(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke TypedEnter()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);
}
