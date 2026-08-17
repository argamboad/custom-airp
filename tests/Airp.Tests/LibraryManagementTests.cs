using Microsoft.Extensions.Logging.Abstractions;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Creating, finding and deleting library entries, and knowing who uses them.
/// </summary>
public sealed class LibraryManagementTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "airp-tests", Guid.NewGuid().ToString("n"));

    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    private string Folder => Path.Combine(_root, "characters");

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    [Fact]
    public async Task Create_writes_the_file_and_the_name_resolves()
    {
        await TextLibrary.CreateAsync(Folder, "elena", "You are Elena.");

        (await TextLibrary.ReadAsync(Folder, "elena")).ShouldBe("You are Elena.");
        (await TextLibrary.ReadAsync(Folder, "ELENA.txt")).ShouldBe("You are Elena.");
    }

    [Fact]
    public async Task Create_refuses_to_overwrite()
    {
        // "Create" silently becoming "replace" would destroy a page of the reader's own
        // writing over a reused name.
        await TextLibrary.CreateAsync(Folder, "elena", "the original");

        await Should.ThrowAsync<InvalidOperationException>(
            () => TextLibrary.CreateAsync(Folder, "Elena", "an accident"));

        (await TextLibrary.ReadAsync(Folder, "elena")).ShouldBe("the original");
    }

    [Theory]
    [InlineData("  ")]
    [InlineData("a/b")]
    [InlineData("con:trol")]
    public async Task Create_refuses_names_that_cannot_be_files(string name)
        => await Should.ThrowAsync<ArgumentException>(
            () => TextLibrary.CreateAsync(Folder, name, "text"));

    [Fact]
    public async Task Delete_removes_the_file_and_says_which()
    {
        await TextLibrary.CreateAsync(Folder, "elena", "text");

        var deleted = TextLibrary.Delete(Folder, "ELENA");

        deleted.ShouldNotBeNull();
        TextLibrary.Find(Folder, "elena").ShouldBeNull();
    }

    [Fact]
    public void Delete_of_nothing_reports_nothing_rather_than_throwing()
        => TextLibrary.Delete(Folder, "nobody").ShouldBeNull();

    [Fact]
    public async Task Conversations_using_a_character_are_found_by_name()
    {
        var provider = Provider();
        await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Vardhal", Speaker = "Elena", CharacterName = "elena" });
        await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Coast road", Speaker = "Elena", CharacterName = "Elena.txt" });
        await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Unrelated", Speaker = "Mira", CharacterName = "mira" });

        var used = await provider.ConversationsUsingAsync(persona: false, "elena");

        used.ShouldBe(["Coast road", "Vardhal"]);
    }

    [Fact]
    public async Task A_persona_is_looked_up_in_its_own_column()
    {
        var provider = Provider();
        await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Vardhal", Speaker = "Elena", CharacterName = "allan", PersonaName = "elena" });

        (await provider.ConversationsUsingAsync(persona: true, "allan")).ShouldBeEmpty();
        (await provider.ConversationsUsingAsync(persona: true, "elena")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_deleted_conversation_no_longer_counts_as_a_user()
    {
        // Resolution falling back to the default inside a conversation nobody can open is not
        // a consequence worth blocking a delete over.
        var provider = Provider();
        var chat = await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Vardhal", Speaker = "Elena", CharacterName = "elena" });

        await provider.DeleteConversationAsync(chat.Id);

        (await provider.ConversationsUsingAsync(persona: false, "elena")).ShouldBeEmpty();
    }

    [Fact]
    public async Task An_inline_definition_is_not_a_library_reference()
    {
        var provider = Provider();
        await provider.CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Standalone", Speaker = "Elena", CharacterDefinition = "You are Elena, written for this story alone." });

        (await provider.ConversationsUsingAsync(persona: false, "elena")).ShouldBeEmpty();
    }
}
