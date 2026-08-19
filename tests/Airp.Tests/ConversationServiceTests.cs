using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Application.Text;
using Airp.Domain;
using Airp.Domain.Conversations;
using Shouldly;

namespace Airp.Tests;

public class ConversationServiceTests
{
    private static ChatMessage Message(string id, ChatRole role, string text, int minute) => new()
    {
        Id = id,
        ConversationId = "chat-1",
        Role = role,
        Text = text,
        SentAtUtc = new DateTimeOffset(2026, 8, 5, 12, minute, 0, TimeSpan.Zero),
    };

    private static (ConversationService Service, IConversationProvider Provider) Build()
    {
        var provider = Substitute.For<IConversationProvider>();
        var service = new ConversationService(provider, NullLogger<ConversationService>.Instance);

        return (service, provider);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsWhatTheProviderHolds()
    {
        var (service, provider) = Build();
        provider.GetMessagesAsync("chat-1", Arg.Any<CancellationToken>())
            .Returns([Message("m1", ChatRole.User, "hello", 0)]);

        var messages = await service.GetMessagesAsync("chat-1");

        messages.ShouldHaveSingleItem().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task SendAsync_ReturnsTheTurnsTheProviderAdded()
    {
        var (service, provider) = Build();
        provider.SendAsync("chat-1", "hello", Arg.Any<string?>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .Returns([
                Message("m1", ChatRole.User, "hello", 0),
                Message("m2", ChatRole.Assistant, "hi there", 1),
            ]);

        var added = await service.SendAsync("chat-1", "hello");

        added.Count.ShouldBe(2);
        added[1].Role.ShouldBe(ChatRole.Assistant);
    }

    [Fact]
    public async Task SendAsync_RejectsAnEmptyMessage()
    {
        var (service, _) = Build();

        await Should.ThrowAsync<ArgumentException>(() => service.SendAsync("chat-1", "   "));
    }

    [Fact]
    public async Task SendAsync_LetsAFailurePropagateSoTheDraftIsKept()
    {
        // The provider persisted the user's turn before the model failed — that is its
        // invariant, tested where it lives. What matters here is that the failure reaches the
        // caller unchanged, so the composer can say the right thing about the draft.
        var (service, provider) = Build();
        provider.SendAsync("chat-1", "hello", Arg.Any<string?>(), Arg.Any<IProgress<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ReplyTimeoutException("no reply"));

        await Should.ThrowAsync<ReplyTimeoutException>(() => service.SendAsync("chat-1", "hello"));
    }

    [Fact]
    public async Task DeleteFromAsync_RejectsBlankArguments()
    {
        var (service, _) = Build();

        await Should.ThrowAsync<ArgumentException>(() => service.DeleteFromAsync(" ", "m1"));
        await Should.ThrowAsync<ArgumentException>(() => service.DeleteFromAsync("chat-1", " "));
    }

    [Fact]
    public async Task DeleteFromAsync_ReturnsWhatSurvived()
    {
        var (service, provider) = Build();
        provider.DeleteFromAsync("chat-1", "m2", Arg.Any<CancellationToken>())
            .Returns([Message("m1", ChatRole.User, "kept", 0)]);

        var remaining = await service.DeleteFromAsync("chat-1", "m2");

        remaining.ShouldHaveSingleItem().Id.ShouldBe("m1");
    }

    [Fact]
    public void Merge_DeduplicatesAndOrdersChronologically()
    {
        IReadOnlyList<ChatMessage> existing = [Message("m2", ChatRole.User, "second", 1), Message("m1", ChatRole.Assistant, "first", 0)];
        IReadOnlyList<ChatMessage> added = [Message("m3", ChatRole.Assistant, "third", 2)];

        var merged = ChatTranscript.Merge(existing, added);

        merged.Select(static m => m.Id).ShouldBe(["m1", "m2", "m3"]);
    }

    [Fact]
    public void Merge_PrefersTheLongerCopyOfAStreamedReply()
    {
        // A reply is captured mid-stream and again once complete; the complete one wins.
        IReadOnlyList<ChatMessage> existing = [Message("m1", ChatRole.Assistant, "partial", 0)];
        IReadOnlyList<ChatMessage> added = [Message("m1", ChatRole.Assistant, "the whole finished reply", 0)];

        ChatTranscript.Merge(existing, added)
            .ShouldHaveSingleItem()
            .Text.ShouldBe("the whole finished reply");
    }

    [Fact]
    public void Merge_KeepsTheLongerCopyEvenWhenTheShorterArrivesLast()
    {
        IReadOnlyList<ChatMessage> existing = [Message("m1", ChatRole.Assistant, "the whole finished reply", 0)];
        IReadOnlyList<ChatMessage> added = [Message("m1", ChatRole.Assistant, "partial", 0)];

        ChatTranscript.Merge(existing, added)
            .ShouldHaveSingleItem()
            .Text.ShouldBe("the whole finished reply");
    }

    [Fact]
    public void Merge_WithNothingAdded_ReturnsWhatWasThere()
        => ChatTranscript.Merge([Message("m1", ChatRole.User, "only", 0)], []).Count.ShouldBe(1);
}
