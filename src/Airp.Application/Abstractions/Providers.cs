using Airp.Domain.Conversations;

namespace Airp.Application.Abstractions;

/// <summary>
/// The phases a send reports through its <see cref="IProgress{T}"/>, named rather than spelt
/// out at each end.
/// </summary>
/// <remarks>
/// These are shown to the reader verbatim, but they are also read: everything from
/// <see cref="Waiting"/> onwards happens after the site has confirmed it took the message,
/// which is what tells a caller whether an abandoned send left anything behind. Comparing
/// against constants keeps that judgement from resting on a string literal typed twice.
/// </remarks>
public static class SendPhase
{
    /// <summary>Opening the conversation, before anything has been written.</summary>
    public const string Opening = "Opening the conversation";

    /// <summary>Filling the composer. Nothing has been sent.</summary>
    public const string Typing = "Typing";

    /// <summary>Submitting. Whether it went is not yet known.</summary>
    public const string Sending = "Sending";

    /// <summary>Submitted and confirmed: the site has the message, and owes a reply.</summary>
    public const string Waiting = "Waiting for a reply";

    /// <summary>Submitted, and the reply has started to arrive.</summary>
    public const string Arriving = "Reply arriving";

    /// <summary>Whether a phase is one the site could only reach by accepting the message.</summary>
    /// <param name="phase">The last phase reported, if any.</param>
    /// <returns><see langword="true"/> when the message is on the site.</returns>
    public static bool IsSubmitted(string? phase) => phase is Waiting or Arriving;
}

/// <summary>
/// Identifies a pluggable conversation backend. Implementing the provider interfaces plus
/// this one is all a future backend needs to be usable by the terminal.
/// </summary>
public interface IProviderIdentity
{
    /// <summary>Stable key used in configuration to select this adapter, e.g. <c>local</c>.</summary>
    string Key { get; }

    /// <summary>Human-readable name shown in the terminal header.</summary>
    string DisplayName { get; }
}

/// <summary>Reads chats from the remote site.</summary>
public interface IChatProvider : IProviderIdentity
{
    /// <summary>Fetches every chat visible to the signed-in account.</summary>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The chats, in the order the site presented them.</returns>
    /// <exception cref="Domain.ContractException">The store answered in a shape the adapter does not recognise.</exception>
    Task<IReadOnlyList<Chat>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches one chat in full, including fields the list view omits.
    /// </summary>
    /// <param name="chatId">Identifier of the chat.</param>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The chat, or <see langword="null"/> when it no longer exists.</returns>
    Task<Chat?> GetAsync(string chatId, CancellationToken cancellationToken = default);

}

/// <summary>Reads the messages of a conversation from the remote site.</summary>
public interface IConversationProvider : IProviderIdentity
{
    /// <summary>Fetches a conversation's messages, oldest first.</summary>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The messages, in chronological order. Empty when the site returned none.</returns>
    /// <exception cref="Domain.ContractException">No message payload could be recognised.</exception>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> GetMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message and waits for the chat's reply.
    /// </summary>
    /// <remarks>
    /// <strong>This spends the account's credits and writes to the conversation.</strong>
    /// Implementations type into the page's own composer and press its own send control, so
    /// the site's client code validates, bills and records the turn exactly as it would for
    /// a person typing.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="text">The message to send.</param>
    /// <param name="instruction">
    /// A one-off direction for the reply this message asks for, such as "keep it brief" or
    /// "have her leave before he answers". It steers the turn without becoming part of it: it
    /// goes in the prompt's instruction layer and is never stored as something anybody said.
    /// </param>
    /// <param name="progress">Receives status while waiting for the reply.</param>
    /// <param name="cancellationToken">
    /// Aborts waiting. It does not un-send a message that already reached the site.
    /// </param>
    /// <returns>The messages added by this exchange, oldest first.</returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> SendAsync(
        string conversationId,
        string text,
        string? instruction = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a message and every message after it, then re-reads what survived.
    /// </summary>
    /// <remarks>
    /// This cannot be undone: the site removes the messages from the conversation itself,
    /// not merely from this client's copy. Callers are expected to have confirmed it.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="messageId">The first message to remove; it goes too.</param>
    /// <param name="cancellationToken">Token used to abort the read that follows.</param>
    /// <returns>The transcript as the site reports it afterwards.</returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> DeleteFromAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for the most recent reply to be written again.
    /// </summary>
    /// <remarks>
    /// This spends the account's credits and replaces the reply that was there. The previous
    /// wording is not kept by this client.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="reason">Why, from the site's own list. May be none.</param>
    /// <param name="instructions">Optional guidance for the new reply.</param>
    /// <param name="progress">Receives status while waiting.</param>
    /// <param name="cancellationToken">Aborts waiting; it cannot un-ask.</param>
    /// <returns>The transcript once the new reply has settled.</returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> RegenerateAsync(
        string conversationId,
        Domain.Conversations.RegenerateReason reason,
        string? instructions = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the chat carry on from its last reply, without a message from you.
    /// </summary>
    /// <remarks>
    /// This spends the account's credits. The site offers it on the newest reply only, and
    /// hides the control while one is still being written.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="instruction">
    /// A direction for the turn, replacing the default "carry on" wording. Two answers to what
    /// this turn should be would leave the reply satisfying neither, so it replaces rather than
    /// joins.
    /// </param>
    /// <param name="progress">Receives status while waiting.</param>
    /// <param name="cancellationToken">Aborts waiting; it cannot un-ask.</param>
    /// <returns>The transcript once the continuation has settled.</returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> ContinueAsync(
        string conversationId,
        string? instruction = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entire conversation from the account.
    /// </summary>
    /// <remarks>
    /// Irreversible, and it takes every message with it. Callers are expected to have
    /// confirmed it.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>A task that completes once the site has accepted the deletion.</returns>
    Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a conversation a name of your own.
    /// </summary>
    /// <remarks>
    /// This sets a custom name on the chat; it does not rename the chat. A blank name is
    /// refused rather than treated as clearing it, which is what the site's own control does.
    /// </remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>A task that completes once the site has accepted the name.</returns>
    Task RenameConversationAsync(string conversationId, string name, CancellationToken cancellationToken = default);

    /// <summary>Reads the conversation's reply settings.</summary>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The settings; levels the site has never been given are null.</returns>
    Task<Domain.Conversations.ChatSettings> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the conversation's reply settings.</summary>
    /// <remarks>This alters the account's conversation and affects every reply after it.</remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="changes">Only the levels to change; the rest stay as they are.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The settings as the site reports them afterwards.</returns>
    Task<Domain.Conversations.ChatSettings> UpdateSettingsAsync(
        string conversationId,
        Domain.Conversations.ChatSettings changes,
        CancellationToken cancellationToken = default);
}
