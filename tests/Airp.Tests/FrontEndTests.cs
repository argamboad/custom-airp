using Airp.Proxy;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The label a front end puts in front of the reader's words comes off before anything else.
/// </summary>
/// <remarks>
/// Measured on the first real run: every turn Janitor sent arrived as <c>Allan: …</c>. Left on,
/// it is stored as part of a turn the reader never typed, and it hides a command — a
/// <c>/recap</c> arrives as <c>Allan: /recap</c> and would have been stored as a turn.
/// </remarks>
public class FrontEndTests
{
    [Fact]
    public void The_label_on_every_message_comes_off_the_newest()
        => FrontEnd.Unlabel(["Allan: *I sit down.*", "Allan: /recap"]).ShouldBe("/recap");

    [Fact]
    public void A_first_message_loses_its_label_too()
        => FrontEnd.Unlabel(["Allan: Hi there."]).ShouldBe("Hi there.");

    [Fact]
    public void A_name_with_a_space_is_still_a_label()
        => FrontEnd.Unlabel(["Allan McAllister: one", "Allan McAllister: two"]).ShouldBe("two");

    [Fact]
    public void Only_the_label_goes_and_the_rest_keeps_its_lines()
        => FrontEnd.Unlabel(["Allan: first line\nsecond line"]).ShouldBe("first line\nsecond line");

    [Fact]
    public void Prose_with_a_colon_after_the_label_keeps_it()
        => FrontEnd.Unlabel(["Allan: She said: go."]).ShouldBe("She said: go.");

    [Fact]
    public void A_word_and_a_colon_that_the_other_messages_do_not_share_is_prose()
        => FrontEnd.Unlabel(["*I wait.*", "Note: the door is open."]).ShouldBe("Note: the door is open.");

    [Fact]
    public void A_message_with_no_label_is_left_alone()
        => FrontEnd.Unlabel(["*I wait.*"]).ShouldBe("*I wait.*");
}
