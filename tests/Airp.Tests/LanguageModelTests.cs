using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Secrets;
using NSubstitute;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// A handler that answers from a script instead of the network, and keeps the request it was
/// given so a test can assert on what would have been sent.
/// </summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;

    public ScriptedHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    /// <summary>The last request that reached the handler.</summary>
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>The body of the last request, as sent.</summary>
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body, Encoding.UTF8, "application/json"),
        };
    }
}

public class SecretStoreTests
{
    private static DpapiSecretStore Store(out string directory)
    {
        directory = Path.Combine(Path.GetTempPath(), "airp-secret-tests", Guid.NewGuid().ToString("N"));
        return new DpapiSecretStore(NullLogger<DpapiSecretStore>.Instance, directory);
    }

    [Fact]
    public async Task Missing_secret_reads_as_null_rather_than_throwing()
    {
        var store = Store(out _);

        (await store.GetAsync("AIRP_TEST_ABSENT_" + Guid.NewGuid().ToString("N"))).ShouldBeNull();
    }

    [Fact]
    public async Task Falls_back_to_an_environment_variable_of_the_same_name()
    {
        var store = Store(out _);
        var name = "AIRP_TEST_ENV_" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(name, "from-environment");

            (await store.GetAsync(name)).ShouldBe("from-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task A_stored_secret_wins_over_the_environment()
    {
        // The precedence is the whole point of having both: moving a key into the store has to
        // be enough on its own, without hunting down a variable set months ago for another tool.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = Store(out var directory);
        var name = "AIRP_TEST_BOTH_" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(name, "from-environment");
            await store.SetAsync(name, "from-store");

            (await store.GetAsync(name)).ShouldBe("from-store");
            (await store.DescribeAsync(name)).ShouldContain("encrypted store");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Removing_a_stored_secret_uncovers_the_environment_again()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = Store(out var directory);
        var name = "AIRP_TEST_REMOVE_" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(name, "from-environment");
            await store.SetAsync(name, "from-store");
            await store.RemoveAsync(name);

            (await store.GetAsync(name)).ShouldBe("from-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Describing_a_secret_never_reveals_it()
    {
        var store = Store(out _);
        var name = "AIRP_TEST_DESCRIBE_" + Guid.NewGuid().ToString("N");

        try
        {
            Environment.SetEnvironmentVariable(name, "super-secret-value");

            var described = await store.DescribeAsync(name);

            described.ShouldNotContain("super-secret-value");
            described.ShouldContain(name);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}

public class OpenRouterClientTests
{
    private const string SuccessBody = """
        {
          "model": "deepseek/deepseek-v4-flash",
          "choices": [
            { "message": { "role": "assistant", "content": "I could say the same about you." },
              "finish_reason": "stop" }
          ],
          "usage": { "prompt_tokens": 114, "completion_tokens": 59 }
        }
        """;

    private static OpenRouterClient Build(
        ScriptedHandler handler,
        string? key = "sk-or-test",
        Action<AirpOptions>? configure = null)
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(key));

        return new OpenRouterClient(
            new HttpClient(handler),
            secrets,
            TestOptions.Default(configure),
            NullLogger<OpenRouterClient>.Instance);
    }

    [Fact]
    public async Task Sends_the_configured_model_and_the_messages_in_order()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);
        var client = Build(handler);

        await client.CompleteAsync(
        [
            new ModelMessage(ModelRole.System, "You are Elena."),
            new ModelMessage(ModelRole.User, "Hello."),
        ]);

        var sent = JsonNode.Parse(handler.LastBody!)!;

        sent["model"]!.GetValue<string>().ShouldBe("deepseek/deepseek-v4-flash");
        sent["stream"]!.GetValue<bool>().ShouldBeFalse();

        var messages = sent["messages"]!.AsArray();
        messages.Count.ShouldBe(2);
        messages[0]!["role"]!.GetValue<string>().ShouldBe("system");
        messages[1]!["role"]!.GetValue<string>().ShouldBe("user");
        messages[1]!["content"]!.GetValue<string>().ShouldBe("Hello.");
    }

    [Fact]
    public async Task Carries_the_key_as_a_bearer_token()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        await Build(handler).CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]);

