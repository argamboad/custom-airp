namespace Airp.Application.Text;

/// <summary>What a command costs the reader, which is the first thing they want to know.</summary>
public enum CommandCost
{
    /// <summary>Reads what is already on disk. No model call, no money.</summary>
    Free,

    /// <summary>Calls the model. This spends credits.</summary>
    Billed,

    /// <summary>Writes to the conversation's own state, but calls no model.</summary>
    Write,
}

/// <summary>Whether a command needs something typed after it.</summary>
public enum CommandArgument
{
    /// <summary>Takes nothing; anything typed after it is a mistake worth reporting.</summary>
    None,

    /// <summary>Refused without one, rather than run on an empty argument.</summary>
    Required,
}

/// <summary>One command the composer recognises.</summary>
/// <param name="Name">The word after the slash, lower case.</param>
/// <param name="Argument">Whether it needs an argument.</param>
/// <param name="Cost">What running it costs.</param>
/// <param name="Usage">How it is typed, for the help listing.</param>
/// <param name="Summary">One line saying what it does.</param>
/// <param name="NeedsStore">
/// Whether it reads or writes the local store directly. The three that steer a turn and the
/// two that only read the screen go through the provider seam like everything else in the
/// terminal, and would keep working against a future backend; the rest would not, and say so
/// rather than failing.
/// </param>
public sealed record SlashCommand(
    string Name,
    CommandArgument Argument,
    CommandCost Cost,
    string Usage,
    string Summary,
    bool NeedsStore = false);

/// <summary>What the composer's text turned out to be.</summary>
public enum SlashParseKind
{
    /// <summary>Ordinary prose. Send it.</summary>
    Message,

    /// <summary>A command this client knows.</summary>
    Command,

    /// <summary>Something that looks like a command and is not one. Refuse it.</summary>
    Unknown,
}

/// <summary>The result of reading the composer.</summary>
/// <param name="Kind">Which of the three it is.</param>
/// <param name="Command">The command, when there is one.</param>
/// <param name="Text">
/// The message to send, the command's argument, or the unrecognised name — whichever the
/// <paramref name="Kind" /> calls for.
/// </param>
public readonly record struct SlashParse(SlashParseKind Kind, SlashCommand? Command, string Text);

/// <summary>
/// The commands the composer understands, and the reading of a line into one.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the alternative is typing directions into the message itself, and a
/// message is permanent. <c>(OOC: skip to the evening)</c> reaches the model, is stored
/// forever, is counted in every later prompt, gets embedded for retrieval and may be
/// summarised as something that happened. A command routes the same words into the prompt
/// layer they belong in — or into no prompt at all — and leaves the transcript alone.
/// </para>
/// <para>
/// <strong>An unrecognised command is refused, never sent.</strong> A typo would otherwise
/// cost the same as the message it was meant to be and land in the transcript as nonsense the
/// character has to react to, and the append-only rule means it could not be taken back. The
/// escape for prose that genuinely starts with a slash is to double it.
/// </para>
/// <para>All members are pure and thread-safe.</para>
/// </remarks>
public static class SlashCommands
{
    /// <summary>Every command, in the order the help lists them.</summary>
    public static IReadOnlyList<SlashCommand> All { get; } =
    [
        new("do", CommandArgument.Required, CommandCost.Billed, "/do <direction>",
            "Steer this turn. Alone it writes the next beat; above your prose it steers that message"),
        new("ask", CommandArgument.Required, CommandCost.Billed, "/ask <question>",
            "Ask about the story out of character. The answer is shown, not stored", NeedsStore: true),
        new("focus", CommandArgument.Required, CommandCost.Billed, "/focus <who>",
            "Hand the next turn to a named character"),

        new("card", CommandArgument.None, CommandCost.Free, "/card",
            "The character definition this conversation is using", NeedsStore: true),
        new("persona", CommandArgument.None, CommandCost.Free, "/persona",
            "Who you are playing, and which file it came from", NeedsStore: true),
        new("facts", CommandArgument.None, CommandCost.Free, "/facts",
            "What is being injected as true right now", NeedsStore: true),
        new("trackers", CommandArgument.None, CommandCost.Free, "/trackers",
            "The meters this story keeps, and their values", NeedsStore: true),
        new("audit", CommandArgument.None, CommandCost.Free, "/audit",
            "What the recent turns cost, layer by layer", NeedsStore: true),
        new("cost", CommandArgument.None, CommandCost.Free, "/cost",
            "What this story has cost, and what went on replies you rerolled away", NeedsStore: true),
        new("search", CommandArgument.Required, CommandCost.Free, "/search <words>",
            "Find the turn where something was actually said"),
        new("help", CommandArgument.None, CommandCost.Free, "/help",
            "This list"),

        new("fact", CommandArgument.Required, CommandCost.Write, "/fact <statement>",
            "Record something as true. Pinned: the extractor cannot retire it", NeedsStore: true),
        new("tracker", CommandArgument.Required, CommandCost.Write, "/tracker <name> <value>",
            "Set a meter's value", NeedsStore: true),
    ];

