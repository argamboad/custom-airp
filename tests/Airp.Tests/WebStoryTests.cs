using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Web.Pages;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Playing a story from the browser: a turn ends in a redirect, an answer is shown on the page,
/// and a refusal keeps what was written.
/// </summary>
/// <remarks>
/// The redirect is the part that matters most. A stored turn answered with the page itself
/// would be sent again by a reload, and the same words after a reply are a new turn — so the
/// only safe answer to a stored turn is to send the browser somewhere else.
/// </remarks>
public sealed class WebStoryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "airp-web-story-" + Guid.NewGuid().ToString("N"));
    private readonly TextLibrary _library;

    public WebStoryTests()
    {
        _library = new TextLibrary(_root);
        _library.EnsureCreated();
        File.WriteAllText(Path.Combine(_library.Snippets, "storm.txt"), "Rain hammers the lamp room glass.\n");
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

    private StoryModel Page(string? draft = null) => new(
        Provider(),
        _library,
        new ExportService(TestOptions.Default(), NullLogger<ExportService>.Instance),
        TestOptions.Default()) { Draft = draft };

    private async Task<Chat> StoryAsync()
    {
        var chat = await Provider().CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });
        _model.Says("Elena looks up from the log.");
        await Provider().SendAsync(chat.Id, "I come in from the rain.");
        return chat;
    }

    private async Task<IReadOnlyList<ChatMessage>> TurnsAsync(Chat chat) => await Provider().GetMessagesAsync(chat.Id);

    [Fact]
    public async Task A_message_becomes_a_turn_and_the_page_moves_on_to_the_reply()
    {
        var chat = await StoryAsync();
        _model.Says("She closes the book.");

        var result = await Page("I sit across from her.").OnPostSendAsync(chat.Id, CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{chat.Id}#latest");
        (await TurnsAsync(chat))[^1].Text.ShouldBe("She closes the book.");
    }

    [Fact]
    public async Task Snippets_and_emoji_are_expanded_when_the_turn_is_sent()
    {
        var chat = await StoryAsync();
        _model.Says("She looks at the window.");

        await Page(":storm I wait :fire:").OnPostSendAsync(chat.Id, CancellationToken.None);

        (await TurnsAsync(chat))[^2].Text.ShouldBe("Rain hammers the lamp room glass. I wait \U0001F525");
    }

    [Fact]
    public async Task A_picked_snippet_goes_into_the_box_and_nothing_is_sent()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var calls = _model.Calls.Count;
        var page = Page("I look up.");
        page.Snippet = "storm";

        var result = await page.OnPostInsertAsync(chat.Id, CancellationToken.None);

        result.ShouldBeOfType<PageResult>();
        page.Draft.ShouldBe("I look up. Rain hammers the lamp room glass.");
        page.Notice.ShouldNotBeNull().ShouldContain("Nothing has been sent");
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
        _model.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task Send_with_a_snippet_picked_inserts_it_rather_than_sending_a_page_the_reader_has_not_seen()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var page = Page();
        page.Snippet = "storm";

        (await page.OnPostSendAsync(chat.Id, CancellationToken.None)).ShouldBeOfType<PageResult>();

        page.Draft.ShouldBe("Rain hammers the lamp room glass.");
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
    }

    [Theory]
    [InlineData(null, "Rain.")]
    [InlineData("I wait.", "I wait. Rain.")]
    [InlineData("I wait.\n", "I wait.\nRain.")]
    public void A_snippet_is_added_to_the_end_after_a_space_unless_the_draft_already_ends_in_one(string? draft, string expected)
        => StoryModel.AppendSnippet(draft, "Rain.").ShouldBe(expected);

    [Fact]
    public async Task A_typo_is_refused_on_the_page_and_the_draft_is_kept()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        var calls = _model.Calls.Count;
        var page = Page("/sumary");

        var result = await page.OnPostSendAsync(chat.Id, CancellationToken.None);

        result.ShouldBeOfType<PageResult>();
        page.Error.ShouldNotBeNull().ShouldContain("nothing was stored");
        page.Draft.ShouldBe("/sumary");
        (await TurnsAsync(chat)).Count.ShouldBe(turns);
        _model.Calls.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_turn_is_shown_on_the_page_and_can_be_pinned_as_a_fact()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        _model.Says("She knows you came up the outside stair.");
        var page = Page("/ask what does Elena know?");

        var result = await page.OnPostSendAsync(chat.Id, CancellationToken.None);

        result.ShouldBeOfType<PageResult>();
        page.Aside.ShouldNotBeNull().ShouldContain("She knows you came up the outside stair.");
        page.Draft.ShouldBeNull();
        (await TurnsAsync(chat)).Count.ShouldBe(turns);

        var pinned = Page();
        await pinned.OnPostPinAsync(chat.Id, page.Answer, CancellationToken.None);

        pinned.Aside.ShouldNotBeNull().ShouldContain("Pinned under Elena");
        (await Provider().FactsAsync(chat.Id)).ShouldHaveSingleItem().Text.ShouldBe("She knows you came up the outside stair.");
    }

    [Fact]
    public async Task A_search_links_each_turn_where_it_is_on_the_page_or_among_the_earlier_ones()
    {
        var chat = await StoryAsync();

        for (var i = 0; i < StoryModel.Recent; i++)
        {
            _model.Says($"Reply {i}.");
            await Provider().SendAsync(chat.Id, $"Turn {i}.");
        }

        var page = Page("/search rain");
        await page.OnPostSendAsync(chat.Id, CancellationToken.None);

        var match = page.Matches.ShouldNotBeNull().ShouldHaveSingleItem();
        match.Number.ShouldBe(1);
        page.LinkTo(match.Number).ShouldBe($"/story/{chat.Id}?all=true#t1");
        page.LinkTo(page.Hidden + 1).ShouldBe($"/story/{chat.Id}#t{page.Hidden + 1}");
        page.LinkTo(page.Hidden + page.Shown.Count).ShouldBe($"/story/{chat.Id}#latest");
    }

    [Fact]
    public async Task Carry_on_adds_a_reply_with_nothing_from_the_reader_and_moves_on_to_it()
    {
        var chat = await StoryAsync();
        var yours = (await TurnsAsync(chat)).Count(static m => m.Role == ChatRole.User);
        _model.Says("The lamp turns once more.");

        var result = await Page().OnPostContinueAsync(chat.Id, CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{chat.Id}#latest");
        var after = await TurnsAsync(chat);
        after.Count(static m => m.Role == ChatRole.User).ShouldBe(yours);
        after[^1].Text.ShouldContain("The lamp turns once more.");
    }

    [Fact]
    public async Task Reroll_replaces_the_newest_reply_and_moves_on_to_it()
    {
        var chat = await StoryAsync();
        var turns = (await TurnsAsync(chat)).Count;
        _model.Says("Elena does not look up.");

        var result = await Page().OnPostRerollAsync(chat.Id, RegenerateReason.Steer, "  colder  ", CancellationToken.None);

        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{chat.Id}#latest");

        var after = await TurnsAsync(chat);
        after.Count.ShouldBe(turns);
        after[^1].Text.ShouldBe("Elena does not look up.");
        _model.Calls[^1].ShouldContain(m => m.Content.Contains("colder"));
    }

    [Fact]
    public async Task Branch_copies_the_story_up_to_a_turn_and_goes_into_the_copy()
    {
        var chat = await StoryAsync();
        var first = (await TurnsAsync(chat))[0];

        var result = await Page().OnPostBranchAsync(chat.Id, first.Id, "Vardhal (2)", CancellationToken.None);

        var copy = (await Provider().ListAsync()).Single(c => c.Id != chat.Id);
        copy.Name.ShouldBe("Vardhal (2)");
        result.ShouldBeOfType<RedirectResult>().Url.ShouldBe($"/story/{copy.Id}#latest");
        (await Provider().GetMessagesAsync(copy.Id)).ShouldHaveSingleItem().Text.ShouldBe("I come in from the rain.");
        (await TurnsAsync(chat)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_branch_without_a_name_copies_nothing()
    {
        var chat = await StoryAsync();
        var page = Page();

        await page.OnPostBranchAsync(chat.Id, (await TurnsAsync(chat))[0].Id, "  ", CancellationToken.None);

        page.Error.ShouldNotBeNull().ShouldContain("Nothing was copied");
        (await Provider().ListAsync()).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Export_downloads_the_transcript_under_a_name_that_does_not_say_what_it_is()
    {
        var chat = await StoryAsync();

        var result = await Page().OnGetExportAsync(chat.Id, ExportFormat.PlainText, CancellationToken.None);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.FileDownloadName.ShouldStartWith("story-");
        file.FileDownloadName.ShouldEndWith(".txt");
        file.FileDownloadName.ShouldNotContain("Vardhal");
        Encoding.UTF8.GetString(file.FileContents).ShouldContain("Elena looks up from the log.");
    }

    [Fact]
    public async Task Only_a_reply_can_be_rerolled()
    {
        var chat = await StoryAsync();

        var page = Page();
        await page.OnGetAsync(chat.Id, all: false, CancellationToken.None);

        page.CanReroll.ShouldBeTrue();
        page.Shown[^1].Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public async Task A_story_that_does_not_exist_is_not_found()
        => (await Page("hello").OnPostSendAsync("nosuchstory", CancellationToken.None)).ShouldBeOfType<NotFoundResult>();
}
