using Airp.Application.Abstractions;
using Airp.Application.Dials;
using Airp.Infrastructure.Providers;

namespace Airp.Tests;

/// <summary>
/// An in-memory <see cref="IDialService"/> over the shipped pack, for view tests.
/// </summary>
/// <remarks>
/// The real pack, deliberately: a settings view tested against an invented pack would pass
/// with labels the application never shows. Values live in a dictionary and every write is
/// recorded, so a test can assert exactly which dials an apply touched.
/// </remarks>
internal sealed class FakeDialService : IDialService
{
    private readonly DialPack _pack = DialPackParser.Parse(DialService.DefaultPackText());
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every write made through <see cref="SetAsync"/>, in order.</summary>
    public List<(string Key, string? Value)> Writes { get; } = [];

    /// <summary>Seeds one stored choice, as if the conversation had already set it.</summary>
    /// <param name="key">The dial's key.</param>
    /// <param name="value">The stored-form value.</param>
    /// <returns>This service, for chaining.</returns>
    public FakeDialService With(string key, string value)
    {
        _values[key] = value;
        return this;
    }

    /// <inheritdoc />
    public Task<DialPack> PackAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_pack);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> ValuesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Task SetAsync(
        string conversationId,
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        Writes.Add((key, value));

        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }

        return Task.CompletedTask;
    }
}
