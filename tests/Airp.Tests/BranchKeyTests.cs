using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using NSubstitute;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Branching as it is actually reached: a key, a name, and Enter.
/// </summary>
/// <remarks>
/// Strokes resolve through the real <see cref="KeyMap"/>, because the map is global and a
/// hand-built keystroke once shipped a binding nobody could press. <c>B</c> has to arrive here
/// as a plain character — if the map ever claims it for something else, this fails rather than
/// the key silently doing nothing.
/// </remarks>
public sealed class BranchKeyTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Nav(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Navigation);

    private static KeyStroke Typed(char c)
        => KeyMap.Resolve(
            new ConsoleKeyInfo(c, default, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

    private static KeyStroke Enter()
        => KeyMap.Resolve(
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            KeyboardMode.Standard,
            KeyContext.Text);

    private static async Task RunAsync(ViewAction action)
    {
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

    /// <summary>Seeds a real conversation and a view looking at it.</summary>
    private async Task<(ConversationView View, string Id)> BuildAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var stored = new List<ChatMessage>();

        await using (var store = _factory.CreateDbContext())
        {
            store.Conversations.Add(new ConversationRecord
            {
                Id = id,
                Name = "Vardhal",
                Speaker = "Elena",
                CharacterName = "elena",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });

            for (var i = 1; i <= 4; i++)
            {
                var messageId = Guid.NewGuid().ToString("N");

                store.Messages.Add(new MessageRecord
                {
                    Id = messageId,
                    ConversationId = id,
                    Sequence = i,
                    Role = i % 2 == 1 ? ChatRole.User : ChatRole.Assistant,
                    Text = $"Turn {i}.",
                    SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
                });

                stored.Add(new ChatMessage
                {
                    Id = messageId,
                    ConversationId = id,
                    Role = i % 2 == 1 ? ChatRole.User : ChatRole.Assistant,
                    Text = $"Turn {i}.",
                });
            }

            await store.SaveChangesAsync();
        }

        var conversations = Substitute.For<IConversationService>();

        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ => stored);

        var view = new ConversationView(
            new Chat { Id = id, Name = "Vardhal", Speaker = "Elena" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>(),
            library: null,
            provider: new LocalConversationProvider(
                _factory,
                _model,
                TestOptions.Default(),
                NullLogger<LocalConversationProvider>.Instance));

        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));
        return (view, id);
    }

    /// <summary>Presses B, replaces the offered name, and confirms.</summary>
    private static async Task BranchAsync(ConversationView view, string name)
    {
        await view.HandleKeyAsync(Nav('b'), Context(), CancellationToken.None);

        // The field arrives pre-filled with a suggestion, so it has to be emptied first —
        // which is also the check that backspace reaches the field at all.
        for (var i = 0; i < 80; i++)
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(
                    new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
                    KeyboardMode.Standard,
                    KeyContext.Text),
                Context(),
                CancellationToken.None);
        }

        foreach (var c in name)
        {
            await view.HandleKeyAsync(Typed(c), Context(), CancellationToken.None);
        }

        await RunAsync(await view.HandleKeyAsync(Enter(), Context(), CancellationToken.None));
    }

    [Fact]
    public async Task B_on_a_turn_copies_the_story_up_to_it()
    {
        var (view, id) = await BuildAsync();

        // The cursor lands on the newest turn when a conversation opens. Two steps back puts it
        // on turn 2 of 4, so the copy has to stop there — an exact number, because "fewer than
        // four" would also pass if the cursor were being ignored and the wrong turn chosen.
        for (var i = 0; i < 2; i++)
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(
                    new ConsoleKeyInfo(default, ConsoleKey.UpArrow, false, false, false),
                    KeyboardMode.Standard,
                    KeyContext.Navigation),
                Context(),
                CancellationToken.None);
        }

        await BranchAsync(view, "The other way");

        await using var store = _factory.CreateDbContext();

        var branch = await store.Conversations.SingleAsync(c => c.Id != id);
        branch.Name.ShouldBe("The other way");
        branch.CharacterName.ShouldBe("elena");

        var copied = await store.Messages
            .Where(m => m.ConversationId == branch.Id)
            .OrderBy(m => m.Sequence)
            .Select(m => m.Text)
            .ToListAsync();

        copied.ShouldBe(["Turn 1.", "Turn 2."]);
    }

    [Fact]
    public async Task Escape_leaves_the_name_unasked_and_nothing_copied()
    {
        var (view, id) = await BuildAsync();

        await view.HandleKeyAsync(Nav('b'), Context(), CancellationToken.None);

        await RunAsync(await view.HandleKeyAsync(
            KeyMap.Resolve(
                new ConsoleKeyInfo('', ConsoleKey.Escape, false, false, false),
                KeyboardMode.Standard,
                KeyContext.Text),
            Context(),
            CancellationToken.None));

        await using var store = _factory.CreateDbContext();
        (await store.Conversations.CountAsync()).ShouldBe(1);

        // And the view is back to reading the transcript rather than eating letters.
        view.KeyContext.ShouldBe(KeyContext.Navigation);
    }

    [Fact]
    public async Task A_blank_name_copies_nothing()
    {
        var (view, _) = await BuildAsync();

        await BranchAsync(view, string.Empty);

        await using var store = _factory.CreateDbContext();
        (await store.Conversations.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task The_offered_name_counts_up_rather_than_repeating_itself()
    {
        // Two branches from one story would otherwise both be offered "Vardhal (2)", and the
        // list would carry two rows nothing tells apart.
        var (view, _) = await BuildAsync();

        await view.HandleKeyAsync(Nav('b'), Context(), CancellationToken.None);

        var offered = Render(view.Render(Context()));
        offered.ShouldContain("Vardhal (2)");
    }

    private static string Render(Spectre.Console.Rendering.IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Ansi = Spectre.Console.AnsiSupport.No,
            ColorSystem = Spectre.Console.ColorSystemSupport.NoColors,
            Out = new Spectre.Console.AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 24;
        console.Write(renderable);
        return writer.ToString();
    }
}
