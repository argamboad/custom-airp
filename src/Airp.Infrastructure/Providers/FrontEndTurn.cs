using System.Globalization;
using System.Text;
using Airp.Application.Text;
using Airp.Domain.Conversations;

namespace Airp.Infrastructure.Providers;

/// <summary>What a front end answers a message with, and whether it is a refusal.</summary>
/// <param name="Text">The reply to show, or the reason for refusing.</param>
/// <param name="Refused">Whether nothing was done, so the front end should show an error.</param>
/// <param name="Stored">
/// Whether the story gained a turn. A reply to a message or a <c>/do</c> did; an answer to
/// <c>/ask</c> or <c>/recap</c> is shown once and kept nowhere, so a page must show it rather
/// than expect to find it in the transcript.
/// </param>
public readonly record struct FrontEndOutcome(string Text, bool Refused, bool Stored = false);

/// <summary>
/// Carries out one message typed outside the terminal: a turn of the story, or one of the
/// composer's commands, or a refusal.
/// </summary>
/// <remarks>
/// <para>
/// The same commands the terminal's composer has, read by the same parser and carrying the
/// same directions, because a front end is a chat box and a chat box is where a reader types
/// them. Before this, <c>/ask what does she know?</c> typed in Janitor was stored as a turn of
/// the story — permanent, billed, and answered in character by someone who had just been
/// asked a question nobody in the scene heard.
/// </para>
/// <para>
/// Here rather than in the proxy because two front ends need it: the proxy, for Janitor, and
/// the web pages. Two copies of what counts as a command would be two places for a typo to be
/// read differently — once as a refusal, once as a permanent turn.
/// </para>
/// <para>
/// Anything that starts with a slash and is not run here is refused, never stored: an unknown
/// name, and the commands that only the terminal can show. A doubled slash is the way to send
/// prose that opens with one, exactly as in the composer.
/// </para>
/// </remarks>
public static class FrontEndTurn
{
    /// <summary>The one command a front end has that the composer does not.</summary>
    /// <remarks>
    /// The terminal shows the whole story; a front end shows only what was said in it. This is
    /// the bridge, and it lives here rather than in the composer's table because the terminal
    /// has nothing for it to do.
    /// </remarks>
    public const string Recap = "recap";

    /// <summary>Turns a recap shows when none is asked for.</summary>
    public const int RecapTurns = 4;

    /// <summary>The most a recap shows, whatever is asked for.</summary>
    public const int MostRecapTurns = 20;

    /// <summary>Carries out one message.</summary>
    /// <param name="conversations">The store.</param>
    /// <param name="chat">The conversation the request's tag named.</param>
    /// <param name="said">The reader's newest message, unlabelled.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>What to answer with.</returns>
    public static async Task<FrontEndOutcome> RunAsync(
        LocalConversationProvider conversations,
        Chat chat,
        string said,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(chat);

        var parsed = SlashCommands.Parse(said);

        switch (parsed.Kind)
        {
            case SlashParseKind.Message when parsed.Text.Length == 0:
                return Refuse("There is no message to answer.");

            case SlashParseKind.Message:
                var added = await conversations
                    .SendAsync(chat.Id, parsed.Text, instruction: null, progress: null, cancellationToken)
                    .ConfigureAwait(false);

                return new FrontEndOutcome(added.LastOrDefault()?.Text ?? string.Empty, Refused: false, Stored: true);

            case SlashParseKind.Unknown when parsed.Text.Equals(Recap, StringComparison.OrdinalIgnoreCase):
                return await RecapAsync(conversations, chat, ArgumentOf(said), cancellationToken).ConfigureAwait(false);

            case SlashParseKind.Unknown:
                return Refuse(
                    $"There is no /{parsed.Text} command, so nothing was stored. Type /help for the "
                    + $"list, or //{parsed.Text} to send it as part of the story.");
        }

        var command = parsed.Command!;
        var argument = parsed.Text;

        if (command.Argument == CommandArgument.Required && argument.Length == 0)
        {
            return Refuse($"{command.Usage} — nothing was stored.");
        }

        return command.Name switch
        {
            "do" => await DirectAsync(conversations, chat, argument, cancellationToken).ConfigureAwait(false),
            "focus" => await ContinueAsync(conversations, chat, LocalDirections.Focus(argument), cancellationToken).ConfigureAwait(false),
            "ask" => await AskAsync(conversations, chat, argument, cancellationToken).ConfigureAwait(false),
            "help" => new FrontEndOutcome(Help(), Refused: false),
            _ => Refuse(
                $"/{command.Name} only works in the terminal, so nothing was stored. "
                + "Type /help for what works here."),
        };
    }

