using Microsoft.ML.Tokenizers;
using Airp.Application.Abstractions;

namespace Airp.Application.Context;

/// <summary>
/// Counts how many tokens a prompt will cost, before it is sent.
/// </summary>
/// <remarks>
/// <para>
/// Uses the o200k byte-pair vocabulary, embedded in the build rather than fetched, so counting
/// works offline and identically everywhere.
/// </para>
/// <para>
/// It is still an approximation, because the model that answers has its own vocabulary and a
/// router fronting eighteen hosts does not say which build served a request. But it is an
/// approximation of the right kind. The first version of this divided characters by a
/// constant, which was calibrated against a short Spanish sample and then missed the real
/// English transcript by 31%: Spanish runs about 3.6 characters per token and English about
/// 4.7, so no single constant fits a user who writes in both. A real vocabulary removes the
/// question rather than tuning it.
/// </para>
/// <para>
/// The count the API reports is recorded beside this one, so the two can be compared instead
/// of this one being trusted.
/// </para>
/// </remarks>
public static class TokenEstimator
{
    /// <summary>
    /// Framing a chat API adds around each turn: the role, and the delimiters either side.
    /// </summary>
    /// <remarks>
    /// Measured, not guessed. A transcript of many short turns is substantially framing, and
    /// leaving it out under-counted exactly the shape this application produces.
    /// </remarks>
    private const int PerMessageOverhead = 4;

    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    /// <summary>Counts one piece of text, without message framing.</summary>
    /// <param name="text">The text.</param>
    /// <returns>A token count.</returns>
    public static int ForText(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Tokenizer.CountTokens(text);

    /// <summary>Counts one turn, framing included.</summary>
    /// <param name="message">The turn.</param>
    /// <returns>A token count.</returns>
    public static int ForMessage(ModelMessage message)
        => ForText(message.Content) + PerMessageOverhead;

    /// <summary>Counts a whole prompt.</summary>
    /// <param name="messages">The turns.</param>
    /// <returns>A token count.</returns>
    public static int ForMessages(IEnumerable<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(ForMessage);
    }
}
