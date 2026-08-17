namespace Airp.Domain.Conversations;

/// <summary>
/// Combining readings of the same conversation taken at different moments.
/// </summary>
/// <remarks>
/// <para>
/// A transcript is never read whole and once. It is read when the chat opens, read again
/// after a send, and each read may see more, fewer, or partially-streamed messages than the
/// last. Assigning one reading over another therefore loses history the moment a read comes
/// back short — which is exactly how a long conversation collapses into the two messages
/// that just arrived.
/// </para>
/// <para>
/// Merging is the only safe way to fold a new reading into an old one: it can add a message
/// and it can improve one, but it can never lose one.
/// </para>
/// </remarks>
public static class ChatTranscript
{
    /// <summary>Folds newly seen messages into a transcript already held.</summary>
    /// <param name="existing">The transcript already held.</param>
    /// <param name="added">Messages from a later reading.</param>
    /// <returns>The combined transcript, de-duplicated by identifier and oldest first.</returns>
    public static IReadOnlyList<ChatMessage> Merge(
        IReadOnlyList<ChatMessage> existing,
        IReadOnlyList<ChatMessage> added)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(added);

        if (added.Count == 0)
        {
            return existing;
        }

        var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var message in existing.Concat(added))
        {
            if (byId.TryAdd(message.Id, message))
            {
                order.Add(message.Id);
            }
            else
            {
                // A later copy of the same message is not automatically the better one: a
                // streamed reply is captured mid-flight before it finishes, so the longest
                // text seen for an identifier is the most complete version of it.
                byId[message.Id] = message.Text.Length >= byId[message.Id].Text.Length
                    ? message
                    : byId[message.Id];
            }
        }

        return [.. order.Select(id => byId[id])
            .OrderBy(static m => m.SentAtUtc ?? DateTimeOffset.MinValue)];
    }
}
