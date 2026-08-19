using Airp.Application.Abstractions;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// Turns a model's answer into the one row that says what it cost.
/// </summary>
/// <remarks>
/// <para>
/// One place, because there are four call sites that spend money and they are easy to forget.
/// Two of them — compressing a stretch of transcript, extracting what it established — fire on
/// their own, without the reader asking for anything, and they were invisible for as long as
/// spending was inferred from message rows alone.
/// </para>
/// <para>
/// Every billed call writes a row, including one whose output is thrown away a second later.
/// The charge happened; a ledger that only recorded the keepers would be a ledger that
/// disagreed with the invoice.
/// </para>
/// </remarks>
internal static class Ledger
{
    /// <summary>Builds the row for a call that has just come back.</summary>
    /// <param name="conversationId">The conversation the call was made for.</param>
    /// <param name="kind">What the call was doing.</param>
    /// <param name="reply">What came back, with whatever accounting the API attached.</param>
    /// <param name="messageId">The message it produced, when it produced one.</param>
    /// <returns>The row to add.</returns>
    public static SpendRecord Row(
        string conversationId,
        SpendKind kind,
        ModelReply reply,
        string? messageId = null)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return new SpendRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = conversationId,
            Kind = kind,
            MessageId = messageId,
            AtUtc = DateTimeOffset.UtcNow,
            Model = reply.Model,
            Provider = reply.Provider,
            GenerationId = reply.GenerationId,
            PromptTokens = reply.PromptTokens,
            CompletionTokens = reply.CompletionTokens,
            CachedTokens = reply.CachedTokens,
            CacheWriteTokens = reply.CacheWriteTokens,
            // The one conversion, made where the wire's double stops being the wire's.
            Cost = reply.Cost is { } charged ? (decimal)charged : null,
        };
    }
}
