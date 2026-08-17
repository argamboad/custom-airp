using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

public sealed class TrackerParsingTests
{
    private static List<TrackerRecord> Meters(params (string Name, double Value)[] meters) =>
    [
        .. meters.Select(m => new TrackerRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = "c1",
            Name = m.Name,
            Value = m.Value,
            Max = 100,
        }),
    ];

    [Fact]
    public void Nothing_configured_renders_nothing()
        => Trackers.Render([]).ShouldBeNull();

    [Fact]
    public void The_block_carries_the_current_values_and_the_shape_to_use()
    {
        var rendered = Trackers.Render(Meters(("AFFECTION — Allan", 62)));

        rendered.ShouldNotBeNull();
        rendered.ShouldContain("[AFFECTION — Allan]");
        rendered.ShouldContain("62/100");
        rendered.ShouldContain("{value}/{max}");
    }

    [Fact]
    public void A_rule_travels_with_its_meter()
    {
        var meters = Meters(("STABILITY", 40));
        meters[0].Rule = "Cannot rise while TRUST is below 60.";

        Trackers.Render(meters).ShouldNotBeNull().ShouldContain("Cannot rise while TRUST is below 60.");
    }

    [Fact]
    public void A_rendered_line_moves_the_stored_value()
    {
        var meters = Meters(("TRUST", 40));

        var moved = Trackers.Absorb(meters, "…scene…\n\n[TRUST] ######.... 62/100 | Δ +22 | chosen support", 9);

        moved.ShouldBe(1);
        meters[0].Value.ShouldBe(62);
        meters[0].Delta.ShouldBe(22);
        meters[0].Note.ShouldBe("chosen support");
        meters[0].UpdatedAtSequence.ShouldBe(9);
    }

    [Fact]
    public void The_delta_is_computed_here_rather_than_believed()
    {
        // The model reports one, but it is arithmetic on a number this side already knows, and
        // a model that miscounts would leave the record disagreeing with itself.
        var meters = Meters(("TRUST", 40));

        Trackers.Absorb(meters, "[TRUST] 45/100 | Δ +99 | nonsense", 3);

        meters[0].Delta.ShouldBe(5);
    }

    [Theory]
    [InlineData("[TRUST] ❤️❤️🤍🤍 55/100 | Δ +15 | warmed")]
    [InlineData("[TRUST] 55/100 | Δ +15 | warmed")]
    [InlineData("[TRUST]  ####......  55 / 100  |  delta +15  |  warmed")]
    public void The_bar_is_not_worth_failing_a_parse_over(string line)
    {
        // Models render hearts, blocks or nothing. Which glyph they picked is not information.
        var meters = Meters(("TRUST", 40));

        Trackers.Absorb(meters, line, 1).ShouldBe(1);
        meters[0].Value.ShouldBe(55);
    }

    [Fact]
    public void A_value_past_the_top_of_the_scale_is_clamped()
    {
        // Storing 250/100 would poison every later turn with an impossible starting point.
        var meters = Meters(("TRUST", 90));

        Trackers.Absorb(meters, "[TRUST] 250/100 | Δ +160 | euphoric", 1);

        meters[0].Value.ShouldBe(100);
    }

    [Fact]
    public void A_meter_the_reader_never_defined_is_ignored()
    {
        // A model inventing a meter is writing fiction, not configuration.
        var meters = Meters(("TRUST", 40));

        Trackers.Absorb(meters, "[TRUST] 40/100 | Δ 0 | steady\n[LUST] 80/100 | Δ +80 | invented", 1);

        meters.Count.ShouldBe(1);
        meters[0].Value.ShouldBe(40);
    }

    [Fact]
    public void A_reply_with_no_meters_leaves_them_alone()
        => Trackers.Absorb(Meters(("TRUST", 40)), "She says nothing and turns away.", 1).ShouldBe(0);
}

