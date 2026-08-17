using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;

namespace Airp.Infrastructure.Storage;

/// <summary>
/// Reads and writes <c>airp.json</c>.
/// </summary>
/// <remarks>
/// Saving merges into the existing document rather than replacing it, so hand-written
/// comments elsewhere in the file and any keys this version does not know about survive a
/// round trip through the settings screen.
/// </remarks>
public sealed class JsonConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<JsonConfigurationService> _logger;

    /// <summary>Initialises the service using the default configuration path.</summary>
    /// <param name="options">Bound options, used for <see cref="Current"/>.</param>
    /// <param name="logger">Logger.</param>
    public JsonConfigurationService(IOptionsMonitor<AirpOptions> options, ILogger<JsonConfigurationService> logger)
        : this(options, logger, AppPaths.ConfigurationFile) { }

    /// <summary>Initialises the service against an explicit path. Used by the tests.</summary>
    /// <param name="options">Bound options, used for <see cref="Current"/>.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="path">Full path of the configuration file.</param>
    public JsonConfigurationService(
        IOptionsMonitor<AirpOptions> options,
        ILogger<JsonConfigurationService> logger,
        string path)
    {
        _options = options;
        _logger = logger;
        ConfigurationFilePath = path;
    }

    /// <inheritdoc />
    public string ConfigurationFilePath { get; }

    /// <inheritdoc />
    public AirpOptions Current => _options.CurrentValue;

    /// <summary>
    /// Keys written to a newly created configuration file.
    /// </summary>
    /// <remarks>
    /// Deliberately preferences only, so a newly created file stays small enough to read and
    /// everything unnamed keeps coming from the built-in defaults — which is what lets a
    /// shipped change to a default actually reach an existing installation.
    /// </remarks>
    private static readonly string[] PreferenceKeys =
    [
        "theme", "autoRefreshSeconds", "showLineNumbers",
        "keyboard", "mouseSupport", "exportDirectory",
        "promptHistoryDepth", "restoreSession",
    ];

    /// <inheritdoc />
    public async Task<bool> EnsureExistsAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(ConfigurationFilePath))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(ConfigurationFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await SaveAsync(new AirpOptions(), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Wrote a default configuration file to {Path}.", ConfigurationFilePath);
        return true;
    }

    /// <inheritdoc />
    public async Task SaveAsync(AirpOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        var serialized = JsonSerializer.SerializeToNode(options, SerializerOptions) as JsonObject
                         ?? [];

        // Merge preferences over whatever is already there, leaving any hand-written
        // overrides — and anything a future version adds — untouched.
        var section = root[AirpOptions.SectionName] as JsonObject ?? [];

        foreach (var key in PreferenceKeys)
        {
            if (serialized.TryGetPropertyValue(key, out var value))
            {
                section[key] = value?.DeepClone();
            }
        }

        root[AirpOptions.SectionName] = section;

        var json = root.ToJsonString(SerializerOptions);
        var temporary = ConfigurationFilePath + ".tmp";

        await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, ConfigurationFilePath, overwrite: true);

        _logger.LogInformation("Configuration saved to {Path}.", ConfigurationFilePath);
    }

    private async Task<JsonObject> ReadRootAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigurationFilePath))
        {
            return [];
        }

        try
        {
            var existing = await File.ReadAllTextAsync(ConfigurationFilePath, cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(existing) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "The existing configuration file could not be parsed; it will be replaced.");
            return [];
        }
    }
}
