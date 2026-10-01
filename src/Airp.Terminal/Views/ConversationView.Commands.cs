using System.Globalization;
using Airp.Application.Text;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;

namespace Airp.Terminal.Views;

/// <summary>
/// The composer's slash commands.
/// </summary>
/// <remarks>
/// <para>
/// They exist because a message is permanent. Typing <c>(OOC: skip to the evening)</c> into the
/// composer sends it: it reaches the model, it is stored for good, it is counted in every later
/// prompt, it gets embedded for retrieval and it may be summarised as something that happened.
/// A command carrying the same words routes them into the prompt layer they belong in — or into
/// no prompt at all — and leaves the transcript untouched.
/// </para>
/// <para>
/// Kept beside the view rather than inside it: <see cref="ConversationView"/> is a long file
/// about reading a transcript, and this is a dozen small answers to "what does this word mean".
/// </para>
/// </remarks>
internal sealed partial class ConversationView
{
    /// <summary>
    /// Runs whatever the composer turned out to hold.
    /// </summary>
    /// <param name="parsed">What the parser made of the composed text.</param>
    /// <param name="context">The render context, for limits and sizes.</param>
    /// <returns>What the shell should do next.</returns>
    private ViewAction Dispatch(SlashParse parsed, RenderContext context)
    {
        if (parsed.Kind == SlashParseKind.Unknown)
        {
            // Refused rather than sent. A typo would otherwise cost what the message it was
            // meant to be would have cost, and land in the transcript as a line the character
            // has to react to — which append-only means nobody can take back.
            return ViewAction.Status(
                $"There is no /{parsed.Text} command. Type /help for the list, or //{parsed.Text} "
                + "to send it as a message.",
                StatusKind.Warning);
        }

        var command = parsed.Command!;
        var argument = parsed.Text;

        if (command.Argument == CommandArgument.Required && argument.Length == 0)
        {
            return ViewAction.Status($"{command.Usage} — nothing has been sent.", StatusKind.Warning);
        }

        // Steering a turn and searching go through the provider seam like the rest of the
        // terminal; the ones that read the character file or the fact table do not, and there
        // is no store to read when this is pointed at another backend.
        if (command.NeedsStore && _provider is null)
        {
            return ViewAction.Status(
                $"/{command.Name} needs the local store, and this conversation is not on it.",
                StatusKind.Warning);
        }

        // A free command's draft goes now; a billed or writing one keeps it until the work has
        // actually landed. The asymmetry is deliberate: re-typing /facts costs a second, and
        // re-typing the question that a failed /ask ate costs the reader the thought behind it.
        if (command.Cost == CommandCost.Free)
        {
            _composer.SetText(string.Empty);
            _composer.MarkSaved();
            _composing = false;
        }

        return command.Name switch
        {
            "do" => Direct(argument, context),
            "focus" => Steer(LocalDirections.Focus(argument), $"Handing the turn to {argument}"),
            "ask" => Ask(argument),

            "card" => Report("Character", ct => StoryReports.CharacterAsync(_provider!, _conversation.Id, ct)),
            "persona" => Report("Persona", ct => StoryReports.PersonaAsync(_provider!, _conversation.Id, ct)),
            "facts" => Report("Facts", ct => StoryReports.FactsAsync(_provider!, _conversation.Id, ct)),
            "trackers" => Report("Trackers", ct => StoryReports.TrackersAsync(_provider!, _conversation.Id, ct)),
            "audit" => Report("Audit", ct => StoryReports.AuditAsync(_provider!, _conversation.Id, ct)),
            "cost" => ShowCost(),
            "search" => Find(argument),
            "help" => ViewAction.Push(new TextPaneView("Commands", "typed in the composer", StoryReports.HelpLines())),

            "fact" => AddFact(argument),
            "tracker" => SetTracker(argument),

            _ => ViewAction.Status($"/{command.Name} is not wired up yet.", StatusKind.Warning),
        };
    }

    // ------------------------------------------------------------ the billed three

