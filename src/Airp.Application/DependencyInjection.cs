using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Airp.Application.Abstractions;
using Airp.Application.Services;

namespace Airp.Application;

/// <summary>Registers the application layer with a dependency-injection container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the business services. The site adapters they depend on come from the
    /// infrastructure layer, so call this alongside <c>AddAirpInfrastructure</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="runBackgroundSync">
    /// Whether to run the periodic synchroniser as a hosted service. One-shot commands pass
    /// <see langword="false"/>: they have no UI to keep fresh, and a timer that wakes up and
    /// starts driving the browser mid-command is a source of races rather than freshness.
    /// <see cref="Abstractions.ISynchronizationService"/> remains resolvable either way.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAirpApplication(
        this IServiceCollection services,
        bool runBackgroundSync = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IChatService, ChatService>();
        services.TryAddSingleton<IConversationService, ConversationService>();
        services.TryAddSingleton<ISearchService, SearchService>();
        services.TryAddSingleton<IExportService, ExportService>();

        // The synchroniser is both a hosted service and a service the UI calls directly, so
        // it is registered once and resolved through both contracts.
        services.TryAddSingleton<SynchronizationService>();
        services.TryAddSingleton<ISynchronizationService>(sp => sp.GetRequiredService<SynchronizationService>());

        if (runBackgroundSync)
        {
            services.AddHostedService(sp => sp.GetRequiredService<SynchronizationService>());
        }

        return services;
    }
}
