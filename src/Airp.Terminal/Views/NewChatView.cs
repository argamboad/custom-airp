using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;
using Airp.Application.Options;
using Airp.Application.Text;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Terminal.Ui;

namespace Airp.Terminal.Views;

/// <summary>
/// Starts a conversation from inside the terminal: name, speaker, a character and a persona
/// picked from the library, and the opening written in a real composer.
/// </summary>
/// <remarks>
/// <para>
/// The opening gets the multi-line document because it does the real establishing work — a
/// greeting where each character speaks once, in their own voice, sets them better than
/// paragraphs describing them, and it sits at the top of the transcript for the rest of the
/// story. It shares the panel under the rule with the picked character's world and the persona
/// facing it: focus on the opening and it takes the panel whole, and otherwise the two of them
/// stand side by side in it.
/// </para>
/// <para>
/// The pickers offer names, never contents, because that is what a conversation stores: the
/// association is written to the database, the definitions stay in their files, and editing a
/// file later reaches this conversation too.
/// </para>
/// </remarks>
internal sealed class NewChatView : ViewBase
{
    private const int NameField = 0;
    private const int SpeakerField = 1;
    private const int CharacterField = 2;
    private const int PersonaField = 3;
    private const int OpeningField = 4;

    private readonly IServiceProvider _services;
    private readonly LocalConversationProvider _provider;

    private readonly IReadOnlyList<string?> _characters;
    private readonly IReadOnlyList<string?> _personas;
    private readonly string? _defaultPersona;

    private readonly TextDocument _opening = TextDocument.FromText(null);

    private readonly string _openingsFolder;
    private readonly string _charactersFolder;
    private readonly string _personasFolder;

    /// <summary>The picked character's world, whole, as the card states it.</summary>
    /// <remarks>
    /// Whole rather than a taste of it: three of the cards in a real library are resort
    /// scenarios whose names differ by one word, and the paragraph that tells them apart is
    /// rarely the first one. It gets the big panel and scrolls, which is the only way to
    /// read a section that runs to forty lines.
    /// </remarks>
    private IReadOnlyList<string> _preview = [];

    /// <summary>First visible line of the world, in wrapped lines.</summary>
    private int _previewScroll;

    /// <summary>The persona that will be sent, as its file states it.</summary>
    /// <remarks>
    /// The right-hand column. A persona is chosen as rarely as a character and read even less
    /// often — it is a page written months ago, sent whole on every turn, and the picker used
    /// to offer nothing but its file name against a panel two thirds empty. Beside the world
    /// rather than under it, because the two are read against each other: whether this is the
    /// right person to walk into that place is a question about both at once. It costs nothing
    /// to show — the text is already going into the prompt.
    /// </remarks>
    private IReadOnlyList<string> _personaPreview = [];

    /// <summary>First visible line of the persona, in wrapped lines.</summary>
    private int _personaScroll;

    private string _name = string.Empty;
    private string _speaker = string.Empty;
    private int _character;
    private int _persona;
    private int _focus;

    /// <summary>Whether the opening's current text came from the shelf rather than typing.</summary>
    /// <remarks>
    /// An opening belongs to its character, so picking a character offers their opening —
    /// but only ever over text the picker itself put there. One keystroke of the user's own
    /// prose flips this off, and from then on the picker never touches the field again.
    /// </remarks>
    private bool _openingFromShelf;

    /// <summary>Initialises the flow from the library as it is right now.</summary>
    /// <param name="services">Container used to open the created conversation.</param>
    /// <param name="provider">The store the conversation is created in.</param>
    /// <param name="options">Application options, for the default persona.</param>
    public NewChatView(
        IServiceProvider services,
        LocalConversationProvider provider,
        IOptionsMonitor<AirpOptions> options,
        TextLibrary? library = null)
    {
        _services = services;
        _provider = provider;

        // Injectable because AppPaths.Root is settled once per process, which makes an
        // environment override useless to a test that shares the process with others.
        library ??= new TextLibrary();
        library.EnsureCreated();

        // Null first in both lists: a conversation without a character is legal, and the
        // persona's null slot means "the configured default", which is what most stories want.
        _characters = [null, .. TextLibrary.Names(library.Characters)];
        _personas = [null, .. TextLibrary.Names(library.Personas)];
        _defaultPersona = options.CurrentValue.DefaultPersona;
        _openingsFolder = library.Openings;
        _charactersFolder = library.Characters;
        _personasFolder = library.Personas;

        // The null slot means the configured default, which is a real persona that will
        // really be sent — so the panel has something to show before anything is picked.
        DescribePersona();
    }

