using Spectre.Console;
using Spectre.Console.Rendering;
using Airp.Application.Options;
using Airp.Infrastructure;
using Airp.Terminal.Ui;
using Airp.Terminal.Views;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The in-terminal library manager: three shelves, create, edit, remove.
/// </summary>
public sealed class LibraryViewTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "airp-tests", Guid.NewGuid().ToString("n"));

    private readonly List<string> _opened = [];

    public LibraryViewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));
        File.WriteAllText(Path.Combine(_root, "characters", "elena.txt"), "You are Elena.");
        File.WriteAllText(Path.Combine(_root, "characters", "mira.txt"), "You are Mira.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LibraryView View() => new(
        new TextLibrary(_root),
        provider: null,
        editor: (path, _) =>
        {
            _opened.Add(path);
            return Task.CompletedTask;
        });

    private static RenderContext Context()
        => new(100, 24, Theme.For(ThemeName.Dark), new AirpOptions());

    private static KeyStroke Nav(ConsoleKey key)
        => KeyMap.Resolve(new ConsoleKeyInfo('\0', key, false, false, false), KeyboardMode.Standard, KeyContext.Navigation);

    private static KeyStroke Letter(char c)
        => KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Navigation);

    private static string Render(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Write(renderable);
        return writer.ToString();
    }

    [Fact]
    public void The_shelf_lists_its_names()
    {
        var rendered = Render(View().Render(Context()));

        rendered.ShouldContain("elena");
        rendered.ShouldContain("mira");
        rendered.ShouldContain("Characters");
    }

    [Fact]
    public void The_selected_entry_shows_its_first_paragraph_under_the_list()
    {
        var rendered = Render(View().Render(Context()));

        // elena is selected; the preview is her file's text, not just her name.
        rendered.ShouldContain("You are Elena.");
        rendered.ShouldNotContain("You are Mira.");
    }

    [Fact]
    public async Task The_preview_follows_the_selection()
    {
        var view = View();
        await view.HandleKeyAsync(Nav(ConsoleKey.DownArrow), Context(), CancellationToken.None);

        var rendered = Render(view.Render(Context()));

        rendered.ShouldContain("You are Mira.");
        rendered.ShouldNotContain("You are Elena.");
    }

    [Fact]
    public void A_template_card_previews_its_world_not_the_boilerplate()
    {
        File.WriteAllText(
            Path.Combine(_root, "characters", "a-lighthouse.txt"),
            "You are the narrator and every character of the world described below.\n\n" +
            "=== THE WORLD ===\n\n" +
            "A lighthouse on a cold coast, and the keeper who signed for one winter.\n\n" +
            "=== THE CAST ===\n");

        // First alphabetically, so it is the selected entry.
        var rendered = Render(View().Render(Context()));

        rendered.ShouldContain("A lighthouse on a cold coast");
        rendered.ShouldNotContain("You are the narrator");
    }

    [Fact]
    public async Task The_openings_shelf_previews_too()
    {
        Directory.CreateDirectory(Path.Combine(_root, "openings"));
        File.WriteAllText(
            Path.Combine(_root, "openings", "mira.txt"),
            "The apartment door clicks shut after a long day.");

        var view = View();

        foreach (var _ in Enumerable.Range(0, 3))
        {
            await view.HandleKeyAsync(Nav(ConsoleKey.RightArrow), Context(), CancellationToken.None);
        }

        var rendered = Render(view.Render(Context()));

        rendered.ShouldContain("Openings");
        rendered.ShouldContain("The apartment door clicks shut");
    }

    [Fact]
    public async Task Arrows_switch_shelves()
    {
        var view = View();
        await view.HandleKeyAsync(Nav(ConsoleKey.RightArrow), Context(), CancellationToken.None);

        // Personas shelf is empty in this fixture; the hint says so in that shelf's words.
        Render(view.Render(Context())).ShouldContain("No personas yet");
    }

    [Fact]
    public async Task N_names_and_creates_from_the_skeleton_then_opens_the_editor()
    {
        var view = View();

        // The keymap turns N into SearchNext; the view treats it as the footer promises.
        await view.HandleKeyAsync(Letter('n'), Context(), CancellationToken.None);

        foreach (var c in "ferrin")
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                Context(), CancellationToken.None);
        }

        var action = await view.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), KeyboardMode.Standard, KeyContext.Text),
            Context(), CancellationToken.None);

        var run = action.ShouldBeOfType<ViewAction.RunAction>();
        await run.Work(CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "characters", "ferrin.txt"))
            .ShouldContain("You are the narrator");
        _opened.ShouldHaveSingleItem().ShouldEndWith("ferrin.txt");
    }

    [Fact]
    public async Task Enter_on_an_entry_opens_it_in_the_editor()
    {
        var view = View();

        var action = await view.HandleKeyAsync(Nav(ConsoleKey.Enter), Context(), CancellationToken.None);

        var run = action.ShouldBeOfType<ViewAction.RunAction>();
        await run.Work(CancellationToken.None);

        _opened.ShouldHaveSingleItem().ShouldEndWith("elena.txt");
    }

    [Fact]
    public async Task Delete_asks_first_and_the_confirmation_actually_deletes()
    {
        var view = View();

        var action = await view.HandleKeyAsync(Nav(ConsoleKey.Delete), Context(), CancellationToken.None);

        var confirm = action.ShouldBeOfType<ViewAction.PushAction>().View.ShouldBeOfType<ConfirmView>();

        // Accepting the confirmation runs the delete.
        var accepted = await confirm.HandleKeyAsync(Nav(ConsoleKey.Enter), Context(), CancellationToken.None);
        var run = accepted.ShouldBeOfType<ViewAction.RunAction>();
        await run.Work(CancellationToken.None);

        TextLibrary.Find(Path.Combine(_root, "characters"), "elena").ShouldBeNull();
    }

    [Fact]
    public async Task A_duplicate_name_warns_instead_of_overwriting()
    {
        var view = View();
        await view.HandleKeyAsync(Letter('n'), Context(), CancellationToken.None);

        foreach (var c in "elena")
        {
            await view.HandleKeyAsync(
                KeyMap.Resolve(new ConsoleKeyInfo(c, default, false, false, false), KeyboardMode.Standard, KeyContext.Text),
                Context(), CancellationToken.None);
        }

        var action = await view.HandleKeyAsync(
            KeyMap.Resolve(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false), KeyboardMode.Standard, KeyContext.Text),
            Context(), CancellationToken.None);

        action.ShouldBeOfType<ViewAction.StatusAction>().Kind.ShouldBe(StatusKind.Warning);
        File.ReadAllText(Path.Combine(_root, "characters", "elena.txt")).ShouldBe("You are Elena.");
    }
}
