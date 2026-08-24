using System.Globalization;
using Airp.Domain.Conversations;

namespace Airp.Application.Dials;

/// <summary>
/// The bridge between the original four settings and their dial-pack form.
/// </summary>
/// <remarks>
/// <see cref="ChatSettings"/> predates the pack and remains the shape the provider interface,
/// the CLI verbs and the existing tests speak. These four keys are the same controls under
/// their pack names; the mapping lives in one place so the two vocabularies cannot drift.
/// </remarks>
public static class LegacyDials
{
    /// <summary>The pack key of the Lust dial.</summary>
    public const string Lust = "lust";

    /// <summary>The pack key of the response-length dial.</summary>
    public const string ResponseLength = "response-length";

    /// <summary>The pack key of the creativity dial.</summary>
    public const string Creativity = "creativity";

    /// <summary>The pack key of the inner-thoughts toggle.</summary>
    public const string InnerThoughts = "inner-thoughts";

    /// <summary>The pack key for a legacy setting.</summary>
    /// <param name="setting">The setting.</param>
    /// <returns>The dial key.</returns>
    public static string KeyFor(ChatSetting setting) => setting switch
    {
        ChatSetting.Lust => Lust,
        ChatSetting.ResponseLength => ResponseLength,
        ChatSetting.Creativity => Creativity,
        _ => setting.ToString().ToLowerInvariant(),
    };

    /// <summary>Reads the legacy settings out of a conversation's stored dial values.</summary>
    /// <param name="values">The stored values, keyed by dial key.</param>
    /// <returns>The settings, null where a dial is unset.</returns>
    public static ChatSettings ToSettings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new ChatSettings
        {
            Lust = Level(values, Lust),
            ResponseLength = Level(values, ResponseLength),
            Creativity = Level(values, Creativity),
            // Reported as it applies, never as null: the toggle's default is off, and the old
            // contract always answered true or false. Null stays reserved for partial updates,
            // where it means "leave it alone".
            InnerThoughts = values.TryGetValue(InnerThoughts, out var thoughts)
                && DialEngine.IsOn(thoughts),
        };
    }

    /// <summary>The stored-form writes a partial settings change asks for.</summary>
    /// <param name="changes">Only the levels to change; null entries are left alone.</param>
    /// <returns>Key and stored value pairs, one per assigned setting.</returns>
    public static IReadOnlyList<(string Key, string Value)> FromSettings(ChatSettings changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var writes = new List<(string, string)>();

        foreach (var setting in changes.Assigned())
        {
            writes.Add((
                KeyFor(setting),
                changes.Level(setting)!.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (changes.InnerThoughts is { } thoughts)
        {
            writes.Add((InnerThoughts, thoughts ? "true" : "false"));
        }

        return writes;
    }

    private static int? Level(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var stored)
           && int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)
            ? level
            : null;
}
