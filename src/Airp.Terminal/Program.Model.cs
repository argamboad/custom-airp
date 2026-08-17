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

/// <summary>Talking to and about the model: ask, models, secrets, configuration.</summary>
internal static partial class Program
{

    /// <summary>Sends one message to the configured model and prints the reply.</summary>
    /// <remarks>
    /// A smoke test with a purpose. It is the shortest path to answering "does my key work,
    /// and do I like how this model writes", which is a question best settled by reading a
    /// reply rather than a leaderboard — and settling it needs no storage, no adapter and no
    /// browser. <c>--system</c> is here because a roleplay model judged without a character
    /// in front of it is not being judged on the job it will do.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> AskAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var text = Positional(args).ElementAtOrDefault(1);

        if (string.IsNullOrWhiteSpace(text))
        {
            AnsiConsole.MarkupLine("[red]Nothing to ask.[/]");
            AnsiConsole.MarkupLine("[grey]airp ask \"your message\" [[--model <id>]] [[--system \"...\"]][/]");
            return 64;
        }

        var model = ValueAfter(args, "--model");
        var system = ValueAfter(args, "--system");
        var client = services.GetRequiredService<ILanguageModelClient>();
        var options = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;

        var messages = new List<ModelMessage>();
        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ModelMessage(ModelRole.System, system));
        }

        messages.Add(new ModelMessage(ModelRole.User, text));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var reply = await AnsiConsole.Status()
            .StartAsync($"Asking {model ?? options.Model.Name}…", async _ =>
                await client.CompleteAsync(messages, model, cancellationToken: cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        stopwatch.Stop();

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(reply.Text);
        AnsiConsole.WriteLine();

        var cost = reply.PromptTokens is { } input && reply.CompletionTokens is { } output
            ? $"{input} in / {output} out · "
            : string.Empty;

        var host = reply.Provider is { Length: > 0 } served ? $" via {served}" : string.Empty;

        AnsiConsole.MarkupLine(
            $"[grey]{Markup.Escape(reply.Model ?? model ?? options.Model.Name)}{Markup.Escape(host)} · "
            + $"{cost}{stopwatch.Elapsed.TotalSeconds:F1}s[/]");

        if (reply.WasTruncated)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Cut off at the {options.Model.MaxTokens}-token ceiling — raise Model:MaxTokens for longer replies.[/]");
        }

        return 0;
    }


    /// <summary>Lists the model identifiers the configured API offers.</summary>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line; <c>--find</c> filters the list.</param>
    /// <param name="cancellationToken">Token used to abort the call.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> ListModelsAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var client = services.GetRequiredService<ILanguageModelClient>();
        var filter = ValueAfter(args, "--find");

        var models = await AnsiConsole.Status()
            .StartAsync("Reading the model list…", async _ =>
                await client.ListModelsAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        var shown = string.IsNullOrWhiteSpace(filter)
            ? models
            : [.. models.Where(m => m.Contains(filter, StringComparison.OrdinalIgnoreCase))];

        foreach (var id in shown)
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(id)}");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[grey]{shown.Count} of {models.Count} model(s).[/]");
        return 0;
    }


    /// <summary>Stores, describes or removes an API key.</summary>
    /// <remarks>
    /// The value is never taken from the command line, because a command line lands in the
    /// shell's history file and stays there. It is prompted for instead, and the prompt hides
    /// what is typed.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the operation.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> SecretAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var positional = Positional(args);
        var action = positional.ElementAtOrDefault(1)?.ToLowerInvariant();
        var name = positional.ElementAtOrDefault(2)
            ?? services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue.Model.ApiKeyName;

        var secrets = services.GetRequiredService<ISecretStore>();

        switch (action)
        {
            case "set":
                var value = AnsiConsole.Prompt(
                    new TextPrompt<string>($"Paste the value for [bold]{Markup.Escape(name)}[/]:").Secret());

                await secrets.SetAsync(name, value.Trim(), cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Stored, encrypted for this Windows account.[/]");
                AnsiConsole.MarkupLine(
                    "[grey]It now takes precedence over any environment variable of the same name.[/]");
                return 0;

            case "remove":
                await secrets.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"[green]Removed from the encrypted store.[/]");
                AnsiConsole.MarkupLine(
                    $"[grey]Now reading: {Markup.Escape(await secrets.DescribeAsync(name, cancellationToken).ConfigureAwait(false))}[/]");
                return 0;

            case "show" or null:
                var where = await secrets.DescribeAsync(name, cancellationToken).ConfigureAwait(false);
                AnsiConsole.MarkupLine($"{Markup.Escape(name)}: [grey]{Markup.Escape(where)}[/]");
                return 0;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown action '{Markup.Escape(action)}'.[/]");
                AnsiConsole.MarkupLine("[grey]airp secret set|show|remove [[NAME]][/]");
                return 64;
        }
    }


    /// <summary>Shows what is actually in effect, and where everything lives.</summary>
    /// <remarks>
    /// The one command that answers "what is this process going to do", so it reports the
    /// model and its budgets alongside the paths. The API key appears by name only — the value
    /// never passes through configuration, precisely so a command like this cannot print it.
    /// </remarks>
    private static int PrintConfiguration(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfigurationService>();
        var options = configuration.Current;

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Value");

        table.AddRow("Configuration file", configuration.ConfigurationFilePath);
        table.AddRow("Application data", AppPaths.Root);
        table.AddRow("Database", AppPaths.Resolve(options.DatabaseFile));
        table.AddRow("Characters", Path.Combine(AppPaths.Root, "characters"));
        table.AddRow("Personas", Path.Combine(AppPaths.Root, "personas"));
        table.AddRow("Logs", AppPaths.Logs);
        table.AddRow("Model", options.Model.Name);
        table.AddRow("Background model", options.Model.BackgroundModel ?? "(same as model)");
        table.AddRow("Embedding model", options.Model.EmbeddingModel);
        table.AddRow("API key", $"secret '{options.Model.ApiKeyName}' (value never shown)");
        table.AddRow("Context budget", $"{options.Model.ContextBudget:N0} tokens");
        table.AddRow("Max reply", $"{options.Model.MaxTokens:N0} tokens");
        table.AddRow("Default persona", options.DefaultPersona ?? "(none)");
        table.AddRow("Theme", options.Theme.ToString());
        table.AddRow("Keyboard", options.Keyboard.ToString());
        table.AddRow("Auto refresh", $"{options.AutoRefreshSeconds}s");

        AnsiConsole.Write(table);
        return 0;
    }
}
