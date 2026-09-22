using Airp.Terminal;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// What build this is, and how you ask it.
/// </summary>
/// <remarks>
/// The number used to be a literal in the build properties that nothing read and nothing
/// showed, so a tool installed from a working tree was indistinguishable from a release and
/// a release cut from a later tag still called itself 1.0.0. It is the nearest v* tag now,
/// stamped at build time.
/// </remarks>
public class VersionTests
{
    [Fact]
    public void The_build_stamps_a_version()
    {
        AppVersion.Full.ShouldNotBeNullOrWhiteSpace();
        AppVersion.Full.ShouldNotBe("unknown");
    }

    /// <summary>
    /// A tag was actually found, rather than defaulted to.
    /// </summary>
    /// <remarks>
    /// <c>0.0.0</c> is what the version falls back to when there is no <c>v*</c> tag in reach,
    /// and the way that happens in practice is a shallow clone: a checkout with no tags builds
    /// happily and stamps a number that means nothing. Asserting the absence of the default
    /// rather than the presence of a particular number is what keeps this true after the next
    /// release.
    /// </remarks>
    [Fact]
    public void The_version_came_from_a_tag_rather_than_the_fallback()
        => AppVersion.Short.ShouldNotStartWith("0.0.0");

    /// <summary>
    /// The short form is the release number and nothing else.
    /// </summary>
    /// <remarks>
    /// It goes in the header, where the commit would be forty characters of noise pinned to
    /// the right of every screen.
    /// </remarks>
    [Fact]
    public void The_short_form_drops_the_commit()
    {
        AppVersion.Short.ShouldNotContain("+");
        AppVersion.Full.ShouldStartWith(AppVersion.Short);
    }

    /// <summary>
    /// A commit is abbreviated, never printed whole.
    /// </summary>
    /// <remarks>
    /// The SDK appends all forty characters. Seven is what git shows, and what fits.
    /// </remarks>
    [Fact]
    public void A_commit_is_abbreviated()
    {
        if (!AppVersion.Full.Contains('+', StringComparison.Ordinal))
        {
            // Built off the tagged commit, so there is no build metadata to abbreviate.
            return;
        }

        AppVersion.Full[(AppVersion.Full.IndexOf('+', StringComparison.Ordinal) + 1)..]
            .Length.ShouldBe(7);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("VERSION")]
    public void The_version_is_answered_without_building_a_host(string argument)
        => Airp.Terminal.Program.Immediate([argument]).ShouldBe(Airp.Terminal.Program.EarlyExit.Version);

    /// <summary>
    /// A leading flag still reaches the branch that handles it.
    /// </summary>
    /// <remarks>
    /// Positional stops at the first argument beginning with a dash, so a line that starts
    /// with one resolved to no command and fell through to the default. 'airp --help' started
    /// the terminal interface rather than printing the usage, and had done since the check was
    /// written; '--version' would have landed in the same hole.
    /// </remarks>
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void The_usage_is_answered_whether_it_is_asked_as_a_flag_or_a_command(string argument)
        => Airp.Terminal.Program.Immediate([argument]).ShouldBe(Airp.Terminal.Program.EarlyExit.Usage);

    [Fact]
    public void No_arguments_asks_for_neither()
        => Airp.Terminal.Program.Immediate([]).ShouldBe(Airp.Terminal.Program.EarlyExit.None);

    /// <summary>An ordinary command is not diverted by a flag that merely looks similar.</summary>
    [Theory]
    [InlineData("audit")]
    [InlineData("new Vardhal --speaker Elena")]
    [InlineData("ask --model helpful-model")]
    public void An_ordinary_command_is_dispatched(string line)
        => Airp.Terminal.Program.Immediate(line.Split(' ')).ShouldBe(Airp.Terminal.Program.EarlyExit.None);
}
