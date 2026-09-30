using System.Globalization;
using System.Text;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Application.Text;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Airp.Web.Pages;

/// <summary>One story: read from its latest turns back, and played from the bottom.</summary>
/// <param name="conversations">The store.</param>
/// <param name="library">The shelves, for the snippets the composer expands.</param>
/// <param name="export">Renders the transcript for a download.</param>
/// <param name="options">Live application options, for the length limits.</param>
public sealed class StoryModel(
    LocalConversationProvider conversations,
    TextLibrary library,
    IExportService export,
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

    /// <summary>The snippet picked from the list, to be put into the box.</summary>
    [BindProperty]
    public string? Snippet { get; set; }

    /// <summary>What the last action did, when it changed only what is on the page.</summary>
    public string? Notice { get; private set; }

    /// <summary>Why the last action did nothing, when it did nothing.</summary>
    public string? Error { get; private set; }

    /// <summary>An answer that is not a turn — <c>/ask</c>, <c>/recap</c>, <c>/facts</c> and the rest — shown once.</summary>
    public string? Aside { get; private set; }

    /// <summary>The bare answer to an <c>/ask</c>, which the page offers to pin as a fact.</summary>
    public string? Answer { get; private set; }

    /// <summary>What a <c>/search</c> found, each linked to its turn.</summary>
    public IReadOnlyList<SearchMatch>? Matches { get; private set; }

    /// <summary>The snippets the composer expands, by name.</summary>
    public IReadOnlyList<string> Snippets { get; private set; } = [];

    /// <summary>
    /// Whether the newest turn is a reply that can be written again — not a written opening
    /// with nothing said since (<see cref="RegenerateReasons.CanReplace"/>).
    /// </summary>
    public bool CanReroll { get; private set; }

    /// <summary>Whether there is a reply to carry on from.</summary>
    public bool CanContinue => Shown.Any(static m => m.Role == ChatRole.Assistant);

    /// <summary>A turn's position among the visible ones, from 1, which its anchor and a search both use.</summary>
    /// <param name="shownIndex">Its index in <see cref="Shown"/>.</param>
    /// <returns>The number.</returns>
    public int Number(int shownIndex) => Hidden + shownIndex + 1;

    /// <summary>The name a branch is offered, so that Branch is a valid answer without typing.</summary>
    public string BranchName => SlashCommands.BranchName(Chat?.Name ?? string.Empty);

    /// <summary>Where a page returns to after a turn: the start of the newest one.</summary>
    /// <param name="id">The story's id.</param>
    /// <returns>The path.</returns>
    public static string Latest(string id) => $"/story/{Uri.EscapeDataString(id)}#latest";

    /// <summary>Where a numbered turn is, whether or not it is among the recent ones on the page.</summary>
    /// <param name="number">The turn's number.</param>
    /// <returns>The link.</returns>
    public string LinkTo(int number)
    {
        var total = Hidden + Shown.Count;
        var anchor = number == total ? "latest" : $"t{number}";
        var id = Uri.EscapeDataString(Chat!.Id);

        return number > Hidden
            ? $"/story/{id}#{anchor}"
            : $"/story/{id}?all=true#{anchor}";
    }

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
    /// Snippets and emoji shortcodes are expanded first. The terminal expands them as they are
    /// typed; a page without a script has only the moment of sending, and a <c>:storm</c> stored
    /// literally would be a permanent turn nobody meant.
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

        // A snippet picked but not inserted is inserted now rather than sent: the reader has not
        // seen it in the box, and a sent page is permanent.
        if (!string.IsNullOrWhiteSpace(Snippet))
        {
            return InsertPicked();
        }

        var said = ShortcodeScanner.ExpandAll(Draft ?? string.Empty, SnippetText);
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

        // A fact or a meter was written: the page's own view of the story is stale.
        await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false);

        Draft = null;
        Aside = outcome.Text;
        Answer = outcome.Answer;
        Matches = outcome.Matches;
        return Page();
    }

    /// <summary>Puts the picked snippet at the end of the box and hands the box back, unsent.</summary>
    /// <remarks>
    /// The terminal expands a snippet into the composer, where it can still be edited before
    /// anything is sent. Without a script the cursor's position is not known, so the end of what
    /// is written is the only place it can go.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The page, with the snippet in the box.</returns>
    public async Task<IActionResult> OnPostInsertAsync(string id, CancellationToken cancellationToken)
        => await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false) ? InsertPicked() : NotFound();

    /// <summary>Adds a snippet's text to a draft: straight on if it is empty or ends in space, after a space otherwise.</summary>
    /// <param name="draft">What is in the box.</param>
    /// <param name="text">The snippet's text.</param>
    /// <returns>The draft with the snippet at its end.</returns>
    public static string AppendSnippet(string? draft, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var written = draft ?? string.Empty;

        return written.Length == 0 || char.IsWhiteSpace(written[^1])
            ? written + text
            : written + " " + text;
    }

    private PageResult InsertPicked()
    {
        var name = Snippet?.Trim() ?? string.Empty;

        if (name.Length == 0)
        {
            Error = "Pick a snippet first.";
            return Page();
        }

        if (SnippetText(name) is not { } text)
        {
            Error = $"There is no snippet called \"{name}\" on the shelf any more. Nothing was added.";
            return Page();
        }

        Draft = AppendSnippet(Draft, text);
        Notice = $"Added {name} to the end of your message. Nothing has been sent.";
        return Page();
    }

    /// <summary>Lets the story carry on from its last reply, with nothing from the reader.</summary>
    /// <remarks>
    /// No confirmation, as in the terminal: reading on is the whole point, and the button says
    /// that it costs credits.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>A redirect to the reply, or the page with an error on it.</returns>
    public async Task<IActionResult> OnPostContinueAsync(string id, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        if (!CanContinue)
        {
            Error = "There is no reply to carry on from yet.";
            return Page();
        }

        try
        {
            await conversations
                .ContinueAsync(id, instruction: null, progress: null, cancellationToken: cancellationToken)
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

    /// <summary>Pins an <c>/ask</c> answer as a fact, as the terminal's answer pane does.</summary>
    /// <param name="id">The story's id.</param>
    /// <param name="answer">The answer, as the page was given it.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The page, saying what was pinned.</returns>
    public async Task<IActionResult> OnPostPinAsync(string id, string? answer, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var outcome = await FrontEndTurn.PinAsync(conversations, Chat!, answer ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Refused)
        {
            Error = outcome.Text;
        }
        else
        {
            Aside = outcome.Text;
        }

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

    /// <summary>Copies the story up to a turn into a new one, and goes into the copy.</summary>
    /// <remarks>
    /// Branching keeps everything and destroys nothing, so there is no confirmation, as in the
    /// terminal: the worst a mistaken tap does is add one story to the list. The page goes into
    /// the copy because a branch is made to be played.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="messageId">The last turn the copy keeps.</param>
    /// <param name="name">What to call the copy.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>A redirect into the copy, or the page with an error on it.</returns>
    public async Task<IActionResult> OnPostBranchAsync(
        string id,
        string? messageId,
        string? name,
        CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: false, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Error = "A story needs a name. Nothing was copied.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            return BadRequest();
        }

        try
        {
            var branch = await conversations.BranchAsync(id, messageId, name.Trim(), cancellationToken).ConfigureAwait(false);
            return Redirect(Latest(branch.Id));
        }
        catch (AirpException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    /// <summary>The visible transcript as a file to save: Markdown, JSON or plain text.</summary>
    /// <remarks>
    /// The file is named by the date and nothing else. A phone announces a download by its name,
    /// and the tab already keeps a story's name off the screen for the same reason.
    /// </remarks>
    /// <param name="id">The story's id.</param>
    /// <param name="format">Which format.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The file, or not found.</returns>
    public async Task<IActionResult> OnGetExportAsync(string id, ExportFormat format, CancellationToken cancellationToken)
    {
        if (!await LoadAsync(id, all: true, cancellationToken).ConfigureAwait(false))
        {
            return NotFound();
        }

        var text = export.Render(
            new ConversationTranscript
            {
                ConversationId = Chat!.Id,
                Title = Chat.Name,
                Speaker = Chat.Speaker,
                Messages = Shown,
            },
            format);

        var (extension, type) = format switch
        {
            ExportFormat.Json => ("json", "application/json"),
            ExportFormat.PlainText => ("txt", "text/plain"),
            _ => ("md", "text/markdown"),
        };

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return File(Encoding.UTF8.GetBytes(text), type + "; charset=utf-8", $"story-{stamp}.{extension}");
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
        CanReroll = RegenerateReasons.CanReplace(turns);
        Snippets = TextLibrary.Names(library.Snippets);
        return true;
    }

    /// <summary>A snippet's text by name, trimmed as the terminal inserts it; null when there is none.</summary>
    private string? SnippetText(string name)
        => TextLibrary.Find(library.Snippets, name) is { } path
            ? System.IO.File.ReadAllText(path).TrimEnd()
            : null;
}
