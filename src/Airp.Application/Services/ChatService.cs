using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Application.Text;
using Airp.Domain;
using Airp.Domain.Conversations;

namespace Airp.Application.Services;

/// <summary>
/// Default <see cref="IChatService"/>: an in-memory cache in front of the site adapter,
/// backed by the offline cache so the list renders instantly on a cold start.
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly IChatProvider _provider;
    private readonly ILogger<ChatService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private IReadOnlyList<Chat> _chats = [];

    /// <summary>Initialises the service.</summary>
    /// <param name="provider">Adapter used to read chats.</param>
    /// <param name="logger">Logger.</param>
    public ChatService(IChatProvider provider, ILogger<ChatService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<Chat> Cached => _chats;

    /// <inheritdoc />
    public DateTimeOffset? LastRefreshedUtc { get; private set; }

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<Chat>>? Changed;

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chat>> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_chats.Count > 0)
        {
            return _chats;
        }

        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Chat>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogDebug("Refreshing chat list from {Provider}.", _provider.DisplayName);
            var fetched = await _provider.ListAsync(cancellationToken).ConfigureAwait(false);

            // Preserve any prompt text we already pulled; the list endpoint rarely includes it.
            var merged = Merge(_chats, fetched);
            Publish(merged);
            LastRefreshedUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Chat list refreshed: {Count} chats.", merged.Count);
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Chat refresh failed; keeping {Count} cached chats.", _chats.Count);
            if (_chats.Count == 0)
            {
                throw;
            }

            return _chats;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<Chat?> GetAsync(string chatId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);

        var known = _chats.FirstOrDefault(c => c.Id == chatId);

        // The list already carries everything a chat has, so a cached row is the answer.
        if (known is not null)
        {
            return known;
        }

        try
        {
            var fresh = await _provider.GetAsync(chatId, cancellationToken).ConfigureAwait(false);
            if (fresh is null)
            {
                return known;
            }

            Publish(Merge(_chats, [fresh]));
            return _chats.FirstOrDefault(c => c.Id == chatId) ?? fresh;
        }
        catch (AirpException ex)
        {
            _logger.LogWarning(ex, "Detail fetch failed for {ChatId}; serving cached copy.", chatId);
            return known;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Chat> Filter(string? query)
        => FuzzyMatcher.Rank(_chats, query, static c => c.SearchableText);

    internal static IReadOnlyList<Chat> Merge(
        IReadOnlyList<Chat> existing,
        IReadOnlyList<Chat> fetched)
    {
        if (existing.Count == 0)
        {
            return fetched;
        }

        var byId = existing.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var merged = new List<Chat>(fetched.Count);

        foreach (var incoming in fetched)
        {
            if (!byId.TryGetValue(incoming.Id, out var old))
            {
                merged.Add(incoming);
                continue;
            }

            // A fresh row wins, but a field the list omitted this time is kept rather than
            // blanked rather than lost.
            merged.Add(incoming with
            {
                Speaker = incoming.Speaker ?? old.Speaker,
                LatestMessage = incoming.LatestMessage ?? old.LatestMessage,
                Url = incoming.Url ?? old.Url,
            });
        }

        // Chats only present in memory are gone from the store; drop them.
        return merged;
    }

    private void Publish(IReadOnlyList<Chat> chats)
    {
        _chats = chats;
        Changed?.Invoke(this, chats);
    }
}
