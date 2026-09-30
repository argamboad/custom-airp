using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Web.Pages;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// What the browser can change about a story besides playing it: its dials, its name, the turns
/// from one point on, and whether it exists at all.
/// </summary>
/// <remarks>
/// The two deletions are a page each, asked before done, because the pages carry no script to
/// ask with. What a confirmation says is tested along with what the deletion does: a count that
/// is wrong on the page is a promise the button then breaks.
/// </remarks>
public sealed class WebManageTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<Chat> StoryAsync(string name = "Vardhal", int exchanges = 2)
    {
        var chat = await Provider().CreateAsync(new NewConversation { Name = name, Speaker = "Elena" });

        for (var i = 0; i < exchanges; i++)
        {
            _model.Says($"Reply {i}.");
            await Provider().SendAsync(chat.Id, $"Turn {i}.");
        }

        return chat;
    }

    [Fact]
    public async Task Delete_from_here_says_how_many_go_and_then_removes_exactly_those()
    {
        var chat = await StoryAsync();
        var turns = await Provider().GetMessagesAsync(chat.Id);
        var page = new DeleteFromModel(Provider());

        await page.OnGetAsync(chat.Id, turns[2].Id, CancellationToken.None);

        page.Doomed.ShouldBe(2);
        page.Remaining.ShouldBe(2);
        page.Preview.ShouldBe("Turn 1.");

        var result = await new DeleteFromModel(Provider()).OnPostAsync(chat.Id, turns[2].Id, CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{chat.Id}#latest");
        (await Provider().GetMessagesAsync(chat.Id)).Select(static m => m.Text).ShouldBe(["Turn 0.", "Reply 0."]);
    }

    [Fact]
    public async Task Delete_from_a_turn_that_is_not_in_the_story_is_not_found_and_removes_nothing()
    {
        var chat = await StoryAsync();

        (await new DeleteFromModel(Provider()).OnPostAsync(chat.Id, "nosuchturn", CancellationToken.None))
            .ShouldBeOfType<NotFoundResult>();
        (await Provider().GetMessagesAsync(chat.Id)).Count.ShouldBe(4);
    }

    [Fact]
    public async Task Deleting_a_story_names_it_first_and_then_takes_it_off_the_list()
    {
        var chat = await StoryAsync();
        await StoryAsync("Cadgwith");
        var page = new DeleteStoryModel(Provider());

        await page.OnGetAsync(chat.Id, CancellationToken.None);

        page.Chat.ShouldNotBeNull().Name.ShouldBe("Vardhal");
        page.Turns.ShouldBe(4);

        (await new DeleteStoryModel(Provider()).OnPostAsync(chat.Id, CancellationToken.None))
            .ShouldBeOfType<RedirectResult>().Url.ShouldBe("/");
        (await Provider().ListAsync()).ShouldHaveSingleItem().Name.ShouldBe("Cadgwith");
    }

    [Fact]
    public async Task Rename_changes_the_name_and_refuses_a_blank_one()
    {
        var chat = await StoryAsync();

        (await new IndexModel(Provider()).OnPostRenameAsync(chat.Id, "  The Lamp Room  ", CancellationToken.None))
            .ShouldBeOfType<RedirectResult>();
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Name.ShouldBe("The Lamp Room");

        var blank = new IndexModel(Provider());
        (await blank.OnPostRenameAsync(chat.Id, " ", CancellationToken.None)).ShouldBeOfType<PageResult>();
        blank.Error.ShouldNotBeNull().ShouldContain("Nothing was changed");
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Name.ShouldBe("The Lamp Room");
    }

    [Fact]
    public async Task The_dials_apply_what_changed_and_default_clears_a_choice()
    {
        var chat = await StoryAsync();
        var dials = new FakeDialService().With("pacing", "1");
        var page = new DialsModel(dials, Provider())
        {
            Values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["lust"] = "3",
                ["inner-thoughts"] = "true",
                ["pacing"] = string.Empty,
                ["veils"] = "gore, spiders",
            },
        };

        await page.OnPostAsync(chat.Id, CancellationToken.None);

        page.Error.ShouldBeNull();
        page.Applied.ShouldNotBeNull().ShouldContain("Inner thoughts → On");
        dials.Writes.ShouldContain(("lust", "3"));
        dials.Writes.ShouldContain(("inner-thoughts", "true"));
        dials.Writes.ShouldContain(("pacing", null));
        dials.Writes.ShouldContain(("veils", """["gore","spiders"]"""));
    }

    [Fact]
    public async Task A_dial_value_it_does_not_take_changes_nothing_at_all()
    {
        var chat = await StoryAsync();
        var dials = new FakeDialService();
        var page = new DialsModel(dials, Provider())
        {
            Values = new(StringComparer.OrdinalIgnoreCase) { ["lust"] = "3", ["pov"] = "fourth-wall" },
        };

        await page.OnPostAsync(chat.Id, CancellationToken.None);

        page.Error.ShouldNotBeNull().ShouldContain("Nothing was changed");
        dials.Writes.ShouldBeEmpty();
    }
}