    /// <summary>Finds a command by name.</summary>
    /// <param name="name">The name, without the slash. Case does not matter.</param>
    /// <returns>The command, or <see langword="null"/> when nothing is called that.</returns>
    public static SlashCommand? Find(string? name)
        => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names starting with what has been typed, for the completion rail.</summary>
    /// <param name="prefix">What follows the slash so far; empty offers everything.</param>
    /// <returns>The matching commands, in listing order.</returns>
    public static IReadOnlyList<SlashCommand> Matching(string? prefix)
        => string.IsNullOrEmpty(prefix)
            ? All
            : [.. All.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Reads what is in the composer.
    /// </summary>
    /// <remarks>
    /// Only a slash in the very first column counts. A slash anywhere else is punctuation —
    /// <c>and/or</c>, a date, a closing tag — and prose is far more common than commands, so
    /// the rule that costs nothing to remember is the one that only looks at the start.
    /// </remarks>
    /// <param name="text">The composer's contents.</param>
    /// <returns>What it is, and what to do with it.</returns>
    public static SlashParse Parse(string? text)
    {
        var composed = (text ?? string.Empty).Trim();

        if (composed.Length == 0 || composed[0] != '/')
        {
            return new SlashParse(SlashParseKind.Message, null, composed);
        }

        // A doubled slash is how you send prose that genuinely opens with one. Stripping the
        // first is the whole of it: what remains is a message like any other and is never
        // looked at again for commands.
        if (composed.StartsWith("//", StringComparison.Ordinal))
        {
            return new SlashParse(SlashParseKind.Message, null, composed[1..]);
        }

        // The name runs to the first whitespace, newline included: a command whose argument
        // begins on the next line is still that command.
        var end = 1;
        while (end < composed.Length && !char.IsWhiteSpace(composed[end]))
        {
            end++;
        }

        var name = composed[1..end];
        var argument = composed[end..].Trim();

        return Find(name) is { } command
            ? new SlashParse(SlashParseKind.Command, command, argument)
            : new SlashParse(SlashParseKind.Unknown, null, name);
    }

    /// <summary>
    /// Splits a <c>/do</c> argument into the direction and the message it steers, if any.
    /// </summary>
    /// <remarks>
    /// A blank line is the separator because it is what a writer already types between a note
    /// to themselves and the prose it applies to. With no blank line the whole thing is the
    /// direction and there is no message — which is the common case, and the one that has to
    /// be typed without thinking.
    /// </remarks>
    /// <param name="argument">Everything after <c>/do</c>.</param>
    /// <returns>The direction, and the message to send with it or empty for none.</returns>
    public static (string Direction, string Message) SplitDirection(string? argument)
    {
        var text = (argument ?? string.Empty).Trim();
        var separator = text.IndexOf("\n\n", StringComparison.Ordinal);

        return separator < 0
            ? (text, string.Empty)
            : (text[..separator].Trim(), text[(separator + 2)..].Trim());
    }
}
