using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Services;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Web.Pages;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A story's written opening is not rerolled before the reader has taken a turn.
/// </summary>
/// <remarks>
/// The opening is a page the reader wrote, and it sits at the top of the story for the rest of
/// its life; a reroll there would trade it for a guess. The rule is told apart by two things
/// because either alone is wrong, so both of the cases that would fool one of them are here: a
/// beat written by <c>/do</c> with no turn of the reader's, and a reply that follows one.
/// </remarks>
public sealed class OpeningRerollTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<Chat> OpenedAsync()
        => await Provider().CreateAsync(new NewConversation
        {
            Name = "Vardhal",
            Speaker = "Elena",
            Opening = "*Rain against the lamp room glass.*",
        });

    [Fact]
    public async Task The_opening_alone_is_not_rerolled_and_stays_as_written()
    {
        var chat = await OpenedAsync();

        var refused = await Should.ThrowAsync<AirpException>(
            () => Provider().RegenerateAsync(chat.Id, RegenerateReason.None));

        refused.Message.ShouldContain("opening");
        _model.Calls.ShouldBeEmpty();
        (await Provider().GetMessagesAsync(chat.Id)).ShouldHaveSingleItem().Text.ShouldBe("*Rain against the lamp room glass.*");
        RegenerateReasons.CanReplace(await Provider().GetMessagesAsync(chat.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_beat_the_model_wrote_after_the_opening_can_be_rerolled_with_no_turn_of_the_readers()
    {
        var chat = await OpenedAsync();
        _model.Says("The keeper looks up.");
        await Provider().ContinueAsync(chat.Id, instruction: "move on");

        RegenerateReasons.CanReplace(await Provider().GetMessagesAsync(chat.Id)).ShouldBeTrue();

        _model.Says("The keeper does not look up.");
        var after = await Provider().RegenerateAsync(chat.Id, RegenerateReason.None);

        after[^1].Text.ShouldContain("The keeper does not look up.");
    }

    [Fact]
    public async Task Once_the_reader_has_taken_a_turn_the_reply_to_it_is_offered_on_the_page()
    {
        var chat = await OpenedAsync();
        var page = new StoryModel(
            Provider(),
            new TextLibrary(Path.Combine(Path.GetTempPath(), "airp-none-" + Guid.NewGuid().ToString("N"))),
            new ExportService(TestOptions.Default(), NullLogger<ExportService>.Instance),
            TestOptions.Default());

        await page.OnGetAsync(chat.Id, all: false, CancellationToken.None);
        page.CanReroll.ShouldBeFalse();

        _model.Says("Elena looks up.");
        await Provider().SendAsync(chat.Id, "I come in from the rain.");

        await page.OnGetAsync(chat.Id, all: false, CancellationToken.None);
        page.CanReroll.ShouldBeTrue();
    }
}
