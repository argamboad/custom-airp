using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;
using Spectre.Console.Rendering;
using Airp.Application.Options;
using Airp.Application.Context;
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
/// The opening gets the multi-line document and most of the screen because it does the real
/// establishing work — a greeting where each character speaks once, in their own voice, sets
/// them better than paragraphs describing them, and it sits at the top of the transcript for
/// the rest of the story.
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

    /// <summary>How much of the picked character's world is shown while choosing.</summary>
    /// <remarks>
    /// Enough to recognise a place, not enough to read it. Three of the cards in a real
    /// library are resort scenarios whose names differ by one word, and the name alone does
    /// not tell you which one you are about to play.
    /// </remarks>
    private const int PreviewLines = 6;

    private IReadOnlyList<string> _preview = [];

    /// <summary>
    /// What the picked character costs, every turn, for the life of the conversation.
    /// </summary>
    /// <remarks>
    /// The one number that decides whether a story compresses at turn twenty or turn two
    /// hundred, and it was previously invisible until the reader was already playing. The
    /// character layer is never summarised and never dropped: it is re-sent whole on every
    /// turn, so it is the only part of the prompt whose size is a permanent decision.
    /// </remarks>
    private int _characterTokens;

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
        _characterTokens = 0;

        if (_characters[_character] is not { } name)
        {
            return;
        }

        if (TextLibrary.Find(_charactersFolder, name) is not { } path)
        {
            return;
        }

        _preview = TextLibrary.Preview(path, PreviewLines);

        try
        {
            _characterTokens = TokenEstimator.ForText(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A card that cannot be read is a problem for the moment it is used, not for the
            // moment it is being looked at. Show the name and no number.
            _characterTokens = 0;
        }
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
            + Draw.Literal(_focus == CharacterField ? "  ←→" : string.Empty, theme.Muted)
            + (_characterTokens > 0
                ? Draw.Literal(
                    $"  {_characterTokens:N0} tokens every turn",
                    _characterTokens >= 20000 ? theme.Warning : theme.Muted)
                : string.Empty)));

        // The world, as the card itself states it. Shown under the picker rather than after
        // the decision, because three of these names differ by a single word and the card is
        // the thing being chosen.
        foreach (var line in _preview.SelectMany(l => Draw.Wrap(l, width - 13)).Take(PreviewLines))
        {
            rows.Add(new Markup(Draw.Literal("           " + line, theme.Muted)));
        }

        rows.Add(new Markup(
            Label(PersonaField, "Persona")
            + Draw.Literal(
                _personas[_persona]
                    ?? (_defaultPersona is null ? "(none)" : $"(default: {_defaultPersona})"),
                _personas[_persona] is null ? theme.Muted : theme.Text)
            + Draw.Literal(_focus == PersonaField ? "  ←→" : string.Empty, theme.Muted)));

        rows.Add(new Rule { Style = theme.Border });
        rows.Add(new Markup(
            Label(OpeningField, "Opening")
            + Draw.Literal("the first message, written by you — worth more than it looks", theme.Muted)));

        var text = _opening.Text;

        if (text.Length > 0)
        {
            var height = Math.Max(3, context.Height - 12);
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
                return ViewAction.None;

            case AppCommand.MoveRight when _focus == PersonaField:
                _persona = (_persona + 1) % _personas.Count;
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
