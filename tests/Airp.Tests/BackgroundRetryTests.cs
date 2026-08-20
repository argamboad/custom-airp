using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Context;
using Airp.Domain;
using Airp.Domain.Conversations;
using Airp.Infrastructure;
using Airp.Infrastructure.Providers;
using Airp.Infrastructure.Storage.Local;
using Shouldly;

namespace Airp.Tests;

/// <summary>
/// The calls nobody is watching, when the host answers with nothing.
/// </summary>
/// <remarks>
/// A failed reply is visible and the reader presses the key again. A failed summary or
/// extraction is a log line. Observed over seven extractions on one real story: two died on a
/// 200 with no message content — the host, not the request — and one of those was the pass over
/// the first sixty-two messages, which no later call will ever look at again, because the same
/// breath that failed to extract from them summarised them successfully.
/// </remarks>
public sealed class BackgroundRetryTests : IDisposable
{
    private readonly SharedContextFactory _factory = new();
    private readonly ScriptedModel _model = new();
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "airp-retry-" + Guid.NewGuid().ToString("N"));

    public BackgroundRetryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "characters"));

        File.WriteAllText(
            Path.Combine(_root, "characters", "elena.txt"),
            "You are Elena. " + string.Join(' ', Enumerable.Repeat("detail", 3000)));
    }

    public void Dispose()
    {
        _factory.Dispose();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ModelChoice Choice() => new("test-model", 0.3, 400);

    [Fact]
    public async Task A_host_that_answers_with_nothing_is_asked_once_more()
    {
        _model.Empty().Summarises("They met at the dock.");

        var reply = await Background.CompleteAsync(
            _model,
            [new ModelMessage(ModelRole.User, "compress this")],
            Choice(),
            NullLogger.Instance,
            "The summary",
            CancellationToken.None);

        reply.Text.ShouldStartWith("They met at the dock.");
        _model.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_rejected_key_is_not_paid_for_twice()
    {
        // The account or the configuration, not the moment. It will be exactly as wrong on the
        // second call and will cost a second call.
        _model.Rejected().Says("never reached");

        await Should.ThrowAsync<ModelUnavailableException>(async () =>
            await Background.CompleteAsync(
                _model,
                [new ModelMessage(ModelRole.User, "compress this")],
                Choice(),
                NullLogger.Instance,
                "The summary",
                CancellationToken.None));

        _model.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Two_empty_answers_running_give_up_rather_than_loop()
    {
        _model.Empty().Empty().Says("never reached");

        await Should.ThrowAsync<ModelUnavailableException>(async () =>
            await Background.CompleteAsync(
                _model,
                [new ModelMessage(ModelRole.User, "compress this")],
                Choice(),
                NullLogger.Instance,
                "The summary",
                CancellationToken.None));

        _model.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_summary_survives_one_empty_answer()
    {
        // End to end, on the shape where it matters: a character in a file, a transcript over
        // the budget, and the first summarising call coming back with nothing.
        var id = Guid.NewGuid().ToString("N");

        await using (var store = _factory.CreateDbContext())
        {
            store.Conversations.Add(new ConversationRecord
            {
                Id = id,
                Name = "Vardhal",
                Speaker = "Elena",
                CharacterName = "elena",
                CreatedAtUtc = DateTimeOffset.UnixEpoch,
            });

            for (var i = 1; i <= 60; i++)
            {
                store.Messages.Add(new MessageRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ConversationId = id,
                    Sequence = i,
                    Role = i % 2 == 1 ? ChatRole.User : ChatRole.Assistant,
                    Text = $"Turn {i}. " + string.Join(' ', Enumerable.Repeat("word", 60)),
                    SentAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(i),
                });
            }

            await store.SaveChangesAsync();
        }

        _model.Empty().Summarises("They met at the dock.").Says("{\"facts\":[],\"retired\":[]}").Says("Fine.");

        var provider = new LocalConversationProvider(
            _factory,
            _model,
            TestOptions.Default(o =>
            {
                o.Model.ContextBudget = 6000;
                o.Model.MaxTokens = 200;
            }),
            NullLogger<LocalConversationProvider>.Instance,
            embeddings: null,
            library: new TextLibrary(_root));

        await provider.SendAsync(id, "Hello.");

        await using var check = _factory.CreateDbContext();

        (await check.Summaries.CountAsync(s => s.ConversationId == id))
            .ShouldBe(1, "the first attempt came back empty; the second is the one that counts");
    }
}