    /// <summary>Reads the picked character's world and what it will cost.</summary>
    /// <remarks>
    /// Deliberately not a new section in the card and not a fifth shelf. <c>=== THE WORLD ===</c>
    /// is already written to be read by a person arriving somewhere — that is what the skeleton
    /// asks for — and it is already sent to the model, so displaying it adds nothing to any
    /// prompt and cannot drift from what the card actually says. A preview kept anywhere else
    /// would be a second copy of the same paragraph going quietly stale.
    /// </remarks>
    private void DescribeCharacter()
    {
        _preview = [];
        _previewScroll = 0;

        if (_characters[_character] is not { } name)
        {
            return;
        }

        if (TextLibrary.Find(_charactersFolder, name) is { } path)
        {
            _preview = TextLibrary.Preview(path, int.MaxValue);
        }
    }

    /// <summary>Reads the persona that would be sent, named or defaulted.</summary>
    /// <remarks>
    /// The null slot is not "no persona": it is <c>Airp:DefaultPersona</c>, which resolves to
    /// a file like any other. Showing the file the picker's own label names keeps the panel
    /// and the field saying the same thing.
    /// </remarks>
    private void DescribePersona()
    {
        _personaPreview = [];
        _personaScroll = 0;

        if ((_personas[_persona] ?? _defaultPersona) is not { } name)
        {
            return;
        }

        if (TextLibrary.Find(_personasFolder, name) is not { } path)
        {
            return;
        }

        _personaPreview = TextLibrary.Preview(path, int.MaxValue);
    }

    /// <inheritdoc />
    public override string Title => "New chat";