    /// <summary>
    /// Runs a <c>/do</c>, which is two commands wearing one name.
    /// </summary>
    /// <remarks>
    /// With a message under it, the direction steers that message's reply. Without one, there
    /// is nothing for the reader to have said and the direction stands alone — the model writes
    /// the next beat under it. The same words either way; what changes is whether a turn of the
    /// reader's own goes into the transcript alongside.
    /// </remarks>
    private ViewAction Direct(string argument, RenderContext context)
    {
        var (direction, message) = SlashCommands.SplitDirection(argument);

        if (direction.Length == 0)
        {
            return ViewAction.Status("/do <direction> — nothing has been sent.", StatusKind.Warning);
        }

        return message.Length == 0
            ? Steer(LocalDirections.Direction(direction), "Writing")
            : Send(context, message, LocalDirections.Direction(direction));
    }

    /// <summary>Asks for a turn under a one-off direction, with no message of the reader's own.</summary>
    /// <param name="instruction">The fully-framed directive.</param>
    /// <param name="label">What the progress line calls it.</param>
    /// <returns>The action that runs it.</returns>
    private ViewAction Steer(string instruction, string label)
    {
        _composing = false;

        return ViewAction.Run(label, async ct =>
        {
            _pending.Begin();

            var before = Visible.Count(static m => m.Role == ChatRole.Assistant);

            IReadOnlyList<ChatMessage> updated;
            try
            {
                updated = await _conversations
                    .ContinueAsync(_conversation.Id, instruction, _pending, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _pending.Clear();
            }

            Accept(updated);
            await RefreshSpendAsync(ct).ConfigureAwait(false);

            return Visible.Count(static m => m.Role == ChatRole.Assistant) > before
                ? Arrived("Reply received.")
                : ViewAction.Status("No reply came back.", StatusKind.Warning);
        });
    }

    /// <summary>Asks a question about the story and shows the answer without storing it.</summary>
    private ViewAction Ask(string question)
    {
        var provider = _provider!;
        _composing = false;

        return ViewAction.Run("Asking", async ct =>
        {
            _pending.Begin();

            AskAnswer answer;
            try
            {
                answer = await provider
                    .AskAsync(_conversation.Id, question, _pending, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _pending.Clear();
            }

            // The draft goes only now. A question that failed is one the reader would have to
            // type again, and it was theirs.
            _composer.SetText(string.Empty);
            _composer.MarkSaved();

            await RefreshSpendAsync(ct).ConfigureAwait(false);

            return ViewAction.Push(new AskView(
                provider,
                _conversation.Id,
                _conversation.Speaker ?? _conversation.Name,
                answer));
        });
    }

    // ------------------------------------------------------------ the free ones

    /// <summary>Opens a pane over one of the reading commands' reports, or says why there is none.</summary>
    /// <remarks>
    /// The lines come from <see cref="StoryReports"/>, which every front end reads from, so
    /// "the facts" or "what this story has cost" means the same thing here and on a phone's
    /// browser.
    /// </remarks>
    /// <param name="title">What the progress line calls it.</param>
    /// <param name="read">Produces the report.</param>
    private static ViewAction Report(string title, Func<CancellationToken, Task<StoryReport>> read)
        => ViewAction.Run(title, async ct =>
        {
            var report = await read(ct).ConfigureAwait(false);

            return report.IsEmpty
                ? ViewAction.Status(report.Subtitle, StatusKind.Info)
                : ViewAction.Push(new TextPaneView(report.Title, report.Subtitle, report.Lines));
        });

    /// <summary>Shows what this story has cost, and brings the header's figure up to date with it.</summary>
    private ViewAction ShowCost()
        => ViewAction.Run("Cost", async ct =>
        {
            var report = await StoryReports.CostAsync(_provider!, _conversation.Id, _conversation.Name, ct)
                .ConfigureAwait(false);

            await RefreshSpendAsync(ct).ConfigureAwait(false);

            return report.IsEmpty
                ? ViewAction.Status(report.Subtitle, StatusKind.Info)
                : ViewAction.Push(new TextPaneView(report.Title, report.Subtitle, report.Lines));
        });

    /// <summary>Runs the in-chat search that <c>/</c> in navigation mode opens.</summary>
    private ViewAction Find(string query)
    {
        _composing = false;
        _composer.SetText(string.Empty);
        _composer.MarkSaved();

        _search.Value = query;
        _activeQuery = query;

        var visible = Visible;
        var hits = visible.Count(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (hits == 0)
        {
            _activeQuery = string.Empty;
            return ViewAction.Status($"\"{query}\" is not in this conversation.", StatusKind.Warning);
        }

        StepMatch(1, visible);
        return ViewAction.Status($"{hits} message(s) match. N for the next one.", StatusKind.Success);
    }

    // ------------------------------------------------------------ the writes

    /// <summary>Pins a statement as true.</summary>
    private ViewAction AddFact(string text)
    {
        var subject = _conversation.Speaker ?? _conversation.Name;

        return ViewAction.Run("Recording", async ct =>
        {
            await _provider!.AddFactAsync(_conversation.Id, subject, text, ct).ConfigureAwait(false);

            _composer.SetText(string.Empty);
            _composer.MarkSaved();
            _composing = false;

            return ViewAction.Status(
                $"Recorded under {subject}. It is in every prompt from the next turn on.",
                StatusKind.Success);
        });
    }

    /// <summary>Sets a meter's value; the value is the last word, so a name can be two.</summary>
    private ViewAction SetTracker(string argument)
    {
        if (SlashCommands.SplitTracker(argument) is not var (name, value))
        {
            return ViewAction.Status(
                "/tracker <name> <value> — the value has to be a number.",
                StatusKind.Warning);
        }

        return ViewAction.Run("Setting", async ct =>
        {
            await _provider!.SetTrackerAsync(_conversation.Id, name, value: value, cancellationToken: ct)
                .ConfigureAwait(false);

            _composer.SetText(string.Empty);
            _composer.MarkSaved();
            _composing = false;

            return ViewAction.Status($"{name} is now {value:0.##}.", StatusKind.Success);
        });
    }

    // ── Branching ────────────────────────────────────────────────────────────────────────

    /// <summary>Starts asking what to call the copy.</summary>
    /// <remarks>
    /// A name is asked for rather than generated because the reader is about to have two
    /// conversations with the same character, the same persona and the same first hundred
    /// turns, and the only thing that will tell them apart in the list is what they are called.
    /// A default is offered so that Enter is a valid answer.
    /// </remarks>
    /// <returns>The resulting action.</returns>
    private ViewAction BeginBranch()
    {
        if (_provider is null)
        {
            return ViewAction.Status(
                "Branching needs the local store.",
                StatusKind.Warning);
        }

        if (Selected is null)
        {
            return ViewAction.Status("Select the turn to branch from first.", StatusKind.Warning);
        }

        _branching = true;
        _branchName.Value = SlashCommands.BranchName(_conversation.Name);


        return ViewAction.None;
    }

    /// <summary>Handles a key while the name is being typed.</summary>
    /// <param name="stroke">The key.</param>
    /// <returns>The resulting action.</returns>
    private ViewAction HandleBranchKey(KeyStroke stroke)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                _branching = false;
                _branchName.Clear();
                return ViewAction.Status("Branch cancelled.");

            case AppCommand.Accept or AppCommand.NewLine:
            {
                var name = _branchName.Value.Trim();
                var from = Selected;

                _branching = false;
                _branchName.Clear();

                if (from is null)
                {
                    return ViewAction.None;
                }

                if (name.Length == 0)
                {
                    return ViewAction.Status("A story needs a name. Nothing was copied.", StatusKind.Warning);
                }

                return ViewAction.Run("Branching", async ct =>
                {
                    var branch = await _provider!
                        .BranchAsync(_conversation.Id, from.Id, name, ct)
                        .ConfigureAwait(false);

                    // The list is cached, and a branch that does not appear until the next
                    // refresh reads as one that was not made.
                    if (_chats is not null)
                    {
                        await _chats.RefreshAsync(ct).ConfigureAwait(false);
                    }

                    return ViewAction.Status(
                        $"Branched into \"{branch.Name}\". The original is untouched.",
                        StatusKind.Success);
                });
            }
        }

        _branchName.Handle(stroke);
        return ViewAction.None;
    }
}
