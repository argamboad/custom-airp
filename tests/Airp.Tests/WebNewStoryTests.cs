using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Web.Pages;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Starting a story from the browser, against real files on real shelves: the shelf is offered,
/// the opening arrives pre-filled, and only names that are on a shelf are ever stored.
/// </summary>
public sealed class WebNewStoryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "airp-web-new-" + Guid.NewGuid().ToString("N"));
    private readonly TextLibrary _library;

    public WebNewStoryTests()
    {
        _library = new TextLibrary(_root);
        _library.EnsureCreated();

        File.WriteAllText(
            Path.Combine(_library.Characters, "Vardhal.txt"),
            "You are the narrator.\n\n=== THE WORLD ===\n\nA lighthouse on a headland.\nThe keeper writes everything down.\n\n=== RULES ===\nStay in the scene.");
        File.WriteAllText(Path.Combine(_library.Characters, "_template.txt"), "=== THE WORLD ===\nA skeleton.");
        File.WriteAllText(Path.Combine(_library.Openings, "Vardhal.txt"), "*Rain against the lamp room glass.*\n\n");
        File.WriteAllText(Path.Combine(_library.Personas, "Allan.txt"), "A traveller.");
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

    private NewStoryModel Page() => new(Provider(), _library, TestOptions.Default());

    [Fact]
    public async Task The_shelf_offers_each_character_by_the_first_line_of_its_world()
    {
        var page = Page();

        await page.OnGetAsync(character: null, CancellationToken.None);

        // The template is a working paper, not something to play.
        page.Characters.ShouldBe([("Vardhal", "A lighthouse on a headland.")]);
    }

    [Fact]
    public async Task Picking_a_character_fills_in_its_own_opening_and_shows_its_world()
    {
        var page = Page();

        var result = await page.OnGetAsync("vardhal", CancellationToken.None);

        result.ShouldBeOfType<PageResult>();
        page.Character.ShouldBe("Vardhal");
        page.Opening.ShouldBe("*Rain against the lamp room glass.*");
        page.World.ShouldBe("A lighthouse on a headland. The keeper writes everything down.");
        page.Personas.ShouldBe(["Allan"]);
    }

    [Fact]
    public void A_world_wrapped_for_an_editor_is_joined_back_into_paragraphs_and_keeps_its_lists()
        => NewStoryModel.Reflow(["A harbour town,", "forty people.", "", "Who lives here:", "- the keeper", "- his sister", "", "Storms come", "from the west."])
            .ShouldBe("A harbour town, forty people.\n\nWho lives here:\n- the keeper\n- his sister\n\nStorms come from the west.");

    [Fact]
    public async Task A_character_that_is_not_on_the_shelf_is_not_found()
    {
        (await Page().OnGetAsync("Nobody", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Page().OnPostAsync("Nobody", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Provider().ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Starting_stores_the_names_and_goes_straight_into_the_story()
    {
        var page = Page();
        page.Opening = "  *Rain.*  ";
        page.Persona = "allan";
        page.Speaker = " Keeper ";

        var result = await page.OnPostAsync("VARDHAL", CancellationToken.None);

        var chat = (await Provider().ListAsync()).ShouldHaveSingleItem();
        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{chat.Id}#latest");

        // Named after the character when nothing else was given, spelled as the shelf spells it.
        chat.Name.ShouldBe("Vardhal");
        chat.Speaker.ShouldBe("Keeper");

        var turn = (await Provider().GetMessagesAsync(chat.Id)).ShouldHaveSingleItem();
        turn.Text.ShouldBe("*Rain.*");
        _model.Calls.ShouldBeEmpty();

        // The name, not a copy: editing the file must reach this story.
        await using var store = _factory.CreateDbContext();
        var record = store.Conversations.Single();
        record.CharacterName.ShouldBe("Vardhal");
        record.CharacterDefinition.ShouldBeNull();
        record.PersonaName.ShouldBe("allan");
    }

    [Fact]
    public async Task A_persona_that_is_not_on_the_shelf_is_refused_and_nothing_is_started()
    {
        var page = Page();
        page.Persona = "Stranger";

        var result = await page.OnPostAsync("Vardhal", CancellationToken.None);

        result.ShouldBeOfType<PageResult>();
        page.Error.ShouldNotBeNull().ShouldContain("Nothing was started");
        (await Provider().ListAsync()).ShouldBeEmpty();
    }
}
