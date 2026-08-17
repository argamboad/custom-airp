using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers how the command line is split into a command and its flags.
/// </summary>
/// <remarks>
/// Small surface, but it is the first thing every invocation goes through, and it failed
/// silently: a flag's value sitting where a command was looked for produced "Unknown command"
/// for a line that was perfectly valid.
/// </remarks>
public class CommandLineTests
{
    /// <summary>Mirrors <c>Program.Positional</c>, which is private to the entry point.</summary>
    private static string[] Positional(string[] args)
        => [.. args.TakeWhile(static a => !a.StartsWith('-'))];

    private static string Command(string[] args)
        => Positional(args).FirstOrDefault()?.ToLowerInvariant() ?? "run";

    [Fact]
    public void No_arguments_runs_the_terminal()
        => Command([]).ShouldBe("run");

    [Theory]
    [InlineData("--provider", "local")]
    [InlineData("--profile", "work")]
    [InlineData("--theme", "Light")]
    public void A_flags_value_is_not_mistaken_for_a_command(string flag, string value)
    {
        // The reported failure: 'airp --provider local' answered "Unknown command 'local'".
        Command([flag, value]).ShouldBe("run");
    }

    [Fact]
    public void A_leading_command_still_wins()
        => Command(["export", "--format", "md"]).ShouldBe("export");

    [Fact]
    public void A_commands_own_argument_stays_positional()
    {
        var positional = Positional(["new", "Vardhal", "--speaker", "Elena"]);

        positional.ShouldBe(["new", "Vardhal"]);
    }

    [Fact]
    public void Flag_values_never_leak_into_the_positional_list()
    {
        var positional = Positional(["secret", "set", "--provider", "local", "NAME"]);

        // 'NAME' is past the first flag, so it is not positional. Callers that need a value
        // after a flag ask for it by name instead.
        positional.ShouldBe(["secret", "set"]);
    }

    /// <summary>Mirrors how <c>send</c> finds the message it was given.</summary>
    private static string? MessageFor(string[] args)
        => Positional(args).ElementAtOrDefault(1) ?? ValueAfter(args, "--text");

    private static string? ValueAfter(string[] args, string flag)
    {
        var at = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    [Fact]
    public void A_message_before_the_flags_is_read_positionally()
        => MessageFor(["send", "Where are we?", "--chat", "QA"]).ShouldBe("Where are we?");

    [Fact]
    public void A_message_after_a_flag_has_to_be_named()
    {
        // The consequence of reading positionals only up to the first flag. Worth pinning
        // rather than discovering: this exact shape reads as "nothing to send" otherwise, and
        // the line looks perfectly reasonable to whoever typed it.
        MessageFor(["send", "--chat", "QA", "Where are we?"]).ShouldBeNull();
        MessageFor(["send", "--chat", "QA", "--text", "Where are we?"]).ShouldBe("Where are we?");
    }

    [Fact]
    public void Send_is_the_command_either_way()
    {
        Command(["send", "Where are we?"]).ShouldBe("send");
        Command(["send", "--chat", "QA", "--text", "Where are we?"]).ShouldBe("send");
    }
}
