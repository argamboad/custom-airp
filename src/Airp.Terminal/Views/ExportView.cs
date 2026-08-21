using Airp.Application.Abstractions;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// Pick a format, preview it, then write it to disk or put it on the clipboard.
/// </summary>
/// <remarks>
/// The preview is the real rendered output, not an approximation, so what you see is exactly
/// what gets written.
/// </remarks>
internal sealed class ExportView : ViewBase
{
    private static readonly ExportFormat[] Formats =
        [ExportFormat.Markdown, ExportFormat.Json, ExportFormat.PlainText];

    private readonly IExportService _export;
    private readonly IClipboardService _clipboard;
    private readonly object _value;
    private readonly string _label;

    private int _format;
    private int _scroll;

    /// <summary>Initialises the view.</summary>
    /// <param name="export">Export renderer.</param>
    /// <param name="clipboard">Clipboard access.</param>
    /// <param name="value">The object being exported.</param>
    /// <param name="label">A short description shown in the header.</param>
    public ExportView(IExportService export, IClipboardService clipboard, object value, string label)
    {
        _export = export;
        _clipboard = clipboard;
        _value = value;
        _label = label;
    }

    /// <inheritdoc />
    public override string Title => "Export";

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("← →", "Change format"),
        new("Enter", "Write to a file"),
        new("C", "Copy to clipboard"),
        new("↑ ↓", "Scroll preview"),
        new("Esc", "Back"),
    ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;

        var tabs = Draw.Tabs([.. Formats.Select(Describe)], _format, theme);

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal($"{_label}   ", theme.Heading) + tabs),
            new Rule { Style = theme.Border },
        };

        string preview;
        try
        {
            preview = _export.Render(_value, Formats[_format]);
        }
        catch (Exception ex)
        {
            rows.Add(new Markup(Draw.Literal($"This value cannot be rendered as {Describe(Formats[_format])}: {ex.Message}", theme.Error)));
            return new Rows(rows);
        }

        var lines = preview.ReplaceLineEndings("\n").Split('\n');
        var available = Math.Max(1, context.Height - 2);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, lines.Length - available));

        foreach (var line in lines.Skip(_scroll).Take(available))
        {
            rows.Add(new Markup(Draw.Literal(Draw.Fit(line, context.Width - 2), theme.Text)));
        }

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveLeft:
                _format = (_format - 1 + Formats.Length) % Formats.Length;
                _scroll = 0;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveRight or AppCommand.Tab:
                _format = (_format + 1) % Formats.Length;
                _scroll = 0;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveUp:
                _scroll = Math.Max(0, _scroll - 1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown:
                _scroll++;
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageUp:
                _scroll = Math.Max(0, _scroll - Math.Max(1, context.Height - 3));
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.PageDown:
                _scroll += Math.Max(1, context.Height - 3);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Accept or AppCommand.Export:
            {
                var format = Formats[_format];
                return ValueTask.FromResult(ViewAction.Run("Writing the export", async ct =>
                {
                    var path = await _export.ExportAsync(_value, format, null, ct).ConfigureAwait(false);
                    return ViewAction.Sequence(
                        ViewAction.Pop,
                        ViewAction.Status($"Written to {path}", StatusKind.Success));
                }));
            }

            case AppCommand.Copy when _clipboard.IsAvailable:
            {
                var text = _export.Render(_value, Formats[_format]);
                return ValueTask.FromResult(ViewAction.Run("Copying", async ct =>
                    await _clipboard.CopyAsync(text, ct).ConfigureAwait(false)
                        ? ViewAction.Sequence(
                            ViewAction.Pop,
                            ViewAction.Status("Copied to the clipboard.", StatusKind.Success))
                        : ViewAction.Status("The clipboard rejected the copy.", StatusKind.Warning)));
            }

            case AppCommand.Copy:
                return ValueTask.FromResult(
                    ViewAction.Status("No clipboard is available in this environment.", StatusKind.Warning));

            case AppCommand.Quit:
                return ValueTask.FromResult(ViewAction.Quit);

            default:
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    private static string Describe(ExportFormat format) => format switch
    {
        ExportFormat.Markdown => "Markdown",
        ExportFormat.Json => "JSON",
        _ => "Plain text",
    };
}
