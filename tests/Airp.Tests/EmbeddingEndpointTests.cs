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
