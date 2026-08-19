using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Spectre.Console;

namespace Airp.Terminal;

/// <summary>What the stories have cost.</summary>
internal static partial class Program
{
    /// <summary>
    /// Reports spending over a window, by conversation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The month is the default window because a bill arrives monthly, and the question this
    /// answers is nearly always "what am I on track for this month" rather than "what have I
    /// ever spent".
    /// </para>
    /// <para>
    /// Two columns matter more than the total. <em>Discarded</em> is what was paid for replies
    /// that were then rerolled away — the one line of spending that buys nothing and the only
    /// one the reader can act on directly. <em>Cached</em> is the share of prompt tokens the
    /// provider did not have to read again, which is the prompt layer order working or not.
    /// </para>
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> CostAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider keeps a spend ledger.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        if (!Window(args, out var from, out var to, out var label, out var error))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            // Escaped: the flags are written in square brackets and Spectre reads those as
            // markup, so the usage line for a bad argument used to crash instead of print.
            AnsiConsole.MarkupLine(
                "[grey]" + Markup.Escape("airp cost [--month YYYY-MM] [--all] [--chat <id>] [--json]") + "[/]");
            return 64;
        }

        var chat = ValueAfter(args, "--chat");

        var report = await local.SpendAsync(from, to, chat, cancellationToken).ConfigureAwait(false);

        if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine(Serialise(report, label));
            return 0;
        }

        if (args.Contains("--providers", StringComparer.OrdinalIgnoreCase))
        {
            PrintProviders(report, label);
            return 0;
        }

        Print(report, label);
        return 0;
    }

    /// <summary>Reads the window out of the command line.</summary>
    /// <remarks>
    /// A bad month is refused rather than quietly treated as "everything". A report that
    /// silently widened its window would be read as this month's and acted on.
    /// </remarks>
    private static bool Window(
        string[] args,
        out DateTimeOffset? from,
        out DateTimeOffset? to,
        out string label,
        out string error)
    {
        from = null;
        to = null;
        label = "all time";
        error = string.Empty;

        if (args.Contains("--all", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var month = ValueAfter(args, "--month");

        if (month is null)
        {
            var now = DateTimeOffset.Now;
            from = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
            to = from.Value.AddMonths(1);
            label = from.Value.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            return true;
        }

        if (!DateTime.TryParseExact(
                month,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            error = $"'{month}' is not a month. Write it as 2026-08.";
            return false;
        }

        from = new DateTimeOffset(parsed.Year, parsed.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);
        to = from.Value.AddMonths(1);
        label = from.Value.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return true;
    }

    /// <summary>Draws the report.</summary>
    private static void Print(SpendReport report, string label)
    {
        if (report.Conversations.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]Nothing was spent in {Markup.Escape(label)}.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("chat");
        table.AddColumn(new TableColumn("calls").RightAligned());
        table.AddColumn(new TableColumn("in").RightAligned());
        table.AddColumn(new TableColumn("out").RightAligned());
        table.AddColumn(new TableColumn("cached").RightAligned());
        table.AddColumn(new TableColumn("cost").RightAligned());
        table.AddColumn(new TableColumn("discarded").RightAligned());

        foreach (var line in report.Conversations)
        {
            table.AddRow(
                Markup.Escape(line.Name),
                line.Calls.ToString(CultureInfo.CurrentCulture),
                Tokens(line.PromptTokens),
                Tokens(line.CompletionTokens),
                Share(line.CachedShare),
                Money(line.Cost),
                line.DiscardedCost > 0 ? $"[yellow]{Money(line.DiscardedCost)}[/]" : "—");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[green]{Markup.Escape(label)}: {Money(report.Cost)}[/]"
            + $"[grey] over {report.Calls} call(s), {Tokens(report.PromptTokens)} in / "
            + $"{Tokens(report.CompletionTokens)} out, {Share(report.CachedShare)} cached.[/]");

        var kinds = report.ByKind
            .Where(static k => k.Calls > 0)
            .Select(k => $"{Describe(k.Kind)} {Money(k.Cost)} ({k.Calls})");

        AnsiConsole.MarkupLine($"[grey]  {Markup.Escape(string.Join("  ·  ", kinds))}[/]");

        if (report.DiscardedCost > 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  {Money(report.DiscardedCost)} of that went on replies that were "
                + "regenerated away.[/]");
        }

        if (report.Unpriced > 0)
        {
            // Said out loud rather than folded in. A total quietly missing calls would be
            // trusted exactly as much as a complete one.
            AnsiConsole.MarkupLine(
                $"[grey]  {report.Unpriced} call(s) came back with no price, so the total is a floor.[/]");
        }

        AnsiConsole.MarkupLine("[grey]  Embeddings are not counted; the whole corpus costs under a cent.[/]");
    }

    /// <summary>
    /// Draws what each host served, which is what a deny list is decided from.
    /// </summary>
    /// <remarks>
    /// <em>out/call</em> earns its column. A host whose serving stack is broken does not fail
    /// the request — it answers, is charged for, and returns a few dozen tokens of noise where
    /// the others return eight hundred. Averaged per call that stands out at a glance, where in
    /// a total it hides.
    /// </remarks>
    private static void PrintProviders(SpendReport report, string label)
    {
        if (report.ByProvider.Count == 0)
        {
            AnsiConsole.MarkupLine($"[green]Nothing was spent in {Markup.Escape(label)}.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("served by");
        table.AddColumn(new TableColumn("slug").LeftAligned());
        table.AddColumn(new TableColumn("calls").RightAligned());
        table.AddColumn(new TableColumn("in").RightAligned());
        table.AddColumn(new TableColumn("cached").RightAligned());
        table.AddColumn(new TableColumn("out/call").RightAligned());
        table.AddColumn(new TableColumn("cost").RightAligned());

        foreach (var host in report.ByProvider)
        {
            table.AddRow(
                Markup.Escape(host.Provider),
                Markup.Escape(host.Provider.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal)),
                host.Calls.ToString(CultureInfo.CurrentCulture),
                Tokens(host.PromptTokens),
                Share(host.CachedShare),
                host.PerCall.ToString("N0", CultureInfo.CurrentCulture),
                Money(host.Cost));
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            "[grey]The slug column is a guess at the name the router wants in "
            + "Model:IgnoreProviders — usually right, and copyable from the model's page when "
            + "it is not. A slug matching no host is dropped in silence, so play a turn and "
            + "check 'served by' before believing it took.[/]");
    }

    /// <summary>Renders the report as JSON, for anything that is not a person reading it.</summary>
    private static string Serialise(SpendReport report, string label)
        => JsonSerializer.Serialize(
            new
            {
                window = label,
                fromUtc = report.FromUtc,
                toUtc = report.ToUtc,
                cost = report.Cost,
                discardedCost = report.DiscardedCost,
                calls = report.Calls,
                unpricedCalls = report.Unpriced,
                promptTokens = report.PromptTokens,
                completionTokens = report.CompletionTokens,
                cachedTokens = report.CachedTokens,
                cachedShare = report.CachedShare,
                embeddingsCounted = false,
                byKind = report.ByKind.Select(static k => new
                {
                    kind = Describe(k.Kind),
                    calls = k.Calls,
                    cost = k.Cost,
                }),
                conversations = report.Conversations.Select(static c => new
                {
                    id = c.ConversationId,
                    name = c.Name,
                    speaker = c.Speaker,
                    calls = c.Calls,
                    cost = c.Cost,
                    discardedCalls = c.DiscardedCalls,
                    discardedCost = c.DiscardedCost,
                    promptTokens = c.PromptTokens,
                    completionTokens = c.CompletionTokens,
                    cachedTokens = c.CachedTokens,
                    cachedShare = c.CachedShare,
                    unpricedCalls = c.Unpriced,
                    firstAtUtc = c.FirstAtUtc,
                    lastAtUtc = c.LastAtUtc,
                    byKind = c.ByKind.Select(static k => new
                    {
                        kind = Describe(k.Kind),
                        calls = k.Calls,
                        cost = k.Cost,
                    }),
                }),
            },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            });

    /// <summary>What a kind of work is called in a report.</summary>
    private static string Describe(SpendKind kind) => kind switch
    {
        SpendKind.Reply => "replies",
        SpendKind.Aside => "questions",
        SpendKind.Summary => "compression",
        SpendKind.Facts => "extraction",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Money, to four places.
    /// </summary>
    /// <remarks>
    /// Two would round a real turn — around $0.0028 — to a cent or to nothing, which is the
    /// difference between a report that answers the question and one that says everything cost
    /// zero.
    /// </remarks>
    private static string Money(decimal amount) => amount.ToString("$0.0000", CultureInfo.CurrentCulture);

    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.0", CultureInfo.CurrentCulture) + "M",
        >= 1_000 => (count / 1_000d).ToString("0.0", CultureInfo.CurrentCulture) + "k",
        _ => count.ToString(CultureInfo.CurrentCulture),
    };

    private static string Share(double? fraction)
        => fraction is { } value ? value.ToString("P0", CultureInfo.CurrentCulture) : "—";
}
