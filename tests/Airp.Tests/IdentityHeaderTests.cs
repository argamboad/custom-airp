using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

/// <summary>
/// The conversation header says which card is in play and who you are in it.
/// </summary>
/// <remarks>
/// Sessions run long and the choice was made weeks ago; the header is where a reader looks
/// without asking. The labels resolve exactly as a turn resolves them, so a name whose file
/// is gone says so — two live conversations once played for days on an empty character layer
/// that nothing on screen admitted to.
/// </remarks>
public sealed class IdentityHeaderTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "airp-identity-" + Guid.NewGuid().ToString("N"));

    public IdentityHeaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));
        Directory.CreateDirectory(Path.Combine(_root, "personas"));
        File.WriteAllText(Path.Combine(_root, "characters", "elena.txt"), "You are Elena.");
        File.WriteAllText(Path.Combine(_root, "personas", "allan.txt"), "Allan is a traveller.");
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
        NullLogger<LocalConversationProvider>.Instance,
        embeddings: null,
        library: new TextLibrary(_root));

    // Collapsed to single spaces: the header lives in the centred reading column, long lines
    // wrap, and a phrase split across the fold is still the phrase.
    private async Task<string> RenderedHeaderAsync(string? characterName, string? personaName)
        => await RenderedHeaderAsync(await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = characterName,
            PersonaName = personaName,
            Opening = "The lamp is lit.",
        }));

    private async Task<string> RenderedHeaderAsync(Chat chat)
        => System.Text.RegularExpressions.Regex.Replace(await RenderedRawAsync(chat), @"\s+", " ");

    private async Task<string> RenderedRawAsync(Chat chat)
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(chat.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(
            [
                new() { Id = "m1", ConversationId = chat.Id, Role = ChatRole.Assistant, Text = "The lamp is lit." },
            ]);

        var view = new ConversationView(
            chat,
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>(),
            library: new TextLibrary(_root),
            provider: Provider(),
            options: TestOptions.Default());

        var load = (await view.OnActivatedAsync(CancellationToken.None)).ShouldBeOfType<ViewAction.RunAction>();
        await load.Work(CancellationToken.None);

        return Render(view.Render(new RenderContext(120, 30, Theme.For(ThemeName.Dark), new AirpOptions())));
    }

    private static string Render(IRenderable renderable)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });

        console.Profile.Width = 120;
        console.Profile.Height = 30;

        var writer = new StringWriter();
        console.Profile.Out = new AnsiConsoleOutput(writer);
        console.Write(renderable);
        return writer.ToString();
    }

    [Fact]
    public async Task The_header_names_the_card_and_the_persona_in_play()
    {
        var header = await RenderedHeaderAsync("elena", "allan");

        header.ShouldContain("elena");
        header.ShouldContain("as allan");
    }

    [Fact]
    public async Task A_card_whose_file_is_gone_is_said_out_loud()
    {
        var header = await RenderedHeaderAsync("ghost", "allan");

        header.ShouldContain("ghost (missing)");
    }

    [Fact]
    public async Task No_card_at_all_is_said_too()
    {
        var header = await RenderedHeaderAsync(characterName: null, personaName: "allan");

        header.ShouldContain("no card");
    }

    [Fact]
    public async Task The_cost_shows_the_total_and_the_days_share()
    {
        var chat = await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
            PersonaName = "allan",
            Opening = "The lamp is lit.",
        });

        // A long-lived story: calls two months back, earlier this month, and today. All
        // three windows earn their place.
        await using (var store = _factory.CreateDbContext())
        {
            var monthStart = new DateTimeOffset(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));

            store.Spend.Add(new SpendRecord
            {
                Id = "s1", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = monthStart.AddDays(-30), Cost = 0.30m,
            });
            store.Spend.Add(new SpendRecord
            {
                Id = "s2", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = DateTimeOffset.Now, Cost = 0.05m,
            });
            await store.SaveChangesAsync();
        }

        var header = await RenderedHeaderAsync(chat);

        header.ShouldContain("$0.3500");
        header.ShouldContain("$0.0500 this month");

        // Today's figure equals the month's here, so it is suppressed rather than repeated.
        header.ShouldNotContain("today");
    }

    [Fact]
    public async Task A_window_equal_to_the_one_before_it_is_not_repeated()
    {
        var chat = await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
            PersonaName = "allan",
            Opening = "The lamp is lit.",
        });

        // Started earlier this month, played again today: the month equals the total and
        // disappears; today differs and shows.
        await using (var store = _factory.CreateDbContext())
        {
            store.Spend.Add(new SpendRecord
            {
                Id = "s1", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = DateTimeOffset.Now.AddDays(-1), Cost = 0.30m,
            });
            store.Spend.Add(new SpendRecord
            {
                Id = "s2", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = DateTimeOffset.Now, Cost = 0.05m,
            });
            await store.SaveChangesAsync();
        }

        // Guard: this test's premise needs yesterday inside the current month. On the 1st,
        // yesterday falls outside and the story becomes the long-lived case instead.
        if (DateTime.Today.Day == 1)
        {
            return;
        }

        var header = await RenderedHeaderAsync(chat);

        header.ShouldContain("$0.3500");
        header.ShouldNotContain("this month");
        header.ShouldContain("$0.0500 today");
    }

    [Fact]
    public async Task A_story_played_only_today_does_not_repeat_itself()
    {
        var chat = await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
            PersonaName = "allan",
            Opening = "The lamp is lit.",
        });

        await using (var store = _factory.CreateDbContext())
        {
            store.Spend.Add(new SpendRecord
            {
                Id = "s1", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = DateTimeOffset.Now, Cost = 0.05m,
            });
            await store.SaveChangesAsync();
        }

        var header = await RenderedHeaderAsync(chat);

        header.ShouldContain("$0.0500");
        header.ShouldNotContain("today");
    }

    [Fact]
    public async Task The_chat_the_participants_and_the_cost_each_keep_their_own_line()
    {
        var chat = await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            CharacterName = "elena",
            PersonaName = "allan",
            Opening = "The lamp is lit.",
        });

        await using (var store = _factory.CreateDbContext())
        {
            store.Spend.Add(new SpendRecord
            {
                Id = "s1", ConversationId = chat.Id, Kind = SpendKind.Reply,
                AtUtc = DateTimeOffset.Now, Cost = 0.05m,
            });
            await store.SaveChangesAsync();
        }

        var lines = (await RenderedRawAsync(chat)).Split('\n');

        var counts = Array.FindIndex(lines, static l => l.Contains("message 1/1"));
        var participants = Array.FindIndex(lines, static l => l.Contains("as allan"));
        var cost = Array.FindIndex(lines, static l => l.Contains("$0.0500"));

        counts.ShouldBeGreaterThanOrEqualTo(0);
        participants.ShouldBe(counts + 1);
        cost.ShouldBe(participants + 1);
    }

    [Fact]
    public async Task Playing_without_a_persona_is_stated_without_nagging()
    {
        // A legitimate way to play, so it reads as information rather than a warning.
        var header = await RenderedHeaderAsync("elena", personaName: null);

        header.ShouldContain("as no persona");
    }
}
