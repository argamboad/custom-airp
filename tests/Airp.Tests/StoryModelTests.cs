using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Application.Services;
using Airp.Infrastructure;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Airp.Web.Pages;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A story's own model: chosen from what the provider lists, and never the reason a turn cannot
/// be written.
/// </summary>
/// <remarks>
/// Two moments can find a model unavailable. Choosing one that is not listed saves nothing and
/// says what the story stays on; a saved one the provider stops serving writes that turn with
/// the default and records that it did. Only a refusal about the model itself does that — an
/// empty account or a rejected key would refuse the default identically.
/// </remarks>
public sealed class StoryModelTests : IDisposable
{
    private const string Default = "deepseek/deepseek-v4-flash";

    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new()
    {
        Catalogue =
        [
            new ModelInfo("big/model", 200_000),
            new ModelInfo("small/model", 32_768),
            new ModelInfo("gone/model", 128_000),
            new ModelInfo("tiny/model", 16_000),
            new ModelInfo("cognitivecomputations/dolphin-mistral-24b-venice-edition", 128_000),
        ],
    };

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<Chat> StoryAsync()
        => await Provider().CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });

    [Fact]
    public async Task A_listed_model_is_saved_under_the_providers_spelling_with_its_window()
    {
        var chat = await StoryAsync();

        var change = await Provider().SetModelAsync(chat.Id, " BIG/Model ");

        change.Saved.ShouldBeTrue();
        change.Model.ShouldBe("big/model");
        change.Context.ShouldBe(200_000);
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("big/model");
    }

    [Fact]
    public async Task A_model_whose_window_is_under_the_budget_is_saved_and_says_the_budget_shrinks()
    {
        var chat = await StoryAsync();

        var change = await Provider().SetModelAsync(chat.Id, "tiny/model");

        change.Saved.ShouldBeTrue();
        change.Message.ShouldContain("shrinks to fit");
    }

    [Fact]
    public async Task A_model_the_provider_does_not_list_is_not_saved_and_the_story_stays_on_what_it_had()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");

        var change = await Provider().SetModelAsync(chat.Id, "big/modle");

        change.Saved.ShouldBeFalse();
        change.Message.ShouldContain("not available");
        change.Message.ShouldContain("stays on big/model");
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("big/model");
    }

    [Fact]
    public async Task A_model_that_cannot_be_checked_is_not_saved()
    {
        var chat = await StoryAsync();
        _model.CatalogueFailure = new ModelUnavailableException("Could not reach the provider.");

        var change = await Provider().SetModelAsync(chat.Id, "big/model");

        change.Saved.ShouldBeFalse();
        change.Message.ShouldContain("could not be checked");
        change.Message.ShouldContain($"stays on {Default}");
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Default)]
    public async Task Nothing_or_the_default_itself_clears_the_story_back_to_the_default(string? model)
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");

        var change = await Provider().SetModelAsync(chat.Id, model);

        change.Saved.ShouldBeTrue();
        change.Model.ShouldBeNull();
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Fact]
    public async Task Replies_and_questions_are_written_by_the_storys_model()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");
        _model.Says("She looks up.").Says("She knows.");

        await Provider().SendAsync(chat.Id, "I come in.");
        await Provider().AskAsync(chat.Id, "What does she know?");

        _model.Models.ShouldBe(["big/model", "big/model"]);
    }

    [Fact]
    public async Task Replies_and_questions_go_out_with_reasoning_off()
    {
        var chat = await StoryAsync();
        _model.Says("She looks up.").Says("She knows.");

        await Provider().SendAsync(chat.Id, "I come in.");
        await Provider().AskAsync(chat.Id, "What does she know?");

        _model.Reasonings.ShouldBe([false, false]);
    }

    [Fact]
    public async Task A_model_that_has_gone_writes_nothing_the_default_writes_the_turn_and_the_reply_says_so()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "gone/model");
        _model.HasNoSuchModel().Says("The default wrote this.");

        var added = await Provider().SendAsync(chat.Id, "I come in.");

        _model.Models.ShouldBe(["gone/model", Default]);
        added[^1].Text.ShouldBe("The default wrote this.");
        added[^1].FellBackFrom.ShouldBe("gone/model");
        (await Provider().GetMessagesAsync(chat.Id))[^1].FellBackFrom.ShouldBe("gone/model");

        // Kept: "no endpoints" is often a host missing for an hour, and the next turn tries again.
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("gone/model");
    }

    [Fact]
    public async Task A_failure_that_is_not_about_the_model_does_not_spend_a_second_call_on_the_default()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");
        _model.Fails("The account is out of credit.");

        await Should.ThrowAsync<ReplyMissingException>(() => Provider().SendAsync(chat.Id, "I come in."));

        _model.Models.ShouldBe(["big/model"]);
    }

    [Fact]
    public async Task A_branch_keeps_its_storys_model()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "small/model");
        _model.Says("She looks up.");
        var turns = await Provider().SendAsync(chat.Id, "I come in.");

        var branch = await Provider().BranchAsync(chat.Id, turns[^1].Id, "Vardhal (2)");

        branch.Model.ShouldBe("small/model");
    }

    // ── On the screens ───────────────────────────────────────────────────────────────────

    private static KeyStroke Nav(ConsoleKey key)
        => KeyMap.Resolve(new ConsoleKeyInfo('\0', key, false, false, false), KeyboardMode.Standard, KeyContext.Navigation);

    private static RenderContext Context() => new(100, 30, Theme.For(ThemeName.Dark), new AirpOptions());

    private async Task<(ChatSettingsView View, List<string?> Changed, FakeDialService Dials)> SettingsAsync(Chat chat, IReadOnlyList<string> choices)
    {
        var changed = new List<string?>();
        var dials = new FakeDialService();
        var view = new ChatSettingsView(
            dials,
            chat.Id,
            chat.Name,
            provider: Provider(),
            defaultModel: Default,
            modelChoices: choices,
            modelChanged: changed.Add);

        var load = (await view.OnActivatedAsync(CancellationToken.None)).ShouldBeOfType<ViewAction.RunAction>();
        await load.Work(CancellationToken.None);
        return (view, changed, dials);
    }

    private static async Task<ViewAction> ApplyAsync(ChatSettingsView view)
    {
        var apply = (await view.HandleKeyAsync(Nav(ConsoleKey.Enter), Context(), CancellationToken.None))
            .ShouldBeOfType<ViewAction.RunAction>();
        return await apply.Work(CancellationToken.None);
    }

    [Fact]
    public async Task The_terminal_settings_lead_with_the_model_and_apply_it_like_a_dial()
    {
        var chat = await StoryAsync();
        var (view, changed, dials) = await SettingsAsync(chat, ["big/model", "small/model"]);

        // The first row is the model, on the default; one step right is the first choice.
        await view.HandleKeyAsync(Nav(ConsoleKey.RightArrow), Context(), CancellationToken.None);
        await ApplyAsync(view);

        changed.ShouldBe(["big/model"]);
        dials.Writes.ShouldBeEmpty();
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("big/model");
    }

    [Fact]
    public async Task A_model_the_terminal_cannot_set_is_said_and_the_row_goes_back_to_what_the_story_has()
    {
        var chat = await StoryAsync();
        var (view, changed, _) = await SettingsAsync(chat, ["missing/model"]);

        await view.HandleKeyAsync(Nav(ConsoleKey.RightArrow), Context(), CancellationToken.None);
        var said = (await ApplyAsync(view)).ShouldBeOfType<ViewAction.StatusAction>();

        said.Kind.ShouldBe(StatusKind.Warning);
        said.Text.ShouldContain("not available");
        changed.ShouldBeEmpty();
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Fact]
    public async Task The_web_settings_change_the_model_and_say_so_or_say_why_not()
    {
        var chat = await StoryAsync();

        var page = new DialsModel(new FakeDialService(), Provider(), TestOptions.Default());
        await page.OnPostModelAsync(chat.Id, "big/model", CancellationToken.None);
        page.Applied.ShouldNotBeNull().ShouldContain("big/model");

        var refused = new DialsModel(new FakeDialService(), Provider(), TestOptions.Default());
        await refused.OnPostModelAsync(chat.Id, "missing/model", CancellationToken.None);
        refused.Error.ShouldNotBeNull().ShouldContain("stays on big/model");
    }

    [Fact]
    public async Task A_story_started_on_an_unavailable_model_starts_on_the_default_and_its_page_says_so_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "airp-model-" + Guid.NewGuid().ToString("N"));
        var library = new TextLibrary(root);
        library.EnsureCreated();
        File.WriteAllText(Path.Combine(library.Characters, "Vardhal.txt"), "=== THE WORLD ===\nA lighthouse.");
        var temp = new TempDataDictionary(new DefaultHttpContext(), Substitute.For<ITempDataProvider>());

        try
        {
            var form = new NewStoryModel(Provider(), library, TestOptions.Default()) { Model = "missing/model", TempData = temp };
            await form.OnPostAsync("Vardhal", CancellationToken.None);

            var chat = (await Provider().ListAsync()).ShouldHaveSingleItem();
            chat.Model.ShouldBeNull();

            var story = new StoryModel(
                Provider(),
                library,
                new ExportService(TestOptions.Default(), NullLogger<ExportService>.Instance),
                TestOptions.Default()) { TempData = temp };
            await story.OnGetAsync(chat.Id, all: false, CancellationToken.None);

            story.Arrival.ShouldNotBeNull().ShouldContain("not available");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task The_story_page_says_when_the_newest_reply_was_written_by_the_default()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "gone/model");
        _model.HasNoSuchModel().Says("The default wrote this.");
        await Provider().SendAsync(chat.Id, "I come in.");

        var story = new StoryModel(
            Provider(),
            new TextLibrary(Path.Combine(Path.GetTempPath(), "airp-none-" + Guid.NewGuid().ToString("N"))),
            new ExportService(TestOptions.Default(), NullLogger<ExportService>.Instance),
            TestOptions.Default());
        await story.OnGetAsync(chat.Id, all: false, CancellationToken.None);

        story.FellBackFrom.ShouldBe("gone/model");
    }

    [Fact]
    public async Task Prices_beside_the_choices_are_left_out_rather_than_failing_when_the_list_cannot_be_read()
    {
        _model.CatalogueFailure = new ModelUnavailableException("Could not reach the provider.");

        (await Provider().ModelsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_model_whose_list_overstates_its_window_is_saved_with_the_window_it_can_really_read()
    {
        var chat = await StoryAsync();

        var change = await Provider().SetModelAsync(chat.Id, "cognitivecomputations/dolphin-mistral-24b-venice-edition");

        change.Saved.ShouldBeTrue();
        change.Context.ShouldBe(32_768);
    }

    [Fact]
    public void A_configured_window_beats_the_shipped_correction_which_beats_the_list()
    {
        var settings = new ModelOptions { Windows = new Dictionary<string, int> { ["mine/model"] = 16_000 } };

        settings.WindowOf("mine/model", 128_000).ShouldBe(16_000);
        settings.WindowOf("cognitivecomputations/dolphin-mistral-24b-venice-edition", 128_000).ShouldBe(32_768);
        settings.WindowOf("cognitivecomputations/dolphin-mistral-24b-venice-edition", 8_000).ShouldBe(8_000);
        settings.WindowOf("other/model", 64_000).ShouldBe(64_000);
        settings.WindowOf("other/model", null).ShouldBeNull();
    }

    [Theory]
    [InlineData(0.6, 0.3)]
    [InlineData(1.0, 0.6)]
    [InlineData(1.2, 0.75)]
    [InlineData(1.4, 0.9)]
    [InlineData(0.4, 0.15)]
    public void The_dials_temperature_is_spread_across_the_range_the_model_writes_well_in(double asked, double sent)
        => new ModelOptions().TemperatureFor("cognitivecomputations/dolphin-mistral-24b-venice-edition", asked).ShouldBe(sent);

    [Fact]
    public void A_model_with_no_measured_range_gets_the_dials_own_temperature()
        => new ModelOptions().TemperatureFor("deepseek/deepseek-v4-flash", 1.2).ShouldBe(1.2);

    [Fact]
    public async Task The_storys_model_is_sent_its_own_temperature_and_the_default_stepping_in_is_sent_the_defaults()
    {
        // Measured: every roleplay finetune on the list turned to token soup at 1.3, DeepSeek
        // did not. A story on one at Creativity 3 was being sent 1.2.
        var options = TestOptions.Default(o =>
            o.Model.Temperatures = new Dictionary<string, TemperatureRange>
            {
                ["gone/model"] = new() { Min = 0.3, Max = 0.9 },
                ["big/model"] = new() { Min = 0.3, Max = 0.9 },
            });

        LocalConversationProvider Scaled() => new(_factory, _model, options, NullLogger<LocalConversationProvider>.Instance);

        var chat = await StoryAsync();
        await Scaled().SetModelAsync(chat.Id, "big/model");
        _model.Says("She looks up.");
        await Scaled().SendAsync(chat.Id, "I come in.");

        var ownTemperature = _model.Temperatures[^1];

        await Scaled().SetModelAsync(chat.Id, "gone/model");
        _model.HasNoSuchModel().Says("The default wrote this.");
        await Scaled().SendAsync(chat.Id, "I sit down.");

        // The configured reply temperature is 1.0 on the dial's scale: 0.6 on this model's.
        ownTemperature.ShouldBe(0.6);
        _model.Models.TakeLast(2).ShouldBe(["gone/model", Default]);
        _model.Temperatures.TakeLast(2).ShouldBe([0.6, 1.0]);
    }
}
