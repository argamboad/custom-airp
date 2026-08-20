using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Spectre.Console;

namespace Airp.Terminal;

/// <summary>What a story has established: facts, inner thoughts, trackers, and the audit.</summary>
internal static partial class Program
{

    /// <summary>Lists, states and retires what the story holds to be true.</summary>
    /// <remarks>
    /// Extraction is a model reading a transcript, and a model reading a transcript is
    /// sometimes wrong. A wrong fact is worse than a wrong summary: it is asserted into every
    /// prompt from then on, the character acts on it, and the transcript then agrees with it.
    /// This is the way out of that loop, and the piece that makes the world state something the
    /// reader owns rather than something that happens to them.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> FactAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider keeps a world state.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        var positional = Positional(args);
        var action = positional.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "list";

        var chats = await local.ListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ValueAfter(args, "--chat");

        var chat = wanted is null
            ? chats.FirstOrDefault()
            : chats.FirstOrDefault(c =>
                c.Id == wanted || c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (chat is null)
        {
            AnsiConsole.MarkupLine("[yellow]No conversation to work with.[/]");
            return chats.Count == 0 ? 0 : 66;
        }

        switch (action)
        {
            case "add":
                var subject = ValueAfter(args, "--subject");
                var text = positional.ElementAtOrDefault(2) ?? ValueAfter(args, "--text");

                if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(text))
                {
                    AnsiConsole.MarkupLine("[red]A fact needs a subject and a sentence.[/]");
                    AnsiConsole.MarkupLine(
                        "[grey]airp fact add \"Has a scar on her forearm\" --subject Elena [[--chat <name>]][/]");
                    return 64;
                }

                var added = await local.AddFactAsync(chat.Id, subject, text, cancellationToken)
                    .ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]Recorded as {added.Id[..8]}, pinned.[/]");
                AnsiConsole.MarkupLine("[grey]The extractor cannot retire it; you can.[/]");
                return 0;

            case "retire":
                var id = positional.ElementAtOrDefault(2) ?? ValueAfter(args, "--id");

                if (string.IsNullOrWhiteSpace(id))
                {
                    AnsiConsole.MarkupLine("[red]Which fact? Pass the id shown by 'airp fact'.[/]");
                    return 64;
                }

                var retired = await local.RetireFactAsync(chat.Id, id, cancellationToken).ConfigureAwait(false);

