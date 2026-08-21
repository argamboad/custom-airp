using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Logging;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Spectre.Console;

namespace Airp.Terminal;

/// <summary>Entry point and command dispatch.</summary>
internal static partial class Program
{

    /// <summary>
    /// Puts the console into UTF-8 so everything past ASCII survives the trip to the screen.
    /// </summary>
    /// <remarks>
    /// Without this the encoding is whatever code page the console happens to be on, and on
    /// Windows that is usually one of the legacy 8-bit pages — which cannot represent the box
    /// drawing, the spinner, the em dashes or any emoji, and replaces each of them with a
    /// question mark. Best-effort: a console that refuses is a console drawing the fallback
    /// glyphs, not a reason to fail to start.
    /// </remarks>
    private static void UseUtf8Output()
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // No console, or one that will not be told. Neither is fatal.
        }
    }

    /// <summary>Runs the requested command.</summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        AppPaths.EnsureCreated();
        UseUtf8Output();

        var command = Positional(args).FirstOrDefault()?.ToLowerInvariant() ?? "run";

        if (command is "help" or "--help" or "-h" or "/?")
        {
            PrintUsage();
            return 0;
        }

        // Only the interactive terminal wants a background refresh loop. For a one-shot
        // command it would wake up mid-run for nothing.
        using var host = BuildHost(args, runBackgroundSync: command is "run");
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync().ConfigureAwait(false);

        try
        {
            return command switch
            {
                "settings" => await ChatSettingsAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "export" => await ExportAllAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "ask" => await AskAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "models" => await ListModelsAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "secret" => await SecretAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "new" => await NewConversationAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "import" => await ImportAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "audit" => await AuditAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "rebuild" => await RebuildAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "purge" => await PurgeAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "cost" => await CostAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "fact" or "facts" => await FactAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "send" => await SendMessageAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "library" => await LibraryAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "character" or "characters" => await LibraryEntryAsync(
                        host.Services, args, LibraryKind.Character, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "persona" or "personas" => await LibraryEntryAsync(
                        host.Services, args, LibraryKind.Persona, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "snippet" or "snippets" => await LibraryEntryAsync(
                        host.Services, args, LibraryKind.Snippet, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "thoughts" => await InnerThoughtsAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "tracker" or "trackers" => await TrackerAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "config" => await ConfigurationAsync(host.Services, args, lifetime.ApplicationStopping)
                    .ConfigureAwait(false),
                "run" => await RunTerminalAsync(host.Services, lifetime.ApplicationStopping).ConfigureAwait(false),
                _ => Unknown(command),
            };
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (AirpException ex)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(ex.RecoveryHint)}[/]");
            return 1;
        }
        catch (Exception ex)
        {
            // A stack trace on the console is never the right answer for a user-facing tool;
            // the log has the full detail for anyone who needs it.
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            AnsiConsole.MarkupLine($"[grey]Full detail: {Markup.Escape(AppPaths.Logs)}[/]");
            host.Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(Program))
                .LogError(ex, "The command failed.");

            return 70;
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    private static IHost BuildHost(string[] args, bool runBackgroundSync)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // The host has already added appsettings.json and appsettings.{Environment}.json from
        // the content root, which is the binary's directory. Only the user's own file, the
        // environment and the command line are left to add, in that order.
        builder.Configuration
            .AddJsonFile(AppPaths.ConfigurationFile, optional: true, reloadOnChange: true)
            .AddInMemoryCollection(ReadEnvironmentOverrides())
            .AddCommandLine(args, CommandLineMappings);

        var provider = new FileLoggerProvider(AppPaths.Logs);
        builder.Logging.ClearProviders();
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddProvider(provider);

        builder.Services.AddSingleton(provider);
        builder.Services.AddAirpInfrastructure(builder.Configuration);
        builder.Services.AddAirpApplication(runBackgroundSync);
        builder.Services.AddSingleton<Shell>();

        return builder.Build();
    }

    /// <summary>
    /// Maps <c>AIRP_*</c> environment variables onto the <c>Airp</c> configuration
    /// section.
    /// </summary>
    /// <remarks>
    /// The stock environment-variable provider strips the prefix and leaves a root-level key,
    /// which would never reach options bound from a section — so <c>AIRP_Theme=Light</c>
    /// would be silently ignored. This re-roots each variable under the section so the
    /// documented form works: <c>AIRP_Theme</c> and <c>AIRP_Model__Name</c> become
    /// <c>Airp:Theme</c> and <c>Airp:Model:Name</c>.
    /// </remarks>
    /// <returns>Configuration entries ready to add to the builder.</returns>
    internal static IEnumerable<KeyValuePair<string, string?>> ReadEnvironmentOverrides()
        => EnvironmentOverrides.Read();

    private static IDictionary<string, string> CommandLineMappings => new Dictionary<string, string>
    {
        ["--provider"] = "Airp:Provider",
        ["--theme"] = "Airp:Theme",
        ["--transcript-width"] = "Airp:TranscriptWidthPercent",
        ["--keyboard"] = "Airp:Keyboard",
        ["--refresh"] = "Airp:AutoRefreshSeconds",
    };

    private static async Task<int> RunTerminalAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfigurationService>();
        await configuration.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);

        var options = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;

        var stack = await BuildInitialStackAsync(services, options, cancellationToken).ConfigureAwait(false);
        var shell = services.GetRequiredService<Shell>();

        await shell.RunAsync(stack, cancellationToken).ConfigureAwait(false);

        AnsiConsole.Clear();
        return 0;
    }

    /// <summary>
    /// Builds the view stack the shell starts with, restoring the last chat when the
    /// user left one open and session restore is enabled.
    /// </summary>
    private static async Task<IReadOnlyList<IView>> BuildInitialStackAsync(
        IServiceProvider services,
        AirpOptions options,
        CancellationToken cancellationToken)
    {
        var list = ActivatorUtilities.CreateInstance<ChatListView>(services);

        if (!options.RestoreSession)
        {
            return [list];
        }

        // The most recently active chat, which the list orders first. The old flavour kept
        // the exact chat under a cache key; the store already remembers activity order, and
        // "the one you were in" and "the newest one" are the same thing in one-player use.
        var chats = services.GetRequiredService<IChatService>();
        var chat = (await chats.GetAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault();

        // Reopened as whatever the row actually is. Restoring a chat straight onto the
        // chat screen showed a definition a chat does not have, and left the reader
        // looking at a page about something they had never asked for.
        return chat is null
            ? [list]
            : [list, RowView.For(chat, services)];
    }

    /// <summary>
    /// The leading arguments, up to the first flag.
    /// </summary>
    /// <remarks>
    /// The grammar is <c>airp [command] [argument] [--flags…]</c>, so everything from the
    /// first <c>-</c> onwards belongs to a flag — either as the flag or as its value.
    /// <para>
    /// Scanning the whole line for the first token without a leading dash, which is what this
    /// replaced, could not tell a command from a flag's value: <c>airp --provider local</c>
    /// found <c>local</c> and reported it as an unknown command. The same fault was already
    /// reachable through <c>--profile</c> and <c>--theme</c>.
    /// </para>
    /// </remarks>
    /// <param name="args">The command line.</param>
    /// <returns>The positional arguments, in order.</returns>
    private static string[] Positional(string[] args)
        => [.. args.TakeWhile(static a => !a.StartsWith('-'))];

    /// <summary>Reads the value following a flag, if it was given one.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="flag">The flag to look for.</param>
    /// <returns>The value, or <see langword="null"/>.</returns>
    private static string? ValueAfter(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith('-')
            ? args[index + 1]
            : null;
    }

    /// <summary>Finds the local adapter and the conversation a command is about.</summary>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line; <c>--chat</c> picks one by name or id.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The adapter and the chat, either of which is null when there is none.</returns>
    private static async Task<(LocalConversationProvider? Local, Chat? Chat)> ResolveChatAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider keeps this.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return (null, null);
        }

        var chats = await local.ListAsync(cancellationToken).ConfigureAwait(false);
        var wanted = ValueAfter(args, "--chat");

        var chat = wanted is null
            ? chats.FirstOrDefault()
            : chats.FirstOrDefault(c =>
                c.Id == wanted || c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (chat is null)
        {
            AnsiConsole.MarkupLine("[yellow]No conversation to work with.[/]");
        }

        return (local, chat);
    }

    /// <summary>
    /// Works out whether an argument names something in the library or points at a file.
    /// </summary>
    /// <param name="wanted">What the reader typed, or null.</param>
    /// <param name="folder">The library folder to look in.</param>
    /// <param name="what">The word for it, for the message when neither is found.</param>
    /// <param name="cancellationToken">Token used to abort the read.</param>
    /// <returns>The name to store, the text to store, and whether nothing matched.</returns>
    private static async Task<(string? Name, string? Text, bool Failed)> ResolveAsync(
        string? wanted,
        string folder,
        string what,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            return (null, null, false);
        }

        if (await TextLibrary.ReadAsync(folder, wanted, cancellationToken).ConfigureAwait(false) is not null)
        {
            return (Path.GetFileNameWithoutExtension(wanted.Trim()), null, false);
        }

        if (File.Exists(wanted))
        {
            return (null, await File.ReadAllTextAsync(wanted, cancellationToken).ConfigureAwait(false), false);
        }

        AnsiConsole.MarkupLine($"[red]No {what} called '{Markup.Escape(wanted)}', and no file there.[/]");

        var names = TextLibrary.Names(folder);

        AnsiConsole.MarkupLine(names.Count > 0
            ? $"[grey]Available: {Markup.Escape(string.Join(", ", names))}[/]"
            : $"[grey]Nothing there yet. Put a .txt in {Markup.Escape(folder)}[/]");

        return (null, null, true);
    }

    /// <summary>Builds a stable file name for a chat, so a second run skips rather than duplicates.</summary>
    /// <param name="chat">The chat being written.</param>
    /// <param name="format">Output format, which decides the extension.</param>
    /// <returns>A file name, without a directory.</returns>
    private static string BulkExportFileName(Chat chat, ExportFormat format)
    {
        var extension = format switch
        {
            ExportFormat.Json => "json",
            ExportFormat.Markdown => "md",
            _ => "txt",
        };

        // The identifier goes in the name because two chats can share a title, and a collision
        // here would quietly leave one of them out of the archive.
        var name = Airp.Application.Services.ExportService.Slug(chat.Name);
        var id = Airp.Application.Services.ExportService.Slug(chat.Id);
        return $"{name}-{id}.{extension}";
    }

    /// <summary>Reads a numeric level following a flag.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="flag">The flag to look for.</param>
    /// <returns>The level, or <see langword="null"/> when the flag was not given.</returns>
    private static int? LevelAfter(string[] args, string flag)
        => int.TryParse(ValueAfter(args, flag), out var level) ? level : null;

    private static int Unknown(string command)
    {
        AnsiConsole.MarkupLine($"[red]Unknown command '{Markup.Escape(command)}'.[/]");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine("[bold]airp[/] — a keyboard-driven terminal client");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage[/]");
        AnsiConsole.MarkupLine("  airp [grey][[run]][/]              Start the terminal interface (default)");
        AnsiConsole.MarkupLine("  airp settings --chat <id>   Show a conversation's lust, length and creativity");
        AnsiConsole.MarkupLine("  airp settings --chat <id> --lust 2 --length 3 --creativity 2");
        AnsiConsole.MarkupLine("  airp export             Write every conversation to disk, one file each");
        AnsiConsole.MarkupLine("  airp export --format md Use Markdown instead of JSON");
        AnsiConsole.MarkupLine("  airp export --out <dir> Write somewhere other than the export directory");
        AnsiConsole.MarkupLine("  airp export --overwrite Rewrite files that are already there");
        AnsiConsole.MarkupLine("  airp audit [[<chat>]]     Show what each reply was built from, and what it cost");
        AnsiConsole.MarkupLine("  airp fact               List what the story holds to be true");
        AnsiConsole.MarkupLine("  airp fact add \"…\" --subject Elena   State one yourself; the model cannot retire it");
        AnsiConsole.MarkupLine("  airp fact retire <id>   Mark one as no longer true");
        AnsiConsole.MarkupLine("  airp rebuild <chat>     Show what rebuilding its memory would replace");
        AnsiConsole.MarkupLine("  airp rebuild <chat> --yes   Make the summaries and facts again from the transcript");
        AnsiConsole.MarkupLine("  airp purge              List the deleted conversations still on disk");
        AnsiConsole.MarkupLine("  airp purge --yes        Erase them for good, and vacuum the database");

        AnsiConsole.MarkupLine("  airp library --samples  Write a worked example into the library");
        AnsiConsole.MarkupLine("  airp cost               What this month has cost, by chat");
        AnsiConsole.MarkupLine("  airp cost --month 2026-07   A particular month");
        AnsiConsole.MarkupLine("  airp cost --all --json  Everything, as JSON");
        AnsiConsole.MarkupLine("  airp cost --providers   Which host served what, and how well");
        AnsiConsole.MarkupLine("  airp import             Bring exported transcripts into the local store");
        AnsiConsole.MarkupLine("  airp import <path> --character elena.txt");
        AnsiConsole.MarkupLine("  airp new \"Name\"         Start a conversation in the local store");
        AnsiConsole.MarkupLine("  airp new \"…\" --speaker Elena --character elena --persona allan");
        AnsiConsole.MarkupLine("  airp send \"message\"     Play one turn without the terminal, and print the reply");
        AnsiConsole.MarkupLine("  airp send \"…\" --chat <name>   …in a conversation other than the most recent");
        AnsiConsole.MarkupLine("  airp library            Show the characters and personas on disk");
        AnsiConsole.MarkupLine("  airp character new <name>    Start a character file from the skeleton");
        AnsiConsole.MarkupLine("  airp character edit <name>   Open one in your editor (EDITOR, else notepad)");
        AnsiConsole.MarkupLine("  airp character show|remove <name>");
        AnsiConsole.MarkupLine("  airp persona new|show|remove <name>   The same, for who you play as");
        AnsiConsole.MarkupLine("  airp snippet new|edit|show|remove <name>   Prose the composer expands via :name + Tab");
        AnsiConsole.MarkupLine("  airp thoughts on|off    Show what each character is not saying");
        AnsiConsole.MarkupLine("  airp tracker            List the meters a story keeps");
        AnsiConsole.MarkupLine("  airp tracker add ADMIRATION --value 40 --means \"…\" --scale \"…\"");
        AnsiConsole.MarkupLine("  airp ask \"message\"      Send one message to the model and print the reply");
        AnsiConsole.MarkupLine("  airp ask \"…\" --model <id>   …using a different model than the configured one");
        AnsiConsole.MarkupLine("  airp ask \"…\" --system \"…\"   …with a character definition in front of it");
        AnsiConsole.MarkupLine("  airp models             List the models the API offers");
        AnsiConsole.MarkupLine("  airp models --find deepseek  …filtered");
        AnsiConsole.MarkupLine("  airp secret set         Store an API key, encrypted for this account");
        AnsiConsole.MarkupLine("  airp secret show        Say where the key is read from, without printing it");
        AnsiConsole.MarkupLine("  airp config             Show the effective configuration and paths");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Options[/]");
        AnsiConsole.MarkupLine("  --theme <name>             Dark, Light, HighContrast or Monochrome");
        AnsiConsole.MarkupLine("  --keyboard <mode>          Standard or Vim");
        AnsiConsole.MarkupLine("  --refresh <seconds>        Background refresh interval; 0 disables it");
    }
}
