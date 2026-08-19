using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Infrastructure;
using Microsoft.Extensions.Configuration;
using Airp.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// Reaching embeddings somewhere other than where the replies come from.
/// </summary>
/// <remarks>
/// The split exists because the two are not always one service. A provider's own API is
/// cheaper than the same model through a router and caches prefixes, which is what this
/// prompt's layer order is built for — but DeepSeek's, the obvious candidate, has no
/// embeddings endpoint at all. Without the split, pointing the client at it would take
/// retrieval away and say nothing.
/// </remarks>
public class EmbeddingEndpointTests
{
    private const string SuccessBody = """
        { "data": [ { "embedding": [0.1, 0.2, 0.3] } ] }
        """;

    private static OpenRouterEmbeddingClient Build(
        ScriptedHandler handler,
        Action<AirpOptions>? configure = null,
        Action<ISecretStore>? onSecrets = null)
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("sk-test"));
        onSecrets?.Invoke(secrets);

        return new OpenRouterEmbeddingClient(
            new HttpClient(handler),
            secrets,
            TestOptions.Default(configure),
            NullLogger<OpenRouterEmbeddingClient>.Instance);
    }

    [Fact]
    public void Unset_it_falls_back_to_the_api_the_replies_come_from()
    {
        // A setting that must be filled in to keep working is a setting that breaks every
        // existing install on upgrade.
        var options = new ModelOptions();

        options.ResolvedEmbeddingBaseUrl.ShouldBe(options.BaseUrl);
        options.ResolvedEmbeddingApiKeyName.ShouldBe(options.ApiKeyName);
    }

    [Fact]
    public void A_blank_setting_is_treated_as_unset_rather_than_as_an_empty_address()
    {
        var options = new ModelOptions { EmbeddingBaseUrl = "   ", EmbeddingApiKeyName = "" };

        options.ResolvedEmbeddingBaseUrl.ShouldBe(options.BaseUrl);
        options.ResolvedEmbeddingApiKeyName.ShouldBe(options.ApiKeyName);
    }

    [Fact]
    public async Task With_no_split_the_call_goes_to_the_chat_api()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        await Build(handler).EmbedAsync(["a line of the story"]);

        handler.LastRequest!.RequestUri!.ToString()
            .ShouldBe("https://openrouter.ai/api/v1/embeddings");
    }

    [Fact]
    public async Task With_a_split_the_call_goes_where_the_split_says()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var client = Build(handler, o =>
        {
            // Replies from DeepSeek direct; embeddings still somewhere that offers them.
            o.Model.BaseUrl = "https://api.deepseek.com/v1";
            o.Model.EmbeddingBaseUrl = "https://openrouter.ai/api/v1";
        });

        await client.EmbedAsync(["a line of the story"]);

        handler.LastRequest!.RequestUri!.ToString()
            .ShouldBe("https://openrouter.ai/api/v1/embeddings");
    }

    [Fact]
    public async Task A_split_endpoint_can_carry_its_own_key()
    {
        // A second endpoint usually means a second account.
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);
        ISecretStore? captured = null;

        var client = Build(
            handler,
            o =>
            {
                o.Model.ApiKeyName = "DEEPSEEK_API_KEY";
                o.Model.EmbeddingApiKeyName = "OPENROUTER_API_KEY";
            },
            secrets => captured = secrets);

        await client.EmbedAsync(["a line of the story"]);

        await captured!.Received().GetAsync("OPENROUTER_API_KEY", Arg.Any<CancellationToken>());
        await captured!.DidNotReceive().GetAsync("DEEPSEEK_API_KEY", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_key_names_the_secret_the_embeddings_call_actually_wanted()
    {
        // Naming the chat key here would send the reader to set the wrong secret.
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var client = Build(
            handler,
            o => o.Model.EmbeddingApiKeyName = "SOME_OTHER_KEY",
            secrets => secrets
                .GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>(null)));

        var thrown = await Should.ThrowAsync<Airp.Domain.ModelUnavailableException>(
            async () => await client.EmbedAsync(["a line"]));

        thrown.Message.ShouldContain("SOME_OTHER_KEY");
    }
}

