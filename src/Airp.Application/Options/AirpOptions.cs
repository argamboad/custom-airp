using System.ComponentModel.DataAnnotations;

namespace Airp.Application.Options;

/// <summary>Colour palettes shipped with the terminal UI.</summary>
public enum ThemeName
{
    /// <summary>Light-on-dark palette (default).</summary>
    Dark = 0,

    /// <summary>Dark-on-light palette.</summary>
    Light,

    /// <summary>Maximum contrast, no dim text.</summary>
    HighContrast,

    /// <summary>No colour at all; useful for logging terminal output to a file.</summary>
    Monochrome,
}

/// <summary>Keyboard dialects the terminal can be driven with.</summary>
public enum KeyboardMode
{
    /// <summary>Arrow keys, Home/End, Page Up/Down, Ctrl chords.</summary>
    Standard = 0,

    /// <summary>
    /// Standard bindings plus <c>hjkl</c>, <c>G</c>, <c>n</c>/<c>N</c> and <c>u</c>, where you
    /// are navigating rather than typing.
    /// </summary>
    /// <remarks>
    /// A shortcut layer, not a modal editor. This said <c>gg</c>, <c>dd</c> and "modal editing"
    /// for a long time and none of the three has ever existed: <c>g</c> has no binding here,
    /// <c>d</c> is Diff in both dialects, and a text field takes every printable key as itself.
    /// </remarks>
    Vim,
}

/// <summary>
/// Root configuration for the application. Bound from <c>airp.json</c>, environment
/// variables prefixed <c>AIRP_</c>, and command line switches, in that order.
/// </summary>
public sealed class AirpOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Airp";

    /// <summary>Colour palette used by the terminal UI.</summary>
    public ThemeName Theme { get; set; } = ThemeName.Dark;

    /// <summary>
    /// How much of the window a conversation occupies, as a percentage. 100 fills it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal is as wide as its window; a reply is continuous prose. Those are two
    /// different requirements, and a maximised window gave lines of a hundred and eighty
    /// characters — past about ninety the eye loses the start of the next line on the return
    /// sweep, which is why a newspaper sets narrow columns on a wide page. The column is
    /// centred, so what is left over is a margin either side rather than a hole down one.
    /// </para>
    /// <para>
    /// Sixty by default and a percentage rather than a column count, because the right answer
    /// depends on the window and the reader's eyes rather than on anything this application
    /// knows. <strong>100 means full width</strong> and no margin at all, which is where this
    /// started. Values outside 30 to 100 are clamped: below thirty a maximised window gives a
    /// column too narrow to read, and the two ends of that range are the only ones worth
    /// defending against a typo.
    /// </para>
    /// </remarks>
    public int TranscriptWidthPercent { get; set; } = 60;

    /// <summary>
    /// How often the background synchroniser re-reads the store. Zero or a negative value
    /// disables automatic refresh.
    /// </summary>
    public int AutoRefreshSeconds { get; set; } = 60;
    /// <summary>Keyboard dialect.</summary>
    public KeyboardMode Keyboard { get; set; } = KeyboardMode.Standard;

    /// <summary>Enable click and scroll-wheel handling in addition to the keyboard.</summary>
    public bool MouseSupport { get; set; }

    /// <summary>
    /// Directory that exports are written to when no path is supplied.
    /// </summary>
    /// <remarks>
    /// Configured relative — <c>./exports</c> — and made absolute against the application's
    /// own root while options are being built, so nothing that reads it can accidentally
    /// resolve it against whatever directory the terminal was launched from. It was that,
    /// once, and transcripts went wherever the shell happened to be standing.
    /// </remarks>
    public string ExportDirectory { get; set; } = "./exports";

    /// <summary>
    /// Name of the persona a conversation uses when it does not name one itself.
    /// </summary>
    /// <remarks>
    /// A file name in the personas folder, without the extension. The descriptions themselves
    /// live on disk and nowhere else — an earlier version also accepted them here, and a name
    /// defined in both places silently preferred the wrong one.
    /// </remarks>
    public string? DefaultPersona { get; set; }

    /// <summary>
    /// SQLite file holding conversations this machine owns.
    /// </summary>
    /// <remarks>
    /// Relative paths resolve against the application data directory, like every other path
    /// here. It holds the whole transcript in clear text, so it belongs where the rest of the
    /// account's state lives rather than wherever the terminal happened to be launched from.
    /// </remarks>
    public string DatabaseFile { get; set; } = "./airp.db";

    /// <summary>Restore the previously selected chat and view on start-up.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>
    /// Which conversation adapter the terminal talks to. <c>local</c> is the only one this
    /// application ships.
    /// </summary>
    /// <remarks>
    /// Kept as a seam rather than removed: the terminal talks to provider interfaces and
    /// neither knows nor cares what stands behind them, which is what let a second flavour
    /// exist in the first place. A value nothing registered fails at startup by name.
    /// </remarks>
    public string Provider { get; set; } = "local";

    /// <summary>
    /// Your own wording for the reply dials, keyed by <c>Lust</c>, <c>ResponseLength</c> or
    /// <c>Creativity</c>.
    /// </summary>
    /// <remarks>
    /// Empty by default, in which case the shipped scales apply. A replacement must supply
    /// exactly five levels; a shorter one is ignored rather than used with gaps, because levels
    /// are read by index and the top of the dial would quietly mean whatever the bottom did.
    /// </remarks>
    public Dictionary<string, ScaleOptions> Scales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Language model used by adapters that generate replies themselves.</summary>
    public ModelOptions Model { get; set; } = new();

    /// <summary>Upper bound on one outgoing message, in characters. Zero means no limit.</summary>
    /// <remarks>
    /// The composer refuses to send past it rather than trusting anything downstream to
    /// truncate politely. The old default came from a site's field size; a local store has no
    /// such field, so the default here is no limit at all.
    /// </remarks>
    public int MessageCharacterLimit { get; set; }

    /// <summary>Upper bound on regenerate instructions, in characters. Zero means no limit.</summary>
    public int InstructionCharacterLimit { get; set; }

    /// <summary>Resolves <see cref="AutoRefreshSeconds"/> to a timespan, or null when disabled.</summary>
    public TimeSpan? AutoRefreshInterval =>
        AutoRefreshSeconds > 0 ? TimeSpan.FromSeconds(AutoRefreshSeconds) : null;
}
