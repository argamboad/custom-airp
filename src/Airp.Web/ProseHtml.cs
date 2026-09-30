using System.Text;
using System.Text.Encodings.Web;
using Airp.Application.Text;
using Microsoft.AspNetCore.Html;

namespace Airp.Web;

/// <summary>A turn's text as HTML, drawn by the terminal's conventions.</summary>
/// <remarks>
/// <para>
/// The same reading as the terminal's, through the same <see cref="ProseFormat"/>: what was
/// between asterisks is an action, set in italic; what was in quotes is speech, set plainly;
/// both lose their markers. A reader moving between the two should recognise a reply by its
/// shape in either, and a second parser would be a second place for them to disagree.
/// </para>
/// <para>
/// Every piece of text is encoded before it is written. The stored words are whatever a model
/// or a reader produced, and a page that trusted them would run whatever they said.
/// </para>
/// </remarks>
public static class ProseHtml
{
    /// <summary>Renders a turn.</summary>
    /// <param name="text">The stored text.</param>
    /// <returns>One paragraph per stretch between blank lines, lines within it broken.</returns>
    public static IHtmlContent Render(string? text)
    {
        var html = new StringBuilder();
        var paragraph = new List<string>();

        foreach (var line in (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim().Length == 0)
            {
                Flush(html, paragraph);
                continue;
            }

            paragraph.Add(Line(line));
        }

        Flush(html, paragraph);
        return new HtmlString(html.ToString());
    }

    private static void Flush(StringBuilder html, List<string> paragraph)
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        html.Append("<p>").AppendJoin("<br>", paragraph).Append("</p>");
        paragraph.Clear();
    }

    private static string Line(string line)
    {
        var formatted = ProseFormat.Format(line);
        var html = new StringBuilder();
        var cursor = 0;

        foreach (var run in formatted.Runs)
        {
            if (run.Start > cursor)
            {
                html.Append(HtmlEncoder.Default.Encode(formatted.Text[cursor..run.Start]));
            }

            var piece = HtmlEncoder.Default.Encode(formatted.Text.Substring(run.Start, run.Length));
            html.Append(run.Kind == ProseKind.Action ? $"<em>{piece}</em>" : piece);
            cursor = run.Start + run.Length;
        }

        if (cursor < formatted.Text.Length)
        {
            html.Append(HtmlEncoder.Default.Encode(formatted.Text[cursor..]));
        }

        return html.ToString();
    }
}
