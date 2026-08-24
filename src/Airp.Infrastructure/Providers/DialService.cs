using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Dials;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage.Local;

namespace Airp.Infrastructure.Providers;

/// <summary>
/// The dial pack in force, and each conversation's choices, backed by the local store.
/// </summary>
/// <remarks>
/// <para>
/// The pack resolves in two steps. First the source: <c>dials.json</c> beside the
/// configuration file when it exists, the embedded default otherwise — the file replaces the
/// default whole, no merging, because a half-merged pack is a pack nobody wrote. Then the
/// overlay: an <c>Airp:Scales</c> section that renames or rewords the three original dials
/// keeps working exactly as it always has, applied over whichever source won.
/// </para>
/// <para>
/// The pack is read once and cached for the life of the process. A dial pack is edited the
/// way a character is edited — between sessions, in an editor — and rereading a file on every
/// prompt would spend IO on a thing that changes never.
/// </para>
/// </remarks>
public sealed class DialService : IDialService
{
    private const string PackFileName = "dials.json";
    private const string EmbeddedPack = "Airp.Infrastructure.Dials.default-dials.json";

    private readonly IDbContextFactory<AirpDbContext> _stores;
    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<DialService> _logger;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private DialPack? _pack;

    /// <summary>Initialises the service.</summary>
    /// <param name="stores">Factory for the local store.</param>
    /// <param name="options">Application options, for the <c>Airp:Scales</c> overlay.</param>
    /// <param name="logger">Logger. Never receives a dial's text.</param>
    public DialService(
        IDbContextFactory<AirpDbContext> stores,
        IOptionsMonitor<AirpOptions> options,
        ILogger<DialService> logger)
    {
        _stores = stores;
        _options = options;
        _logger = logger;
    }

    /// <summary>Where the reader's own pack lives, when they have one.</summary>
    public static string PackFilePath => Path.Combine(AppPaths.Root, PackFileName);

    /// <summary>The shipped pack's text, for <c>airp dials --write</c>.</summary>
    /// <returns>The embedded default, comments and all.</returns>
    public static string DefaultPackText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPack)
            ?? throw new InvalidOperationException($"The embedded pack '{EmbeddedPack}' is missing from the assembly.");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <inheritdoc />
    public async Task<DialPack> PackAsync(CancellationToken cancellationToken = default)
    {
        if (_pack is { } cached)
        {
            return cached;
        }

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _pack ??= await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> ValuesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        await using var store = await _stores.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ValuesAsync(store, conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a conversation's choices from an already open store.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The stored values, keyed by dial key.</returns>
    internal static async Task<IReadOnlyDictionary<string, string>> ValuesAsync(
        AirpDbContext store,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var rows = await store.DialValues
            .AsNoTracking()
            .Where(v => v.ConversationId == conversationId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ToDictionary(static v => v.Key, static v => v.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string conversationId,
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var store = await _stores.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await SetAsync(store, conversationId, key, value, cancellationToken).ConfigureAwait(false);
        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stages one write against an already open store, without saving.</summary>
    /// <param name="store">The open store.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="key">The dial's key.</param>
    /// <param name="value">The stored-form value, or null to clear.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    internal static async Task SetAsync(
        AirpDbContext store,
        string conversationId,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        var row = await store.DialValues
            .FirstOrDefaultAsync(
                v => v.ConversationId == conversationId && v.Key == key,
                cancellationToken)
            .ConfigureAwait(false);

        if (value is null)
        {
            // Clearing returns the dial to the pack's default. A dial never set and a dial
            // cleared are the same state, so the row goes rather than holding a null.
            if (row is not null)
            {
                store.DialValues.Remove(row);
            }

            return;
        }

        if (row is null)
        {
            store.DialValues.Add(new DialValueRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                Key = key,
                Value = value,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Value = value;
            row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task<DialPack> LoadAsync(CancellationToken cancellationToken)
    {
        string json;
        string source;

        if (File.Exists(PackFilePath))
        {
            json = await File.ReadAllTextAsync(PackFilePath, cancellationToken).ConfigureAwait(false);
            source = PackFileName;
        }
        else
        {
            json = DefaultPackText();
            source = "the shipped pack";
        }

        DialPack pack;

        try
        {
            pack = DialPackParser.Parse(json);
        }
        catch (System.Text.Json.JsonException ex)
        {
            // A file that does not parse must not take the dials down with it: the shipped
            // pack always parses, so the session runs on it and the log says why.
            _logger.LogWarning(ex, "dials.json is not valid JSON; using the shipped pack instead.");
            pack = DialPackParser.Parse(DefaultPackText());
            source = "the shipped pack (dials.json is invalid)";
        }

        foreach (var skipped in pack.Skipped)
        {
            _logger.LogWarning(
                "Dial '{Key}' in {Source} was skipped: {Reason}.", skipped.Key, source, skipped.Reason);
        }

        return Overlay(pack, _options.CurrentValue);
    }

    /// <summary>
    /// Applies the <c>Airp:Scales</c> wording overrides onto the three dials they name.
    /// </summary>
    /// <remarks>
    /// The section predates the pack and keeps working: a title alone renames the dial, and a
    /// full five levels replace label and text while the pack's sampler values stay — the
    /// overlay owns the words, never the numbers.
    /// </remarks>
    private static DialPack Overlay(DialPack pack, AirpOptions options)
    {
        if (options.Scales.Count == 0)
        {
            return pack;
        }

        var dials = pack.Dials.Select(dial =>
        {
            var setting = dial.Key switch
            {
                LegacyDials.Lust => ChatSetting.Lust,
                LegacyDials.ResponseLength => ChatSetting.ResponseLength,
                LegacyDials.Creativity => ChatSetting.Creativity,
                _ => (ChatSetting?)null,
            };

            if (setting is null || !options.Scales.TryGetValue(setting.ToString()!, out var scale))
            {
                return dial;
            }

            var levels = scale.Levels.Count == SettingScales.Steps
                ? dial.Levels
                    .Select((level, i) => level with
                    {
                        Label = scale.Levels[i].Label,
                        Text = dial.Lever == DialLever.Sampler ? level.Text : scale.Levels[i].Description,
                        Description = dial.Lever == DialLever.Sampler ? scale.Levels[i].Description : level.Description,
                    })
                    .ToArray()
                : dial.Levels;

            return new DialDefinition
            {
                Key = dial.Key,
                Kind = dial.Kind,
                Lever = dial.Lever,
                Maps = dial.Maps,
                Enabled = dial.Enabled,
                Default = dial.Default,
                Title = string.IsNullOrWhiteSpace(scale.Title) ? dial.Title : scale.Title,
                Help = dial.Help,
                Levels = levels,
                Options = dial.Options,
                OnText = dial.OnText,
                Template = dial.Template,
                Accepts = dial.Accepts,
                Examples = dial.Examples,
            };
        }).ToArray();

        return new DialPack { Dials = dials, Skipped = pack.Skipped };
    }
}
