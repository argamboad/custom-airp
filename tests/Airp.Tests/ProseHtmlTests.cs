using Airp.Web;
using Microsoft.AspNetCore.Html;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A turn in the browser reads the way it reads in the terminal, and nothing in it runs.
/// </summary>
public class ProseHtmlTests
{
    private static string Html(string text)
    {
        using var writer = new StringWriter();
        ProseHtml.Render(text).WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }

    [Fact]
    public void Actions_are_italic_and_speech_plain_and_both_lose_their_markers()
    {
        var html = Html("*She closes the lid.* \"You are late.\"");

        html.ShouldContain("<em>She closes the lid.</em>");
        html.ShouldContain("You are late.");
        html.ShouldNotContain("*");
        html.ShouldNotContain("&quot;You");
    }

    [Fact]
    public void Blank_lines_make_paragraphs_and_single_breaks_stay_breaks()
        => Html("First line.\nSecond line.\n\nNew paragraph.")
            .ShouldBe("<p>First line.<br>Second line.</p><p>New paragraph.</p>");

    [Fact]
    public void Nothing_in_a_turn_is_markup()
    {
        // The stored words are whatever a model or a reader wrote.
        var html = Html("<script>alert(1)</script> *<b>bold</b>*");

        html.ShouldNotContain("<script>");
        html.ShouldNotContain("<b>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void An_empty_turn_is_empty()
        => Html(string.Empty).ShouldBe(string.Empty);
}
