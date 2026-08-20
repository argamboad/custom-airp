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

            "card" => Show("Character", ResolveCharacterAsync, "no character definition"),
            "persona" => Show("Persona", ResolvePersonaAsync, "no persona"),
            "facts" => ShowFacts(),
            "trackers" => ShowTrackers(),
            "audit" => ShowAudit(),
            "cost" => ShowCost(),
            "search" => Find(argument),
            "help" => ViewAction.Push(new TextPaneView("Commands", "typed in the composer", HelpLines())),

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
                ? ViewAction.Status("Reply received.", StatusKind.Success)
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

    /// <summary>Opens a pane over text resolved from the library.</summary>
    /// <param name="title">Pane title.</param>
    /// <param name="resolve">Produces the text and a line saying where it came from.</param>
    /// <param name="missing">What to say when there is nothing to show.</param>
    private ViewAction Show(
        string title,
        Func<CancellationToken, Task<(string? Text, string Source)>> resolve,
        string missing)
        => ViewAction.Run(title, async ct =>
        {
            var (text, source) = await resolve(ct).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(text)
                ? ViewAction.Status($"This conversation has {missing}.", StatusKind.Warning)
                : ViewAction.Push(new TextPaneView(title, source, [.. text.Replace("\r\n", "\n").Split('\n')]));
        });

    /// <summary>
    /// Resolves the character exactly as a turn would.
    /// </summary>
    /// <remarks>
    /// The same three-branch rule the prompt uses — the conversation's own text, then the file
    /// it names, then nothing — because a pane that resolved differently would answer "what is
    /// this character" with something the model has never been sent.
    /// </remarks>
    private async Task<(string? Text, string Source)> ResolveCharacterAsync(CancellationToken cancellationToken)
    {
        var conversation = await _provider!.RawAsync(_conversation.Id, cancellationToken).ConfigureAwait(false);

        var text = await TextLibrary.ResolveAsync(
                _library.Characters,
                conversation?.CharacterDefinition,
                conversation?.CharacterName,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return (text, Describe(conversation?.CharacterDefinition, conversation?.CharacterName, null));
    }

    /// <summary>Resolves the persona exactly as a turn would.</summary>
    private async Task<(string? Text, string Source)> ResolvePersonaAsync(CancellationToken cancellationToken)
    {
        var conversation = await _provider!.RawAsync(_conversation.Id, cancellationToken).ConfigureAwait(false);
        var fallback = _options?.CurrentValue.DefaultPersona;

        var text = await TextLibrary.ResolveAsync(
                _library.Personas,
                conversation?.Persona,
                conversation?.PersonaName,
                fallback,
                cancellationToken)
            .ConfigureAwait(false);

        return (text, Describe(conversation?.Persona, conversation?.PersonaName, fallback));
    }

    /// <summary>Says which of the three branches the text came from.</summary>
    /// <remarks>
    /// Worth a line of its own. "I edited the file and it had no effect" is almost always a
    /// conversation holding its own copy, or naming a different file than the one being edited,
    /// and neither is visible from anywhere else.
    /// </remarks>
    private static string Describe(string? own, string? named, string? fallback)
        => !string.IsNullOrWhiteSpace(own) ? "written for this conversation"
        : !string.IsNullOrWhiteSpace(named) ? $"from the file {named}"
        : !string.IsNullOrWhiteSpace(fallback) ? $"the default, {fallback}"
        : "nothing resolved";

    /// <summary>Shows what is being injected as true right now.</summary>
    private ViewAction ShowFacts()
        => ViewAction.Run("Facts", async ct =>
        {
            var facts = await _provider!.FactsAsync(_conversation.Id, ct).ConfigureAwait(false);
            var live = facts.Where(static f => f.ValidToSequence is null).ToList();

            if (live.Count == 0)
            {
                return ViewAction.Status(
                    "Nothing is being injected as true yet. /fact <statement> writes one.",
                    StatusKind.Info);
            }

            var lines = new List<string>();

            foreach (var group in live.GroupBy(static f => f.Subject).OrderBy(static g => g.Key))
            {
                lines.Add(group.Key);

                foreach (var fact in group.OrderBy(static f => f.ValidFromSequence))
                {
                    lines.Add("  · " + fact.Text);
                }

                lines.Add(string.Empty);
            }

            var retired = facts.Count - live.Count;

            return ViewAction.Push(new TextPaneView(
                "Facts",
                retired > 0 ? $"{live.Count} live, {retired} retired" : $"{live.Count} live",
                lines));
        });

    /// <summary>Shows the meters and their values.</summary>
    private ViewAction ShowTrackers()
        => ViewAction.Run("Trackers", async ct =>
        {
            var meters = await _provider!.TrackersAsync(_conversation.Id, ct).ConfigureAwait(false);

            if (meters.Count == 0)
            {
                return ViewAction.Status(
                    "This conversation keeps no meters. /tracker <name> <value> starts one.",
                    StatusKind.Info);
            }

            var lines = new List<string>();

            foreach (var meter in meters)
            {
                lines.Add($"{meter.Name}  {meter.Value:0.##} / {meter.Max:0.##}"
                    + (meter.Delta != 0 ? $"   last moved {meter.Delta:+0.##;-0.##}" : string.Empty));

                if (!string.IsNullOrWhiteSpace(meter.Note))
                {
                    lines.Add("  " + meter.Note);
                }

                lines.Add(string.Empty);
            }

            return ViewAction.Push(new TextPaneView("Trackers", $"{meters.Count} meter(s)", lines));
        });

    /// <summary>Shows what the recent turns cost, layer by layer.</summary>
    private ViewAction ShowAudit()
        => ViewAction.Run("Audit", async ct =>
        {
            var turns = await _provider!.AuditAsync(_conversation.Id, ct).ConfigureAwait(false);
            var asides = await _provider!.AsidesAsync(_conversation.Id, ct).ConfigureAwait(false);

            if (turns.Count == 0 && asides.Count == 0)
            {
                return ViewAction.Status("Nothing has been generated in this conversation yet.", StatusKind.Info);
            }

            var lines = new List<string>();

            foreach (var turn in turns.OrderByDescending(static t => t.Sequence).Take(12))
            {
                lines.Add($"#{turn.Sequence}{(turn.Hidden ? "  (rolled back)" : string.Empty)}"
                    + $"   {turn.PromptTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} in, "
                    + $"{turn.CompletionTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} out"
                    + $"   served by {turn.Provider ?? "unknown"}");

                if (!string.IsNullOrWhiteSpace(turn.Context))
                {
                    lines.Add("  " + turn.Context);
                }

                lines.Add(string.Empty);
            }

            // Asides are billed and store no message, so they appear nowhere else. Leaving them
            // out is how a per-chat cost quietly stops adding up.
            if (asides.Count > 0)
            {
                lines.Add("Questions asked out of character");

                foreach (var aside in asides.Take(8))
                {
                    lines.Add($"  · {aside.Question}");
                    lines.Add($"    {aside.PromptTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} in, "
                        + $"{aside.CompletionTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "?"} out"
                        + $"   served by {aside.Provider ?? "unknown"}");
                }
            }

            return ViewAction.Push(new TextPaneView(
                "Audit",
                asides.Count > 0 ? $"{turns.Count} turn(s), {asides.Count} question(s)" : $"{turns.Count} turn(s)",
                lines));
        });

    /// <summary>
    /// Shows what this story has cost, and what it bought.
    /// </summary>
    /// <remarks>
    /// The header carries one figure because one is all a header should carry. This is where the
    /// figure comes apart: which kinds of call it went on, how much of the prompt the provider
    /// served from cache, and what was paid for replies that were then rerolled away — the only
    /// line of spending here that bought nothing at all.
    /// </remarks>
    private ViewAction ShowCost()
        => ViewAction.Run("Cost", async ct =>
        {
            var report = await _provider!
                .SpendAsync(conversationId: _conversation.Id, cancellationToken: ct)
                .ConfigureAwait(false);

            if (report.Conversations.FirstOrDefault() is not { } spend)
            {
                return ViewAction.Status("Nothing has been spent on this story yet.", StatusKind.Info);
            }

            _spent = spend;

            var lines = new List<string>
            {
                $"{spend.Cost:$0.0000}   over {spend.Calls} billed call(s)",
                string.Empty,
            };

            foreach (var kind in spend.ByKind)
            {
                lines.Add($"  {Name(kind.Kind),-14}{kind.Cost:$0.0000}   {kind.Calls} call(s)");
            }

            lines.Add(string.Empty);
            lines.Add($"  {"tokens",-14}{spend.PromptTokens:N0} in, {spend.CompletionTokens:N0} out");

            lines.Add(spend.CachedShare is { } share
                ? $"  {"cached",-14}{share:P0} of the prompt was served from cache"
                : $"  {"cached",-14}the provider never said");

            if (spend.DiscardedCost > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"  {spend.DiscardedCost:$0.0000} went on {spend.DiscardedCalls} reply(ies) "
                    + "you regenerated away. They are still in the audit.");
            }

            if (spend.Unpriced > 0)
            {
                lines.Add(string.Empty);
                lines.Add($"  {spend.Unpriced} call(s) came back with no price, so this is a floor.");
            }

            lines.Add(string.Empty);
            lines.Add("  Embeddings are not counted; the whole corpus costs under a cent.");

            return ViewAction.Push(new TextPaneView("Cost", _conversation.Name, lines));
        });

    /// <summary>What a kind of billed work is called on screen.</summary>
    private static string Name(Airp.Infrastructure.Storage.Local.SpendKind kind) => kind switch
    {
        Airp.Infrastructure.Storage.Local.SpendKind.Reply => "replies",
        Airp.Infrastructure.Storage.Local.SpendKind.Aside => "questions",
        Airp.Infrastructure.Storage.Local.SpendKind.Summary => "compression",
        Airp.Infrastructure.Storage.Local.SpendKind.Facts => "extraction",
        _ => kind.ToString().ToLowerInvariant(),
    };

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

    /// <summary>The command list, as the pane shows it.</summary>
    private static IReadOnlyList<string> HelpLines()
    {
        var lines = new List<string>();

        foreach (var group in SlashCommands.All.GroupBy(static c => c.Cost))
        {
            lines.Add(group.Key switch
            {
                CommandCost.Billed => "Billed — these call the model",
                CommandCost.Write => "These write to the conversation",
                _ => "Free — these only read what is already here",
            });

            foreach (var command in group)
            {
                lines.Add($"  {command.Usage}");
                lines.Add($"      {command.Summary}");
            }

            lines.Add(string.Empty);
        }

        lines.Add("A message that genuinely starts with a slash is sent by doubling it: //like this.");

        return lines;
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

    /// <summary>
    /// Sets a meter's value.
    /// </summary>
    /// <remarks>
    /// The value is the last word, not the first, so a meter whose name is two words still
    /// works: <c>/tracker her patience 40</c>. Splitting the other way would have made the name
    /// a single token and quietly created a second meter the first time one was typed with a
    /// space in it.
    /// </remarks>
    private ViewAction SetTracker(string argument)
    {
        var cut = argument.LastIndexOf(' ');

        if (cut <= 0
            || !double.TryParse(
                argument[(cut + 1)..],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return ViewAction.Status(
                "/tracker <name> <value> — the value has to be a number.",
                StatusKind.Warning);
        }

        var name = argument[..cut].Trim();

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
        _branchName.Value = Suggest(_conversation.Name);


        return ViewAction.None;
    }

    /// <summary>A name that will not collide with the one already in the list.</summary>
    /// <remarks>
    /// Numbered rather than suffixed with the turn, because a reader branching twice from the
    /// same message would otherwise get the same name twice — and the turn number means nothing
    /// once the copy has grown its own turns.
    /// </remarks>
    /// <param name="name">The name of the conversation being branched.</param>
    /// <returns>The suggestion.</returns>
    private static string Suggest(string name)
    {
        var trimmed = name.Trim();
        var open = trimmed.LastIndexOf(" (", StringComparison.Ordinal);

        if (open > 0
            && trimmed.EndsWith(')')
            && int.TryParse(
                trimmed.AsSpan(open + 2, trimmed.Length - open - 3),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{trimmed[..open]} ({number + 1})");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{trimmed} (2)");
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

/// <summary>
/// The wording sent to the model for the directions a command can carry.
/// </summary>
/// <remarks>
/// A direction cannot go to the model bare. Every layer above it has spent its words telling
/// the model to stay in character and to leave the reader's turn alone, and a bare
/// <c>have Mariana leave</c> arriving after all of that reads as something the reader said out
/// loud. The frame says whose instruction this is and restates the one rule it is most likely
/// to be read as suspending.
/// </remarks>
internal static class LocalDirections
{
    /// <summary>Frames a free-form direction for the next turn.</summary>
    /// <param name="direction">What the reader typed.</param>
    /// <returns>The directive.</returns>
    public static string Direction(string direction)
        => "A direction for this reply, from the reader, out of character. It is not something "
        + "anyone said aloud and nobody in the scene knows it was given. Write the next turn "
        + "following it, and still never write the user's words, actions or thoughts.\n\n"
        + direction.Trim();

    /// <summary>Frames a hand-off to a named character.</summary>
    /// <param name="who">The name the reader typed.</param>
    /// <returns>The directive.</returns>
    public static string Focus(string who)
        => "A direction for this reply, from the reader, out of character. Give this turn to "
        + who.Trim()
        + ". Let them carry it — what they do, say and notice — and keep everyone else to what "
        + "they need for that. Still never write the user's words, actions or thoughts.";
}
