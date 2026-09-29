using System.Text.RegularExpressions;

namespace Airp.Proxy;

/// <summary>
/// Undoes what a front end does to the reader's words before sending them.
/// </summary>
public static partial class FrontEnd
{
    [GeneratedRegex(@"^([^\r\n:]{1,32}):[ \t]+")]
    private static partial Regex LabelPattern { get; }

    /// <summary>
    /// The reader's newest message without the <c>Name: </c> the front end put in front of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Janitor sends every one of the reader's messages as <c>Allan: …</c>, with their persona's
    /// name, measured on the first real run: each turn it sent arrived labelled, and the turns
    /// played in the terminal did not. Stored as it arrives, the label becomes part of a turn
    /// the reader never typed — and a command typed in Janitor, <c>/recap</c>, arrives as
    /// <c>Allan: /recap</c>, which does not start with a slash and would be stored as a turn.
    /// </para>
    /// <para>
    /// It is removed only when it cannot be prose: short, on the first line, and at the start of
    /// every message of the reader's in the request. The front end repeats its label on all of
    /// them; a line that merely begins with a word and a colon does not repeat itself across a
    /// conversation.
    /// </para>
    /// </remarks>
    /// <param name="userTurns">The reader's messages in the request, oldest first; at least one.</param>
    /// <returns>The newest of them, unlabelled.</returns>
    public static string Unlabel(IReadOnlyList<string> userTurns)
    {
        ArgumentNullException.ThrowIfNull(userTurns);

        var newest = userTurns[^1];

        if (LabelPattern.Match(newest) is not { Success: true } label)
        {
            return newest;
        }

        var name = label.Groups[1].Value + ":";

        return userTurns.All(turn => turn.StartsWith(name, StringComparison.Ordinal))
            ? newest[label.Length..]
            : newest;
    }
}
