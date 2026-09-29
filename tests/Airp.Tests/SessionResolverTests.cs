using Airp.Domain.Conversations;
using Airp.Proxy;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers the one problem the proxy has that the terminal does not.
/// </summary>
/// <remarks>
/// Getting this wrong writes a turn into somebody else's conversation, and the store is
/// append-only, so it stays wrong — and the turn is billed. Only a tag the reader wrote says where
/// a request goes (ADR 0017); every case without one is a refusal.
/// </remarks>
public class SessionResolverTests
{
    private static Chat Chat(string id, string name, string? speaker = null) => new()
    {
        Id = id,
        Name = name,
        Speaker = speaker,
    };

    private static readonly IReadOnlyList<Chat> Two =
    [
        Chat("aaa111", "Vardhal", "Elena"),
        Chat("bbb222", "Harbor", "Blake"),
    ];

    [Fact]
    public void A_tag_names_the_conversation_outright()
    {
        var resolved = SessionResolver.Resolve("You are a character. [[rp:bbb222]] Stay in scene.", Two);

        resolved.ConversationId.ShouldBe("bbb222");
        resolved.Tagged.ShouldBeTrue();
    }

    [Theory]
    [InlineData("[[rp:aaa111]]")]
    [InlineData("[[ rp : aaa111 ]]")]
    [InlineData("[[RP:aaa111]]")]
    public void The_tag_is_read_loosely_enough_to_survive_being_typed_by_hand(string tag)
        => SessionResolver.Resolve(tag, Two).ConversationId.ShouldBe("aaa111");

    [Fact]
    public void A_tag_naming_nothing_fails_and_says_it_was_a_tag()
    {
        // Two different fixes: a missing tag is added, a wrong one is corrected. The refusal
        // says which, and neither falls back to writing somewhere else.
        var resolved = SessionResolver.Resolve("[[rp:zzz999]] Elena is here.", Two);

        resolved.ConversationId.ShouldBeNull();
        resolved.Tagged.ShouldBeTrue();
    }

    [Fact]
    public void A_character_name_alone_is_not_enough()
    {
        // This used to resolve, because only one conversation had Elena. A test chat in the
        // front end that mentioned her would then have written a billed turn into that story.
        var resolved = SessionResolver.Resolve("You are Elena, a mercenary in Vardhal.", Two);

        resolved.ConversationId.ShouldBeNull();
        resolved.Tagged.ShouldBeFalse();
    }

    [Fact]
    public void An_opening_that_matches_a_story_is_not_enough_either()
        => SessionResolver.Resolve("So what happened out there today?", Two).ConversationId.ShouldBeNull();

    [Fact]
    public void An_empty_store_resolves_to_nothing_rather_than_throwing()
        => SessionResolver.Resolve("[[rp:aaa111]]", []).ConversationId.ShouldBeNull();

    [Fact]
    public void The_tag_wins_over_a_character_name_that_points_elsewhere()
    {
        // The reader said which one; a name in the text does not get to overrule that.
        var resolved = SessionResolver.Resolve("You are Blake. [[rp:aaa111]]", Two);

        resolved.ConversationId.ShouldBe("aaa111");
    }
}
