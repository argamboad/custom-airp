using System.Text.RegularExpressions;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using NSubstitute;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Tests;

/// <summary>
/// The transcript as it is actually drawn.
/// </summary>
/// <remarks>
/// Formatting a reply is display only, and "display only" is the part worth a test: what is
/// drawn changes, and what is stored does not. A message whose asterisks were stripped on the
/// way into the store would be a message the next prompt sends back differently, which breaks
/// the prefix cache and quietly rewrites what the model wrote.
/// </remarks>
public class ProseRenderingTests
{
    private const string Reply = "*She closes the lid.* \"You are late.\" *The room is quiet.*";

    private static (ConversationView View, IConversationService Conversations) Build()
    {
        var conversations = Substitute.For<IConversationService>();

        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = Reply },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Elena" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        return (view, conversations);
    }

    private static async Task RunAsync(ViewAction action)
    {
        if (action is ViewAction.RunAction run)
        {
            await run.Work(CancellationToken.None);
        }
    }

    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static string Render(IRenderable renderable, bool colour = false)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = colour ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = colour ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Profile.Width = 100;
        console.Profile.Height = 24;
        console.Write(renderable);
        return writer.ToString();
    }

    /// <summary>
    /// The words that were actually drawn: escape sequences removed, whitespace flattened.
    /// </summary>
    /// <remarks>
    /// Asserting on a phrase means asserting across a possible line break. Locally the console
    /// is a real terminal and the transcript fits; on a build runner there is no terminal, the
    /// profile falls back to a narrower default, and a sentence wraps — so "The room is quiet."
    /// arrives with a newline inside it and a substring check fails for a reason that has
    /// nothing to do with what is being tested. Flattening compares the words that were drawn,
    /// which is the actual claim.
    /// </remarks>
    private static string Flat(string rendered)
    {
        // Colours come off with NoColors; decorations do not. An italic run is still
        // announced with ESC[3m, so "The room is quiet." reaches a plain-text assertion
        // with escapes through the middle of it — and whether they are emitted at all
        // depends on what Spectre decides about the environment, which is why this
        // passed on a terminal and failed on every build runner.
        //
        // Removed rather than replaced with a space: an escape sits between the highlighted
        // word and the punctuation after it, so a space would put one there too and turn
        // "quiet." into "quiet ." — which is how this was got wrong the first time.
        var text = Regex.Replace(rendered, "\u001b\\[[0-9;]*[A-Za-z]", string.Empty);

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task The_markers_are_gone_from_what_is_drawn()
    {
        var (view, _) = Build();
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        var rendered = Render(view.Render(Context()));

        Flat(rendered).ShouldContain("She closes the lid.");
        Flat(rendered).ShouldContain("You are late.");
        rendered.ShouldNotContain("*");
        rendered.ShouldNotContain("\"You are late");
    }

    [Fact]
    public async Task The_stored_message_keeps_every_marker()
    {
        // The one that would be expensive to get wrong. The stored wording is what the next
        // prompt sends, and rewriting it would break the prefix cache and change what the
        // model said — which the append-only store exists to prevent.
        var (view, conversations) = Build();
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        Render(view.Render(Context()));

        var stored = await conversations.GetMessagesAsync("c");
        stored.ShouldHaveSingleItem().Text.ShouldBe(Reply);
    }

    [Fact]
    public async Task An_action_is_drawn_in_italics_and_dialogue_is_not()
    {
        var (view, _) = Build();
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        var rendered = Render(view.Render(Context()), colour: true);

        // SGR 3 is italic. It has to open before the action and be closed again by the time
        // the spoken line is drawn, or the whole reply reads as narration.
        var action = rendered.IndexOf("She closes the lid.", StringComparison.Ordinal);
        var spoken = rendered.IndexOf("You are late.", StringComparison.Ordinal);

        action.ShouldBeGreaterThan(0);
        spoken.ShouldBeGreaterThan(action);

        rendered[..action].ShouldContain("\u001b[3");
        rendered[action..spoken].ShouldContain("\u001b[0m");
    }

    [Fact]
    public async Task A_search_still_highlights_through_the_formatting()
    {
        // The runs and the search are two reasons to colour the same characters. Formatting
        // must not be the thing that makes a match invisible.
        var (view, _) = Build();
        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        var unsearched = Render(view.Render(Context()), colour: true);

        await view.HandleKeyAsync(
            KeyMap.Resolve(
                new ConsoleKeyInfo('/', default, false, false, false),
                KeyboardMode.Standard,
                KeyContext.Navigation),
            Context(),
            CancellationToken.None);

        foreach (var c in "quiet")
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                Context(),
                CancellationToken.None);
        }

        await view.HandleKeyAsync(
            KeyMap.Resolve(
                new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
                KeyboardMode.Standard,
                KeyContext.Text),
            Context(),
            CancellationToken.None);

        var rendered = Render(view.Render(Context()), colour: true);

        // The word is still on screen, still inside its italic action, and now painted
        // differently. Compared against the same frame before the search rather than
        // against a fixed escape sequence, which would only be testing the palette.
        rendered.ShouldContain("quiet");
        rendered.ShouldNotBe(unsearched);
        var after = Flat(Render(view.Render(Context())));
        after.ShouldContain("The room is quiet.", customMessage: "rendered: <<" + after + ">>");
    }

    [Fact]
    public async Task A_reply_with_no_markers_is_drawn_unchanged()
    {
        var conversations = Substitute.For<IConversationService>();
        conversations.GetMessagesAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChatMessage>>(_ =>
            [
                new() { Id = "1", ConversationId = "c", Role = ChatRole.Assistant, Text = "She closes the lid and says nothing." },
            ]);

        var view = new ConversationView(
            new Chat { Id = "c", Name = "Student", Speaker = "Elena" },
            conversations,
            Substitute.For<IClipboardService>(),
            Substitute.For<IExportService>());

        await RunAsync(await view.OnActivatedAsync(CancellationToken.None));

        Flat(Render(view.Render(Context()))).ShouldContain("She closes the lid and says nothing.");
    }
}
