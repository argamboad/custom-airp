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

/// <summary>Conversations from the command line: new, send, import, export, settings.</summary>
internal static partial class Program
{

    /// <summary>Starts a conversation in the local store.</summary>
    /// <remarks>
    /// Only the local adapter can do this — conversations on a site are created on the site —
    /// so the command resolves the concrete type rather than a provider interface, and says so
    /// plainly when the configured adapter is a different one.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the write.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> NewConversationAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider can start a conversation.[/]");
            AnsiConsole.MarkupLine("[grey]Set Airp:Provider to 'local', or pass --provider local.[/]");
            return 64;
        }

        var name = Positional(args).ElementAtOrDefault(1) ?? ValueAfter(args, "--name");

        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine("[red]The conversation needs a name.[/]");
            AnsiConsole.MarkupLine(
                "[grey]airp new \"Name\" [[--speaker Elena]] [[--character <file>]] [[--opening \"…\"]][/]");
            return 64;
        }

        // Both flags take a name from the library or the path of a file, and which it is is
        // decided by what exists rather than by spelling them differently. A library name is
        // stored as a name, so later edits to the description reach this conversation; a path
        // is read once, because a file outside the library is not something this looks after.
        var library = new TextLibrary();
        library.EnsureCreated();

        var configured = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;

        var (characterName, definition, characterFailed) = await ResolveAsync(
            ValueAfter(args, "--character"), library.Characters, "character", cancellationToken)
            .ConfigureAwait(false);

        if (characterFailed)
        {
            return 66;
        }

        var (personaName, persona, personaFailed) = await ResolveAsync(
            ValueAfter(args, "--persona"), library.Personas, "persona", cancellationToken)
            .ConfigureAwait(false);

        if (personaFailed)
        {
            return 66;
        }

        var chat = await local.CreateAsync(
            new Airp.Domain.Conversations.NewConversation
            {
                Name = name,
                Speaker = ValueAfter(args, "--speaker"),
                CharacterDefinition = definition,
                Opening = ValueAfter(args, "--opening"),
                CharacterName = characterName,
                PersonaName = personaName,
                Persona = persona,
            },
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine($"[green]Started '{Markup.Escape(chat.Name)}'.[/]");
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(chat.Id)}[/]");
        AnsiConsole.MarkupLine("[grey]Run 'airp' to open it.[/]");
        return 0;
    }


    /// <summary>Plays one turn without the terminal, and prints the reply.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately against <c>IConversationProvider</c> rather than the local adapter, so this
    /// exercises the same call the conversation view makes, for whichever flavour is configured.
    /// A test that drove turns through the proxy instead would leave the terminal's own send
    /// path uncovered, which is the one a reader actually uses.
    /// </para>
    /// <para>
    /// The reply goes to standard output on its own, with everything else as markup around it,
    /// so a script can read the reply without parsing decoration.
    /// </para>
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line; <c>--chat</c> picks one by name or id.</param>
    /// <param name="cancellationToken">Token used to abort the exchange.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> SendMessageAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var text = Positional(args).ElementAtOrDefault(1) ?? ValueAfter(args, "--text");

        if (string.IsNullOrWhiteSpace(text))
        {
            AnsiConsole.MarkupLine("[red]Nothing to send.[/]");
            AnsiConsole.MarkupLine("[grey]airp send \"your message\" [[--chat <name>]][/]");

            // The message has to come before the flags, because a positional argument is only
            // read up to the first one. --text is the way round it for anyone who writes the
            // flags first, and saying so here is cheaper than the alternative: "Nothing to send"
            // in response to a line that plainly has something to send.
            AnsiConsole.MarkupLine(
                "[grey]With the flags first, name it: airp send --chat <name> --text \"your message\"[/]");

            return 64;
        }

        var chats = await services.GetRequiredService<IChatProvider>()
            .ListAsync(cancellationToken)
            .ConfigureAwait(false);

        var wanted = ValueAfter(args, "--chat");

        var chat = wanted is null
            ? chats.FirstOrDefault()
            : chats.FirstOrDefault(c =>
                c.Id == wanted || c.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase));

        if (chat is null)
        {
            AnsiConsole.MarkupLine(wanted is null
                ? "[yellow]No conversation to send to.[/]"
                : $"[yellow]No conversation matching '{Markup.Escape(wanted)}'.[/]");

            AnsiConsole.MarkupLine("[grey]airp new \"name\" --speaker <who> to start one.[/]");
            return 66;
        }

        var added = await services.GetRequiredService<IConversationProvider>()
            .SendAsync(chat.Id, text, instruction: null, progress: null, cancellationToken)
            .ConfigureAwait(false);

        var reply = added.LastOrDefault(static m => m.Role == ChatRole.Assistant);

        if (reply is null)
        {
            // The turn was stored and nothing came back. Saying so plainly matters more than an
            // exit code: the next thing a person does is retype the message, which stores it twice.
            AnsiConsole.MarkupLine("[yellow]Your message was stored, but no reply came back.[/]");
            AnsiConsole.MarkupLine("[grey]Do not send it again — refresh the conversation first.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(chat.Name)}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(reply.Text);
        AnsiConsole.WriteLine();

        return 0;
    }


    /// <summary>Brings exported transcripts into the local store.</summary>
    /// <remarks>
    /// Safe to run twice: conversations already present are recognised and left alone, so a
    /// partial import can simply be repeated rather than unpicked.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the import.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> ImportAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        if (services.GetService<LocalConversationProvider>() is not { } local)
        {
            AnsiConsole.MarkupLine("[red]Only the local provider has somewhere to import to.[/]");
            AnsiConsole.MarkupLine("[grey]Pass --provider local.[/]");
            return 64;
        }

        var options = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;
        var path = Positional(args).ElementAtOrDefault(1)
                   ?? AppPaths.Resolve(options.ExportDirectory);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Nothing at {Markup.Escape(path)}.[/]");
            AnsiConsole.MarkupLine("[grey]airp import [[<file or directory>]] [[--character <file>]][/]");
            return 66;
        }

        var definitionPath = ValueAfter(args, "--character");
        string? definition = null;

        if (!string.IsNullOrWhiteSpace(definitionPath))
        {
            if (!File.Exists(definitionPath))
            {
                AnsiConsole.MarkupLine($"[red]No character file at {Markup.Escape(definitionPath)}.[/]");
                return 66;
            }

            definition = await File.ReadAllTextAsync(definitionPath, cancellationToken).ConfigureAwait(false);
        }

        AnsiConsole.MarkupLine($"[grey]Reading {Markup.Escape(path)}[/]");
        AnsiConsole.WriteLine();

        var progress = new Progress<string>(line => AnsiConsole.MarkupLine($"  {Markup.Escape(line)}"));
        var result = await local.ImportAsync(path, definition, progress, cancellationToken).ConfigureAwait(false);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]{result.Imported} conversation(s), {result.Messages} message(s).[/]"
            + $"[grey] {result.Skipped} already there, {result.Ignored} file(s) ignored.[/]");

        if (result.Imported > 0 && definition is null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[yellow]These carry no character definition — the export never had one.[/]");
            AnsiConsole.MarkupLine(
                "[grey]They continue on their own history, which the model can read. Pass "
                + "--character <file> to give it the character in so many words.[/]");
        }

        return 0;
    }


    /// <summary>Writes every conversation in the account to disk, one file each.</summary>
    /// <remarks>
    /// The interactive export takes one conversation at a time, which is right when you want a
    /// copy of what you are reading and useless when the whole account has to come down before
    /// a subscription lapses and the conversations stop being reachable.
    /// <para>
    /// Two properties matter more than speed. File names are derived from the chat rather than
    /// stamped with the clock, so running it twice skips what it already has instead of writing
    /// second copies; and one conversation that fails does not abandon the rest, because the
    /// run that counts is the one before the account goes away. Between them, the command can
    /// be re-run until the failure list is empty.
    /// </para>
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the run.</param>
    /// <returns>The process exit code; non-zero when any conversation failed.</returns>
    private static async Task<int> ExportAllAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var requested = ValueAfter(args, "--format") ?? "json";
        ExportFormat format;

        switch (requested.ToLowerInvariant())
        {
            case "json":
                format = ExportFormat.Json;
                break;
            case "markdown" or "md":
                format = ExportFormat.Markdown;
                break;
            case "text" or "txt" or "plain":
                format = ExportFormat.PlainText;
                break;
            default:
                AnsiConsole.MarkupLine($"[red]Unknown format '{Markup.Escape(requested)}'.[/]");
                AnsiConsole.MarkupLine("[grey]Use json, markdown or text.[/]");
                return 64;
        }

        var options = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;
        var directory = ValueAfter(args, "--out") ?? options.ExportDirectory;
        Directory.CreateDirectory(directory);

        var overwrite = args.Contains("--overwrite", StringComparer.OrdinalIgnoreCase);
        var chats = services.GetRequiredService<IChatService>();
        var conversations = services.GetRequiredService<IConversationService>();
        var export = services.GetRequiredService<IExportService>();

        // The list is re-read rather than taken from the cache. A chat missing from this run is
        // a chat missing from the archive, and a stale cache is exactly where one would hide.
        var all = await AnsiConsole.Status()
            .StartAsync("Reading the chat list…", async _ =>
                await chats.RefreshAsync(cancellationToken).ConfigureAwait(false))
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[grey]{all.Count} conversation(s) → {Markup.Escape(Path.GetFullPath(directory))}[/]");
        AnsiConsole.WriteLine();

        var written = 0;
        var skipped = 0;
        var failures = new List<(string Name, string Reason)>();

        foreach (var (chat, index) in all.Select(static (c, i) => (c, i)))
        {
            var label = $"[{index + 1}/{all.Count}] {chat.Name}";
            var path = Path.Combine(directory, BulkExportFileName(chat, format));

            if (!overwrite && File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(label)} — already written[/]");
                skipped++;
                continue;
            }

            try
            {
                var messages = await conversations
                    .GetMessagesAsync(chat.Id, forceRefresh: true, cancellationToken)
                    .ConfigureAwait(false);

                var transcript = new ConversationTranscript
                {
                    ConversationId = chat.Id,
                    Title = chat.Name,
                    Speaker = chat.Speaker,
                    Messages = messages,
                };

                await export.ExportAsync(transcript, format, path, cancellationToken).ConfigureAwait(false);

                AnsiConsole.MarkupLine(
                    $"[green]{Markup.Escape(label)}[/] [grey]— {messages.Count} message(s)[/]");
                written++;
            }
            catch (OperationCanceledException)
            {
                // A cancelled run is the reader's decision, not a per-chat failure; what has
                // already been written stays on disk and a later run resumes from there.
                throw;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(label)} — {Markup.Escape(ex.Message)}[/]");
                failures.Add((chat.Name, ex.Message));
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]{written} written[/][grey], {skipped} already on disk, {failures.Count} failed.[/]");

        if (failures.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]These did not come down. Run 'airp export' again to retry only these:[/]");

            foreach (var (name, reason) in failures)
            {
                AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(name)} [grey]— {Markup.Escape(reason)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]These files hold your conversations in full. Treat them as private.[/]");

        return failures.Count > 0 ? 1 : 0;
    }


    /// <summary>
    /// Shows, and optionally changes, a conversation's reply settings.
    /// </summary>
    /// <remarks>
    /// The same three dials the terminal offers under <c>S</c>, from a script. With no level
    /// arguments this only reads, which makes it a safe way to see what a conversation is
    /// currently set to.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="cancellationToken">Token used to abort the command.</param>
    /// <returns>The process exit code.</returns>
    private static async Task<int> ChatSettingsAsync(
        IServiceProvider services,
        string[] args,
        CancellationToken cancellationToken)
    {
        var conversationId = ValueAfter(args, "--chat");
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            AnsiConsole.MarkupLine("[red]Which conversation? Pass --chat <id>.[/]");
            AnsiConsole.MarkupLine("[grey]Conversation identifiers are shown in the terminal's chat list.[/]");
            return 64;
        }

        var conversations = services.GetRequiredService<IConversationService>();
        var options = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;

        var changes = new ChatSettings
        {
            Lust = LevelAfter(args, "--lust"),
            ResponseLength = LevelAfter(args, "--length"),
            Creativity = LevelAfter(args, "--creativity"),
        };

        var settings = changes.IsEmpty
            ? await conversations.GetSettingsAsync(conversationId, cancellationToken).ConfigureAwait(false)
            : await conversations.UpdateSettingsAsync(conversationId, changes, cancellationToken).ConfigureAwait(false);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Setting");
        table.AddColumn("Level");
        table.AddColumn("Means");

        foreach (var setting in ChatSettingScale.All)
        {
            var level = settings.Level(setting);
            var described = SettingScales.Describe(setting, level, options);

            table.AddRow(
                Markup.Escape(SettingScales.Title(setting, options)),
                level is { } value ? $"{value}  {Markup.Escape(described.Label)}" : "[grey]not set[/]",
                Markup.Escape(described.Description));
        }

        AnsiConsole.Write(table);

        if (!changes.IsEmpty)
        {
            AnsiConsole.MarkupLine("[green]Applied. These affect every reply from now on.[/]");
        }

        return 0;
    }
}
