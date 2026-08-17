using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain;
using Airp.Domain.Conversations;
using Shouldly;

namespace Airp.Tests;

public class ChatServiceTests
{
    private static Chat Make(string id, string name, string? latest = null) => new()
    {
        Id = id,
        Name = name,
        LatestMessage = latest,
    };

    private static (ChatService Service, IChatProvider Provider) Build()
    {
        var provider = Substitute.For<IChatProvider>();
        var service = new ChatService(provider, NullLogger<ChatService>.Instance);

        return (service, provider);
    }

    [Fact]
    public async Task GetAsync_FetchesOnceThenServesTheHeldList()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>()).Returns([Make("1", "One")]);

        await service.GetAsync();
        await service.GetAsync();

        // The second call is answered from the list already in hand, not another read.
        await provider.Received(1).ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_RaisesChanged()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>()).Returns([Make("1", "One")]);

        IReadOnlyList<Chat>? seen = null;
        service.Changed += (_, chats) => seen = chats;

        await service.RefreshAsync();

        seen.ShouldNotBeNull();
        seen.ShouldHaveSingleItem().Name.ShouldBe("One");
    }

    [Fact]
    public async Task RefreshAsync_WhenTheReadFailsButAListIsHeld_KeepsServingIt()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>()).Returns([Make("1", "One")]);
        await service.RefreshAsync();

        provider.ListAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException("down"));

        var result = await service.RefreshAsync();

        result.ShouldHaveSingleItem();
        service.Cached.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RefreshAsync_WhenTheReadFailsAndNothingIsHeld_Rethrows()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException("down"));

        await Should.ThrowAsync<ModelUnavailableException>(() => service.RefreshAsync());
    }

    [Fact]
    public async Task Filter_RanksByFuzzyRelevance()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>())
            .Returns([Make("1", "North Dock"), Make("2", "Elena and Ferrin"), Make("3", "Vardhal")]);

        await service.RefreshAsync();

        var hits = service.Filter("doc");

        hits[0].Name.ShouldBe("North Dock");
    }

    [Fact]
    public async Task GetAsync_ById_ServesTheHeldCopyWhenTheDetailFetchFails()
    {
        var (service, provider) = Build();
        provider.ListAsync(Arg.Any<CancellationToken>()).Returns([Make("1", "One")]);
        await service.RefreshAsync();

        provider.GetAsync("1", Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException("down"));

        var chat = await service.GetAsync("1");

        chat.ShouldNotBeNull();
        chat.Name.ShouldBe("One");
    }

    [Fact]
    public async Task GetAsync_RejectsABlankIdentifier()
    {
        var (service, _) = Build();

        await Should.ThrowAsync<ArgumentException>(() => service.GetAsync(" "));
    }

    [Fact]
    public void Merge_KeepsDetailTheListOmits()
    {
        IReadOnlyList<Chat> existing = [Make("1", "One", latest: "kept text")];
        IReadOnlyList<Chat> fetched = [Make("1", "One renamed")];

        var merged = ChatService.Merge(existing, fetched);

        merged.ShouldHaveSingleItem();
        merged[0].Name.ShouldBe("One renamed");
        merged[0].LatestMessage.ShouldBe("kept text");
    }

    [Fact]
    public void Merge_DropsChatsTheStoreNoLongerReturns()
    {
        IReadOnlyList<Chat> existing = [Make("1", "One"), Make("2", "Two")];
        IReadOnlyList<Chat> fetched = [Make("1", "One")];

        ChatService.Merge(existing, fetched).ShouldHaveSingleItem();
    }

    [Fact]
    public void Merge_WithNothingHeld_ReturnsTheFetchedListUnchanged()
    {
        IReadOnlyList<Chat> fetched = [Make("1", "One")];

        ChatService.Merge([], fetched).ShouldBeSameAs(fetched);
    }
}