    /// <inheritdoc />
    public override KeyContext KeyContext => KeyContext.Text;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("Tab", "Next field"),
        new("←→", "Pick"),
        new("PgUp/PgDn", "Read the panel"),
        new("Ctrl+Enter", "Create"),
        new("Esc", "Cancel"),
    ];

    private bool Touched
        => _name.Length > 0 || _speaker.Length > 0 || _persona > 0
           || (_opening.CharacterCount > 0 && !_openingFromShelf);

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;
        var width = Math.Max(20, context.Width - 4);
        var rows = new List<IRenderable>();

        string Caret(int field) => _focus == field ? "▌" : string.Empty;

        string Label(int field, string text)
            => Draw.Literal(text.PadRight(11), _focus == field ? theme.Accent : theme.Muted);

        rows.Add(new Markup(
            Label(NameField, "Name") + Draw.Literal(_name, theme.Text)
            + Draw.Literal(Caret(NameField), theme.Selection)
            + (_name.Length == 0 && _focus != NameField
                ? Draw.Literal("  how it appears in your list", theme.Muted)
                : string.Empty)));

        rows.Add(new Markup(
            Label(SpeakerField, "Speaker") + Draw.Literal(_speaker, theme.Text)
            + Draw.Literal(Caret(SpeakerField), theme.Selection)
            + (_speaker.Length == 0 && _focus != SpeakerField
                ? Draw.Literal("  who replies", theme.Muted)
                : string.Empty)));

        rows.Add(new Markup(
            Label(CharacterField, "Character")
            + Draw.Literal(
                _characters[_character] ?? "(none — the opening carries the scene)",
                _characters[_character] is null ? theme.Muted : theme.Text)
            + Draw.Literal(_focus == CharacterField ? "  ←→" : string.Empty, theme.Muted)));

        rows.Add(new Markup(
            Label(PersonaField, "Persona")
            + Draw.Literal(
                _personas[_persona]
                    ?? (_defaultPersona is null ? "(none)" : $"(default: {_defaultPersona})"),
                _personas[_persona] is null ? theme.Muted : theme.Text)
            + Draw.Literal(_focus == PersonaField ? "  ←→" : string.Empty, theme.Muted)));

        rows.Add(new Rule { Style = theme.Border });

        var height = Panel(context);

        // The panel goes to whichever of the three is being looked at. Writing the opening
        // needs the document; everything else is choosing what will be in the scene, and the
        // scene has two halves — so the world and the persona stand side by side, each with
        // the full height of the panel, rather than one of them waiting under the other.
        if (_focus != OpeningField && (_preview.Count > 0 || _personaPreview.Count > 0))
        {
            // Two rows go to the headings and the rules under them, which line up across both
            // columns.
            var body = height - 2;

            if (_preview.Count == 0 || _personaPreview.Count == 0)
            {
                // Only one of them to show: it takes the width rather than sitting in half of
                // it with a rule down the middle of nothing.
                var only = _preview.Count > 0;
                var lines = Wrapped(only ? _preview : _personaPreview, width - 2);
                var scroll = only ? _previewScroll : _personaScroll;

                scroll = Math.Min(scroll, Math.Max(0, lines.Count - body));

                if (only)
                {
                    _previewScroll = scroll;
                }
                else
                {
                    _personaScroll = scroll;
                }

                rows.Add(Section(
                    theme,
                    only ? "Character preview" : "Persona",
                    lines,
                    body,
                    scroll,
                    scrolls: true,
                    indent: "  "));

                return new Rows(rows);
            }

            // Two columns of prose need the rule between them; two spaces would let one run
            // into the other. SplitWidths reserves the rule and the space either side of it.
            var (left, right) = Draw.SplitWidths(width, leftRatio: 0.5, minLeft: 20, minRight: 16);

            var world = Wrapped(_preview, left - 2);
            var persona = Wrapped(_personaPreview, right - 1);

            _previewScroll = Math.Min(_previewScroll, Math.Max(0, world.Count - body));
            _personaScroll = Math.Min(_personaScroll, Math.Max(0, persona.Count - body));

            // The page keys move one of the two, and which one is decided by the focus. The
            // caption of the one they move says so, because the same hint on both columns
            // would be a hint answering the wrong question.
            var onPersona = _focus == PersonaField;

            var grid = new Grid();
            grid.AddColumn(new GridColumn { Width = left, NoWrap = true, Padding = new Padding(0, 0, 0, 0) });
            grid.AddColumn(new GridColumn { Width = 3, NoWrap = true, Padding = new Padding(0, 0, 0, 0) });
            grid.AddColumn(new GridColumn { Width = right, Padding = new Padding(0, 0, 0, 0) });

            // The divider runs the height of the deeper column and stops there. Carried past
            // the last line of both it would be drawing a division through empty panel.
            var deepest = 2 + Math.Max(
                Math.Min(body, world.Count - _previewScroll),
                Math.Min(body, persona.Count - _personaScroll));

            grid.AddRow(
                Section(theme, "Character preview", world, body, _previewScroll, !onPersona, "  "),
                Divider(theme, Math.Max(2, deepest)),
                Section(theme, "Persona", persona, body, _personaScroll, onPersona, " "));

            rows.Add(grid);

            return new Rows(rows);
        }

        rows.Add(new Markup(
            Label(OpeningField, "Opening")
            + Draw.Literal("the first message, written by you — worth more than it looks", theme.Muted)));

        var text = _opening.Text;

        if (text.Length > 0)
        {
            var lines = _opening.Lines.SelectMany(line => Draw.Wrap(line, width)).ToList();

            foreach (var line in lines.TakeLast(height))
            {
                rows.Add(new Markup(Draw.Literal("  " + line, theme.Text)));
            }

            if (_focus == OpeningField)
            {
                rows.Add(new Markup(Draw.Literal("  ▌", theme.Selection)));
            }
        }
        else if (_focus == OpeningField)
        {
            rows.Add(new Markup(Draw.Literal("  ▌", theme.Selection)));
        }

        return new Rows(rows);
    }

    /// <summary>How many rows the panel under the rule gets.</summary>
    private static int Panel(RenderContext context) => Math.Max(3, context.Height - 12);

    /// <summary>A preview, wrapped to the width of the column it will stand in.</summary>
    private static List<string> Wrapped(IReadOnlyList<string> text, int width)
        => [.. text.SelectMany(line => Draw.Wrap(line, Math.Max(1, width)))];

    /// <summary>The divider between the two columns, crossed by the rule under the headings.</summary>
    /// <remarks>
    /// Three columns wide rather than one with padding either side, because padding renders as
    /// spaces and the second row has to be drawn: the headings' rule stops at each column's
    /// edge, and a vertical bar passing between two horizontals that do not reach it reads as
    /// three broken lines rather than one join.
    /// </remarks>
    /// <param name="theme">Active palette.</param>
    /// <param name="height">Rows to draw, headings and rule included.</param>
    private static IRenderable Divider(Theme theme, int height)
    {
        var bar = new Markup(Draw.Literal(" │ ", theme.Border));

        return new Rows([
            bar,
            new Markup(Draw.Literal("─┼─", theme.Border)),
            .. Enumerable.Repeat((IRenderable)bar, Math.Max(0, height - 2))]);
    }

    /// <summary>Builds one column of the panel: a caption saying what it is, then what fits.</summary>
    /// <remarks>
    /// The caption names the field the column previews rather than the file in it. The
    /// character and the persona are both already spelled out in the form three rows up, and a
    /// caption repeating one of them was a line that said nothing the reader had not just read
    /// — while leaving the two columns unlabelled the moment there were two of them.
    /// </remarks>
    /// <param name="theme">Active palette.</param>
    /// <param name="what">What this column holds, in the reader's words.</param>
    /// <param name="text">The column's text, already wrapped to its width.</param>
    /// <param name="room">Rows the text may use, the heading and its rule excluded.</param>
    /// <param name="scroll">First visible line.</param>
    /// <param name="scrolls">Whether the page keys currently move this column.</param>
    /// <param name="indent">Left margin, which differs either side of the separator.</param>
    private static IRenderable Section(
        Theme theme,
        string what,
        List<string> text,
        int room,
        int scroll,
        bool scrolls,
        string indent)
    {
        var caption = Draw.Literal(indent + what, theme.Heading);

        if (text.Count > room)
        {
            caption += Draw.Literal(
                $"   {scroll + 1}–{Math.Min(text.Count, scroll + room)} of {text.Count}"
                + (scrolls ? "   PgUp/PgDn" : string.Empty),
                theme.Muted);
        }

        // Heading, rule, then the text — the same shape <see cref="Draw.Heading"/> makes and
        // the chat list's preview already uses. A caption sitting straight on the paragraph it
        // labels reads as the paragraph's first line.
        var lines = new List<IRenderable>
        {
            new Markup(caption),
            new Rule { Style = theme.Border },
        };

        lines.AddRange(text
            .Skip(scroll)
            .Take(room)
            .Select(line => new Markup(Draw.Literal(indent + line, theme.Text))));

        return new Rows(lines);
    }

    /// <inheritdoc />
    public override async ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        // Ctrl+S is flow control (XOFF) in a Windows console and never reaches the
        // application there, which left this form with no way to finish. Ctrl+Enter arrives
        // intact, so the create chord is both — and the footer names the one that works.
        if (stroke.Key is { Key: ConsoleKey.Enter, Modifiers: ConsoleModifiers.Control })
        {
            return await CreateAsync(cancellationToken).ConfigureAwait(false);
        }

        switch (stroke.Command)
        {
            case AppCommand.Save:
                return await CreateAsync(cancellationToken).ConfigureAwait(false);

            case AppCommand.Back when !Touched:
                return ViewAction.Pop;

            case AppCommand.Back:
                return ViewAction.Push(new ConfirmView(
                    "Cancel",
                    "Throw away this new chat?",
                    ["Nothing has been created yet; what you typed here is lost."],
                    "Throw away",
                    static _ => Task.FromResult(ViewAction.Pop)));

            case AppCommand.Tab or AppCommand.MoveDown when _focus < OpeningField:
                _focus++;
                return ViewAction.None;

            case AppCommand.Tab:
                _focus = NameField;
                return ViewAction.None;

            case AppCommand.MoveUp when _focus > NameField && _focus != OpeningField:
                _focus--;
                return ViewAction.None;

            case AppCommand.MoveUp when _focus == OpeningField && _opening.CursorLine == 0:
                _focus = PersonaField;
                return ViewAction.None;

            // Enter walks the short fields, because that is what typing a form feels like;
            // in the opening it is a line break, because prose has paragraphs.
            case AppCommand.Accept or AppCommand.NewLine when _focus < OpeningField:
                _focus++;
                return ViewAction.None;

            case AppCommand.Accept or AppCommand.NewLine:
                _opening.InsertNewLine();
                return ViewAction.None;

            case AppCommand.MoveLeft when _focus == CharacterField:
                _character = (_character + _characters.Count - 1) % _characters.Count;
                await OfferOpeningAsync(cancellationToken).ConfigureAwait(false);
                DescribeCharacter();
                return ViewAction.None;

            case AppCommand.MoveRight when _focus == CharacterField:
                _character = (_character + 1) % _characters.Count;
                await OfferOpeningAsync(cancellationToken).ConfigureAwait(false);
                DescribeCharacter();
                return ViewAction.None;

            case AppCommand.MoveLeft when _focus == PersonaField:
                _persona = (_persona + _personas.Count - 1) % _personas.Count;
                DescribePersona();
                return ViewAction.None;

            case AppCommand.MoveRight when _focus == PersonaField:
                _persona = (_persona + 1) % _personas.Count;
                DescribePersona();
                return ViewAction.None;

            // The arrows walk the form and either text is often longer than the room it got,
            // so the page keys are what reads them. They follow the focus, because that is
            // also what decides which of the two got the room. Clamped at the bottom by the
            // renderer, the only place that knows how many lines the text wrapped to.
            case AppCommand.PageUp when _focus == PersonaField:
                _personaScroll = Math.Max(0, _personaScroll - (Panel(context) - 1));
                return ViewAction.None;

            case AppCommand.PageDown when _focus == PersonaField:
                _personaScroll += Panel(context) - 1;
                return ViewAction.None;

            case AppCommand.PageUp when _focus != OpeningField:
                _previewScroll = Math.Max(0, _previewScroll - (Panel(context) - 1));
                return ViewAction.None;

            case AppCommand.PageDown when _focus != OpeningField:
                _previewScroll += Panel(context) - 1;
                return ViewAction.None;

            case AppCommand.MoveUp when _focus == OpeningField:
                _opening.MoveVertical(-1);
                return ViewAction.None;

            case AppCommand.MoveDown when _focus == OpeningField:
                _opening.MoveVertical(1);
                return ViewAction.None;

            case AppCommand.MoveLeft when _focus == OpeningField:
                _opening.MoveLeft();
                return ViewAction.None;

            case AppCommand.MoveRight when _focus == OpeningField:
                _opening.MoveRight();
                return ViewAction.None;

            case AppCommand.DeleteBack:
                switch (_focus)
                {
                    case NameField when _name.Length > 0:
                        _name = _name[..^1];
                        break;
                    case SpeakerField when _speaker.Length > 0:
                        _speaker = _speaker[..^1];
                        break;
                    case OpeningField:
                        _opening.Backspace();
                        _openingFromShelf = false;
                        break;
                }

                return ViewAction.None;

            case AppCommand.Undo when _focus == OpeningField:
                _opening.Undo();
                return ViewAction.None;
        }

        if (stroke.Character is not '\0' and not '\r' and not '\n' && !char.IsControl(stroke.Character))
        {
            switch (_focus)
            {
                case NameField:
                    _name += stroke.Character;
                    break;
                case SpeakerField:
                    _speaker += stroke.Character;
                    break;
                case OpeningField:
                    _opening.InsertText(stroke.Character.ToString());
                    _openingFromShelf = false;
                    break;
            }
        }

        return ViewAction.None;
    }

    /// <summary>Pre-fills the opening with the picked character's own, when that is safe.</summary>
    private async Task OfferOpeningAsync(CancellationToken cancellationToken)
    {
        if (_opening.CharacterCount > 0 && !_openingFromShelf)
        {
            return;
        }

        var text = _characters[_character] is { } name
            ? await TextLibrary.ReadAsync(_openingsFolder, name, cancellationToken).ConfigureAwait(false)
            : null;

        _opening.SetText(text?.TrimEnd());
        _openingFromShelf = !string.IsNullOrEmpty(text);
    }

    private async Task<ViewAction> CreateAsync(CancellationToken cancellationToken)
    {
        // The name falls back to the character or the speaker rather than blocking: the list
        // needs something to show, but nobody should retype "Elena" three times to start.
        var name = _name.Trim();

        if (name.Length == 0)
        {
            name = _characters[_character] ?? _speaker.Trim();
        }

        if (name.Length == 0)
        {
            return ViewAction.Status("Give it a name, or at least a speaker.", StatusKind.Warning);
        }

        var opening = _opening.Text.Trim();

        var chat = await _provider.CreateAsync(
                new Airp.Domain.Conversations.NewConversation
                {
                    Name = name,
                    Speaker = _speaker.Trim().Length > 0 ? _speaker.Trim() : null,
                    Opening = opening.Length > 0 ? opening : null,
                    CharacterName = _characters[_character],
                    PersonaName = _personas[_persona],
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Straight into the conversation. Creating a chat and then hunting for it in the list
        // would be the terminal making the reader do its filing.
        return ViewAction.Sequence(
            ViewAction.Pop,
            ViewAction.Push(RowView.For(chat, _services)),
            ViewAction.Status($"\"{chat.Name}\" started.", StatusKind.Success));
    }
}
