using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Domain.Search;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Searching the words of every chat.
/// </summary>
/// <remarks>
/// The search reads transcripts through the conversation service, which under the local
/// adapter is the store itself. A chat whose transcript cannot be read is counted and
/// reported rather than skipped silently — a result that quietly covered less than it
/// appeared to would be worse than a smaller one that says so.
/// </remarks>
public class SearchServiceTests
{
    private static Chat Chat(string id, string name) => new() { Id = id, Name = name };

    private static ChatMessage Message(string id, ChatRole role, string text) => new()
    {
        Id = id,
        ConversationId = "c1",
        Role = role,
        Text = text,
        SentAtUtc = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero),
    };

    private static (SearchService Service, IConversationService Conversations) Build(params Chat[] chats)
    {
        var chatService = Substitute.For<IChatService>();
        chatService.GetAsync(Arg.Any<CancellationToken>()).Returns(chats);
        chatService.Cached.Returns(chats);

        var conversations = Substitute.For<IConversationService>();
        conversations
            .GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return (new SearchService(chatService, conversations), conversations);
    }

    private static void Holds(IConversationService conversations, string chatId, params ChatMessage[] messages)
        => conversations
            .GetMessagesAsync(chatId, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(messages);

    [Fact]
    public async Task SearchAsync_FindsTheWordsInsideMessages()
    {
        var (service, conversations) = Build(Chat("c1", "North Dock"));
        Holds(
            conversations,
            "c1",
            Message("m1", ChatRole.Assistant, "The rain has started again against the high windows."),
            Message("m2", ChatRole.User, "Nothing about weather here."));

        var results = await service.SearchAsync("rain");

        var hit = results.Hits.ShouldHaveSingleItem();
        hit.ChatId.ShouldBe("c1");
        hit.Scope.ShouldBe(SearchScope.Messages);
        hit.Snippet.ShouldContain("rain");
    }

    [Fact]
    public async Task SearchAsync_SaysWhoWroteTheMatchingMessage()
    {
        var (service, conversations) = Build(Chat("c1", "Blake"));
        Holds(conversations, "c1", Message("m1", ChatRole.User, "I said something memorable"));

        var hit = (await service.SearchAsync("memorable")).Hits.ShouldHaveSingleItem();

        hit.Speaker.ShouldBe("You");
        hit.SentAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task SearchAsync_ReportsChatsItCouldNotRead()
    {
        // A transcript that cannot be read is counted, not hidden. Silently skipping it would
        // make the result look complete when it is not.
        var (service, conversations) = Build(Chat("c1", "Readable"), Chat("c2", "Broken"));
        Holds(conversations, "c1", Message("m1", ChatRole.Assistant, "findable"));
        conversations
            .GetMessagesAsync("c2", Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException("unreadable"));

        var results = await service.SearchAsync("findable");

        results.ChatsSearched.ShouldBe(1);
        results.ChatsSkipped.ShouldBe(1);
        results.IsPartial.ShouldBeTrue();
    }

    [Fact]
    public async Task SearchAsync_WithEverythingReadable_IsNotPartial()
    {
        var (service, conversations) = Build(Chat("c1", "One"));
        Holds(conversations, "c1", Message("m1", ChatRole.Assistant, "here"));

        (await service.SearchAsync("here")).IsPartial.ShouldBeFalse();
    }

    [Fact]
    public async Task SearchAsync_MatchesAChatByName()
    {
        var (service, _) = Build(Chat("c1", "North Dock"));

        var results = await service.SearchAsync("dock");

        results.Hits.ShouldContain(static h => h.Scope == SearchScope.Names);
    }

    [Fact]
    public async Task SearchAsync_RanksNameMatchesAboveMessageMatches()
    {
        var (service, conversations) = Build(Chat("c1", "Rain"));
        Holds(conversations, "c1", Message("m1", ChatRole.Assistant, "rain again"));

        var hits = (await service.SearchAsync("rain")).Hits;

        hits[0].Scope.ShouldBe(SearchScope.Names);
    }

    [Fact]
    public async Task SearchAsync_RequiresALiteralMatchInsideMessages()
    {
        // Fuzzy matching across thousands of characters of prose hits nearly every message,
        // which is indistinguishable from finding nothing.
        var (service, conversations) = Build(Chat("c1", "Chat"));
        Holds(conversations, "c1", Message("m1", ChatRole.Assistant, "the rain in spain"));

        (await service.SearchAsync("rn")).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_SkipsNonDialoguePayloads()
    {
        var (service, conversations) = Build(Chat("c1", "Chat"));
        Holds(conversations, "c1", Message("m1", ChatRole.Data, "tracker: findme"));

        (await service.SearchAsync("findme")).Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task SearchAsync_WithAnEmptyQuery_ReturnsNothing()
        => (await Build(Chat("c1", "Chat")).Service.SearchAsync("  ")).Hits.ShouldBeEmpty();

    [Fact]
    public async Task SearchAsync_ScopedToNames_DoesNotReadTranscripts()
    {
        var (service, conversations) = Build(Chat("c1", "Student"));
        Holds(conversations, "c1", Message("m1", ChatRole.Assistant, "Student is also in here"));

        var results = await service.SearchAsync("Student", SearchScope.Names);

        results.Hits.ShouldHaveSingleItem().Scope.ShouldBe(SearchScope.Names);
        results.ChatsSearched.ShouldBe(0);
        await conversations.DidNotReceive()
            .GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BuildSnippet_FlattensLineBreaksAndTrimsAroundTheMatch()
    {
        var text = new string('x', 200) + "needle" + new string('y', 200);

        var snippet = SearchService.BuildSnippet(text, 200);

        snippet.ShouldStartWith("…");
        snippet.ShouldEndWith("…");
        snippet.ShouldContain("needle");
    }

    [Fact]
    public void BuildSnippet_LeavesAShortMessageWhole()
        => SearchService.BuildSnippet("a short line", 2).ShouldBe("a short line");
}
