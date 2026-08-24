using Airp.Application.Dials;

namespace Airp.Application.Abstractions;

/// <summary>
/// The dial pack in force, and each conversation's choices against it.
/// </summary>
/// <remarks>
/// The pack answers "what controls exist"; the values answer "where has this conversation set
/// them". The two are deliberately separate reads: the settings screen needs both, the prompt
/// needs both, but a CLI listing the pack needs no conversation at all.
/// </remarks>
public interface IDialService
{
    /// <summary>The pack in force: the reader's own file when present, the shipped one otherwise.</summary>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The pack.</returns>
    Task<DialPack> PackAsync(CancellationToken cancellationToken = default);

    /// <summary>A conversation's stored choices, keyed by dial key.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The stored values. Dials never touched are absent.</returns>
    Task<IReadOnlyDictionary<string, string>> ValuesAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>Sets or clears one dial for one conversation.</summary>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="key">The dial's key.</param>
    /// <param name="value">The stored-form value, or null to clear the choice back to the default.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A task that completes when the value is stored.</returns>
    Task SetAsync(
        string conversationId,
        string key,
        string? value,
        CancellationToken cancellationToken = default);
}
