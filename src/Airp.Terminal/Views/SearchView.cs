using Microsoft.Extensions.DependencyInjection;
using Airp.Application.Abstractions;
using Airp.Domain.Search;
using Airp.Terminal.Ui;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Airp.Terminal.Views;

/// <summary>
/// Search across the words of every chat.
/// </summary>
/// <remarks>
/// It reads the offline copies, so a chat that has never been opened has nothing to search.
/// That is reported rather than hidden — a search that quietly skipped half the conversations
/// would be worse than one that says what it covered.
/// </remarks>
internal sealed class SearchView : ViewBase, IMouseAware
{
    private readonly ISearchService _search;
    private readonly IChatService _chats;
    private readonly IServiceProvider _services;
    private readonly TextInput _query = new() { Placeholder = "search everything…" };
    private readonly ListState _list = new();

    private IReadOnlyList<SearchHit> _hits = [];
    private SearchResults _results;
    private SearchScope _scope = SearchScope.All;
    private string _lastSearched = string.Empty;

    /// <summary>Initialises the view.</summary>
    /// <param name="search">Search service.</param>
    /// <param name="chats">Chat cache, used to open a hit.</param>
    /// <param name="services">Container used to construct child views.</param>
    public SearchView(ISearchService search, IChatService chats, IServiceProvider services)
    {
        _search = search;
        _chats = chats;
        _services = services;
    }

    /// <inheritdoc />
    public override string Title => "Search";

    /// <inheritdoc />
    public override KeyContext KeyContext => KeyContext.Text;

    /// <inheritdoc />
    public override IReadOnlyList<KeyHint> KeyHints =>
    [
        new("Enter", "Search / open"),
        new("↑↓", "Move"),
        new("Tab", "Change scope"),
        new("Esc", "Back"),
    ];

    /// <inheritdoc />
    public override IRenderable Render(RenderContext context)
    {
        var theme = context.Theme;

        var rows = new List<IRenderable>
        {
            new Markup(Draw.Literal("Search: ", theme.Accent) + _query.ToMarkup(theme)),
            new Markup(Draw.Literal(
                $"scope: {DescribeScope()}   ·   {_hits.Count} result(s)",
                theme.Muted)
                + (_results.IsPartial
                    ? Draw.Literal(
                        $"   ·   {_results.ChatsSkipped} chat(s) not searched — open them once to include them",
                        theme.Warning)
                    : string.Empty)),
            new Rule { Style = theme.Border },
        };

        if (_hits.Count == 0)
        {
            rows.Add(new Markup(Draw.Literal(
                _lastSearched.Length == 0
                    ? "Type a query and press Enter."
                    : $"Nothing matches \"{_lastSearched}\".",
                theme.Muted)));

            return new Rows(rows);
        }

        var available = Math.Max(1, context.Height - 3);
        var (start, length) = _list.Viewport(available / 2);

        for (var i = start; i < Math.Min(_hits.Count, start + length); i++)
        {
            var hit = _hits[i];
            var selected = i == _list.Selected;
            var marker = selected ? '>' : ' ';

            rows.Add(new Markup(Draw.Literal(
                $"{marker} {Draw.Pad(hit.ChatName, 28)} {DescribeScope(hit.Scope)}"
                + (hit.Speaker is { Length: > 0 } speaker ? $"  {speaker}" : string.Empty)
                + (hit.SentAtUtc is { } sent ? $"  {Draw.Age(sent)}" : string.Empty),
                selected ? theme.Selection : theme.Heading)));

            rows.Add(new Markup(
                "    " + Draw.Highlight(
                    Draw.Fit(hit.Snippet, context.Width - 6),
                    _lastSearched,
                    theme.Muted,
                    theme.Highlight)));
        }

        return new Rows(rows);
    }

    /// <inheritdoc />
    public override ValueTask<ViewAction> HandleKeyAsync(
        KeyStroke stroke,
        RenderContext context,
        CancellationToken cancellationToken)
    {
        switch (stroke.Command)
        {
            case AppCommand.Back:
                return ValueTask.FromResult(ViewAction.Pop);

            case AppCommand.MoveUp:
                _list.Move(-1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.MoveDown:
                _list.Move(1);
                return ValueTask.FromResult(ViewAction.None);

            case AppCommand.Tab:
                _scope = _scope switch
                {
                    SearchScope.All => SearchScope.Names,
                    SearchScope.Names => SearchScope.Messages,
                    _ => SearchScope.All,
                };

                return ValueTask.FromResult(ViewAction.Status($"Scope: {DescribeScope()}"));

            case AppCommand.Accept or AppCommand.NewLine:
                return ValueTask.FromResult(
                    _hits.Count > 0 && _query.Value == _lastSearched ? Open() : RunSearch());

            default:
                _query.Handle(stroke);
                return ValueTask.FromResult(ViewAction.None);
        }
    }

    /// <inheritdoc />
    public ViewAction OnClick(int row, RenderContext context)
    {
        // Each hit occupies two rows: a heading and a snippet.
        var index = _list.IndexAtRow((row - 3) / 2, Math.Max(1, (context.Height - 3) / 2));
        if (index < 0)
        {
            return ViewAction.None;
        }

        _list.Select(index);
        return ViewAction.None;
    }

    private ViewAction RunSearch()
    {
        if (_query.IsEmpty)
        {
            return ViewAction.Status("Type something to search for.", StatusKind.Warning);
        }

        var query = _query.Value;
        var scope = _scope;

        return ViewAction.Run($"Searching for \"{query}\"", async ct =>
        {
            _results = await _search.SearchAsync(query, scope, 200, ct).ConfigureAwait(false);
            _hits = _results.Hits;
            _lastSearched = query;
            _list.SetCount(_hits.Count);
            _list.SelectFirst();

            var covered = _results.IsPartial
                ? $" Searched {_results.ChatsSearched} chat(s); {_results.ChatsSkipped} have no local copy yet."
                : string.Empty;

            return _hits.Count == 0
                ? ViewAction.Status($"Nothing matches \"{query}\".{covered}", StatusKind.Warning)
                : ViewAction.Status($"{_hits.Count} result(s).{covered}", StatusKind.Success);
        });
    }

    private ViewAction Open()
    {
        if (_list.Selected < 0 || _list.Selected >= _hits.Count)
        {
            return ViewAction.None;
        }

        var hit = _hits[_list.Selected];
        var chat = _chats.Cached.FirstOrDefault(c => c.Id == hit.ChatId);

        return chat is null
            ? ViewAction.Status("That row is no longer in the list.", StatusKind.Warning)
            : ViewAction.Push(RowView.For(chat, _services));
    }

    private string DescribeScope() => _scope switch
    {
        SearchScope.Names => "chat names",
        SearchScope.Messages => "messages",
        _ => "everything",
    };

    private static string DescribeScope(SearchScope scope) => scope switch
    {
        SearchScope.Names => "name",
        SearchScope.Messages => "message",
        _ => "match",
    };
}
