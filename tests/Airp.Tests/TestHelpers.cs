using Microsoft.Extensions.Options;
using Airp.Application.Options;

namespace Airp.Tests;

/// <summary>
/// A trivial <see cref="IOptionsMonitor{T}"/> so tests can vary configuration without a
/// container. Substituting the interface works too, but this reads better at call sites and
/// supports mutating options mid-test.
/// </summary>
/// <typeparam name="T">The options type.</typeparam>
internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Convenience factories for the option objects the services need.</summary>
internal static class TestOptions
{
    public static StaticOptionsMonitor<AirpOptions> Default(Action<AirpOptions>? configure = null)
    {
        var options = new AirpOptions();
        configure?.Invoke(options);
        return new StaticOptionsMonitor<AirpOptions>(options);
    }
}
