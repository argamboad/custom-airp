using Airp.Terminal.Ui;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Decoding SGR mouse reports, and above all never handing one back as an Escape key.
/// </summary>
/// <remarks>
/// A tap is a press and then a release. Only the press was recognised, so the release came
/// back as "not a mouse report", and the shell took the escape that began it for the Escape
/// key — which is Back. Tapping a chat opened it and the release closed it again.
/// </remarks>
public class MouseInputTests
{
    /// <summary>Decodes what follows an escape that has already been read.</summary>
    private static MouseEvent? Decode(string afterEscape)
    {
        var input = new Queue<char>(afterEscape);
        return MouseInput.TryDecode(() => input.Count > 0 ? input.Dequeue() : null);
    }

    [Fact]
    public void A_press_is_a_click_where_it_landed()
        => Decode("[<0;12;5M").ShouldBe(new MouseEvent(MouseEventKind.LeftClick, 12, 5));

    [Fact]
    public void The_release_that_ends_a_tap_is_ignored_rather_than_refused()
        => Decode("[<0;12;5m").ShouldNotBeNull().Kind.ShouldBe(MouseEventKind.Ignored);

    [Fact]
    public void The_wheel_away_scrolls_up()
        => Decode("[<64;1;1M").ShouldNotBeNull().Kind.ShouldBe(MouseEventKind.ScrollUp);

    [Fact]
    public void The_wheel_towards_scrolls_down()
        => Decode("[<65;1;1M").ShouldNotBeNull().Kind.ShouldBe(MouseEventKind.ScrollDown);

    [Theory]
    [InlineData("[<1;12;5M")] // middle button
    [InlineData("[<0;12M")] // malformed
    [InlineData("[<0;12;5")] // cut short
    public void Any_report_once_the_prefix_is_read_is_never_an_escape(string report)
        => Decode(report).ShouldNotBeNull().Kind.ShouldBe(MouseEventKind.Ignored);

    [Theory]
    [InlineData("")]
    [InlineData("[A")]
    public void What_is_not_a_mouse_report_is_left_for_the_keyboard(string input)
        => Decode(input).ShouldBeNull();
}
