using System.Reflection;

namespace Airp.Application.Text;

/// <summary>A partially typed word sitting under the caret.</summary>
/// <param name="Start">Index of the word's first character within the line.</param>
/// <param name="Length">How many characters have been typed so far.</param>
/// <param name="Prefix">Those characters.</param>
public readonly record struct WordToken(int Start, int Length, string Prefix);

/// <summary>
/// The English dictionary behind the composer's word completion.
/// </summary>
/// <remarks>
/// <para>
/// The list is common English rather than a full dictionary. Completion earns its place by
/// saving keystrokes on words that are long enough to be worth finishing, and a full
/// dictionary makes that worse rather than better: it buries <c>because</c> under a dozen
/// technical words nobody typing a message will want, and every rare word it adds is another
/// candidate competing for the same seven slots.
/// </para>
/// <para>
/// Matching is by prefix, not fuzzy. A completion list is a promise that the word starts with
/// what you typed; a subsequence match that reorders your letters reads as a malfunction even
/// when it is technically a hit.
/// </para>
/// <para>The list is read once, on first use, and is immutable thereafter.</para>
/// </remarks>
public static class WordList
{
    /// <summary>Shortest prefix that will produce suggestions.</summary>
    /// <remarks>
    /// Below this the list is noise — there are hundreds of matches for two letters, none of
    /// them a real guess, and a strip that opens on the second letter of every word would
    /// flicker through an entire sentence.
    /// </remarks>
    public const int MinimumPrefix = 3;

    private static readonly Lazy<string[]> Sorted = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Every word, lowercase and sorted.</summary>
    public static IReadOnlyList<string> All => Sorted.Value;

    /// <summary>
    /// Finds the word being typed at a caret position.
    /// </summary>
    /// <remarks>
    /// Only a caret at the <em>end</em> of a word counts. Completing from the middle of one
    /// would rewrite the half the user has already moved past.
    /// </remarks>
    /// <param name="line">The line the caret is on.</param>
    /// <param name="column">The caret's column within that line.</param>
    /// <returns>The token, or <see langword="null"/> when the caret is not finishing a word.</returns>
    public static WordToken? TokenAt(string? line, int column)
    {
        var text = line ?? string.Empty;
        var caret = Math.Clamp(column, 0, text.Length);

        // Mid-word: there is more of the word to the right, so this is an edit rather than a
        // write. An apostrophe counts as "more of the word" — the caret in "won|'t" is inside
        // a contraction, and completing "won" to "wonder" there would produce "wonder't".
        if (caret < text.Length && (char.IsLetter(text[caret]) || text[caret] is '\'' or '’'))
        {
            return null;
        }

        var start = caret;
        while (start > 0 && char.IsLetter(text[start - 1]))
        {
            start--;
        }

        var prefix = text[start..caret];
        if (prefix.Length < MinimumPrefix)
        {
            return null;
        }

        // An apostrophe makes the preceding letters part of a contraction, not a word to
        // complete: offering "note" for the "not" in "won't" would be nonsense.
        if (start > 0 && text[start - 1] is '\'' or '’')
        {
            return null;
        }

        return new WordToken(start, prefix.Length, prefix);
    }

    /// <summary>
    /// Ranks dictionary words that begin with a prefix.
    /// </summary>
    /// <remarks>
    /// Shortest first. A shorter completion is both the likelier intention and the one whose
    /// ending the user can predict, and offering <c>information</c> above <c>inform</c> for
    /// "info" puts the surprising choice under the key that accepts.
    /// </remarks>
    /// <param name="prefix">What has been typed. Shorter than <see cref="MinimumPrefix"/> returns nothing.</param>
    /// <param name="limit">Maximum results.</param>
    /// <returns>The matches, best first.</returns>
    public static IReadOnlyList<string> Suggest(string? prefix, int limit = 7)
    {
        if (limit <= 0 || prefix is not { Length: >= MinimumPrefix })
        {
            return [];
        }

        var words = Sorted.Value;
        var lower = prefix.ToLowerInvariant();

        var matches = new List<string>(limit + 1);

        for (var i = LowerBound(words, lower); i < words.Length; i++)
        {
            if (!words[i].StartsWith(lower, StringComparison.Ordinal))
            {
                break;
            }

            // The word already typed in full is not a suggestion, it is the status quo.
            if (words[i].Length != lower.Length)
            {
                matches.Add(words[i]);
            }
        }

        matches.Sort(static (left, right) => left.Length != right.Length
            ? left.Length.CompareTo(right.Length)
            : string.CompareOrdinal(left, right));

        return matches.Count <= limit ? matches : matches[..limit];
    }

    /// <summary>
    /// Restores the capitalisation the user was already using.
    /// </summary>
    /// <remarks>
    /// The dictionary is lowercase, so accepting a suggestion at the start of a sentence would
    /// otherwise quietly lowercase it — a correction the user then has to undo by hand, which
    /// costs more than the completion saved.
    /// </remarks>
    /// <param name="typed">What the user typed.</param>
    /// <param name="word">The lowercase dictionary word.</param>
    /// <returns>The word, cased to match.</returns>
    public static string MatchCase(string typed, string word)
    {
        if (typed.Length == 0 || word.Length == 0)
        {
            return word;
        }

        // All caps only counts past one letter: a lone capital is a sentence start, not
        // shouting, and "I" would otherwise turn "Its" into "ITS".
        if (typed.Length > 1 && typed.All(static c => !char.IsLetter(c) || char.IsUpper(c)))
        {
            return word.ToUpperInvariant();
        }

        return char.IsUpper(typed[0])
            ? char.ToUpperInvariant(word[0]) + word[1..]
            : word;
    }

    private static string[] Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = Array.Find(
            assembly.GetManifestResourceNames(),
            static n => n.EndsWith("Words.txt", StringComparison.Ordinal));

        if (name is null)
        {
            return [];
        }

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);

        var words = new SortedSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var word = line.Trim().ToLowerInvariant();
            if (word.Length >= MinimumPrefix && word.All(char.IsAsciiLetter))
            {
                words.Add(word);
            }
        }

        return [.. words];
    }

    /// <summary>Index of the first word at or after <paramref name="prefix"/> in sort order.</summary>
    /// <param name="words">The sorted words.</param>
    /// <param name="prefix">The prefix being sought.</param>
    /// <returns>Where the prefix's run begins.</returns>
    private static int LowerBound(string[] words, string prefix)
    {
        var low = 0;
        var high = words.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;
            if (string.CompareOrdinal(words[middle], prefix) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
