using System.Globalization;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// A stretch of transcript, written out for a model that is being asked about it rather than
/// asked to continue it.
/// </summary>
/// <remarks>
/// The summariser and the fact extractor both need this and both had their own copy of the same
/// expression, which is how the two of them came to agree on a label that was wrong in both.
/// </remarks>
internal static class Transcript
{
    /// <summary>Names the reader as the story knows them.</summary>
    /// <remarks>
    /// <para>
    /// Labelling the reader's turns <c>User</c> looked harmless and was not. The extractor is
    /// told that a fact's subject is "a character's name", so it dutifully filed everything
    /// about the reader under <c>User</c> — producing world-state lines like <c>User: User has
    /// named the squirrel Arnaldo</c>, sitting directly above a summary that calls the same
    /// person Allan Sanlúcar El Kettani. The character's own memory did not know they were one
    /// person.
    /// </para>
    /// <para>
    /// The persona's file name is the name available here without parsing prose or guessing:
    /// a conversation stores the name and the text lives in a file. Rendered back into words, a
    /// slug is a good deal better than <c>User</c> even when it loses an accent — and when
    /// there is no persona at all, <c>User</c> is the honest answer.
    /// </para>
    /// </remarks>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The label for the reader's turns.</returns>
    public static string Reader(ConversationRecord conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        if (string.IsNullOrWhiteSpace(conversation.PersonaName))
        {
            return "User";
        }

        var words = conversation.PersonaName
            .Split(['-', '_', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static word => word.Length == 1
                ? word.ToUpperInvariant()
                : char.ToUpperInvariant(word[0]) + word[1..])
            .ToArray();

        return words.Length == 0 ? "User" : string.Join(' ', words);
    }

    /// <summary>Names the character whose turns these are.</summary>
    /// <param name="conversation">The conversation.</param>
    /// <returns>The label for the replies.</returns>
    public static string Character(ConversationRecord conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        return string.IsNullOrWhiteSpace(conversation.Speaker) ? "Character" : conversation.Speaker;
    }

    /// <summary>Writes a stretch out with both sides named.</summary>
    /// <param name="conversation">The conversation the turns belong to.</param>
    /// <param name="messages">The turns, oldest first.</param>
    /// <returns>The transcript.</returns>
    public static string Render(ConversationRecord conversation, IReadOnlyList<MessageRecord> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var reader = Reader(conversation);
        var character = Character(conversation);

        return string.Join(
            "\n\n",
            messages.Select(m => string.Create(
                CultureInfo.InvariantCulture,
                $"{(m.Role == ChatRole.Assistant ? character : reader)}: {m.Text}")));
    }
}