public sealed class TrackerFlowTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(o => o.Model.ContextBudget = 100000),
        NullLogger<LocalConversationProvider>.Instance);

    private async Task<string> SeedAsync()
        => (await Provider().CreateAsync(new Airp.Domain.Conversations.NewConversation { Name = "Vardhal", Speaker = "Elena", CharacterDefinition = "You are Elena." })).Id;

    [Fact]
    public async Task A_story_with_no_meters_says_nothing_about_meters()
    {
        var id = await SeedAsync();
        _model.Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        _model.Calls[^1].Any(m => m.Content.Contains("{value}/{max}")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_meter_reaches_the_prompt_and_its_new_value_is_kept()
    {
        var id = await SeedAsync();
        await Provider().SetTrackerAsync(id, "TRUST", value: 40);
        _model.Says("Ella afloja un poco.\n\n[TRUST] ######.... 58/100 | Δ +18 | shared something");

        await Provider().SendAsync(id, "Te cuento algo.");

        _model.Calls[^1].Any(m => m.Content.Contains("[TRUST]")).ShouldBeTrue();

        var stored = (await Provider().TrackersAsync(id)).Single();
        stored.Value.ShouldBe(58);
        stored.Delta.ShouldBe(18);
        stored.Note.ShouldBe("shared something");
    }

    [Fact]
    public async Task The_value_survives_the_turn_that_set_it_scrolling_away()
    {
        // The reason the value is stored rather than left in the transcript: a card that only
        // instructs the format needs the model to still be able to see the previous number.
        var id = await SeedAsync();
        await Provider().SetTrackerAsync(id, "TRUST", value: 40);
        _model.Says("[TRUST] 58/100 | Δ +18 | opened up").Says("Sigue.");

        await Provider().SendAsync(id, "One.");
        await Provider().SendAsync(id, "Two.");

        // Two messages mention the meter: the stored reply that rendered it, and the block
        // this side injects. Only the injected one is the contract under test.
        var block = _model.Calls[^1].Single(m => m.Content.Contains("{value}/{max}"));
        block.Content.ShouldContain("58/100");
    }

    [Fact]
    public async Task Meters_can_be_added_and_taken_away_freely()
    {
        var id = await SeedAsync();

        await Provider().SetTrackerAsync(id, "TRUST", value: 40);
        await Provider().SetTrackerAsync(id, "SUSPICION", value: 10, max: 5);
        (await Provider().TrackersAsync(id)).Count.ShouldBe(2);

        // Clamped on the way in: a starting value above the scale is a typo, not an intent.
        (await Provider().TrackersAsync(id)).Single(t => t.Name == "SUSPICION").Value.ShouldBe(5);

        (await Provider().RemoveTrackerAsync(id, "SUSPICION")).ShouldBeTrue();
        (await Provider().RemoveTrackerAsync(id, "SUSPICION")).ShouldBeFalse();
        (await Provider().TrackersAsync(id)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Setting_a_rule_does_not_reset_the_value()
    {
        var id = await SeedAsync();
        await Provider().SetTrackerAsync(id, "STABILITY", value: 40);
        await Provider().SetTrackerAsync(id, "STABILITY", rule: "Cannot rise while TRUST < 60.");

        var stored = (await Provider().TrackersAsync(id)).Single();
        stored.Value.ShouldBe(40);
        stored.Rule.ShouldBe("Cannot rise while TRUST < 60.");
    }

    [Fact]
    public async Task Inner_thoughts_are_off_until_asked_for()
    {
        var id = await SeedAsync();
        _model.Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        _model.Calls[^1].Any(m => m.Content.Contains("inner thoughts")).ShouldBeFalse();
    }

    [Fact]
    public async Task Inner_thoughts_reach_the_prompt_once_turned_on()
    {
        var id = await SeedAsync();
        await Provider().SetInnerThoughtsAsync(id, true);
        _model.Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        var directive = _model.Calls[^1].Single(m => m.Content.Contains("inner thoughts"));
        directive.Content.ShouldContain("never for the user");
    }

    [Fact]
    public async Task Inner_thoughts_can_be_turned_back_off()
    {
        var id = await SeedAsync();
        await Provider().SetInnerThoughtsAsync(id, true);
        await Provider().SetInnerThoughtsAsync(id, false);
        _model.Says("Fine.");

        await Provider().SendAsync(id, "Hello.");

        _model.Calls[^1].Any(m => m.Content.Contains("inner thoughts")).ShouldBeFalse();
    }
}

public sealed class TrackerSemanticsTests
{
    private static TrackerRecord Meter(string name) => new()
    {
        Id = "t1",
        ConversationId = "c1",
        Name = name,
        Value = 45,
        Max = 100,
    };

    [Fact]
    public void A_bare_name_leaves_the_model_inferring()
    {
        // Not a failure, but worth pinning: with only a name, nothing in the prompt says what
        // moves the meter or what its numbers mean.
        var rendered = Trackers.Render([Meter("ADMIRATION")]);

        rendered.ShouldNotBeNull().ShouldContain("[ADMIRATION]");
        rendered.ShouldNotContain("measures:");
        rendered.ShouldNotContain("scale:");
    }

    [Fact]
    public void What_it_measures_and_what_the_numbers_mean_both_reach_the_prompt()
    {
        var meter = Meter("ADMIRATION");
        meter.Means = "Rises when you do something difficult well; falls when you posture.";
        meter.Anchors = "0 unimpressed · 50 respects you · 100 would follow you anywhere";
        meter.Rule = "Cannot pass 70 while TRUST is below 40.";

        var rendered = Trackers.Render([meter]);

        rendered.ShouldNotBeNull().ShouldContain("measures: Rises when you do something difficult well");
        rendered.ShouldContain("scale: 0 unimpressed");
        rendered.ShouldContain("rule: Cannot pass 70");
    }

    [Fact]
    public void The_instruction_says_how_far_a_meter_may_move()
    {
        // Without a size, "move it a little" is read as anything from 1 to 30.
        var rendered = Trackers.Render([Meter("TRUST")]);

        rendered.ShouldNotBeNull().ShouldContain("one to three points");
        rendered.ShouldContain("Δ 0");
    }
}
