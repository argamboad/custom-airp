using Airp.Domain.Conversations;
using Airp.Proxy;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers the one problem the proxy has that the terminal does not.
/// </summary>
/// <remarks>
/// Getting this wrong writes a turn into somebody else's conversation, and the store is
/// append-only, so it stays wrong. Every case here is therefore about refusing rather than
/// guessing.
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

    private static readonly IReadOnlyDictionary<string, string> Openings =
        new Dictionary<string, string>
        {
            ["aaa111"] = "No esperaba encontrar a alguien en la playa a esta hora.",
            ["bbb222"] = "So what happened out there today?",
        };

    [Fact]
    public void A_tag_names_the_conversation_outright()
    {
        var resolved = SessionResolver.Resolve(
            "You are a character. [[rp:bbb222]] Stay in scene.",
            "anything at all",
            Two,
            Openings);

        resolved.ConversationId.ShouldBe("bbb222");
        resolved.How.ShouldBe(SessionMatch.Tag);
    }

    [Theory]
    [InlineData("[[rp:aaa111]]")]
    [InlineData("[[ rp : aaa111 ]]")]
    [InlineData("[[RP:aaa111]]")]
    public void The_tag_is_read_loosely_enough_to_survive_being_typed_by_hand(string tag)
        => SessionResolver.Resolve(tag, null, Two, Openings).ConversationId.ShouldBe("aaa111");

    [Fact]
    public void A_tag_naming_nothing_fails_rather_than_falling_back()
    {
        // Falling through here would write into whichever conversation the other strategies
        // liked, which is the opposite of what someone who typed an explicit id wanted.
        var resolved = SessionResolver.Resolve(
            "[[rp:zzz999]] Elena is here.",
            "No esperaba encontrar a alguien en la playa a esta hora.",
            Two,
            Openings);

        resolved.ConversationId.ShouldBeNull();
    }

    [Fact]
    public void A_character_name_resolves_when_only_one_conversation_has_it()
    {
        var resolved = SessionResolver.Resolve(
            "You are Elena, a mercenary in Vardhal.",
            "anything",
            Two,
            Openings);

        resolved.ConversationId.ShouldBe("aaa111");
        resolved.How.ShouldBe(SessionMatch.Speaker);
    }

    [Fact]
    public void Two_conversations_with_the_same_character_are_ambiguous_not_a_coin_toss()
    {
        IReadOnlyList<Chat> both = [Chat("aaa111", "One", "Elena"), Chat("ccc333", "Two", "Elena")];

        var resolved = SessionResolver.Resolve("You are Elena.", null, both, Openings);

        resolved.ConversationId.ShouldBeNull();
        resolved.Ambiguous.ShouldBeTrue();
    }

    [Fact]
    public void The_opening_turn_identifies_a_transcript_when_nothing_else_does()
    {
        // A front end that truncates keeps the start of a scene long after the middle is gone.
        var resolved = SessionResolver.Resolve(
            "Some framing with no character name in it.",
            "No esperaba encontrar a alguien en la playa a esta hora.",
            Two,
            Openings);

        resolved.ConversationId.ShouldBe("aaa111");
        resolved.How.ShouldBe(SessionMatch.Opening);
    }

    [Fact]
    public void An_opening_survives_being_reformatted_in_transit()
    {
        // Re-wrapped lines and swapped punctuation do not make it a different message.
        var resolved = SessionResolver.Resolve(
            "no character name here",
            "No esperaba encontrar\na alguien en la playa, a esta hora...",
            Two,
            Openings);

        resolved.ConversationId.ShouldBe("aaa111");
    }

    [Fact]
    public void Nothing_recognisable_resolves_to_nothing()
    {
        var resolved = SessionResolver.Resolve("Hello there.", "Hello there.", Two, Openings);

        resolved.ConversationId.ShouldBeNull();
        resolved.How.ShouldBe(SessionMatch.None);
        resolved.Ambiguous.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_store_resolves_to_nothing_rather_than_throwing()
        => SessionResolver.Resolve("[[rp:aaa111]]", "x", [], new Dictionary<string, string>())
            .ConversationId.ShouldBeNull();

    [Fact]
    public void The_tag_wins_over_a_character_name_that_points_elsewhere()
    {
        // The reader said which one. Inference does not get to overrule that.
        var resolved = SessionResolver.Resolve(
            "You are Blake. [[rp:aaa111]]",
            null,
            Two,
            Openings);

        resolved.ConversationId.ShouldBe("aaa111");
    }
}
