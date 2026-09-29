using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Domain.Conversations;
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

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private StoryModel Page(string? draft = null) => new(Provider(), TestOptions.Default()) { Draft = draft };

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
    public async Task An_answer_that_is_not_a_turn_is_shown_on_the_page_and_nowhere_else()
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
