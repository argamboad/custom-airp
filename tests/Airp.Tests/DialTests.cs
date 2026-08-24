using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Dials;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Providers;
using Shouldly;

namespace Airp.Tests;

/// <summary>The pack: what parses, what is refused, and why.</summary>
public sealed class DialPackTests
{
    [Fact]
    public void The_shipped_pack_parses_whole()
    {
        var pack = DialPackParser.Parse(DialService.DefaultPackText());

        pack.Skipped.ShouldBeEmpty();
        pack.Dials.Count.ShouldBe(16);

        // The four originals are all there, under their pack names.
        pack.Find("lust").ShouldNotBeNull();
        pack.Find("response-length").ShouldNotBeNull();
        pack.Find("creativity").ShouldNotBeNull();
        pack.Find("inner-thoughts").ShouldNotBeNull();
    }

    [Fact]
    public void A_scale_without_exactly_five_levels_is_refused_whole()
    {
        // Levels are read by index: a four-level scale would make the top of the dial
        // quietly mean what the fourth level meant.
        var pack = DialPackParser.Parse("""
            {
              "dials": {
                "short": {
                  "kind": "scale", "lever": "prompt", "title": "Short",
                  "levels": [
                    { "label": "A", "text": "a" }, { "label": "B", "text": "b" },
                    { "label": "C", "text": "c" }, { "label": "D", "text": "d" }
                  ]
                }
              }
            }
            """);

        pack.Dials.ShouldBeEmpty();
        pack.Skipped.ShouldHaveSingleItem().Reason.ShouldContain("5");
    }

    [Fact]
    public void A_sampler_dial_without_a_parameter_to_set_is_refused()
    {
        var pack = DialPackParser.Parse("""
            {
              "dials": {
                "loose": {
                  "kind": "scale", "lever": "sampler", "maps": "top_secret", "title": "Loose",
                  "levels": [
                    { "label": "A", "value": 1 }, { "label": "B", "value": 2 },
                    { "label": "C", "value": 3 }, { "label": "D", "value": 4 }, { "label": "E", "value": 5 }
                  ]
                }
              }
            }
            """);

        pack.Dials.ShouldBeEmpty();
        pack.Skipped.ShouldHaveSingleItem().Reason.ShouldContain("maps");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        // The shipped pack documents itself with comments; a parser refusing them would
        // refuse its own default.
        var pack = DialPackParser.Parse("""
            {
              // a comment
              "dials": {
                "note": { "kind": "toggle", "lever": "prompt", "title": "Note", "on": "say less", },
              },
            }
            """);

        pack.Dials.ShouldHaveSingleItem().Key.ShouldBe("note");
    }
}

/// <summary>The engine: what reaches the prompt, and what reaches the sampler.</summary>
public sealed class DialEngineTests
{
    private static readonly DialPack Pack = DialPackParser.Parse(DialService.DefaultPackText());

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(static p => p.Key, static p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void A_scale_renders_its_level_in_the_screens_own_words()
    {
        var directives = DialEngine.Directives(Pack, Values(("lust", "3")));

        directives.ShouldNotBeNull();
        directives.ShouldContain("Lust: Explicit — sexually forward, fast escalation, anatomically detailed.");
    }

    [Fact]
    public void A_sampler_dial_injects_nothing_and_moves_the_parameter()
    {
        // A number the model can see is a number it performs; creativity is temperature only.
        var values = Values(("creativity", "4"), ("anti-loop", "3"), ("response-length", "0"));

        var directives = DialEngine.Directives(Pack, values);
        var sampler = DialEngine.Sampler(Pack, values);

        (directives ?? string.Empty).ShouldNotContain("Creativity");
        (directives ?? string.Empty).ShouldNotContain("Anti-loop");
        sampler.Temperature.ShouldBe(1.4);
        sampler.FrequencyPenalty.ShouldBe(0.7);
        sampler.MaxTokens.ShouldBe(200);
    }

    [Fact]
    public void A_disabled_dial_is_pinned_to_its_default_not_off()
    {
        // Disabled means pinned: the default applies on every prompt, and a stored choice is
        // overridden rather than erased.
        var pack = DialPackParser.Parse("""
            {
              "dials": {
                "guard": {
                  "kind": "toggle", "lever": "prompt", "title": "Guard",
                  "enabled": false, "default": true,
                  "on": "hold the line"
                }
              }
            }
            """);

        var directives = DialEngine.Directives(pack, Values(("guard", "false")));

        directives.ShouldNotBeNull();
        directives.ShouldContain("hold the line");
    }

    [Fact]
    public void The_shipped_agency_guard_injects_nothing_until_asked()
    {
        // The persona layer already carries the standard rule; the dial is pure opt-in, so an
        // untouched conversation's prompt is exactly what it was before dials became a pack.
        DialEngine.Directives(Pack, Values(("agency-guard", "4"))).ShouldBeNull();
    }

    [Fact]
    public void A_toggle_choice_list_and_text_all_render_their_shapes()
    {
        var directives = DialEngine.Directives(Pack, Values(
            ("inner-thoughts", "true"),
            ("pov", "third-past"),
            ("veils", """["graphic violence","character death"]"""),
            ("language", "Spanish")));

        directives.ShouldNotBeNull();
        directives.ShouldContain("inner thoughts");
        directives.ShouldContain("third person limited, past tense");
        directives.ShouldContain("Never depict on the page: graphic violence, character death.");
        directives.ShouldContain("Write every reply in Spanish");
    }

    [Fact]
    public void Nothing_set_injects_nothing()
        => DialEngine.Directives(Pack, Values()).ShouldBeNull();

    [Fact]
    public void A_stored_value_the_pack_cannot_read_says_nothing_rather_than_something_wrong()
    {
        // A level out of range, an option that was renamed: silence, never a wrong sentence.
        DialEngine.Directives(Pack, Values(("lust", "9"), ("pov", "gone"))).ShouldBeNull();
        DialEngine.Sampler(Pack, Values(("creativity", "9"))).Temperature.ShouldBeNull();
    }

    [Fact]
    public void The_legacy_settings_roundtrip_through_dial_values()
    {
        var writes = LegacyDials.FromSettings(
            new ChatSettings { Lust = 3, Creativity = 1, InnerThoughts = true });

        var values = writes.ToDictionary(
            static w => w.Key, static w => w.Value, StringComparer.OrdinalIgnoreCase);

        var settings = LegacyDials.ToSettings(values);

        settings.Lust.ShouldBe(3);
        settings.Creativity.ShouldBe(1);
        settings.ResponseLength.ShouldBeNull();
        settings.InnerThoughts.ShouldBe(true);
    }
}

/// <summary>The dials against a real store and a real send.</summary>
public sealed class DialProviderTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();

