using Airp.Application.Options;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Airp.Web.Pages;

/// <summary>One story: read from its latest turns back, and played from the bottom.</summary>
/// <param name="conversations">The store.</param>
/// <param name="options">Live application options, for the length limits.</param>
public sealed class StoryModel(
    LocalConversationProvider conversations,
    IOptionsMonitor<AirpOptions> options) : PageModel
{
    /// <summary>
    /// How many turns a page shows before offering the rest. A real story runs to several
    /// hundred turns of long prose; a phone loads the end, which is what is read.
    /// </summary>
    public const int Recent = 40;

    /// <summary>The story.</summary>
    public Chat? Chat { get; private set; }

    /// <summary>The turns on the page, oldest first.</summary>
    public IReadOnlyList<ChatMessage> Shown { get; private set; } = [];

    /// <summary>How many earlier turns are not on the page.</summary>
    public int Hidden { get; private set; }

    /// <summary>What to call the replies' author: the speaker, or the story's name.</summary>
    public string Speaker => Chat?.Speaker ?? Chat?.Name ?? "Reply";

    /// <summary>What the reader is writing; kept when a send is refused.</summary>
    [BindProperty]
    public string? Draft { get; set; }

    /// <summary>Why the last action did nothing, when it did nothing.</summary>
    public string? Error { get; private set; }

    /// <summary>An answer that is not a turn — <c>/ask</c>, <c>/recap</c>, <c>/help</c> — shown once.</summary>
    public string? Aside { get; private set; }

    /// <summary>Whether the newest turn is a reply, which is the one a reroll replaces.</summary>
    public bool CanReroll => Shown.Count > 0 && Shown[^1].Role == ChatRole.Assistant;

    /// <summary>Where a page returns to after a turn: the start of the newest one.</summary>
    /// <param name="id">The story's id.</param>
    /// <returns>The path.</returns>
    public static string Latest(string id) => $"/story/{Uri.EscapeDataString(id)}#latest";

    /// <summary>Reads the story.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="all">Whether to show every turn rather than the recent ones.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, or not found.</returns>
    public async Task<IActionResult> OnGetAsync(string id, bool all, CancellationToken cancellationToken)
        => await LoadAsync(id, all, cancellationToken).ConfigureAwait(false) ? Page() : NotFound();

    /// <summary>Sends what the reader wrote: a turn, a command, or a refusal.</summary>
    /// <remarks>
    /// <para>
    /// Through the same handling as a message from Janitor, so a command means the same thing in
    /// every front end and a typo is refused everywhere rather than stored somewhere.
    /// </para>
    /// <para>
    /// A turn that was stored ends in a redirect, so reloading the page cannot send it again —
    /// the same words after a reply are a new turn, not a retry. An answer that was not stored is
    /// shown on the page this response draws, because there is nowhere else it could be read.
    /// </para>
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>A redirect to the new reply, or the page with an answer or an error on it.</returns>
    public async Task<IActionResult> OnPostSendAsync(string id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var said = Draft ?? string.Empty;
        var limit = options.CurrentValue.MessageCharacterLimit;

        if (limit > 0 && said.Trim().Length > limit)
        {
            Error = $"That is {said.Trim().Length:N0} characters and the limit is {limit:N0}. Nothing was sent.";
            return Page();
        }

        FrontEndOutcome outcome;

        try
        {
            outcome = await FrontEndTurn.RunAsync(conversations, Chat!, said, cancellationToken).ConfigureAwait(false);
        }
        catch (ReplyMissingException ex)
        {
            // The turn is stored and unanswered. Saying so beats a failure the reader would answer
            // by sending the same words again.
            Draft = null;
            Error = ex.Message + " Your message is saved — do not send it again.";
            await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false);
            return Page();
        }
        catch (AirpException ex)
        {
            Error = ex.Message;
            return Page();
        }

        if (outcome.Refused)
        {
            Error = outcome.Text;
            return Page();
        }

        if (outcome.Stored)
        {
            return Redirect(Latest(id));
        }

        Draft = null;
        Aside = outcome.Text;
        return Page();
    }

    /// <summary>Replaces the newest reply with a new one.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="reason">What was wrong with it.</param>
    /// <param name="instructions">What should be different, if anything.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>A redirect to the new reply, or the page with an error on it.</returns>
    public async Task<IActionResult> OnPostRerollAsync(
        string id,
        RegenerateReason reason,
        string? instructions,
        CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var steer = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();
        var limit = options.CurrentValue.InstructionCharacterLimit;

        if (limit > 0 && steer?.Length > limit)
        {
            Error = $"The instructions are {steer.Length:N0} characters and the limit is {limit:N0}. Nothing was asked for.";
            return Page();
        }

        try
        {
            await conversations
                .RegenerateAsync(id, reason, steer, progress: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AirpException ex)
        {
            Error = ex.Message;
            await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false);
            return Page();
        }

        return Redirect(Latest(id));
    }

    private async Task<bool> LoadAsync(string id, bool all, CancellationToken cancellationToken)
    {
        Chat = await conversations.GetAsync(id, cancellationToken).ConfigureAwait(false);

        if (Chat is null)
        {
            return false;
        }

        var turns = (await conversations.GetMessagesAsync(id, cancellationToken).ConfigureAwait(false))
            .Where(static m => m.IsDialogue)
            .ToList();

        Shown = all ? turns : [.. turns.TakeLast(Recent)];
        Hidden = turns.Count - Shown.Count;
        return true;
    }
}
