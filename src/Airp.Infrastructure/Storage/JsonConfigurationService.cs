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
/// <para>
/// Saving merges into the existing document rather than replacing it, so any keys this
/// version does not know about survive a round trip through the settings screen.
/// </para>
/// <para>
/// <strong>Comments do not survive, so the ones that matter are regenerated.</strong> A
/// <see cref="JsonNode"/> cannot carry them: the file is parsed to a tree and written back
/// from it, and anything that was not a key or a value is gone by then. The keys whose legal
/// values a reader cannot guess — the two enums — get a comment written above them on every
/// save, listing the values from the enums themselves so the list cannot go stale. A comment
/// someone adds by hand elsewhere is lost on the next save, which is worth saying plainly
/// rather than claiming otherwise.
/// </para>
/// <para>
/// Reading tolerates comments and trailing commas, matching what the configuration provider
/// itself accepts. Without that, a file with a comment in it parsed as corrupt and was
/// replaced wholesale — the annotations this very class writes would have destroyed the
/// user's settings the first time they saved.
/// </para>
/// </remarks>
public sealed class JsonConfigurationService : IConfigurationService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>What the configuration provider accepts, so the writer accepts it too.</summary>
    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The keys whose legal values a reader cannot guess, and where to find them.</summary>
    /// <remarks>
    /// The values come from the enums rather than from a list written out here, so adding a
    /// palette adds it to the file's own documentation and cannot be forgotten.
    /// </remarks>
    private static readonly (string Key, string[] Values)[] Annotated =
    [
        ("theme", Enum.GetNames<ThemeName>()),
        ("keyboard", Enum.GetNames<KeyboardMode>()),
    ];

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
        "theme", "transcriptWidthPercent", "autoRefreshSeconds", "showLineNumbers",
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
    public async Task<IReadOnlyList<string>> RewriteAsync(CancellationToken cancellationToken = default)
    {
        var root = await ReadRootAsync(cancellationToken).ConfigureAwait(false);
        var section = root[AirpOptions.SectionName] as JsonObject ?? [];

        var defaults = JsonSerializer.SerializeToNode(new AirpOptions(), SerializerOptions) as JsonObject
                       ?? [];

        var added = new List<string>();

        foreach (var key in PreferenceKeys)
        {
            if (section.ContainsKey(key) || !defaults.TryGetPropertyValue(key, out var value))
            {
                continue;
            }

            section[key] = value?.DeepClone();
            added.Add(key);
        }

        root[AirpOptions.SectionName] = section;

        await WriteAsync(root, cancellationToken).ConfigureAwait(false);

        return added;
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

        await WriteAsync(root, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Annotates the document and puts it on disk, via a temporary file.</summary>
    /// <param name="root">The whole document.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    private async Task WriteAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var json = Annotate(root.ToJsonString(SerializerOptions));
        var temporary = ConfigurationFilePath + ".tmp";

        await File.WriteAllTextAsync(temporary, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, ConfigurationFilePath, overwrite: true);

        _logger.LogInformation("Configuration saved to {Path}.", ConfigurationFilePath);
    }

    /// <summary>Writes a comment above each key whose values are a closed set.</summary>
    /// <remarks>
    /// Text, because the tree it came from cannot hold a comment. Only keys sitting directly
    /// inside the <c>Airp</c> section are touched — matched on the exact indentation the
    /// serialiser gives that depth — so a <c>theme</c> a reader has nested inside something of
    /// their own is left alone.
    /// </remarks>
    /// <param name="json">The serialised document.</param>
    /// <returns>The same document with the comments in it.</returns>
    private static string Annotate(string json)
    {
        var lines = json.ReplaceLineEndings("\n").Split('\n').ToList();

        var section = lines.FindIndex(line =>
            line.TrimStart().StartsWith($"\"{AirpOptions.SectionName}\":", StringComparison.Ordinal));

        if (section < 0)
        {
            return json;
        }

        // The serialiser indents by two, so the section's keys sit two further in than it does.
        var indent = new string(' ', lines[section].Length - lines[section].TrimStart().Length + 2);

        foreach (var (key, values) in Annotated)
        {
            var at = lines.FindIndex(
                section,
                line => line.StartsWith($"{indent}\"{key}\":", StringComparison.Ordinal));

            if (at < 0)
            {
                continue;
            }

            lines.Insert(at, $"{indent}// one of: {string.Join(", ", values)}");
        }

        return string.Join(Environment.NewLine, lines);
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
            return JsonNode.Parse(existing, documentOptions: ReaderOptions) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "The existing configuration file could not be parsed; it will be replaced.");
            return [];
        }
    }
}