    public void Dispose() => _factory.Dispose();

    private LocalConversationProvider Provider() => new(
        _factory,
        _model,
        TestOptions.Default(),
        NullLogger<LocalConversationProvider>.Instance);

    private DialService Dials() => new(
        _factory,
        TestOptions.Default(),
        NullLogger<DialService>.Instance);

    private async Task<string> StartAsync()
        => (await Provider().CreateAsync(
            new NewConversation { Name = "Vardhal", Speaker = "Elena" })).Id;

    [Fact]
    public async Task A_new_dial_reaches_the_prompt_of_the_next_send()
    {
        var id = await StartAsync();
        _model.Says("Fine.");

        await Dials().SetAsync(id, "pacing", "1");
        await Provider().SendAsync(id, "Hello.");

        _model.Calls[^1].First(m => m.Content.Contains("Pacing"))
            .Content.ShouldContain("Slow burn");
    }

    [Fact]
    public async Task The_antiloop_dial_reaches_the_sampler_not_the_prompt()
    {
        var id = await StartAsync();
        _model.Says("Fine.");

        await Dials().SetAsync(id, "anti-loop", "2");
        await Provider().SendAsync(id, "Hello.");

        _model.LastFrequencyPenalty.ShouldBe(0.4);
        _model.Calls[^1].ShouldAllBe(m => !m.Content.Contains("Anti-loop"));
    }

    [Fact]
    public async Task Clearing_a_dial_returns_it_to_the_packs_default()
    {
        var id = await StartAsync();
        var dials = Dials();

        await dials.SetAsync(id, "pacing", "4");
        await dials.SetAsync(id, "pacing", null);

        (await dials.ValuesAsync(id)).ShouldNotContainKey("pacing");
    }

    [Fact]
    public async Task A_branch_carries_the_dial_choices()
    {
        var id = await StartAsync();
        _model.Says("Fine.");
        await Dials().SetAsync(id, "consequence", "3");
        var added = await Provider().SendAsync(id, "Hello.");

        var branch = await Provider().BranchAsync(id, added[^1].Id, "Vardhal (2)");

        (await Dials().ValuesAsync(branch.Id)).ShouldContainKeyAndValue("consequence", "3");
    }

    [Fact]
    public async Task Purging_a_conversation_takes_its_dial_choices_with_it()
    {
        var id = await StartAsync();
        await Dials().SetAsync(id, "pacing", "2");

        await Provider().DeleteConversationAsync(id);
        await Provider().PurgeDeletedAsync();

        await using var store = _factory.CreateDbContext();
        (await store.DialValues.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task The_scales_overlay_still_rewords_the_original_dials()
    {
        // Airp:Scales predates the pack and keeps working: the words change, the numbers stay.
        var options = TestOptions.Default(o => o.Scales["Lust"] = new ScaleOptions
        {
            Title = "Heat",
            Levels =
            [
                new ScaleLevel { Label = "Uno", Description = "d1" },
                new ScaleLevel { Label = "Dos", Description = "d2" },
                new ScaleLevel { Label = "Tres", Description = "d3" },
                new ScaleLevel { Label = "Cuatro", Description = "d4" },
                new ScaleLevel { Label = "Cinco", Description = "d5" },
            ],
        });

        var dials = new DialService(_factory, options, NullLogger<DialService>.Instance);
        var pack = await dials.PackAsync();

        var lust = pack.Find("lust").ShouldNotBeNull();
        lust.Title.ShouldBe("Heat");
        lust.Levels[3].Label.ShouldBe("Cuatro");
        lust.Levels[3].Text.ShouldBe("d4");
    }
}
