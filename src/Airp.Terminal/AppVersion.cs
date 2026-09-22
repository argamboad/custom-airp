using System.Reflection;

namespace Airp.Terminal;

/// <summary>
/// What build this is, read once from the assembly.
/// </summary>
/// <remarks>
/// <para>
/// The number is stamped at build time from the nearest <c>v*</c> tag, so there is nothing to
/// maintain here and nothing that can disagree with the tag a release was cut from. A build
/// off the tagged commit is <c>1.0.0</c>; one further along is <c>1.0.1-alpha.0.35+d67c223</c>.
/// </para>
/// <para>
/// Both forms exist because the places that show it have different room. <see cref="Full"/>
/// carries the commit and answers "exactly which build is this", which is the question worth
/// asking of a tool installed from a working tree. <see cref="Short"/> drops it for the header,
/// where the answer only has to be recognisable at a glance.
/// </para>
/// </remarks>
internal static class AppVersion
{
    /// <summary>How much of the commit hash is worth showing.</summary>
    /// <remarks>
    /// The full forty characters are what the SDK appends, and they are unreadable and too wide
    /// for anywhere this is drawn. Seven is what git itself abbreviates to.
    /// </remarks>
    private const int ShaLength = 7;

    static AppVersion()
    {
        var stamped = (Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion)
            ?.Trim();

        if (string.IsNullOrEmpty(stamped))
        {
            // Nothing was stamped, which means this is not a build the release path produced.
            // Saying so is more use than inventing a number that would then be quoted back.
            Full = "unknown";
            Short = "unknown";
            return;
        }

        var plus = stamped.IndexOf('+', StringComparison.Ordinal);

        if (plus < 0)
        {
            Full = stamped;
            Short = stamped;
            return;
        }

        Short = stamped[..plus];

        var sha = stamped[(plus + 1)..];
        Full = sha.Length > ShaLength ? $"{Short}+{sha[..ShaLength]}" : stamped;
    }

    /// <summary>The version with the abbreviated commit, when there is one.</summary>
    public static string Full { get; }

    /// <summary>The version alone, for a line with no room for the commit.</summary>
    public static string Short { get; }
}
