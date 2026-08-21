using Airp.Application.Options;
using Spectre.Console;

namespace Airp.Terminal.Ui;

/// <summary>The colours a view may paint with.</summary>
/// <remarks>
/// Views never name a colour directly. Everything goes through a theme so that adding a
/// palette is a data change, and so the monochrome palette genuinely produces output with no
/// escape codes in it — useful when piping the terminal to a file.
/// </remarks>
internal sealed record Theme
{
    public required string Name { get; init; }

    /// <summary>Primary body text.</summary>
    public required Style Text { get; init; }

    /// <summary>Secondary text: timestamps, counts, hints.</summary>
    public required Style Muted { get; init; }

    /// <summary>Headings and the application title.</summary>
    public required Style Heading { get; init; }

    /// <summary>The currently selected row.</summary>
    public required Style Selection { get; init; }

    /// <summary>Accent used for key names and active state.</summary>
    public required Style Accent { get; init; }

    /// <summary>Success and "saved" messages.</summary>
    public required Style Success { get; init; }

    /// <summary>Warnings and degraded states.</summary>
    public required Style Warning { get; init; }

    /// <summary>Errors.</summary>
    public required Style Error { get; init; }

    /// <summary>Panel borders and separators.</summary>
    public required Style Border { get; init; }

    /// <summary>Search-match highlight.</summary>
    public required Style Highlight { get; init; }

    /// <summary>
    /// Action and narration inside a reply — the parts a card writes between asterisks.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Muted"/> rather than declared beside it, so the four palettes
    /// cannot drift apart on it. Dimmed and italic together on purpose: a console that does not
    /// draw italics still shows the dimming, so what a character did stays distinct from what
    /// they said either way.
    /// </remarks>
    public Style Action => Muted.Combine(new Style(decoration: Decoration.Italic));

    /// <summary>
    /// The background a panel, a side list or a key cap sits on.
    /// </summary>
    /// <remarks>
    /// A background and nothing else: it is combined with a foreground style rather than used
    /// alone, so one tone per palette serves everything that needs to read as a raised surface
    /// rather than as text floating on the terminal's own ground.
    /// </remarks>
    public required Style Surface { get; init; }

    /// <summary>
    /// A state chip — the one-word badge in the header.
    /// </summary>
    /// <remarks>
    /// Loud on purpose, and there is exactly one on screen. Rendered with a space either side
    /// of the word so the background reads as a chip rather than as a highlighted word.
    /// </remarks>
    public required Style Badge { get; init; }

    /// <summary>
    /// A key cap in the footer legend.
    /// </summary>
    /// <remarks>
    /// The accent on the surface tone rather than a second inverted chip: a conversation's
    /// footer carries thirteen of these, and thirteen blocks of reverse video read as a
    /// barcode. Derived so the palettes cannot drift apart on it.
    /// </remarks>
    public Style Key => Accent.Combine(Surface);

    /// <summary>Added lines in a diff.</summary>
    public required Style DiffAdded { get; init; }

    /// <summary>Removed lines in a diff.</summary>
    public required Style DiffRemoved { get; init; }

    /// <summary>Resolves a palette by name.</summary>
    /// <param name="name">The configured palette.</param>
    /// <returns>The matching theme.</returns>
    public static Theme For(ThemeName name) => name switch
    {
        ThemeName.Light => LightTheme,
        ThemeName.HighContrast => HighContrastTheme,
        ThemeName.Monochrome => MonochromeTheme,
        _ => DarkTheme,
    };

    private static Theme DarkTheme { get; } = new()
    {
        Name = "Dark",
        Text = new Style(Color.Grey85),
        Muted = new Style(Color.Grey50),
        Heading = new Style(Color.SkyBlue1, decoration: Decoration.Bold),
        Selection = new Style(Color.Black, Color.SkyBlue1, Decoration.Bold),
        Accent = new Style(Color.Aquamarine1),
        Success = new Style(Color.SpringGreen2),
        Warning = new Style(Color.Orange1),
        Error = new Style(Color.Red1),
        Border = new Style(Color.Grey35),
        Highlight = new Style(Color.Black, Color.Yellow),
        Surface = new Style(background: Color.Grey19),
        Badge = new Style(Color.Black, Color.SpringGreen2, Decoration.Bold),
        DiffAdded = new Style(Color.SpringGreen3),
        DiffRemoved = new Style(Color.IndianRed),
    };

    private static Theme LightTheme { get; } = new()
    {
        Name = "Light",
        Text = new Style(Color.Grey19),
        Muted = new Style(Color.Grey42),
        Heading = new Style(Color.Blue, decoration: Decoration.Bold),
        Selection = new Style(Color.White, Color.Blue, Decoration.Bold),
        Accent = new Style(Color.Purple),
        Success = new Style(Color.Green),
        Warning = new Style(Color.DarkOrange),
        Error = new Style(Color.Red),
        Border = new Style(Color.Grey62),
        Highlight = new Style(Color.Black, Color.Yellow1),
        Surface = new Style(background: Color.Grey93),
        Badge = new Style(Color.White, Color.Green, Decoration.Bold),
        DiffAdded = new Style(Color.Green),
        DiffRemoved = new Style(Color.Red),
    };

    private static Theme HighContrastTheme { get; } = new()
    {
        Name = "HighContrast",
        Text = new Style(Color.White),
        Muted = new Style(Color.White),
        Heading = new Style(Color.Yellow1, decoration: Decoration.Bold),
        Selection = new Style(Color.Black, Color.White, Decoration.Bold),
        Accent = new Style(Color.Cyan1, decoration: Decoration.Bold),
        Success = new Style(Color.Green1, decoration: Decoration.Bold),
        Warning = new Style(Color.Yellow1, decoration: Decoration.Bold),
        Error = new Style(Color.Red1, decoration: Decoration.Bold),
        Border = new Style(Color.White),
        Highlight = new Style(Color.Black, Color.Yellow1, Decoration.Bold),
        Surface = new Style(background: Color.Grey19),
        Badge = new Style(Color.Black, Color.Green1, Decoration.Bold),
        DiffAdded = new Style(Color.Green1, decoration: Decoration.Bold),
        DiffRemoved = new Style(Color.Red1, decoration: Decoration.Bold),
    };

    private static Theme MonochromeTheme { get; } = new()
    {
        Name = "Monochrome",
        Text = Style.Plain,
        Muted = Style.Plain,
        Heading = new Style(decoration: Decoration.Bold),
        Selection = new Style(decoration: Decoration.Invert),
        Accent = new Style(decoration: Decoration.Bold),
        Success = Style.Plain,
        Warning = Style.Plain,
        Error = new Style(decoration: Decoration.Bold),
        Border = Style.Plain,
        Highlight = new Style(decoration: Decoration.Underline),
        Surface = Style.Plain,
        Badge = new Style(decoration: Decoration.Invert),
        DiffAdded = new Style(decoration: Decoration.Bold),
        DiffRemoved = new Style(decoration: Decoration.Dim),
    };
}
