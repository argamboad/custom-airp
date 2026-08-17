using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;

namespace Airp.Application.Services;

/// <summary>
/// Default <see cref="IConversationService"/>: a thin, logged pass-through to the adapter.
/// The adapter owns the conversation — the store on this machine is the durable copy, so
/// there is no second cache to keep honest in front of it.
/// </summary>
public sealed class ConversationService : IConversationService
{
    private readonly IConversationProvider _provider;
    private readonly ILogger<ConversationService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="provider">Adapter used to read and write messages.</param>
    /// <param name="logger">Logger.</param>
    public ConversationService(IConversationProvider provider, ILogger<ConversationService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string conversationId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        return _provider.GetMessagesAsync(conversationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatMessage>> SendAsync(
        string conversationId,
        string text,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A message needs some text.", nameof(text));
        }

        _logger.LogInformation("Sending a message to conversation {ConversationId}.", conversationId);

        return await _provider.SendAsync(conversationId, text, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> DeleteFromAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        _logger.LogInformation(
            "Deleting from message {MessageId} in conversation {ConversationId}.",
            messageId,
            conversationId);

        return _provider.DeleteFromAsync(conversationId, messageId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> RegenerateAsync(
        string conversationId,
        RegenerateReason reason,
        string? instructions = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        _logger.LogInformation(
            "Asking for another reply in conversation {ConversationId} ({Reason}).",
            conversationId,
            reason);

        return _provider.RegenerateAsync(conversationId, reason, instructions, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatMessage>> ContinueAsync(
        string conversationId,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        _logger.LogInformation("Continuing conversation {ConversationId}.", conversationId);

        return _provider.ContinueAsync(conversationId, progress, cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        _logger.LogInformation("Deleting conversation {ConversationId}.", conversationId);

        return _provider.DeleteConversationAsync(conversationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task RenameConversationAsync(
        string conversationId,
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        _logger.LogInformation("Renaming conversation {ConversationId}.", conversationId);

        return _provider.RenameConversationAsync(conversationId, name, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChatSettings> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        return _provider.GetSettingsAsync(conversationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ChatSettings> UpdateSettingsAsync(
        string conversationId,
        ChatSettings changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(changes);

        _logger.LogInformation(
            "Changing {Count} setting(s) on conversation {ConversationId}.",
            changes.Assigned().Count(),
            conversationId);

        return _provider.UpdateSettingsAsync(conversationId, changes, cancellationToken);
    }
}
