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

/// <summary>The character, persona and snippet shelves: list, create, show, edit, remove.</summary>
internal static partial class Program
{

    /// <summary>Shows what descriptions are on disk, and where to put more.</summary>
    /// <param name="services">Resolved services.</param>
    /// <returns>The process exit code.</returns>
    /// <summary>Creates, shows and removes character and persona files by name.</summary>
    /// <remarks>
    /// One handler for both kinds, because they are the same shape of thing in two folders —
    /// the same reason they resolve by one rule. Editing is deliberately absent here: it opens
    /// the terminal's editor view, which the interactive flow owns.
    /// </remarks>
    /// <param name="services">Resolved services.</param>
    /// <param name="args">The command line.</param>
    /// <param name="persona">True for the persona library, false for characters.</param>
    /// <param name="cancellationToken">Token used to abort.</param>
    /// <returns>The process exit code.</returns>
    /// <summary>The three shelves the library verbs operate on.</summary>
    private enum LibraryKind
    {
        Character,
        Persona,
        Snippet,
    }


    private static async Task<int> LibraryEntryAsync(
        IServiceProvider services,
        string[] args,
        LibraryKind kindValue,
        CancellationToken cancellationToken)
    {
        var library = services.GetRequiredService<TextLibrary>();
        library.EnsureCreated();

        var kind = kindValue.ToString().ToLowerInvariant();
        var persona = kindValue == LibraryKind.Persona;
        var folder = kindValue switch
        {
            LibraryKind.Persona => library.Personas,
            LibraryKind.Snippet => library.Snippets,
            _ => library.Characters,
        };
        var verb = Positional(args).ElementAtOrDefault(1)?.ToLowerInvariant();
        var name = Positional(args).ElementAtOrDefault(2) ?? ValueAfter(args, "--name");

        switch (verb)
        {
            case null or "list":
            {
                foreach (var entry in TextLibrary.Names(folder))
                {
                    AnsiConsole.MarkupLine($"  {Markup.Escape(entry)}");
                }

                if (TextLibrary.Names(folder).Count == 0)
                {
                    AnsiConsole.MarkupLine($"[grey]No {kind}s yet. airp {kind} new <name> starts one.[/]");
                }

                return 0;
            }

            case "new" or "add" when !string.IsNullOrWhiteSpace(name):
            {
                // Seeded from a named file when asked, otherwise from the built-in skeleton.
                // Never from an existing entry implicitly — that is what --from is for.
                var from = ValueAfter(args, "--from");
                string content;

                if (from is not null)
                {
                    if (!File.Exists(from))
                    {
                        AnsiConsole.MarkupLine($"[red]No file at {Markup.Escape(from)}.[/]");
                        return 66;
                    }

                    content = await File.ReadAllTextAsync(from, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    content = kindValue switch
                    {
                        LibraryKind.Persona => TextLibrary.PersonaSkeleton,
                        LibraryKind.Snippet => TextLibrary.SnippetSkeleton,
                        _ => TextLibrary.CharacterSkeleton,
                    };
                }

                try
                {
                    var path = await TextLibrary.CreateAsync(folder, name, content, cancellationToken)
                        .ConfigureAwait(false);

                    AnsiConsole.MarkupLine($"[green]Created.[/] [grey]{Markup.Escape(path)}[/]");
                    AnsiConsole.MarkupLine($"[grey]Write it, then: airp new \"…\" --{kind} {Markup.Escape(name)}[/]");
                    return 0;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                    return 65;
                }
            }

            case "show" when !string.IsNullOrWhiteSpace(name):
            {
                if (await TextLibrary.ReadAsync(folder, name, cancellationToken).ConfigureAwait(false) is not { } text)
                {
                    AnsiConsole.MarkupLine($"[yellow]No {kind} named '{Markup.Escape(name)}'.[/]");
                    return 66;
                }

                AnsiConsole.WriteLine(text);
                return 0;
            }

            case "edit" when !string.IsNullOrWhiteSpace(name):
            {
                if (TextLibrary.Find(folder, name) is not { } path)
                {
                    AnsiConsole.MarkupLine($"[yellow]No {kind} named '{Markup.Escape(name)}'.[/]");
                    AnsiConsole.MarkupLine($"[grey]airp {kind} new {Markup.Escape(name)} starts one.[/]");
                    return 66;
                }

                AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Airp.Terminal.Ui.EditorLauncher.Editor)} {Markup.Escape(path)}[/]");
                await Airp.Terminal.Ui.EditorLauncher.OpenAsync(path, cancellationToken).ConfigureAwait(false);

                AnsiConsole.MarkupLine(
                    $"[green]Done.[/] [grey]Every conversation naming '{Markup.Escape(name)}' sees the change from its next turn.[/]");
                return 0;
            }

            case "remove" or "rm" or "delete" when !string.IsNullOrWhiteSpace(name):
            {
                // The one destructive verb, so it is the one that checks first. A file still
                // named by live conversations is only deleted when forced; resolution falls
                // back safely, but the prose itself does not come back.
                if (kindValue != LibraryKind.Snippet && services.GetService<LocalConversationProvider>() is { } local)
                {
                    var used = await local.ConversationsUsingAsync(persona, name, cancellationToken)
                        .ConfigureAwait(false);

                    if (used.Count > 0 && !args.Contains("--force", StringComparer.OrdinalIgnoreCase))
                    {
                        AnsiConsole.MarkupLine(
                            $"[yellow]'{Markup.Escape(name)}' is used by {used.Count} conversation(s):[/]");

                        foreach (var conversation in used)
                        {
                            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(conversation)}[/]");
                        }

                        AnsiConsole.MarkupLine("[grey]They would fall back to the default. --force deletes anyway.[/]");
                        return 65;
                    }
                }

                if (TextLibrary.Delete(folder, name) is not { } deleted)
                {
                    AnsiConsole.MarkupLine($"[yellow]No {kind} named '{Markup.Escape(name)}'.[/]");
                    return 66;
                }

                AnsiConsole.MarkupLine($"[green]Removed.[/] [grey]{Markup.Escape(deleted)}[/]");
                return 0;
            }

            default:
                AnsiConsole.MarkupLine($"[grey]airp {kind} [[list]] | new <name> [[--from <file>]] | show <name> | edit <name> | remove <name> [[--force]][/]");
                return 64;
        }
    }


    

    

    

    private static int Library(IServiceProvider services)
    {
        var library = new TextLibrary();
        library.EnsureCreated();

        var configured = services.GetRequiredService<IOptionsMonitor<AirpOptions>>().CurrentValue;

        void Show(string heading, string folder)
        {
            var names = TextLibrary.Names(folder);

            AnsiConsole.MarkupLine($"[bold]{heading}[/]  [grey]{Markup.Escape(folder)}[/]");

            foreach (var name in names)
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(name)}");
            }

            if (names.Count == 0)
            {
                AnsiConsole.MarkupLine("  [grey]nothing yet — drop a .txt in that folder[/]");
            }

            AnsiConsole.WriteLine();
        }

        Show("Characters", library.Characters);
        Show("Personas", library.Personas);

        if (!string.IsNullOrWhiteSpace(configured.DefaultPersona))
        {
            AnsiConsole.MarkupLine($"[grey]Default persona: {Markup.Escape(configured.DefaultPersona)}[/]");
        }

        AnsiConsole.MarkupLine(
            "[grey]A conversation stores the name, so editing a file reaches every conversation "
            + "using it.[/]");

        return 0;
    }
}
