using Airp.Application.Abstractions;
using Airp.Application.Context;
using Shouldly;

namespace Airp.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void An_empty_prompt_costs_nothing()
        => TokenEstimator.ForMessages([]).ShouldBe(0);

    [Fact]
    public void Framing_is_counted_even_for_a_turn_with_no_text()
    {
        // A transcript of many short turns is mostly framing. Ignoring it under-counted the
        // exact shape this application produces.
        TokenEstimator.ForMessage(new ModelMessage(ModelRole.User, string.Empty)).ShouldBe(4);
    }

    [Fact]
    public void Spanish_and_English_are_counted_on_their_own_terms()
    {
        // The reason a real vocabulary replaced a characters-per-token constant. These two
        // strings are nearly the same length and do not cost the same, which is precisely
        // what no single constant can express.
        var english = TokenEstimator.ForText("He leans back against the lockers.");
        var spanish = TokenEstimator.ForText("I'm sitting in the sand, cleaning the knife.");

        english.ShouldBeGreaterThan(0);
        spanish.ShouldBeGreaterThan(english);
    }

    [Fact]
    public void Counting_matches_what_the_provider_reported_for_a_real_transcript()
    {
        // OpenRouter reported 25,368 prompt tokens for the 95 turns of one real export. The
        // assertion is against that number, not against another implementation of the same
        // guess — which is what makes it worth having. Point AIRP_CALIBRATION_EXPORT at that
        // export to run it; skipped when the variable is unset or the file is absent.
        var export = Environment.GetEnvironmentVariable("AIRP_CALIBRATION_EXPORT");

        if (string.IsNullOrWhiteSpace(export) || !File.Exists(export))
        {
            return;
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(export));

        var messages = document.RootElement.GetProperty("Messages").EnumerateArray()
            .Select(m => new ModelMessage(
                m.GetProperty("Role").GetString() == "User" ? ModelRole.User : ModelRole.Assistant,
                m.GetProperty("Text").GetString() ?? string.Empty))
            .ToArray();

        var counted = TokenEstimator.ForMessages(messages);

        (Math.Abs(counted - 25368) / 25368.0).ShouldBeLessThan(0.10);
    }
}

public class ContextBuilderTests
{
    private static ModelMessage User(string text) => new(ModelRole.User, text);

    /// <summary>Names every argument, so adding a layer cannot silently shift the others.</summary>
    private static BuiltContext Build(
        IReadOnlyList<ModelMessage> history,
        int budget,
        string? character = null,
        string? persona = null,
        string? directives = null,
        string? world = null,
        IReadOnlyList<string>? summaries = null,
        IReadOnlyList<string>? memories = null,
        string? trackers = null,
        string? instruction = null)
        => ContextBuilder.Build(
            character, persona, directives, world, summaries, history, memories, trackers,
            instruction, budget);

    private static IReadOnlyList<ModelMessage> History(int count)
        => [.. Enumerable.Range(1, count).Select(i => User($"turn {i} " + new string('x', 350)))];

    [Fact]
    public void The_layers_are_sent_least_volatile_first()
    {
        // The order is the cache contract. Everything up to the first thing that changed since
        // last turn is reused; memories change every turn, so they go after the transcript.
        var built = Build(
            character: "You are Elena.",
            persona: "You are Allan, 34.",
            directives: "Lust Level: Explicit.",
            world: "Elena has a scar.",
            summaries: ["They met at the dock."],
            history: [User("Hello.")],
            memories: ["Elena distrusts Ferrin."],
            trackers: "[TRUST] ####...... 40/100 | Δ 0 | wary",
            instruction: "Do not write the user's actions.",
            budget: 10000);

        built.Sections.Select(static s => s.Name).ShouldBe(
        [
            ContextBuilder.Layer.Character,
            ContextBuilder.Layer.Persona,
            ContextBuilder.Layer.Directives,
            // What is true in the story changes occasionally, not every turn, so it belongs
            // with the stable layers rather than out at the volatile end.
            ContextBuilder.Layer.WorldState,
            ContextBuilder.Layer.Summaries,
            ContextBuilder.Layer.History,
            ContextBuilder.Layer.Memories,
            ContextBuilder.Layer.Trackers,
            ContextBuilder.Layer.Instruction,
        ]);

        built.Messages[0].Content.ShouldBe("You are Elena.");
        built.Messages[^1].Content.ShouldBe("Do not write the user's actions.");

        // Positional indexing was what broke here when the trackers layer arrived. Ask the
        // section by name instead, so the next layer added cannot quietly move this one.
        built.Sections.Single(static s => s.Name == ContextBuilder.Layer.Memories)
            .Messages[0].Content.ShouldContain("Ferrin");
    }

