using Microsoft.Extensions.DependencyInjection;
using Airp.Application.Abstractions;
using Airp.Application.Dials;
using Airp.Infrastructure.Providers;
using Spectre.Console;

namespace Airp.Terminal;

/// <summary>The <c>dials</c> verb: the pack in force, and a conversation's choices against it.</summary>
internal static partial class Program
{
    /// <summary>
    /// Lists the pack, writes the shipped one out as a starting point, or sets one dial.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="args">Command line arguments.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> DialsAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Contains("--write", StringComparer.OrdinalIgnoreCase))
        {
            return await WritePackAsync(args, cancellationToken).ConfigureAwait(false);
        }

        var dials = services.GetRequiredService<IDialService>();
        var pack = await dials.PackAsync(cancellationToken).ConfigureAwait(false);
        var conversationId = ValueAfter(args, "--chat");

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            if (ValueAfter(args, "--set") is { } assignment)
            {
                return await SetDialAsync(dials, pack, conversationId, assignment, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (ValueAfter(args, "--clear") is { } cleared)
            {
                await dials.SetAsync(conversationId, cleared, value: null, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(cleared)} cleared; the pack's default applies.[/]");
                return 0;
            }
        }

        var values = string.IsNullOrWhiteSpace(conversationId)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : await dials.ValuesAsync(conversationId, cancellationToken).ConfigureAwait(false);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Dial");
        table.AddColumn("Kind");
        table.AddColumn("State");
        table.AddColumn(string.IsNullOrWhiteSpace(conversationId) ? "Default" : "In force");

        foreach (var dial in pack.Dials)
        {
            var effective = DialEngine.Effective(
                dial,
                (IReadOnlyDictionary<string, string>)values);

            table.AddRow(
                Markup.Escape($"{dial.Title}  ({dial.Key})"),
                Markup.Escape(dial.Kind.ToString().ToLowerInvariant()
                              + (dial.Lever == DialLever.Prompt ? string.Empty : $" → {dial.Maps}")),
                dial.Enabled ? "[green]enabled[/]" : "[yellow]pinned[/]",
                effective is null ? "[grey]not set[/]" : Markup.Escape(Label(dial, effective)));
        }

        AnsiConsole.Write(table);

        foreach (var skipped in pack.Skipped)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Skipped[/] {Markup.Escape(skipped.Key)}: {Markup.Escape(skipped.Reason)}");
        }

        var file = File.Exists(DialService.PackFilePath);
        AnsiConsole.MarkupLine(file
            ? $"[grey]Pack: {Markup.Escape(DialService.PackFilePath)}[/]"
            : "[grey]Pack: the shipped defaults. 'airp dials --write' emits them as a file to edit.[/]");

        return 0;
    }

    private static async Task<int> WritePackAsync(string[] args, CancellationToken cancellationToken)
    {
        var path = DialService.PackFilePath;

        if (File.Exists(path) && !args.Contains("--force", StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(path)} already exists.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --force to replace it with the shipped pack.[/]");
            return 64;
        }

        await File.WriteAllTextAsync(path, DialService.DefaultPackText(), cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Wrote {Markup.Escape(path)}.[/]");
        AnsiConsole.MarkupLine("[grey]Edit it freely; it replaces the shipped pack whole. Delete it to go back.[/]");
        return 0;
    }

    private static async Task<int> SetDialAsync(
        IDialService dials,
        DialPack pack,
        string conversationId,
        string assignment,
        CancellationToken cancellationToken)
    {
        var split = assignment.IndexOf('=', StringComparison.Ordinal);

        if (split <= 0)
        {
            AnsiConsole.MarkupLine("[red]--set wants key=value, for example --set pacing=1 or --set language=Spanish.[/]");
            return 64;
        }

        var key = assignment[..split].Trim();
        var raw = assignment[(split + 1)..].Trim();

        if (pack.Find(key) is not { } dial)
        {
            AnsiConsole.MarkupLine($"[red]The pack has no dial called '{Markup.Escape(key)}'.[/]");
            AnsiConsole.MarkupLine("[grey]'airp dials' lists what exists.[/]");
            return 64;
        }

        // The stored form per kind, validated here rather than trusted: a value the engine
        // cannot read would be stored and then silently say nothing on every turn.
        string? value = dial.Kind switch
        {
            DialKind.Scale => DialEngine.LevelIndex(dial, raw) is not null ? raw : null,
            DialKind.Toggle => bool.TryParse(raw, out _) ? raw.ToLowerInvariant() : null,
            DialKind.Choice => dial.Options.FirstOrDefault(
                o => string.Equals(o.Key, raw, StringComparison.OrdinalIgnoreCase))?.Key,
            DialKind.List => DialEngine.StoreItems(raw.Split(',', StringSplitOptions.TrimEntries)),
            DialKind.Text => string.IsNullOrWhiteSpace(raw) ? null : raw,
            _ => null,
        };

        if (value is null)
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(raw)}' is not a value '{Markup.Escape(key)}' takes.[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Expects(dial))}[/]");
            return 64;
        }

        await dials.SetAsync(conversationId, dial.Key, value, cancellationToken).ConfigureAwait(false);
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(dial.Title)} → {Markup.Escape(Label(dial, value))}[/]");
        return 0;
    }

    /// <summary>Names a stored value the way the settings screen would.</summary>
    private static string Label(DialDefinition dial, string value) => dial.Kind switch
    {
        DialKind.Scale when DialEngine.LevelIndex(dial, value) is { } i => dial.Levels[i].Label,
        DialKind.Toggle => DialEngine.IsOn(value) ? "On" : "Off",
        DialKind.Choice => dial.Options.FirstOrDefault(
            o => string.Equals(o.Key, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value,
        DialKind.List => string.Join(", ", DialEngine.Items(value)),
        _ => value,
    };

    /// <summary>What a dial accepts, said the way its own documentation says it.</summary>
    private static string Expects(DialDefinition dial) => dial.Kind switch
    {
        DialKind.Scale => "A level 0-4: " + string.Join(", ", dial.Levels.Select(static (l, i) => $"{i}={l.Label}")),
        DialKind.Toggle => "true or false",
        DialKind.Choice => "One of: " + string.Join(", ", dial.Options.Select(static o => o.Key)),
        DialKind.List => dial.Accepts ?? "Comma-separated items",
        DialKind.Text => dial.Accepts ?? "Free text",
        _ => string.Empty,
    };
}
