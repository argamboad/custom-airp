namespace Airp.Infrastructure;

/// <summary>
/// Resolves the on-disk locations the application uses.
/// </summary>
/// <remarks>
/// Relative paths in configuration resolve against <see cref="Root"/> rather than the
/// working directory. A terminal application gets launched from wherever the user happens
/// to be standing; resolving <c>./airp.db</c> against that would scatter databases across
/// the file system and silently start a fresh history whenever it ran from a new folder.
/// </remarks>
public static class AppPaths
{
    /// <summary>
    /// Name of the application data directory.
    /// </summary>
    /// <remarks>
    /// The ourdream-era client kept its state under <c>OurDream.Terminal</c>, and this
    /// application began life reading that folder. The two are separate programs now, with
    /// separate data: this one owns <c>%LOCALAPPDATA%\Airp</c> and nothing else.
    /// </remarks>
    private const string FolderName = "Airp";

    /// <summary>Base directory for all application state.</summary>
    /// <remarks>
    /// Overridable with the <c>AIRP_HOME</c> environment variable, which is what the
    /// tests and portable installations use.
    /// </remarks>
    public static string Root { get; } = ResolveRoot();

    /// <summary>Directory holding rolling log files.</summary>
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Default location of the configuration file.</summary>
    public static string ConfigurationFile => Path.Combine(Root, "airp.json");

    /// <summary>Resolves a possibly relative configured path against <see cref="Root"/>.</summary>
    /// <param name="configured">The configured path; may be absolute or relative.</param>
    /// <returns>An absolute path.</returns>
    public static string Resolve(string configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(Root, configured));
    }

    /// <summary>Creates the standard directory layout if it does not already exist.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("AIRP_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var baseDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        return Path.Combine(baseDirectory, FolderName);
    }
}