                if (retired is null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]No single live fact starts with '{Markup.Escape(id)}'.[/]");
                    return 66;
                }

                AnsiConsole.MarkupLine($"[green]Retired {retired.Id[..8]}.[/]");
                AnsiConsole.MarkupLine("[grey]It stops being sent. It does not stop existing.[/]");
                return 0;

            default:
                var facts = await local.FactsAsync(chat.Id, cancellationToken).ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[bold]{Markup.Escape(chat.Name)}[/]");
                AnsiConsole.WriteLine();

                if (facts.Count == 0)
                {
                    AnsiConsole.MarkupLine("[grey]Nothing established yet.[/]");
                    AnsiConsole.MarkupLine(
                        "[grey]Facts are extracted when older turns are compressed, or stated with "
                        + "'airp fact add'.[/]");
                    return 0;
                }

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("id");
                table.AddColumn("subject");
                table.AddColumn("fact");
                table.AddColumn("from");
                table.AddColumn("until");
                table.AddColumn("by");

                foreach (var fact in facts)
                {
                    var live = fact.ValidToSequence is null;

                    table.AddRow(
                        fact.Id[..8],
                        Markup.Escape(fact.Subject),
                        live
                            ? Markup.Escape(fact.Text)
                            : $"[strikethrough]{Markup.Escape(fact.Text)}[/]",
                        fact.ValidFromSequence.ToString(),
                        fact.ValidToSequence?.ToString() ?? "—",
                        fact.Pinned ? "[bold]you[/]" : Markup.Escape(fact.Model ?? "—"));
                }

                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[grey]{facts.Count(static f => f.ValidToSequence is null)} in force, "
                    + $"{facts.Count(static f => f.ValidToSequence is not null)} retired. "
                    + "Only those in force are sent.[/]");

                return 0;
        }
    }


    /// <summary>
    /// Turns on the line where each character shows what they did not say.
    /// </summary>
    /// <remarks>
    /// The one thing a scene with a model cannot otherwise give you. You can ask a person what
    /// they are really thinking and get a lie; you cannot ask a character at all without
    /// stepping out of the scene to do it.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> InnerThoughtsAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var (local, chat) = await ResolveChatAsync(services, args, cancellationToken).ConfigureAwait(false);

        if (local is null || chat is null)
        {
            return 66;
        }

        var word = Positional(args).ElementAtOrDefault(1)?.ToLowerInvariant();

        if (word is not ("on" or "off"))
        {
            AnsiConsole.MarkupLine("[grey]airp thoughts on|off [[--chat <name>]][/]");
            return 64;
        }

        await local.SetInnerThoughtsAsync(chat.Id, word == "on", cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(word == "on"
            ? $"[green]On for '{Markup.Escape(chat.Name)}'.[/]"
            : $"[green]Off for '{Markup.Escape(chat.Name)}'.[/]");

        if (word == "on")
        {
            AnsiConsole.MarkupLine(
                "[grey]Each character will add one line of what they are keeping back, after "
                + "they speak. Never for you.[/]");
        }

        return 0;
    }


    /// <summary>Lists, adds and removes the meters a story keeps.</summary>
    /// <remarks>
    /// Off unless asked for, and worth knowing why before turning it on: a meter the model can
    /// see is a meter the model writes toward. It stops describing what happened and starts
    /// arranging for the number to move, which is a different story than the one being told.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> TrackerAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var (local, chat) = await ResolveChatAsync(services, args, cancellationToken).ConfigureAwait(false);

        if (local is null || chat is null)
        {
            return 66;
        }

        var positional = Positional(args);
        var action = positional.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "list";
        var name = positional.ElementAtOrDefault(2) ?? ValueAfter(args, "--name");

        switch (action)
        {
            case "add" or "set" when !string.IsNullOrWhiteSpace(name):
                var stored = await local.SetTrackerAsync(
                        chat.Id,
                        name,
                        double.TryParse(ValueAfter(args, "--value"), out var v) ? v : null,
                        double.TryParse(ValueAfter(args, "--max"), out var m) ? m : null,
                        ValueAfter(args, "--means"),
                        ValueAfter(args, "--scale"),
                        ValueAfter(args, "--rule"),
                        cancellationToken)
                    .ConfigureAwait(false);

                AnsiConsole.MarkupLine($"[green]{Markup.Escape(stored.Name)} at {stored.Value}/{stored.Max}.[/]");

                if (string.IsNullOrWhiteSpace(stored.Means))
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]No --means, so the model infers what moves it — and infers "
                        + "differently each turn.[/]");
                }

                return 0;

            case "remove" or "rm" when !string.IsNullOrWhiteSpace(name):
                var gone = await local.RemoveTrackerAsync(chat.Id, name, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine(gone
                    ? $"[green]Removed {Markup.Escape(name)}.[/]"
                    : $"[red]No meter called {Markup.Escape(name)}.[/]");
                return gone ? 0 : 66;

            case "add" or "set" or "remove" or "rm":
                AnsiConsole.MarkupLine("[red]Which meter? Give it a name.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]airp tracker add ADMIRATION --value 40 --means \"…\" --scale \"…\"[/]");
                return 64;

            default:
                var meters = await local.TrackersAsync(chat.Id, cancellationToken).ConfigureAwait(false);

                if (meters.Count == 0)
                {
                    AnsiConsole.MarkupLine($"[grey]'{Markup.Escape(chat.Name)}' keeps no meters.[/]");
                    return 0;
                }

                foreach (var meter in meters)
                {
                    AnsiConsole.MarkupLine(
                        $"[bold]{Markup.Escape(meter.Name)}[/]  {meter.Value}/{meter.Max}"
                        + (meter.Delta == 0 ? string.Empty : $"  [grey]Δ {meter.Delta:+0.#;-0.#}[/]")
                        + (string.IsNullOrWhiteSpace(meter.Note) ? string.Empty : $"  [grey]{Markup.Escape(meter.Note)}[/]"));

                    foreach (var (label, value) in new[]
                             {
                                 ("measures", meter.Means), ("scale", meter.Anchors), ("rule", meter.Rule),
                             })
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            AnsiConsole.MarkupLine($"  [grey]{label}: {Markup.Escape(value)}[/]");
                        }
                    }
                }

                return 0;
        }
    }


    /// <summary>Shows what each reply of a conversation was built from.</summary>
    /// <remarks>
    /// This is what makes the memory layer debuggable rather than a black box. When a character
    /// says something that does not fit, the question worth asking is what it was shown — and
    /// that is answerable only because it was recorded when the prompt was assembled, not
    /// reconstructed afterwards from state that has moved on.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> AuditAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider records what it sent.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        var chats = await local.ListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = Positional(args).ElementAtOrDefault(1);

        var chat = wanted is null
            ? chats.FirstOrDefault()
            : chats.FirstOrDefault(c =>
                c.Id == wanted || c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (chat is null)
        {
            AnsiConsole.MarkupLine(wanted is null
                ? "[yellow]There are no conversations in the local store.[/]"
                : $"[red]No conversation matching '{Markup.Escape(wanted)}'.[/]");

            foreach (var candidate in chats)
            {
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(candidate.Name)}[/]");
            }

            return chats.Count == 0 ? 0 : 66;
        }

        var turns = await local.AuditAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var summaries = await local.SummariesAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var live = (await local.FactsAsync(chat.Id, cancellationToken).ConfigureAwait(false))
            .Where(static f => f.ValidToSequence is null)
            .ToArray();

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(chat.Name)}[/]");

        // The identifier, because this is the only command that can tell you one, and the proxy
        // needs it: Janitor's Custom Prompt carries [[rp:<id>]] to say which conversation a
        // request belongs to. Without it printed somewhere, that setup step cannot be followed.
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(chat.Id)}[/]");
        AnsiConsole.WriteLine();

        if (turns.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No replies yet.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("when");
        table.AddColumn("served by");
        table.AddColumn("est.");
        table.AddColumn("actual");
        table.AddColumn("out");
        table.AddColumn("context");

        foreach (var turn in turns)
        {
            // The estimate sits beside the reported figure on purpose: a budget nobody ever
            // checks against reality is a budget that quietly stopped meaning anything.
            var drift = turn.EstimatedPromptTokens is { } estimate && turn.PromptTokens is { } actual && actual > 0
                ? $"{estimate} ({100.0 * (estimate - actual) / actual:+0;-0}%)"
                : turn.EstimatedPromptTokens?.ToString() ?? "—";

            table.AddRow(
                turn.Hidden ? $"[strikethrough]{turn.Sequence}[/]" : turn.Sequence.ToString(),
                turn.SentAtUtc.LocalDateTime.ToString("MM-dd HH:mm"),
                Markup.Escape(turn.Provider ?? "—"),
                drift,
                turn.PromptTokens?.ToString() ?? "—",
                turn.CompletionTokens?.ToString() ?? "—",
                Markup.Escape(turn.Context ?? "—"));
        }

        AnsiConsole.Write(table);

        if (live.Length > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]In force[/] [grey]— sent with every turn. 'airp fact' to change them[/]");
            AnsiConsole.WriteLine();

            foreach (var fact in live)
            {
                AnsiConsole.MarkupLine(
                    $"  [grey]{fact.Id[..8]}[/]  {Markup.Escape(fact.Subject)}: {Markup.Escape(fact.Text)}"
                    + (fact.Pinned ? "  [grey](pinned)[/]" : string.Empty));
            }
        }

        if (summaries.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Summaries[/] [grey]— derived; deleting them loses nothing[/]");

            foreach (var summary in summaries)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(
                    $"[grey]turns {summary.FromSequence}-{summary.ToSequence} "
                    + $"({summary.MessageCount} of them), written by "
                    + $"{Markup.Escape(summary.Model ?? "?")}[/]");

                AnsiConsole.WriteLine(summary.Text);
            }
        }

        var strikes = turns.Count(static t => t.Hidden);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[grey]{turns.Count} reply(ies), {strikes} hidden but still stored. "
            + "Struck-through rows were rerolled or deleted.[/]");

        return 0;
    }

    /// <summary>Erases the conversations already deleted.</summary>
    /// <remarks>
    /// Deleting a chat hides it; the transcript stays on disk in the clear, which is not what
    /// "delete" led anyone to expect. This finishes it, and because that cannot be undone it
    /// shows exactly what would go and does nothing until asked twice.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> PurgeAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider keeps a store to purge.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        var waiting = await local.PurgeableAsync(cancellationToken).ConfigureAwait(false);

        if (waiting.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing to purge — no deleted conversations are still stored.[/]");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("deleted");
        table.AddColumn("name");
        table.AddColumn(new TableColumn("messages").RightAligned());

        foreach (var candidate in waiting)
        {
            table.AddRow(
                Markup.Escape(candidate.DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")),
                Markup.Escape(candidate.Name),
                candidate.Messages.ToString());
        }

        AnsiConsole.Write(table);

        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]{waiting.Count} conversation(s) and "
                + $"{waiting.Sum(static c => c.Messages)} message(s) would be erased for good.[/]");
            AnsiConsole.MarkupLine("[grey]Nothing has been touched. Run 'airp purge --yes' to go ahead.[/]");
            return 0;
        }

        var report = await local.PurgeDeletedAsync(cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[green]Purged {report.Conversations} conversation(s): {report.Messages} message(s), "
            + $"{report.Summaries} summary(ies), {report.Facts} fact(s), {report.Trackers} tracker(s), "
            + $"{report.Asides} question(s).[/]");
        AnsiConsole.MarkupLine("[grey]The database was vacuumed, so the space is actually released.[/]");

        if (report.LedgerKept.Rows > 0)
        {
            // Said plainly, because it is the one thing purge deliberately does not erase.
            AnsiConsole.MarkupLine(
                $"[grey]{report.LedgerKept.Rows} spend record(s) worth "
                + $"{report.LedgerKept.Cost:$0.0000} were kept: they hold no story text, and "
                + "dropping them would make every cost report covering that period wrong.[/]");
        }

        return 0;
    }

    /// <summary>
    /// Throws away a conversation's derived memory and builds it again from the transcript.
    /// </summary>
    /// <remarks>
    /// The escape hatch for a memory produced by a version with a bug in it. A story that has
    /// already been played cannot be played again, but summaries, facts and embeddings are
    /// derived, so they can be. Hand-written facts are not derived and are kept.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the work.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> RebuildAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider keeps a memory to rebuild.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        var chats = await local.ListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = Positional(args).ElementAtOrDefault(1);

        var chat = wanted is null
            ? null
            : chats.FirstOrDefault(c =>
                c.Id == wanted || c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (chat is null)
        {
            // No default conversation here, unlike 'airp audit'. Reading the wrong story's audit
            // wastes a glance; rebuilding the wrong story's memory spends money on it.
            AnsiConsole.MarkupLine(wanted is null
                ? "[red]Name the conversation to rebuild.[/]"
                : $"[red]No conversation matching '{Markup.Escape(wanted)}'.[/]");

            foreach (var candidate in chats)
            {
                AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(candidate.Name)}[/]");
            }

            return 64;
        }

        var summaries = await local.SummariesAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var facts = await local.FactsAsync(chat.Id, cancellationToken).ConfigureAwait(false);
        var pinned = facts.Count(static f => f.Pinned);

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(chat.Name)}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]{summaries.Count} summary(ies) and {facts.Count - pinned} extracted fact(s) "
            + $"would be thrown away and produced again.[/]");

        if (pinned > 0)
        {
            AnsiConsole.MarkupLine($"[grey]{pinned} pinned fact(s) will be kept — those are yours, not the model's.[/]");
        }

        if (!args.Contains("--yes", StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                "[yellow]This spends model calls: one summary and one extraction per stretch.[/]");
            AnsiConsole.MarkupLine(
                Markup.Escape("Nothing has been touched. Run 'airp rebuild "
                    + (wanted ?? chat.Name) + " --yes' to go ahead."));

            return 0;
        }

        var report = await AnsiConsole.Status()
            .StartAsync("Rebuilding…", async ctx =>
            {
                var progress = new Progress<string>(message => ctx.Status(Markup.Escape(message)));
                return await local.RebuildMemoryAsync(chat.Id, progress, cancellationToken).ConfigureAwait(false);
            })
            .ConfigureAwait(false);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(string.Empty);
        table.AddColumn(new TableColumn("before").RightAligned());
        table.AddColumn(new TableColumn("after").RightAligned());

        table.AddRow("summaries", report.SummariesRemoved.ToString(), report.SummariesWritten.ToString());
        table.AddRow("extracted facts", report.FactsRemoved.ToString(), report.FactsExtracted.ToString());
        table.AddRow("pinned facts", report.PinnedKept.ToString(), report.PinnedKept.ToString());

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[green]{report.MessagesCovered} message(s) are now covered by a summary.[/]");
        AnsiConsole.MarkupLine(
            "[grey]What it cost is in 'airp cost --chat'. The old calls are still there: "
            + "the ledger records money spent, not memory kept.[/]");

        return 0;
    }
}
