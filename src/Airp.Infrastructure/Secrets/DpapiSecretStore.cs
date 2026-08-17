using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Airp.Application.Abstractions;
using Airp.Domain;

namespace Airp.Infrastructure.Secrets;

/// <summary>
/// Stores secrets as files encrypted against the current Windows account, and falls back to an
/// environment variable of the same name.
/// </summary>
/// <remarks>
/// <para>
/// The encryption is DPAPI at user scope. The practical property is that the file is useless
/// to another account on the machine and useless if copied off it, because the key material
/// never leaves the Windows profile. That matters here because the value being protected bills
/// a card.
/// </para>
/// <para>
/// The environment variable is a fallback rather than a rival. It is where an API key already
/// lives for anyone who set one up for another tool, and it keeps this usable where DPAPI does
/// not exist. A stored secret always wins, so moving a key into the store is enough to make the
/// variable stop mattering — no need to hunt down and unset it.
/// </para>
/// </remarks>
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>
    /// Additional entropy mixed into the protection.
    /// </summary>
    /// <remarks>
    /// Not a secret and not pretending to be one. It scopes the ciphertext to this application,
    /// so a file lifted into another program's DPAPI call does not decrypt by accident.
    /// </remarks>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Airp.SecretStore.v1");

    private readonly ILogger<DpapiSecretStore> _logger;
    private readonly string _directory;

    /// <summary>Initialises the store.</summary>
    /// <param name="logger">Logger. Never receives a secret value.</param>
    /// <param name="directory">
    /// Where the encrypted files live. Defaults to a <c>secrets</c> folder under the
    /// application data directory; supplied explicitly by tests, which must not write into
    /// the real one.
    /// </param>
    public DpapiSecretStore(ILogger<DpapiSecretStore> logger, string? directory = null)
    {
        _logger = logger;
        _directory = directory ?? Path.Combine(AppPaths.Root, "secrets");
    }

    /// <summary>Maps a secret name onto a file path.</summary>
    /// <param name="name">Name of the secret.</param>
    /// <returns>A full path.</returns>
    internal string PathFor(string name)
    {
        var safe = new string([.. name.Select(static c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        return Path.Combine(_directory, safe + ".bin");
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = PathFor(name);

        if (OperatingSystem.IsWindows() && File.Exists(path))
        {
            try
            {
                var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch (CryptographicException ex)
            {
                // Written by another account, or corrupted. Falling through to the environment
                // is more useful than failing outright, and the log explains the surprise.
                _logger.LogWarning(ex, "The stored secret {Name} could not be decrypted by this account.", name);
            }
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    /// <inheritdoc />
    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Encrypted secret storage needs Windows. Set an environment variable of the same name instead.");
        }

        Directory.CreateDirectory(_directory);

        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);

        await File.WriteAllBytesAsync(PathFor(name), encrypted, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stored the secret {Name} ({Length} bytes encrypted).", name, encrypted.Length);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var path = PathFor(name);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Removed the stored secret {Name}.", name);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> DescribeAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (OperatingSystem.IsWindows() && File.Exists(PathFor(name)))
        {
            return Task.FromResult($"encrypted store ({PathFor(name)})");
        }

        return Task.FromResult(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
                ? "not set"
                : $"environment variable {name}");
    }
}
