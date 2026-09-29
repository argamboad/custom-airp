namespace Airp.Application.Text;

/// <summary>
/// The wording sent to the model for the directions a command can carry.
/// </summary>
/// <remarks>
/// <para>
/// A direction cannot go to the model bare. Every layer above it has spent its words telling
/// the model to stay in character and to leave the reader's turn alone, and a bare
/// <c>have Mariana leave</c> arriving after all of that reads as something the reader said out
/// loud. The frame says whose instruction this is and restates the one rule it is most likely
/// to be read as suspending.
/// </para>
/// <para>
/// Shared rather than kept by the terminal, because the proxy runs the same commands for a
/// front end, and a second copy of this wording is a second place for the frame to be lost.
/// </para>
/// </remarks>
public static class LocalDirections
{
    /// <summary>Frames a free-form direction for the next turn.</summary>
    /// <param name="direction">What the reader typed.</param>
    /// <returns>The directive.</returns>
    public static string Direction(string direction)
        => "A direction for this reply, from the reader, out of character. It is not something "
        + "anyone said aloud and nobody in the scene knows it was given. Write the next turn "
        + "following it, and still never write the user's words, actions or thoughts.\n\n"
        + direction.Trim();

    /// <summary>Frames a hand-off to a named character.</summary>
    /// <param name="who">The name the reader typed.</param>
    /// <returns>The directive.</returns>
    public static string Focus(string who)
        => "A direction for this reply, from the reader, out of character. Give this turn to "
        + who.Trim()
        + ". Let them carry it — what they do, say and notice — and keep everyone else to what "
        + "they need for that. Still never write the user's words, actions or thoughts.";
}
