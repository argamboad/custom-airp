using Airp.Application.Text;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Reading the conventions a reply is written in, so the terminal can draw them.
/// </summary>
/// <remarks>
/// The rule that matters most is what happens when a marker does not close. Swallowing the
/// rest of the paragraph is the failure that would actually be noticed: a reply cut off
/// mid-action, or a stray asterisk in a sentence, would grey out everything after it.
/// </remarks>
public class ProseFormatTests
{
    private static string Styled(string line, ProseKind kind)
    {
        var formatted = ProseFormat.Format(line);

        return string.Concat(formatted.Runs
            .Where(r => r.Kind == kind)
            .Select(r => formatted.Text.Substring(r.Start, r.Length)));
    }

    [Fact]
    public void A_single_asterisk_run_is_an_action_and_loses_its_markers()
    {
        // Single asterisks outnumber double ones about ten to one in this project's own
        // transcripts, so this is the common case rather than the fallback.
        var formatted = ProseFormat.Format("*She looks up from the piano.*");

        formatted.Text.ShouldBe("She looks up from the piano.");
        formatted.Runs.ShouldHaveSingleItem().Kind.ShouldBe(ProseKind.Action);
    }

    [Fact]
    public void A_double_asterisk_run_is_an_action_too()
    {
        var formatted = ProseFormat.Format("**She looks up.**");

        formatted.Text.ShouldBe("She looks up.");
        formatted.Runs.ShouldHaveSingleItem().Kind.ShouldBe(ProseKind.Action);
    }

    [Fact]
    public void Quoted_speech_keeps_its_words_and_loses_its_quotes()
    {
        var formatted = ProseFormat.Format("\"You are late,\" she says.");

        formatted.Text.ShouldBe("You are late, she says.");
        Styled("\"You are late,\" she says.", ProseKind.Dialogue).ShouldBe("You are late,");
    }

    [Fact]
    public void Curly_quotes_are_read_the_same_way()
    {
        ProseFormat.Format("“You are late,” she says.").Text.ShouldBe("You are late, she says.");
    }

    [Fact]
    public void A_reply_in_the_shape_these_actually_arrive_in_comes_apart_correctly()
    {
        const string line = "*She closes the lid.* \"You are late.\" *The room is very quiet.*";

        var formatted = ProseFormat.Format(line);

        formatted.Text.ShouldBe("She closes the lid. You are late. The room is very quiet.");
        Styled(line, ProseKind.Action).ShouldBe("She closes the lid.The room is very quiet.");
        Styled(line, ProseKind.Dialogue).ShouldBe("You are late.");
    }

    [Fact]
    public void An_unclosed_asterisk_stays_literal_rather_than_greying_the_rest()
    {
        // The failure worth guarding: a reply that was cut off mid-action would otherwise dim
        // everything after the opening marker.
        var formatted = ProseFormat.Format("*She reaches for the door and then");

        formatted.Text.ShouldBe("*She reaches for the door and then");
        formatted.Runs.ShouldAllBe(r => r.Kind == ProseKind.Narration);
    }

    [Fact]
    public void An_unclosed_quote_stays_literal_too()
    {
        ProseFormat.Format("\"You are late").Text.ShouldBe("\"You are late");
    }

    [Fact]
    public void Arithmetic_and_spaced_asterisks_are_not_actions()
    {
        // Without the no-leading-space rule, "2 * 3 * 4" reads as an action and half the
        // message goes grey.
        var formatted = ProseFormat.Format("It cost 2 * 3 * 4 in the end.");

        formatted.Text.ShouldBe("It cost 2 * 3 * 4 in the end.");
        formatted.Runs.ShouldAllBe(r => r.Kind == ProseKind.Narration);
    }

    [Fact]
    public void A_lone_asterisk_inside_a_double_run_does_not_close_it()
    {
        var formatted = ProseFormat.Format("**she said 3*4 and left**");

        formatted.Text.ShouldBe("she said 3*4 and left");
        formatted.Runs.ShouldHaveSingleItem().Kind.ShouldBe(ProseKind.Action);
    }

    [Fact]
    public void The_runs_cover_the_stripped_text_exactly_and_in_order()
    {
        // The renderer walks these to paint a wrapped segment, so a gap would drop characters
        // off the screen and an overlap would draw them twice.
        const string line = "Before *the middle* and \"the end\" after.";

        var formatted = ProseFormat.Format(line);

        var at = 0;
        foreach (var run in formatted.Runs)
        {
            run.Start.ShouldBe(at);
            at += run.Length;
        }

        at.ShouldBe(formatted.Text.Length);
    }

    [Fact]
    public void An_empty_line_formats_to_nothing()
    {
        var formatted = ProseFormat.Format(string.Empty);

        formatted.Text.ShouldBeEmpty();
        formatted.Runs.ShouldBeEmpty();
    }

    [Fact]
    public void Empty_markers_are_left_alone()
    {
        ProseFormat.Format("**").Text.ShouldBe("**");
        ProseFormat.Format("\"\"").Text.ShouldBe("\"\"");
    }
}
