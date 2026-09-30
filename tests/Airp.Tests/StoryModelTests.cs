using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A story's own model: chosen from what the provider lists, and never the reason a turn cannot
/// be written.
/// </summary>
/// <remarks>
/// Two moments can find a model unavailable. Choosing one that is not listed saves nothing and
/// says what the story stays on; a saved one the provider stops serving writes that turn with
/// the default and records that it did. Only a refusal about the model itself does that — an
/// empty account or a rejected key would refuse the default identically.
/// </remarks>
public sealed class StoryModelTests : IDisposable
{
    private const string Default = "deepseek/deepseek-v4-flash";

    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new()
    {
        Catalogue =
        [
            new ModelInfo("big/model", 200_000),
            new ModelInfo("small/model", 32_768),
            new ModelInfo("gone/model", 128_000),
            new ModelInfo("tiny/model", 16_000),
        ],
    };

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<Chat> StoryAsync()
        => await Provider().CreateAsync(new NewConversation { Name = "Vardhal", Speaker = "Elena" });

    [Fact]
    public async Task A_listed_model_is_saved_under_the_providers_spelling_with_its_window()
    {
        var chat = await StoryAsync();

        var change = await Provider().SetModelAsync(chat.Id, " BIG/Model ");

        change.Saved.ShouldBeTrue();
        change.Model.ShouldBe("big/model");
        change.Context.ShouldBe(200_000);
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("big/model");
    }

    [Fact]
    public async Task A_model_whose_window_is_under_the_budget_is_saved_and_says_the_budget_shrinks()
    {
        var chat = await StoryAsync();

        var change = await Provider().SetModelAsync(chat.Id, "tiny/model");

        change.Saved.ShouldBeTrue();
        change.Message.ShouldContain("shrinks to fit");
    }

    [Fact]
    public async Task A_model_the_provider_does_not_list_is_not_saved_and_the_story_stays_on_what_it_had()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");

        var change = await Provider().SetModelAsync(chat.Id, "big/modle");

        change.Saved.ShouldBeFalse();
        change.Message.ShouldContain("not available");
        change.Message.ShouldContain("stays on big/model");
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("big/model");
    }

    [Fact]
    public async Task A_model_that_cannot_be_checked_is_not_saved()
    {
        var chat = await StoryAsync();
        _model.CatalogueFailure = new ModelUnavailableException("Could not reach the provider.");

        var change = await Provider().SetModelAsync(chat.Id, "big/model");

        change.Saved.ShouldBeFalse();
        change.Message.ShouldContain("could not be checked");
        change.Message.ShouldContain($"stays on {Default}");
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Default)]
    public async Task Nothing_or_the_default_itself_clears_the_story_back_to_the_default(string? model)
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");

        var change = await Provider().SetModelAsync(chat.Id, model);

        change.Saved.ShouldBeTrue();
        change.Model.ShouldBeNull();
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBeNull();
    }

    [Fact]
    public async Task Replies_and_questions_are_written_by_the_storys_model()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");
        _model.Says("She looks up.").Says("She knows.");

        await Provider().SendAsync(chat.Id, "I come in.");
        await Provider().AskAsync(chat.Id, "What does she know?");

        _model.Models.ShouldBe(["big/model", "big/model"]);
    }

    [Fact]
    public async Task A_model_that_has_gone_writes_nothing_the_default_writes_the_turn_and_the_reply_says_so()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "gone/model");
        _model.HasNoSuchModel().Says("The default wrote this.");

        var added = await Provider().SendAsync(chat.Id, "I come in.");

        _model.Models.ShouldBe(["gone/model", Default]);
        added[^1].Text.ShouldBe("The default wrote this.");
        added[^1].FellBackFrom.ShouldBe("gone/model");
        (await Provider().GetMessagesAsync(chat.Id))[^1].FellBackFrom.ShouldBe("gone/model");

        // Kept: "no endpoints" is often a host missing for an hour, and the next turn tries again.
        (await Provider().GetAsync(chat.Id)).ShouldNotBeNull().Model.ShouldBe("gone/model");
    }

    [Fact]
    public async Task A_failure_that_is_not_about_the_model_does_not_spend_a_second_call_on_the_default()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "big/model");
        _model.Fails("The account is out of credit.");

        await Should.ThrowAsync<ReplyMissingException>(() => Provider().SendAsync(chat.Id, "I come in."));

        _model.Models.ShouldBe(["big/model"]);
    }

    [Fact]
    public async Task A_branch_keeps_its_storys_model()
    {
        var chat = await StoryAsync();
        await Provider().SetModelAsync(chat.Id, "small/model");
        _model.Says("She looks up.");
        var turns = await Provider().SendAsync(chat.Id, "I come in.");

        var branch = await Provider().BranchAsync(chat.Id, turns[^1].Id, "Vardhal (2)");

        branch.Model.ShouldBe("small/model");
    }
}