        handler.LastRequest!.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.ShouldBe("sk-or-test");
    }

    [Fact]
    public async Task An_explicit_model_overrides_the_configured_one()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        await Build(handler).CompleteAsync(
            [new ModelMessage(ModelRole.User, "Hello.")],
            model: "deepseek/deepseek-v4-pro");

        JsonNode.Parse(handler.LastBody!)!["model"]!.GetValue<string>()
            .ShouldBe("deepseek/deepseek-v4-pro");
    }

    [Fact]
    public async Task Reads_the_reply_and_the_usage_back()
    {
        var reply = await Build(new ScriptedHandler(HttpStatusCode.OK, SuccessBody))
            .CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]);

        reply.Text.ShouldBe("I could say the same about you.");
        reply.Model.ShouldBe("deepseek/deepseek-v4-flash");
        reply.PromptTokens.ShouldBe(114);
        reply.CompletionTokens.ShouldBe(59);
        reply.FinishReason.ShouldBe("stop");
        reply.WasTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reply_stopped_at_the_ceiling_is_reported_as_truncated()
    {
        const string body = """
            { "choices": [ { "message": { "content": "It went on and" }, "finish_reason": "length" } ] }
            """;

        var reply = await Build(new ScriptedHandler(HttpStatusCode.OK, body))
            .CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]);

        reply.WasTruncated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_missing_key_fails_before_anything_is_sent()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);
        var client = Build(handler, key: null);

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(
            async () => await client.CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]));

        thrown.StatusCode.ShouldBe(401);
        handler.LastRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "airp secret set")]
    [InlineData(HttpStatusCode.PaymentRequired, "credit")]
    [InlineData(HttpStatusCode.NotFound, "airp models")]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limited")]
    public async Task Each_refusal_gets_a_hint_that_matches_it(HttpStatusCode status, string expected)
    {
        // The status is carried so the hint can name the actual problem. A key that is out of
        // credit and one that is wrong both stop the reply, and sending the reader to the wrong
        // remedy costs more time than the failure itself.
        var body = """{ "error": { "message": "nope" } }""";
        var client = Build(new ScriptedHandler(status, body));

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(
            async () => await client.CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]));

        thrown.StatusCode.ShouldBe((int)status);
        thrown.RecoveryHint.ShouldContain(expected);
    }

    [Fact]
    public async Task An_error_body_contributes_its_message_but_not_the_request()
    {
        var body = """{ "error": { "message": "No endpoints found for that model." } }""";

        var thrown = await Should.ThrowAsync<ModelUnavailableException>(async () =>
            await Build(new ScriptedHandler(HttpStatusCode.NotFound, body))
                .CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]));

        thrown.Message.ShouldContain("No endpoints found for that model.");
    }

    [Fact]
    public async Task A_success_with_no_content_is_treated_as_a_failure()
    {
        // An empty string here would be stored as though the model had answered with silence,
        // and the conversation would carry a turn that never happened.
        const string body = """{ "choices": [ { "finish_reason": "stop" } ] }""";

        await Should.ThrowAsync<ModelUnavailableException>(async () =>
            await Build(new ScriptedHandler(HttpStatusCode.OK, body))
                .CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_reported_as_such()
    {
        var thrown = await Should.ThrowAsync<ModelUnavailableException>(async () =>
            await Build(new ScriptedHandler(HttpStatusCode.OK, "<html>gateway</html>"))
                .CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]));

        thrown.Message.ShouldContain("not JSON");
    }

    [Fact]
    public async Task An_empty_conversation_is_rejected_without_a_call()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        await Should.ThrowAsync<ArgumentException>(
            async () => await Build(handler).CompleteAsync([]));

        handler.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task The_base_url_is_honoured_and_not_double_slashed()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);
        var client = Build(handler, configure: o => o.Model.BaseUrl = "https://example.test/v1/");

        await client.CompleteAsync([new ModelMessage(ModelRole.User, "Hello.")]);

        handler.LastRequest!.RequestUri!.ToString()
            .ShouldBe("https://example.test/v1/chat/completions");
    }

    [Fact]
    public async Task Listing_models_returns_the_identifiers()
    {
        const string body = """
            { "data": [ { "id": "deepseek/deepseek-v4-flash" }, { "id": "deepseek/deepseek-v4-pro" } ] }
            """;

        var models = await Build(new ScriptedHandler(HttpStatusCode.OK, body)).ListModelsAsync();

        models.ShouldBe(["deepseek/deepseek-v4-flash", "deepseek/deepseek-v4-pro"]);
    }
}