/// <summary>
/// Where a configured relative path actually lands.
/// </summary>
/// <remarks>
/// Observed on a real machine: exports written to <c>C:\Users\&lt;name&gt;\exports</c> rather than
/// under the application's own root, because <c>./exports</c> reached the file system still
/// relative and the runtime resolved it against the working directory. A terminal application
/// is launched from wherever the reader is standing, so that is a different folder each time.
/// </remarks>
public class ExportPathTests
{
    [Fact]
    public void A_relative_export_directory_is_made_absolute_against_the_application_root()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddLogging();
        services.AddAirpInfrastructure(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AirpOptions>>()
            .CurrentValue;

        Path.IsPathRooted(options.ExportDirectory).ShouldBeTrue();
        options.ExportDirectory.ShouldBe(Airp.Infrastructure.AppPaths.Resolve("./exports"));
        options.ExportDirectory.ShouldStartWith(Airp.Infrastructure.AppPaths.Root);
    }

    [Fact]
    public void An_absolute_export_directory_is_left_where_it_was_put()
    {
        // Someone who names a folder means that folder.
        var wanted = Path.Combine(Path.GetTempPath(), "airp-export-test");

        var settings = new Dictionary<string, string?>
        {
            ["Airp:ExportDirectory"] = wanted,
        };

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddLogging();
        services.AddAirpInfrastructure(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build());

        using var provider = services.BuildServiceProvider();
        var options = provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AirpOptions>>()
            .CurrentValue;

        options.ExportDirectory.ShouldBe(Path.GetFullPath(wanted));
    }
}

/// <summary>
/// Telling the router which hosts to avoid.
/// </summary>
/// <remarks>
/// Measured in one session: a single host returned four consecutive replies of token soup
/// opening with the model's own start-of-sequence marker, while two others answered the same
/// prompt with eight hundred coherent tokens. The model was never the problem; the machine it
/// landed on was. A denied host costs nothing and cannot be reached again.
/// </remarks>
public class ProviderRoutingTests
{
    private const string SuccessBody = """
        {
          "choices": [ { "message": { "content": "She looks up." }, "finish_reason": "stop" } ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 5 }
        }
        """;

    private static OpenRouterClient Build(ScriptedHandler handler, Action<AirpOptions>? configure = null)
    {
        var secrets = Substitute.For<ISecretStore>();
        secrets.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("sk-test"));

        return new OpenRouterClient(
            new HttpClient(handler),
            secrets,
            TestOptions.Default(configure),
            NullLogger<OpenRouterClient>.Instance);
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> SendAsync(
        ScriptedHandler handler,
        Action<AirpOptions>? configure = null)
    {
        await Build(handler, configure).CompleteAsync([new ModelMessage(ModelRole.User, "hello")]);
        return System.Text.Json.Nodes.JsonNode.Parse(handler.LastBody!)!;
    }

    [Fact]
    public async Task With_nothing_configured_the_request_carries_no_routing_at_all()
    {
        // The one field here that is not OpenAI's. Anything else speaking the same shape would
        // either ignore it or refuse the call, and there is no reason to find out which on every
        // turn when nobody asked for routing.
        var sent = await SendAsync(new ScriptedHandler(HttpStatusCode.OK, SuccessBody));

        sent["provider"].ShouldBeNull();
    }

    [Fact]
    public async Task A_denied_host_is_sent_as_the_router_spells_it()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var sent = await SendAsync(handler, o => o.Model.IgnoreProviders = ["deepinfra"]);

        sent["provider"]!["ignore"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["deepinfra"]);
        sent["provider"]!["order"].ShouldBeNull();
    }

    [Fact]
    public async Task Preferred_hosts_keep_the_order_they_were_written_in()
    {
        // It is a preference list, so the order is the whole content of it.
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var sent = await SendAsync(handler, o => o.Model.PreferProviders = ["gmicloud", "baidu"]);

        sent["provider"]!["order"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe(["gmicloud", "baidu"]);
    }

    [Fact]
    public async Task Fallbacks_are_only_mentioned_when_a_choice_has_been_made()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var quiet = await SendAsync(handler, o => o.Model.IgnoreProviders = ["deepinfra"]);
        quiet["provider"]!["allow_fallbacks"].ShouldBeNull();

        var stated = await SendAsync(handler, o =>
        {
            o.Model.PreferProviders = ["gmicloud"];
            o.Model.AllowProviderFallbacks = false;
        });

        stated["provider"]!["allow_fallbacks"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Blank_entries_are_dropped_rather_than_sent_as_empty_names()
    {
        // An empty string is not a host, and a routing object full of them would be refused or
        // ignored wholesale — taking the real entries with it.
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var sent = await SendAsync(handler, o => o.Model.IgnoreProviders = ["  ", "deepinfra", ""]);

        sent["provider"]!["ignore"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_list_of_nothing_but_blanks_sends_no_routing()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SuccessBody);

        var sent = await SendAsync(handler, o => o.Model.IgnoreProviders = ["   "]);

        sent["provider"].ShouldBeNull();
    }
}