    /// <summary>What a front end can type, for <c>/help</c>.</summary>
    /// <returns>The list, as plain text.</returns>
    public static string Help()
        => """
           Commands, out of character. None of them is stored as part of the story.

           /recap [turns] — the story so far: the latest summary, then the last few turns word for word. Free.
           /do <direction> — steer the next turn. Alone it writes the next beat; with your message after a blank line, it steers the reply to that message.
           /focus <who> — hand the next turn to a named character.
           /ask <question> — ask about the story out of character. The answer is shown, never stored.
           /help — this list.

           //… sends a message that starts with a slash as part of the story.
           """;

    /// <summary>Lays out a recap: the latest summary, then the last turns in full.</summary>
    /// <param name="speaker">What to call the replies' author.</param>
    /// <param name="latestSummary">The most recent summary, if the story has one.</param>
    /// <param name="turns">The visible turns, oldest first.</param>
    /// <param name="count">How many of the last turns to show.</param>
    /// <returns>The recap, as plain text.</returns>
    public static string FormatRecap(
        string speaker,
        string? latestSummary,
        IReadOnlyList<ChatMessage> turns,
        int count)
    {
        ArgumentNullException.ThrowIfNull(turns);

        var text = new StringBuilder("(Recap, out of character — nothing stored, nothing billed.)");
        var dialogue = turns.Where(static m => m.IsDialogue).ToList();

        if (!string.IsNullOrWhiteSpace(latestSummary))
        {
            text.Append("\n\nEarlier:\n").Append(latestSummary.Trim());
        }

        if (dialogue.Count == 0)
        {
            return text.Append("\n\nNothing has been said yet.").ToString();
        }

        var shown = dialogue.TakeLast(Math.Clamp(count, 1, MostRecapTurns)).ToList();
        text.Append(shown.Count == 1 ? "\n\nThe last turn:" : $"\n\nThe last {shown.Count} turns:");

        foreach (var turn in shown)
        {
            var who = turn.Role == ChatRole.User ? "You" : speaker;
            text.Append("\n\n").Append(who).Append(":\n").Append(turn.Text.Trim());
        }

        return text.ToString();
    }

    private static async Task<FrontEndOutcome> RecapAsync(
        LocalConversationProvider conversations,
        Chat chat,
        string argument,
        CancellationToken cancellationToken)
    {
        var count = RecapTurns;

        if (argument.Length > 0
            && !int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out count))
        {
            return Refuse($"/recap takes a number of turns, up to {MostRecapTurns} — nothing was stored.");
        }

        var turns = await conversations.GetMessagesAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var summaries = await conversations.SummariesAsync(chat.Id, cancellationToken).ConfigureAwait(false);

        return new FrontEndOutcome(
            FormatRecap(chat.Speaker ?? chat.Name, summaries.LastOrDefault()?.Text, turns, count),
            Refused: false);
    }

    private static async Task<FrontEndOutcome> DirectAsync(
        LocalConversationProvider conversations,
        Chat chat,
        string argument,
        CancellationToken cancellationToken)
    {
        var (direction, message) = SlashCommands.SplitDirection(argument);

        if (direction.Length == 0)
        {
            return Refuse("/do <direction> — nothing was stored.");
        }

        if (message.Length == 0)
        {
            return await ContinueAsync(conversations, chat, LocalDirections.Direction(direction), cancellationToken)
                .ConfigureAwait(false);
        }

        var added = await conversations
            .SendAsync(chat.Id, message, instruction: LocalDirections.Direction(direction), progress: null, cancellationToken)
            .ConfigureAwait(false);

        return new FrontEndOutcome(added.LastOrDefault()?.Text ?? string.Empty, Refused: false, Stored: true);
    }

    private static async Task<FrontEndOutcome> ContinueAsync(
        LocalConversationProvider conversations,
        Chat chat,
        string instruction,
        CancellationToken cancellationToken)
    {
        var before = (await conversations.GetMessagesAsync(chat.Id, cancellationToken).ConfigureAwait(false)).Count;

        var transcript = await conversations
            .ContinueAsync(chat.Id, instruction, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return transcript.Count > before && transcript[^1].Role == ChatRole.Assistant
            ? new FrontEndOutcome(transcript[^1].Text, Refused: false, Stored: true)
            : Refuse("No reply came back, and nothing was added. It is safe to try again.");
    }

    private static async Task<FrontEndOutcome> AskAsync(
        LocalConversationProvider conversations,
        Chat chat,
        string question,
        CancellationToken cancellationToken)
    {
        var answer = await conversations
            .AskAsync(chat.Id, question, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return new FrontEndOutcome(
            "(Out of character — not part of the story, and never stored in it.)\n\n" + answer.Answer.Trim(),
            Refused: false);
    }

    /// <summary>Everything after the command's name.</summary>
    private static string ArgumentOf(string said)
    {
        var text = said.Trim();
        var end = 1;

        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text[end..].Trim();
    }

    private static FrontEndOutcome Refuse(string reason) => new(reason, Refused: true);
}
