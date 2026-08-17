using Airp.Application.Options;
using Airp.Domain.Conversations;
using Shouldly;

namespace Airp.Tests;

public class SettingScaleTests
{
    private static AirpOptions WithScale(string key, string? title, params (string Label, string Description)[] levels)
    {
        var options = new AirpOptions();

        options.Scales[key] = new ScaleOptions
        {
            Title = title,
            Levels = [.. levels.Select(l => new ScaleLevel { Label = l.Label, Description = l.Description })],
        };

        return options;
    }

    private static (string, string)[] Five(string prefix) =>
        [.. Enumerable.Range(1, 5).Select(i => ($"{prefix}{i}", $"means {prefix}{i}"))];

    [Fact]
    public void With_nothing_configured_the_shipped_scale_applies()
    {
        SettingScales.Describe(ChatSetting.Lust, 3, new AirpOptions()).Label.ShouldBe("Explicit");
        SettingScales.Title(ChatSetting.Lust, null).ShouldBe("Lust Level");
    }

    [Fact]
    public void A_replacement_scale_is_used_for_both_the_screen_and_the_prompt()
    {
        // The same text on both sides is the whole constraint: a reader picks a level after
        // reading what it means, so sending the model different words makes the dial mean two
        // things at once.
        var options = WithScale("Lust", "Heat", Five("H"));

        SettingScales.Title(ChatSetting.Lust, options).ShouldBe("Heat");
        SettingScales.Describe(ChatSetting.Lust, 2, options).Label.ShouldBe("H3");

        var directives = SettingScales.Directives(new ChatSettings { Lust = 2 }, options);

        directives.ShouldNotBeNull().ShouldContain("Heat: H3 — means H3.");
    }

    [Fact]
    public void A_scale_with_the_wrong_number_of_levels_is_ignored()
    {
        // Levels are read by index. A short list would quietly make the top of the dial mean
        // whatever the bottom of it meant, which is worse than not applying the scale at all.
        var options = WithScale("Lust", "Heat", ("Low", "a"), ("High", "b"));

        SettingScales.Describe(ChatSetting.Lust, 3, options).Label.ShouldBe("Explicit");
    }

    [Fact]
    public void A_title_can_be_replaced_without_replacing_the_levels()
    {
        var options = WithScale("Lust", "Heat", Five("H"));
        options.Scales["ResponseLength"] = new ScaleOptions { Title = "Length" };

        // Levels absent, so the shipped ones stand; the title still applies.
        SettingScales.Title(ChatSetting.ResponseLength, options).ShouldBe("Length");
        SettingScales.Describe(ChatSetting.ResponseLength, 0, options).Label.ShouldBe("Minimal");
    }

    [Fact]
    public void Keys_are_matched_regardless_of_case()
        => SettingScales.Title(ChatSetting.Lust, WithScale("lust", "Heat", Five("H"))).ShouldBe("Heat");

    [Fact]
    public void Creativity_never_reaches_the_prompt()
    {
        // It is spent on the sampler's temperature, which is a far stronger lever over how
        // varied a reply is than asking a model in words to be varied.
        SettingScales.Directives(new ChatSettings { Creativity = 4 }, null).ShouldBeNull();
    }

    [Fact]
    public void Unset_dials_contribute_nothing()
        => SettingScales.Directives(new ChatSettings(), null).ShouldBeNull();

    [Fact]
    public void An_unset_level_is_described_without_blaming_a_site()
    {
        // The same scale is read by an adapter that talks to a site and by one that does not.
        var described = SettingScales.Describe(ChatSetting.Lust, null, null);

        described.Label.ShouldBe("Not set");
        described.Description.ShouldNotContain("site");
    }
}