    [Fact]
    public void A_persona_is_framed_as_the_user_rather_than_sent_raw()
    {
        // Personas are written in the third person — "Allan is a Scottish philosopher who…" —
        // and a description of a person, sent unlabelled, reads as another person in the scene.
        var built = Build(
            character: "You are Elena.",
            persona: "Allan McAllister is a Scottish philosopher.",
            history: [User("Hello.")],
            budget: 10000);

        var sent = built.Sections.Single(static s => s.Name == ContextBuilder.Layer.Persona)
            .Messages[0].Content;

        sent.ShouldContain("The user is playing");
        sent.ShouldContain("Allan McAllister is a Scottish philosopher.");
        sent.ShouldContain("never write their words or actions");
    }

    [Fact]
    public void No_persona_means_no_framing_either()
    {
        // The frame exists to introduce a description. With none, it would be an instruction
        // about somebody who was never described.
        var built = Build(character: "You are Elena.", history: [User("Hello.")], budget: 10000);

        built.Sections.Single(static s => s.Name == ContextBuilder.Layer.Persona)
            .Messages.ShouldBeEmpty();
    }

    [Fact]
    public void Absent_layers_contribute_nothing()
    {
        var built = Build(history: [User("Hello.")], budget: 1000);

        built.Messages.Count.ShouldBe(1);
        built.Sections.Count(static s => s.Messages.Count > 0).ShouldBe(1);
    }

    [Fact]
    public void Everything_fits_when_the_budget_is_generous()
    {
        var built = Build(history: History(20), budget: 100000);

        built.Messages.Count.ShouldBe(20);
        built.Dropped.ShouldBe(0);
    }

    [Fact]
    public void The_transcript_is_what_gives_when_the_budget_binds()
    {
        var built = Build(
            character: "You are Elena.",
            history: History(50),
            memories: ["Elena distrusts Ferrin."],
            instruction: "Sigue la escena.",
            budget: 1200);

        built.Dropped.ShouldBeGreaterThan(0);
        built.EstimatedTokens.ShouldBeLessThanOrEqualTo(1200);

        // The character, the memories and the directive are all still there: dropping any of
        // them to keep an older line would trade the reason for the reply against the reply.
        built.Messages[0].Content.ShouldBe("You are Elena.");
        built.Messages[^1].Content.ShouldBe("Sigue la escena.");
        built.Sections.Single(static s => s.Name == ContextBuilder.Layer.Memories)
            .Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void The_turns_kept_are_the_ones_nearest_the_reply()
    {
        var built = Build(history: History(50), budget: 1000);

        var kept = built.Sections.Single(static s => s.Name == ContextBuilder.Layer.History).Messages;

        kept.Count.ShouldBeGreaterThan(0);
        kept[^1].Content.ShouldStartWith("turn 50 ");
        kept[0].Content.ShouldNotStartWith("turn 1 ");
    }

    [Fact]
    public void Kept_turns_stay_in_their_original_order()
    {
        // Filled newest-first, so this guards the reversal that puts them back.
        var built = Build(history: History(50), budget: 2000);

        var kept = built.Sections.Single(static s => s.Name == ContextBuilder.Layer.History).Messages;
        var numbers = kept.Select(m => int.Parse(m.Content.Split(' ')[1])).ToArray();

        numbers.ShouldBe([.. numbers.OrderBy(static n => n)]);
    }

    [Fact]
    public void A_budget_too_small_for_even_the_fixed_layers_drops_the_whole_transcript()
    {
        // It does not throw. A prompt with the character and no history is still a prompt; one
        // that failed to build is a turn the reader loses.
        var built = Build(character: new string('x', 4000), history: History(10), budget: 100);

        built.Sections.Single(static s => s.Name == ContextBuilder.Layer.History)
            .Messages.ShouldBeEmpty();
        built.Dropped.ShouldBe(10);
        built.Messages.Count.ShouldBe(1);
    }

    [Fact]
    public void The_accounting_names_every_layer_that_contributed()
    {
        var built = Build(
            character: "You are Elena.",
            history: History(50),
            budget: 1200);

        var described = built.Describe();

        described.ShouldContain("character");
        described.ShouldContain("history");
        described.ShouldContain("dropped");
        described.ShouldContain("/1200");
        described.ShouldNotContain("directives");
    }
}
