using Airp.Infrastructure;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Covers which description reaches the prompt when several could.
/// </summary>
/// <remarks>
/// <para>
/// Worth pinning down because getting it wrong is quiet: the scene still runs, the character
/// still answers, and it simply addresses somebody the reader is not playing.
/// </para>
/// <para>
/// Characters and personas resolve by the same rule, so these tests exercise both folders.
/// An earlier version resolved them differently — personas could also live in the
/// configuration file — and a name defined in both places preferred the wrong one silently.
/// </para>
/// </remarks>
public sealed class TextLibraryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "airp-library-tests", Guid.NewGuid().ToString("N"));

    private readonly TextLibrary _library;

    public TextLibraryTests()
    {
        _library = new TextLibrary(_root);
        _library.EnsureCreated();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string folder, string name, string text)
        => File.WriteAllText(Path.Combine(folder, name + ".txt"), text);

    [Fact]
    public async Task An_empty_library_resolves_to_nothing()
        => (await TextLibrary.ResolveAsync(_library.Personas, null, "allan")).ShouldBeNull();

    [Fact]
    public async Task A_named_file_is_read()
    {
        Write(_library.Personas, "allan", "Sos Allan.");

        (await TextLibrary.ResolveAsync(_library.Personas, null, "allan")).ShouldBe("Sos Allan.");
    }

    [Fact]
    public async Task Names_are_matched_regardless_of_case_or_extension()
    {
        Write(_library.Characters, "elena", "You are Elena.");

        (await TextLibrary.ResolveAsync(_library.Characters, null, "ELENA")).ShouldBe("You are Elena.");
        (await TextLibrary.ResolveAsync(_library.Characters, null, "elena.txt")).ShouldBe("You are Elena.");
    }

    [Fact]
    public async Task Text_held_by_the_conversation_wins_over_any_name()
    {
        // It was written for that story and cannot have been meant for another.
        Write(_library.Personas, "allan", "Sos Allan.");

        (await TextLibrary.ResolveAsync(_library.Personas, "Sos un desconocido.", "allan"))
            .ShouldBe("Sos un desconocido.");
    }

    [Fact]
    public async Task The_default_applies_only_when_the_conversation_names_nothing()
    {
        Write(_library.Personas, "allan", "Sos Allan.");
        Write(_library.Personas, "mira", "Sos Mira.");

        (await TextLibrary.ResolveAsync(_library.Personas, null, null, "allan")).ShouldBe("Sos Allan.");
        (await TextLibrary.ResolveAsync(_library.Personas, null, "mira", "allan")).ShouldBe("Sos Mira.");
    }

    [Fact]
    public async Task A_name_that_no_longer_exists_falls_back_to_the_default()
    {
        // Files get renamed out from under a conversation. Losing the persona is a smaller harm
        // than losing the conversation.
        Write(_library.Personas, "allan", "Sos Allan.");

        (await TextLibrary.ResolveAsync(_library.Personas, null, "borrado", "allan")).ShouldBe("Sos Allan.");
    }

    [Fact]
    public async Task With_no_name_and_no_default_nothing_is_assumed()
    {
        // Even with exactly one file. Choosing it would be choosing who the reader is playing,
        // and it would change the day a second file appeared.
        Write(_library.Personas, "allan", "Sos Allan.");

        (await TextLibrary.ResolveAsync(_library.Personas, null, null)).ShouldBeNull();
    }

    [Fact]
    public async Task Whitespace_is_not_a_description()
    {
        Write(_library.Personas, "allan", "Sos Allan.");

        (await TextLibrary.ResolveAsync(_library.Personas, "   ", "allan")).ShouldBe("Sos Allan.");
    }

    [Fact]
    public void The_folders_are_listed_alphabetically_and_without_extensions()
    {
        Write(_library.Characters, "zoe", "z");
        Write(_library.Characters, "elena", "e");

        TextLibrary.Names(_library.Characters).ShouldBe(["elena", "zoe"]);
    }

    [Fact]
    public void A_folder_that_is_not_there_lists_as_empty_rather_than_throwing()
        => TextLibrary.Names(Path.Combine(_root, "nunca-creada")).ShouldBeEmpty();
}
