using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Web.Pages;
using NSubstitute;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The library from the browser, against real files on real shelves: an entry is saved without
/// losing what it replaced or what someone else saved meanwhile, and a file a story still reads
/// every turn is never deleted from under it.
/// </summary>
public sealed class WebLibraryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "airp-web-library-" + Guid.NewGuid().ToString("N"));
    private readonly TextLibrary _library;

    public WebLibraryTests()
    {
        _library = new TextLibrary(_root);
        _library.EnsureCreated();

        File.WriteAllText(Path.Combine(_library.Characters, "Vardhal.txt"), "=== THE WORLD ===\nA lighthouse on a headland.\n");
        File.WriteAllText(Path.Combine(_library.Openings, "Vardhal.txt"), "*Rain against the glass.*\n");
        File.WriteAllText(Path.Combine(_library.Personas, "Allan.txt"), "A traveller.\r\nTired.\r\n");
        File.WriteAllText(Path.Combine(_library.Personas, "Keeper.txt"), "The one who stays.\n");
        File.WriteAllText(Path.Combine(_library.Snippets, "rain.txt"), "It rains.\n");
    }

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalConversationProvider Provider(string? defaultPersona = null) => new(
        _factory,
        _model,
        TestOptions.Default(o => o.DefaultPersona = defaultPersona),
        NullLogger<LocalConversationProvider>.Instance);

    private static T WithTempData<T>(T page)
        where T : PageModel
    {
        page.TempData = new TempDataDictionary(new DefaultHttpContext(), Substitute.For<ITempDataProvider>());
        return page;
    }

    private LibraryEntryModel Entry(string? defaultPersona = null) => WithTempData(new LibraryEntryModel(Provider(defaultPersona), _library));

    private LibraryDeleteModel Deletion(string? defaultPersona = null) => new(Provider(defaultPersona), _library);

    private string Read(string folder, string name) => File.ReadAllText(Path.Combine(folder, name + ".txt"));

    [Fact]
    public void A_shelf_lists_its_entries_and_an_unknown_one_is_not_found()
    {
        var page = new LibraryModel(_library);

        page.OnGet("personas").ShouldBeOfType<PageResult>();
        page.Entries.Select(static e => e.Name).ShouldBe(["Allan", "Keeper"]);

        new LibraryModel(_library).OnGet("recipes").ShouldBeOfType<NotFoundResult>();
    }

    private LibraryNewModel Draft() => WithTempData(new LibraryNewModel(_library));

    [Fact]
    public async Task A_new_entry_is_a_draft_from_the_template_and_nothing_is_written_until_it_is_saved()
    {
        var page = Draft();
        (await page.OnGetAsync("characters", from: null, CancellationToken.None)).ShouldBeOfType<PageResult>();

        page.Text.ShouldBe(TextLibrary.CharacterSkeleton);
        page.OpeningText.ShouldBe(TextLibrary.OpeningSkeleton);
        TextLibrary.Names(_library.Characters).ShouldBe(["Vardhal"]);

        var save = Draft();
        save.Name = "Mira";
        save.Text = "A harbour.\r\n";
        save.OpeningText = "*Gulls.*";

        (await save.OnPostAsync("characters", from: null, CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Mira");

        Read(_library.Characters, "Mira").ShouldBe("A harbour.\n");
        Read(_library.Openings, "Mira").ShouldBe("*Gulls.*");
    }

    [Fact]
    public async Task Duplicating_copies_the_text_and_the_opening_into_a_draft_named_as_a_copy()
    {
        var page = Draft();
        await page.OnGetAsync("characters", from: "vardhal", CancellationToken.None);

        page.From.ShouldBe("Vardhal");
        page.Name.ShouldBe("Vardhal copy");
        page.Text.ShouldBe("=== THE WORLD ===\nA lighthouse on a headland.\n");
        page.OpeningText.ShouldBe("*Rain against the glass.*\n");
        TextLibrary.Names(_library.Characters).ShouldBe(["Vardhal"]);

        var save = Draft();
        save.Name = page.Name;
        save.Text = page.Text;
        save.OpeningText = page.OpeningText;
        (await save.OnPostAsync("characters", from: "Vardhal", CancellationToken.None)).ShouldBeOfType<RedirectResult>();

        TextLibrary.Names(_library.Characters).ShouldBe(["Vardhal", "Vardhal copy"]);
        Read(_library.Openings, "Vardhal copy").ShouldBe("*Rain against the glass.*\n");
    }

    [Fact]
    public async Task A_draft_with_an_empty_opening_writes_no_opening()
    {
        var save = Draft();
        save.Name = "Allan copy";
        save.Text = "A traveller.";
        (await save.OnPostAsync("personas", from: "Allan", CancellationToken.None)).ShouldBeOfType<RedirectResult>();

        var character = Draft();
        character.Name = "Quiet";
        character.Text = "Nobody speaks.";
        character.OpeningText = "   ";
        await character.OnPostAsync("characters", from: null, CancellationToken.None);

        Read(_library.Personas, "Allan copy").ShouldBe("A traveller.");
        TextLibrary.Find(_library.Openings, "Quiet").ShouldBeNull();
    }

    [Fact]
    public async Task A_name_already_on_the_shelf_is_refused_and_the_draft_is_kept()
    {
        var save = Draft();
        save.Name = "vardhal";
        save.Text = "Typed on the phone.";

        (await save.OnPostAsync("characters", from: null, CancellationToken.None)).ShouldBeOfType<PageResult>();

        save.Error.ShouldNotBeNull().ShouldContain("already exists");
        save.Text.ShouldBe("Typed on the phone.");
        Read(_library.Characters, "Vardhal").ShouldBe("=== THE WORLD ===\nA lighthouse on a headland.\n");
    }

    [Fact]
    public async Task A_character_whose_opening_name_is_taken_writes_neither_file()
    {
        File.WriteAllText(Path.Combine(_library.Openings, "Ghost.txt"), "*Nobody here.*\n");

        var save = Draft();
        save.Name = "Ghost";
        save.Text = "A haunted house.";
        save.OpeningText = "*A door creaks.*";

        (await save.OnPostAsync("characters", from: null, CancellationToken.None)).ShouldBeOfType<PageResult>();

        save.Error.ShouldNotBeNull().ShouldContain("already on the shelf");
        TextLibrary.Find(_library.Characters, "Ghost").ShouldBeNull();
        Read(_library.Openings, "Ghost").ShouldBe("*Nobody here.*\n");
    }

    [Fact]
    public async Task There_is_no_draft_for_an_opening_alone_or_a_copy_of_nothing()
    {
        (await Draft().OnGetAsync("openings", from: null, CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Draft().OnGetAsync("characters", from: "Nobody", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Saving_writes_the_text_and_keeps_what_it_replaced_as_a_backup()
    {
        var page = Entry();
        await page.OnGetAsync("characters", "vardhal", CancellationToken.None);

        page.Text = "=== THE WORLD ===\r\nA lighthouse, abandoned.\r\n";
        var result = await page.OnPostAsync("characters", "vardhal", CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Vardhal");

        // The browser's \r\n become the file's own \n.
        Read(_library.Characters, "Vardhal").ShouldBe("=== THE WORLD ===\nA lighthouse, abandoned.\n");
        File.ReadAllText(Path.Combine(_library.Characters, "Vardhal.txt.bak")).ShouldBe("=== THE WORLD ===\nA lighthouse on a headland.\n");

        // The backup is not an entry.
        TextLibrary.Names(_library.Characters).ShouldBe(["Vardhal"]);
    }

    [Fact]
    public async Task A_file_written_with_windows_line_endings_keeps_them()
    {
        var page = Entry();
        await page.OnGetAsync("personas", "Allan", CancellationToken.None);

        page.Text = "A traveller.\nRested.\n";
        await page.OnPostAsync("personas", "Allan", CancellationToken.None);

        Read(_library.Personas, "Allan").ShouldBe("A traveller.\r\nRested.\r\n");
    }

    [Fact]
    public async Task A_save_over_an_edit_made_elsewhere_is_refused_and_keeps_both_texts()
    {
        var page = Entry();
        await page.OnGetAsync("characters", "Vardhal", CancellationToken.None);
        var opened = page.Version;

        // Meanwhile, on the laptop.
        File.WriteAllText(Path.Combine(_library.Characters, "Vardhal.txt"), "Edited on the laptop.\n");

        var phone = Entry();
        phone.Text = "Typed on the phone.";
        phone.Version = opened;

        (await phone.OnPostAsync("characters", "Vardhal", CancellationToken.None)).ShouldBeOfType<PageResult>();

        Read(_library.Characters, "Vardhal").ShouldBe("Edited on the laptop.\n");
        phone.Error.ShouldNotBeNull().ShouldContain("changed somewhere else");
        phone.Text.ShouldBe("Typed on the phone.");
        phone.Current.ShouldBe("Edited on the laptop.\n");

        // Saving again is a decision made with both on the page, and the laptop's survives it.
        var again = Entry();
        again.Text = phone.Text;
        again.Version = phone.Version;
        (await again.OnPostAsync("characters", "Vardhal", CancellationToken.None)).ShouldBeOfType<RedirectResult>();

        Read(_library.Characters, "Vardhal").ShouldBe("Typed on the phone.");
        File.ReadAllText(Path.Combine(_library.Characters, "Vardhal.txt.bak")).ShouldBe("Edited on the laptop.\n");
    }

    [Fact]
    public async Task Saving_the_text_unchanged_does_not_replace_the_backup()
    {
        File.WriteAllText(Path.Combine(_library.Snippets, "rain.txt.bak"), "An older rain.\n");

        var page = Entry();
        await page.OnGetAsync("snippets", "rain", CancellationToken.None);
        await page.OnPostAsync("snippets", "rain", CancellationToken.None);

        File.ReadAllText(Path.Combine(_library.Snippets, "rain.txt.bak")).ShouldBe("An older rain.\n");
    }

    [Fact]
    public async Task A_characters_page_carries_its_opening_and_saves_it_on_its_own()
    {
        var page = Entry();
        await page.OnGetAsync("characters", "Vardhal", CancellationToken.None);

        page.HasOpening.ShouldBeTrue();
        page.OpeningText.ShouldBe("*Rain against the glass.*\n");

        var save = Entry();
        save.OpeningText = "*Fog.*";
        save.OpeningVersion = page.OpeningVersion;
        (await save.OnPostOpeningAsync("characters", "Vardhal", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Vardhal#opening");

        Read(_library.Openings, "Vardhal").ShouldBe("*Fog.*");
        Read(_library.Characters, "Vardhal").ShouldBe("=== THE WORLD ===\nA lighthouse on a headland.\n");
    }

    [Fact]
    public async Task A_character_without_an_opening_can_be_given_one_named_to_match()
    {
        File.WriteAllText(Path.Combine(_library.Characters, "Mira.txt"), "A harbour.\n");

        await Entry().OnPostAddOpeningAsync("characters", "Mira", CancellationToken.None);

        Read(_library.Openings, "Mira").ShouldBe(TextLibrary.OpeningSkeleton);
    }

    [Fact]
    public async Task A_character_a_story_uses_is_not_deleted_and_the_page_says_which_story()
    {
        await Provider().CreateAsync(new NewConversation { Name = "The headland", CharacterName = "Vardhal" });

        var page = Deletion();
        (await page.OnPostAsync("characters", "Vardhal", CancellationToken.None)).ShouldBeOfType<PageResult>();

        page.UsedBy.ShouldBe(["The headland"]);
        File.Exists(Path.Combine(_library.Characters, "Vardhal.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_default_persona_counts_as_used_by_every_story_that_names_none()
    {
        await Provider("Keeper").CreateAsync(new NewConversation { Name = "The headland", CharacterName = "Vardhal" });

        var page = Deletion("Keeper");
        (await page.OnPostAsync("personas", "Keeper", CancellationToken.None)).ShouldBeOfType<PageResult>();

        page.UsedBy.ShouldBe(["The headland"]);
        File.Exists(Path.Combine(_library.Personas, "Keeper.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task An_entry_no_story_reads_is_deleted()
    {
        await Provider().CreateAsync(new NewConversation { Name = "The headland", CharacterName = "Vardhal", PersonaName = "Allan" });

        (await Deletion().OnPostAsync("personas", "Keeper", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/personas");
        (await Deletion().OnPostAsync("snippets", "rain", CancellationToken.None)).ShouldBeOfType<RedirectResult>();

        TextLibrary.Names(_library.Personas).ShouldBe(["Allan"]);
        TextLibrary.Names(_library.Snippets).ShouldBeEmpty();
    }

    [Fact]
    public void Openings_have_no_tab_and_their_shelf_leads_to_the_characters()
    {
        var page = new LibraryModel(_library);

        page.Tabs.Select(static t => t.Slug).ShouldBe(["characters", "personas", "snippets"]);
        new LibraryModel(_library).OnGet("openings").ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters");
    }

    [Fact]
    public async Task An_opening_is_edited_on_its_characters_page()
        => (await Entry().OnGetAsync("openings", "vardhal", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Vardhal#opening");

    [Fact]
    public async Task An_opening_with_no_character_is_listed_under_the_characters_and_can_still_be_opened()
    {
        File.WriteAllText(Path.Combine(_library.Openings, "Ghost.txt"), "*Nobody here.*\n");

        var shelf = new LibraryModel(_library);
        shelf.OnGet("characters");
        shelf.Orphans.ShouldBe(["Ghost"]);

        var page = Entry();
        (await page.OnGetAsync("openings", "Ghost", CancellationToken.None)).ShouldBeOfType<PageResult>();
        page.Text.ShouldBe("*Nobody here.*\n");
    }

    [Fact]
    public async Task Deleting_a_character_takes_its_opening_with_it()
    {
        var page = Deletion();
        await page.OnGetAsync("characters", "Vardhal", CancellationToken.None);
        page.HasOpening.ShouldBeTrue();

        (await Deletion().OnPostAsync("characters", "Vardhal", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters");

        TextLibrary.Names(_library.Characters).ShouldBeEmpty();
        TextLibrary.Names(_library.Openings).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deleting_an_opening_alone_keeps_the_character_and_goes_back_to_it()
    {
        (await Deletion().OnPostAsync("openings", "Vardhal", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Vardhal");

        TextLibrary.Names(_library.Openings).ShouldBeEmpty();
        TextLibrary.Names(_library.Characters).ShouldBe(["Vardhal"]);
    }

    [Fact]
    public async Task An_entry_that_is_not_on_the_shelf_is_not_found()
    {
        (await Entry().OnGetAsync("characters", "Nobody", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Entry().OnGetAsync("characters", "..\\personas\\Allan", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Deletion().OnPostAsync("recipes", "rain", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
    }
}
