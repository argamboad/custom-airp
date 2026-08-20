using Airp.Application.Context;
using Airp.Application.Options;
using Shouldly;

namespace Airp.Tests;

public class ModelRouterTests
{
    private static ModelOptions Settings(string? background = null) => new()
    {
        Name = "deepseek/deepseek-v4-flash",
        BackgroundModel = background,
        Temperature = 1.0,
        MaxTokens = 1024,
    };

    [Fact]
    public void A_reply_honours_what_the_conversation_asked_for()
    {
        var choice = ModelRouter.For(ModelTask.Reply, Settings(), temperature: 1.4, maxTokens: 200);

        choice.Model.ShouldBe("deepseek/deepseek-v4-flash");
        choice.Temperature.ShouldBe(1.4);
        choice.MaxTokens.ShouldBe(200);
    }

    [Fact]
    public void A_reply_with_nothing_asked_for_falls_back_to_the_configured_settings()
    {
        var choice = ModelRouter.For(ModelTask.Reply, Settings());

        choice.Temperature.ShouldBe(1.0);
        choice.MaxTokens.ShouldBe(1024);
    }

    [Fact]
    public void A_summary_runs_cold_whatever_the_conversation_asked_for()
    {
        // A summariser that embellishes is inventing history the character will believe for the
        // rest of the conversation. The dials belong to the reply, not to the bookkeeping.
        var choice = ModelRouter.For(ModelTask.Summary, Settings(), temperature: 1.4, maxTokens: 2600);

        choice.Temperature.ShouldBe(0.3);
        choice.MaxTokens.ShouldBe(1200);
    }

    [Fact]
    public void Background_work_uses_the_reply_model_until_one_is_named()
    {
        // The default is deliberately not a cheaper model: the saving is fractions of a cent,
        // and whatever reads an adult transcript has to be willing to.
        ModelRouter.For(ModelTask.Summary, Settings()).Model
            .ShouldBe("deepseek/deepseek-v4-flash");
    }

    [Fact]
    public void A_named_background_model_takes_over_the_background_work_only()
    {
        var settings = Settings("deepseek/deepseek-v3.2");

        ModelRouter.For(ModelTask.Summary, settings).Model.ShouldBe("deepseek/deepseek-v3.2");
        ModelRouter.For(ModelTask.Reply, settings).Model.ShouldBe("deepseek/deepseek-v4-flash");
    }

    [Fact]
    public void A_blank_background_model_is_treated_as_unset()
    {
        ModelRouter.For(ModelTask.Summary, Settings("   ")).Model
            .ShouldBe("deepseek/deepseek-v4-flash");
    }
}
