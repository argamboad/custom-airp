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

    [Fact]
    public async Task A_new_entry_starts_from_the_terminals_template_and_opens_in_the_editor()
    {
        var page = new LibraryModel(_library) { Name = "Mira" };

        var result = await page.OnPostAsync("characters", CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe("/library/characters/Mira");
        Read(_library.Characters, "Mira").ShouldBe(TextLibrary.CharacterSkeleton);
    }

    [Fact]
    public async Task A_name_already_on_the_shelf_is_refused_rather_than_overwritten()
    {
        var page = new LibraryModel(_library) { Name = "vardhal" };

        (await page.OnPostAsync("characters", CancellationToken.None)).ShouldBeOfType<PageResult>();

        page.Error.ShouldNotBeNull().ShouldContain("already exists");
        Read(_library.Characters, "Vardhal").ShouldBe("=== THE WORLD ===\nA lighthouse on a headland.\n");
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
    public async Task An_entry_that_is_not_on_the_shelf_is_not_found()
    {
        (await Entry().OnGetAsync("characters", "Nobody", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Entry().OnGetAsync("characters", "..\\personas\\Allan", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
        (await Deletion().OnPostAsync("recipes", "rain", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
    }
}
