using Airp.Application.Abstractions;
using Airp.Application.Dials;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Airp.Web.Pages;

/// <summary>A story's dials — whatever the pack in force declares — read and set together.</summary>
/// <remarks>
/// <para>
/// The terminal's settings screen, as a form. Changes are applied together on Apply, as there:
/// these change every reply afterwards, and passing over a level should not be a decision.
/// </para>
/// <para>
/// A typed value is stored only in a form the dial can read (<see cref="DialEngine.Parse"/>),
/// because one it cannot read would be stored and then silently say nothing on every turn.
/// Leaving a dial on Default clears the story's own choice.
/// </para>
/// </remarks>
/// <param name="dials">The pack and each story's choices.</param>
/// <param name="conversations">The store, for the story itself and its model.</param>
/// <param name="options">Live application options, for the default model and the list to pick from.</param>
public sealed class DialsModel(
    IDialService dials,
    LocalConversationProvider conversations,
    IOptionsMonitor<AirpOptions> options) : PageModel
{
    /// <summary>The model a story uses when it has none of its own.</summary>
    public string DefaultModel => options.CurrentValue.Model.Name;

    /// <summary>
    /// The models the story can be switched to: the configured list, and the story's own model
    /// when it is not on it, so the page never shows a choice it cannot display.
    /// </summary>
    public IReadOnlyList<string> ModelChoices
    {
        get
        {
            var choices = options.CurrentValue.Model.EffectiveChoices
                .Where(c => !string.Equals(c, DefaultModel, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (Chat?.Model is { } own && !choices.Contains(own, StringComparer.OrdinalIgnoreCase))
            {
                choices.Insert(0, own);
            }

            return choices;
        }
    }

    /// <summary>The story.</summary>
    public Chat? Chat { get; private set; }

    /// <summary>The dials the pack offers, in its order.</summary>
    public IReadOnlyList<DialDefinition> Rows { get; private set; } = [];

    /// <summary>The story's own choices, in stored form.</summary>
    public IReadOnlyDictionary<string, string> Stored { get; private set; } = new Dictionary<string, string>();

    /// <summary>What was posted, by dial key; empty or absent means Default.</summary>
    [BindProperty]
    public Dictionary<string, string?> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What was changed, when something was.</summary>
    public string? Applied { get; private set; }

    /// <summary>Why nothing was applied, when nothing was.</summary>
    public string? Error { get; private set; }

    /// <summary>What a dial does when the story has made no choice of its own.</summary>
    /// <param name="dial">The dial.</param>
    /// <returns>The label for the Default option.</returns>
    public static string DefaultLabel(DialDefinition dial)
    {
        ArgumentNullException.ThrowIfNull(dial);

        return dial.Default is null ? "Default — nothing" : "Default — " + DialEngine.Label(dial, dial.Default);
    }

    /// <summary>A stored value as the form shows it: a list comma-separated, anything else as stored.</summary>
    /// <param name="dial">The dial.</param>
    /// <returns>The value to put in the control, or empty for Default.</returns>
    public string Current(DialDefinition dial)
    {
        ArgumentNullException.ThrowIfNull(dial);

        if (!Stored.TryGetValue(dial.Key, out var value))
        {
            return string.Empty;
        }

        return dial.Kind == DialKind.List ? string.Join(", ", DialEngine.Items(value)) : value;
    }

    /// <summary>Shows the dials.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
        => await LoadAsync(id, cancellationToken).ConfigureAwait(false) ? Page() : NotFound();

    /// <summary>Changes the story's model, or clears it back to the default.</summary>
    /// <remarks>
    /// Its own form, apart from the dials: a model is checked against the provider's list
    /// before it is saved, and an unavailable one is refused with what the story stays on —
    /// which has nothing to do with whether the dials beside it were valid.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="model">The model picked, or empty for the default.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>The page, saying what the story now uses or why it did not change.</returns>
    public async Task<IActionResult> OnPostModelAsync(string id, string? model, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var change = await conversations.SetModelAsync(id, model, cancellationToken).ConfigureAwait(false);

        if (change.Saved)
        {
            Applied = change.Message;
        }
        else
        {
            Error = change.Message;
        }

        await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return Page();
    }

    /// <summary>Applies every changed dial, or none if any value is one its dial does not take.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The page, saying what changed or what was refused.</returns>
    public async Task<IActionResult> OnPostAsync(string id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var changes = new List<(DialDefinition Dial, string? Value)>();
        var refused = new List<string>();

        foreach (var dial in Rows)
        {
            var raw = Values.GetValueOrDefault(dial.Key)?.Trim() ?? string.Empty;
            var value = raw.Length == 0 ? null : DialEngine.Parse(dial, raw);

            if (raw.Length > 0 && value is null)
            {
                refused.Add($"{dial.Title} does not take \"{raw}\"");
                continue;
            }

            if (value != Stored.GetValueOrDefault(dial.Key))
            {
                changes.Add((dial, value));
            }
        }

        // All or nothing: applying the valid half of a form would leave the story in a state
        // nobody chose.
        if (refused.Count > 0)
        {
            Error = string.Join("; ", refused) + ". Nothing was changed.";
            return Page();
        }

        foreach (var (dial, value) in changes)
        {
            await dials.SetAsync(id, dial.Key, value, cancellationToken).ConfigureAwait(false);
        }

        Applied = changes.Count == 0
            ? "Nothing was different."
            : "Applied: " + string.Join(", ", changes.Select(static c =>
                c.Dial.Title + " → " + (c.Value is null ? "default" : DialEngine.Label(c.Dial, c.Value)))) + ".";

        await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        return Page();
    }

    private async Task<bool> LoadAsync(string id, CancellationToken cancellationToken)
    {
        Chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (Chat is null)
        {
            return false;
        }

        var pack = await dials.PackAsync(cancellationToken).ConfigureAwait(false);
        Rows = [.. pack.Dials.Where(static d => d.Enabled)];
        Stored = await dials.ValuesAsync(id, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
