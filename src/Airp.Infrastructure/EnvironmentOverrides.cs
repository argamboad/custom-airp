using Airp.Application.Options;

namespace Airp.Infrastructure;

/// <summary>
/// Maps <c>AIRP_*</c> environment variables onto the <c>Airp</c> configuration section.
/// </summary>
/// <remarks>
/// <para>
/// The stock environment-variable provider strips the prefix and leaves a root-level key,
/// which never reaches options bound from a section: <c>AIRP_Theme=Light</c> is read, put at
/// the root, and silently ignored. This re-roots each variable so the documented form works —
/// <c>AIRP_Theme</c> and <c>AIRP_Site__BaseUrl</c> become <c>Airp:Theme</c> and
/// <c>Airp:Site:BaseUrl</c>.
/// </para>
/// <para>
/// Shared rather than duplicated because both hosts need it and the failure it prevents is
/// silent: a host that forgets simply behaves as though the variable were never set, which
/// looks exactly like a typo in the variable name.
/// </para>
/// </remarks>
public static class EnvironmentOverrides
{
    private const string Prefix = "AIRP_";

    /// <summary>Reads the overrides currently in the environment.</summary>
    /// <returns>Configuration entries ready to add to a builder.</returns>
    public static IEnumerable<KeyValuePair<string, string?>> Read()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key.ToString() is not { } name
                || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = name[Prefix.Length..].Replace("__", ":", StringComparison.Ordinal);

            if (path.Length == 0)
            {
                continue;
            }

            // AIRP_HOME relocates the whole application directory and is read directly by
            // AppPaths; it is not a configuration key. Nor are the secrets, which must never
            // enter the configuration graph — anything that dumps configuration would print them.
            if (path.Equals("HOME", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return new KeyValuePair<string, string?>(
                $"{AirpOptions.SectionName}:{path}",
                entry.Value?.ToString());
        }
    }
}
