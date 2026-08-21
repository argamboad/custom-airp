using Airp.Domain.Conversations;
using Airp.Domain.Search;

namespace Airp.Application.Abstractions;

/// <summary>
/// Cached, filterable view over <see cref="IChatProvider"/>. The UI talks to this, not
/// to the provider, so navigation stays instant while the network catches up.
/// </summary>
public interface IChatService
{
    /// <summary>The last known set of chats. Never blocks.</summary>
    IReadOnlyList<Chat> Cached { get; }

    /// <summary>When <see cref="Cached"/> was last refreshed from the site.</summary>
    DateTimeOffset? LastRefreshedUtc { get; }

    /// <summary>Raised whenever the cache changes, from any source.</summary>
    event EventHandler<IReadOnlyList<Chat>>? Changed;

    /// <summary>Returns the cache, populating it from the site on first use.</summary>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The chats.</returns>
    Task<IReadOnlyList<Chat>> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Re-reads the chat list from the site.</summary>
    /// <param name="cancellationToken">Token used to abort the refresh.</param>
    /// <returns>The refreshed chats.</returns>
    Task<IReadOnlyList<Chat>> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a single chat, from cache when possible.</summary>
    /// <param name="chatId">Identifier of the chat.</param>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The chat, or <see langword="null"/> when unknown.</returns>
    Task<Chat?> GetAsync(string chatId, CancellationToken cancellationToken = default);

