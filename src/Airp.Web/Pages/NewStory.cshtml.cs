using System.Text;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Airp.Web.Pages;

/// <summary>Starts a story from the library: pick a character, then settle the rest.</summary>
/// <remarks>
/// <para>
/// Two pages where the terminal has one form, because these pages carry no script: the opening
/// is pre-filled from the character's own on the shelf, and without a script the only way to
/// know which character that is, is to have been asked for it. So the first page is the
/// shelf, and picking from it is a link to the second, which has the opening in it already.
/// </para>
/// <para>
/// Names are only ever taken from the shelves. A story stores the name and reads the file
/// every turn, so a name that matches no file is a story with an empty character layer, and
/// nothing on screen says so.
/// </para>
/// </remarks>
/// <param name="conversations">The store.</param>
/// <param name="library">The shelves.</param>
/// <param name="options">Live application options, for the default persona.</param>
public sealed class NewStoryModel(
    LocalConversationProvider conversations,
    TextLibrary library,
    IOptionsMonitor<AirpOptions> options) : PageModel
{
    /// <summary>The characters to pick from, each with a line of its world to pick by.</summary>
    public IReadOnlyList<(string Name, string Teaser)> Characters { get; private set; } = [];

    /// <summary>The picked character, as the shelf spells it; null while picking.</summary>
    public string? Character { get; private set; }

    /// <summary>The picked character's world, whole, as the card states it.</summary>
    public string? World { get; private set; }

    /// <summary>The personas on the shelf.</summary>
    public IReadOnlyList<string> Personas { get; private set; } = [];

    /// <summary>The persona a story gets when none is picked, if one is configured.</summary>
    public string? DefaultPersona => options.CurrentValue.DefaultPersona;

    /// <summary>The model a story uses when it has none of its own.</summary>
    public string DefaultModel => options.CurrentValue.Model.Name;

    /// <summary>The models a story can start on besides the default.</summary>
    public IReadOnlyList<string> ModelChoices => [.. options.CurrentValue.Model.EffectiveChoices
        .Where(c => !string.Equals(c, DefaultModel, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The model picked; empty for the default.</summary>
    [BindProperty]
    public string? Model { get; set; }

    /// <summary>What the story list will call it; the character's name when left empty.</summary>
    [BindProperty]
    public string? Name { get; set; }

    /// <summary>Who replies, when the story says.</summary>
    [BindProperty]
    public string? Speaker { get; set; }

    /// <summary>A persona by name; empty for the configured default.</summary>
    [BindProperty]
    public string? Persona { get; set; }

    /// <summary>The first message, pre-filled from the openings shelf.</summary>
    [BindProperty]
    public string? Opening { get; set; }

    /// <summary>Why the story was not started, when it was not.</summary>
    public string? Error { get; private set; }

    /// <summary>Shows the shelf, or the form for the character picked from it.</summary>
    /// <param name="character">The character picked, or null to pick one.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found for a character that is not on the shelf.</returns>
    public async Task<IActionResult> OnGetAsync(string? character, CancellationToken cancellationToken)
    {
        if (character is null)
        {
            Characters = [.. TextLibrary.Names(library.Characters).Select(n => (n, Teaser(n)))];
            return Page();
        }

        if (!Pick(character))
        {
            return NotFound();
        }

        Opening = (await TextLibrary.ReadAsync(library.Openings, Character, cancellationToken).ConfigureAwait(false))?.TrimEnd();
        return Page();
    }

    /// <summary>Starts the story and goes straight into it.</summary>
    /// <param name="character">The character picked on the shelf.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>A redirect into the new story, or the form with an error on it.</returns>
    public async Task<IActionResult> OnPostAsync(string? character, CancellationToken cancellationToken)
    {
        if (character is null || !Pick(character))
        {
            return NotFound();
        }

        var persona = string.IsNullOrWhiteSpace(Persona) ? null : Persona.Trim();

        if (persona is not null && !Personas.Contains(persona, StringComparer.OrdinalIgnoreCase))
        {
            Error = $"There is no persona called \"{persona}\" on the shelf. Nothing was started.";
            return Page();
        }

        var name = string.IsNullOrWhiteSpace(Name) ? Character! : Name.Trim();
        var opening = Opening?.Trim();

        var chat = await conversations.CreateAsync(
                new NewConversation
                {
                    Name = name,
                    Speaker = string.IsNullOrWhiteSpace(Speaker) ? null : Speaker.Trim(),
                    Opening = string.IsNullOrEmpty(opening) ? null : opening,
                    CharacterName = Character,
                    PersonaName = persona,
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Straight into it, and by redirect, so a reload cannot start the same story twice.
        // Started whatever becomes of the model: an unavailable one leaves the story on the
        // default, and the story's page says so once, when it opens.
        if (!string.IsNullOrWhiteSpace(Model))
        {
            var change = await conversations.SetModelAsync(chat.Id, Model, cancellationToken).ConfigureAwait(false);

            if (!change.Saved)
            {
                TempData[StoryModel.NoticeKey] = change.Message;
            }
        }

        return Redirect(StoryModel.Latest(chat.Id));
    }

    /// <summary>Settles the picked character from the shelf, and what the form needs about it.</summary>
    private bool Pick(string character)
    {
        Character = TextLibrary.Names(library.Characters)
            .FirstOrDefault(n => n.Equals(character.Trim(), StringComparison.OrdinalIgnoreCase));

        if (Character is null)
        {
            return false;
        }

        World = TextLibrary.Find(library.Characters, Character) is { } path
            ? Reflow(TextLibrary.Preview(path, int.MaxValue))
            : null;

        Personas = TextLibrary.Names(library.Personas);
        return true;
    }

    /// <summary>Joins a card's hard-wrapped lines back into the paragraphs they were cut from.</summary>
    /// <remarks>
    /// Cards are written in an editor and wrapped at its width, and a break made for a hundred
    /// columns is noise on a phone's forty: every one of them lands mid-sentence. Blank lines
    /// still divide paragraphs, and a line that opens a list item keeps its own line.
    /// </remarks>
    /// <param name="lines">The lines, as the card has them.</param>
    /// <returns>One line per paragraph or list item, paragraphs divided by a blank line.</returns>
    internal static string Reflow(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        var open = false;

        foreach (var line in lines.Select(static l => l.Trim()))
        {
            if (line.Length == 0)
            {
                open = false;
                continue;
            }

            if (open && !line.StartsWith("- ", StringComparison.Ordinal) && !line.StartsWith("• ", StringComparison.Ordinal))
            {
                text.Append(' ').Append(line);
                continue;
            }

            if (text.Length > 0)
            {
                text.Append(open ? "\n" : "\n\n");
            }

            text.Append(line);
            open = true;
        }

        return text.ToString();
    }

    /// <summary>The first line of a character's world: enough to tell two resort cards apart.</summary>
    private string Teaser(string name)
        => TextLibrary.Find(library.Characters, name) is { } path
            ? TextLibrary.Preview(path, int.MaxValue).FirstOrDefault(static l => l.Length > 0) ?? string.Empty
            : string.Empty;
}
