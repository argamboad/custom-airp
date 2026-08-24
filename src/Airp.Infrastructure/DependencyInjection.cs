using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Airp.Infrastructure.Storage.Local;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Infrastructure.Clipboard;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Secrets;
using Airp.Infrastructure.Storage;

namespace Airp.Infrastructure;

/// <summary>Registers the infrastructure layer with a dependency-injection container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the local store, the model clients, the secret store and the terminal's services.
    /// </summary>
    /// <remarks>
    /// Every provider is registered against its interface, so pointing the terminal at a
    /// different backend means registering a different set of implementations here and
    /// changing nothing else.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration root used to bind <see cref="AirpOptions"/>.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAirpInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AirpOptions>()
            .Bind(configuration.GetSection(AirpOptions.SectionName))
            // Made absolute once, here, rather than at each place that reads it. A configured
            // "./exports" left relative is resolved by the runtime against the working
            // directory, so exports landed wherever the shell happened to be standing — which
            // is the exact scattering AppPaths exists to prevent, arriving through the one
            // path that did not go through it.
            .PostConfigure(static options => options.ExportDirectory =
                AppPaths.Resolve(options.ExportDirectory))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<ISecretStore, DpapiSecretStore>();
        services.TryAddSingleton<TextLibrary>();
        services.TryAddSingleton<IDialService, DialService>();
        services.AddHttpClient<ILanguageModelClient, OpenRouterClient>();
        services.AddHttpClient<IEmbeddingClient, OpenRouterEmbeddingClient>();

        var flavour = configuration[$"{AirpOptions.SectionName}:{nameof(AirpOptions.Provider)}"]
            ?? "local";

        // The seam stays even with one flavour behind it: a value nothing registered should
        // fail here, by name, rather than resolve to something the user did not choose.
        if (!string.Equals(flavour, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unknown provider '{flavour}'. This application ships 'local' only.");
        }

        AddLocalProvider(services);

        services.TryAddSingleton<IConfigurationService, JsonConfigurationService>();
        services.TryAddSingleton<IClipboardService, TextCopyClipboardService>();

        return services;
    }

    /// <summary>
    /// Registers the local store and the adapter that reads and writes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance answers both provider interfaces, because for a locally owned conversation
    /// "list the chats" and "read a chat" are the same store and the same connection. The two
    /// interfaces stay separate for the sake of adapters where they are genuinely two different
    /// pages.
    /// </para>
    /// <para>
    /// A context <em>factory</em> rather than a context: the adapter is a singleton, and a
    /// singleton that captured a scoped <c>DbContext</c> would share one connection across
    /// every operation for the life of the process.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    private static void AddLocalProvider(IServiceCollection services)
    {
        services.AddDbContextFactory<AirpDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;
            builder.UseSqlite($"Data Source={AppPaths.Resolve(options.DatabaseFile)}");
        });

        services.TryAddSingleton<LocalConversationProvider>();
        services.TryAddSingleton<IChatProvider>(p => p.GetRequiredService<LocalConversationProvider>());
        services.TryAddSingleton<IConversationProvider>(p => p.GetRequiredService<LocalConversationProvider>());
    }
}