    /// <summary>Filters the cache by a free-text query using fuzzy matching.</summary>
    /// <param name="query">The query; empty returns everything.</param>
    /// <returns>Matching chats, best match first.</returns>
    IReadOnlyList<Chat> Filter(string? query);

}
/// <summary>Conversation transcripts, cached so re-opening a chat is instant.</summary>
public interface IConversationService
{
    /// <summary>Fetches a conversation's messages, oldest first.</summary>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="forceRefresh">Bypass the cache even when a copy exists.</param>
    /// <param name="cancellationToken">Token used to abort the fetch.</param>
    /// <returns>The messages, in chronological order.</returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> GetMessagesAsync(
        string conversationId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message and waits for the reply, updating the cached transcript.
    /// </summary>
    /// <remarks>This spends the account's credits and writes to the conversation.</remarks>
    /// <param name="conversationId">Identifier of the conversation.</param>
    /// <param name="text">The message to send.</param>
    /// <param name="instruction">
    /// A one-off direction for the reply, routed to the prompt's instruction layer rather than
    /// stored as part of the message.
    /// </param>
    /// <param name="progress">Receives status while waiting.</param>
    /// <param name="cancellationToken">Aborts waiting; it cannot un-send.</param>
    /// <returns>
    /// The new exchange merged into the cached transcript. That is the whole conversation
    /// whenever the cache holds it, but a caller already displaying a transcript should fold
    /// this into it with <see cref="Domain.Conversations.ChatTranscript.Merge"/> rather than
    /// replacing it, since caching can be switched off.
    /// </returns>
    Task<IReadOnlyList<Domain.Conversations.ChatMessage>> SendAsync(
        string conversationId,
        string text,
        string? instruction = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a message and every message after it from the conversation itself.
    /// </summary>
    /// <remarks>
    /// Irreversible, and not local: the messages are removed from the account's conversation
    /// and the offline copy is replaced with what survived rather than merged with it.
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
    /// This spends the account's credits and replaces the reply that was there. The cached
    /// transcript is replaced with the result rather than merged with it, since the reply it
    /// supersedes is meant to be gone.
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
    /// <param name="changes">Only the levels to change.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The settings as the site reports them afterwards.</returns>
    Task<Domain.Conversations.ChatSettings> UpdateSettingsAsync(
        string conversationId,
        Domain.Conversations.ChatSettings changes,
        CancellationToken cancellationToken = default);
}

/// <summary>Global search across every indexed field.</summary>
public interface ISearchService
{
    /// <summary>
    /// Searches the words of every chat held locally.
    /// </summary>
    /// <remarks>
    /// Chats with no local copy are counted rather than read: fetching one takes tens of
    /// seconds, so a search that waited for all of them would take minutes.
    /// </remarks>
    /// <param name="query">The query. Empty returns no hits.</param>
    /// <param name="scope">Which parts to consider.</param>
    /// <param name="limit">Maximum number of hits.</param>
    /// <param name="cancellationToken">Token used to abort the search.</param>
    /// <returns>The hits, best match first, and what was covered.</returns>
    Task<SearchResults> SearchAsync(
        string query,
        SearchScope scope = SearchScope.All,
        int limit = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>Formats that <see cref="IExportService"/> can produce.</summary>
public enum ExportFormat
{
    /// <summary>Markdown with front matter.</summary>
    Markdown = 0,

    /// <summary>Indented JSON.</summary>
    Json,

    /// <summary>Unadorned text.</summary>
    PlainText,
}

/// <summary>Writes chats and their transcripts to disk.</summary>
public interface IExportService
{
    /// <summary>Renders an object to a string in the requested format.</summary>
    /// <param name="value">A <c>Chat</c> or a <c>ConversationTranscript</c>.</param>
    /// <param name="format">Output format.</param>
    /// <returns>The rendered document.</returns>
    string Render(object value, ExportFormat format);

    /// <summary>Renders an object and writes it to a file.</summary>
    /// <param name="value">A <c>Chat</c> or a <c>ConversationTranscript</c>.</param>
    /// <param name="format">Output format.</param>
    /// <param name="path">Destination path, or <see langword="null"/> to auto-name inside the export directory.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The full path written.</returns>
    Task<string> ExportAsync(
        object value,
        ExportFormat format,
        string? path = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Copies text to the system clipboard.</summary>
public interface IClipboardService
{
    /// <summary>Whether a clipboard is reachable on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Places text on the clipboard.</summary>
    /// <param name="text">The text to copy.</param>
    /// <param name="cancellationToken">Token used to abort the copy.</param>
    /// <returns><see langword="true"/> when the copy succeeded.</returns>
    Task<bool> CopyAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes the on-disk configuration file.</summary>
public interface IConfigurationService
{
    /// <summary>Full path of the configuration file in use.</summary>
    string ConfigurationFilePath { get; }

    /// <summary>The effective options, after file, environment and command-line binding.</summary>
    Options.AirpOptions Current { get; }

    /// <summary>Writes the supplied options back to the configuration file.</summary>
    /// <param name="options">Options to persist.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    Task SaveAsync(Options.AirpOptions options, CancellationToken cancellationToken = default);

    /// <summary>Creates the configuration file with defaults if it does not exist.</summary>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns><see langword="true"/> when a new file was created.</returns>
    Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings an existing configuration file up to the shape this version writes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely additive: keys the file already has keep their values exactly, keys it lacks are
    /// added with their defaults, and the comments saying what the enums accept are put back.
    /// Nothing else touches a file that is already there — <see cref="EnsureExistsAsync"/>
    /// writes defaults once and then never looks inside — so a file written by an older version
    /// keeps whatever shape it had, through any number of reinstalls. It lives in the
    /// application data directory rather than beside the binary, which is the point of it.
    /// </para>
    /// <para>
    /// Additive rather than a save of what is currently in effect, and the difference is not
    /// academic: the effective options have been post-configured, so <c>./exports</c> comes
    /// back as an absolute path. Writing that would bake this machine's directory into a file
    /// meant to be portable.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The keys that were added, in the order written.</returns>
    Task<IReadOnlyList<string>> RewriteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reasons the synchroniser raised an update.</summary>
public enum SyncTrigger
{
    /// <summary>The periodic timer fired.</summary>
    Timer = 0,

    /// <summary>The user pressed refresh.</summary>
    Manual,

    /// <summary>A write from the terminal changed remote state.</summary>
    LocalWrite,

    /// <summary>The session was re-established.</summary>
    Reauthenticated,
}

/// <summary>Details of a completed synchronisation pass.</summary>
/// <param name="Trigger">What caused the pass.</param>
/// <param name="ChatCount">Number of chats after the pass.</param>
/// <param name="Error">The failure, when the pass did not succeed.</param>
/// <param name="AtUtc">When the pass finished, in UTC.</param>
public sealed record SyncCompleted(
    SyncTrigger Trigger,
    int ChatCount,
    Exception? Error,
    DateTimeOffset AtUtc)
{
    /// <summary>Whether the pass succeeded.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>Keeps the terminal's cached state aligned with the live browser session.</summary>
public interface ISynchronizationService
{
    /// <summary>Raised after every pass, successful or not.</summary>
    event EventHandler<SyncCompleted>? Completed;

    /// <summary>Whether a pass is currently running.</summary>
    bool IsSyncing { get; }

    /// <summary>Runs a pass immediately.</summary>
    /// <param name="trigger">Why the pass was requested.</param>
    /// <param name="cancellationToken">Token used to abort the pass.</param>
    /// <returns>The outcome.</returns>
    Task<SyncCompleted> SyncNowAsync(
        SyncTrigger trigger = SyncTrigger.Manual,
        CancellationToken cancellationToken = default);
}
