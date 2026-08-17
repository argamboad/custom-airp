using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Airp.Application.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;

namespace Airp.Application.Services;

/// <summary>
/// Default <see cref="IExportService"/>: renders chats, prompts and generations to
/// Markdown, JSON or plain text, and optionally writes the result to disk.
/// </summary>
public sealed class ExportService : IExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IOptionsMonitor<AirpOptions> _options;
    private readonly ILogger<ExportService> _logger;

    /// <summary>Initialises the service.</summary>
    /// <param name="options">Application options, used for the default export directory.</param>
    /// <param name="logger">Logger.</param>
    public ExportService(IOptionsMonitor<AirpOptions> options, ILogger<ExportService> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Render(object value, ExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(value);

        return format switch
        {
            // A transcript is projected before serialising so each turn becomes its own
            // entry with a resolved speaker, rather than one opaque body string.
            ExportFormat.Json when value is ConversationTranscript transcript
                => JsonSerializer.Serialize(Project(transcript), JsonOptions),
            ExportFormat.Json => JsonSerializer.Serialize(value, value.GetType(), JsonOptions),
            ExportFormat.PlainText => RenderPlainText(value),
            ExportFormat.Markdown => RenderMarkdown(value),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format."),
        };
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(
        object value,
        ExportFormat format,
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        var content = Render(value, format);
        var destination = path ?? BuildDefaultPath(value, format);

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(destination, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Exported {Type} as {Format} to {Path}.", value.GetType().Name, format, destination);
        return Path.GetFullPath(destination);
    }

    /// <summary>Builds the file name used when the caller supplies no path.</summary>
    /// <param name="value">The object being exported.</param>
    /// <param name="format">Output format.</param>
    /// <returns>A path inside the configured export directory.</returns>
    internal string BuildDefaultPath(object value, ExportFormat format)
    {
        var extension = format switch
        {
            ExportFormat.Json => "json",
            ExportFormat.Markdown => "md",
            _ => "txt",
        };

        var name = value switch
        {
            ConversationTranscript transcript => $"transcript-{transcript.Title}",
            Chat chat => chat.Name,
            _ => value.GetType().Name,
        };

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fileName = $"{Slug(name)}-{stamp}.{extension}";
        return Path.Combine(_options.CurrentValue.ExportDirectory, fileName);
    }

    /// <summary>Converts arbitrary text into a file-name-safe slug.</summary>
    /// <remarks>
    /// Public because callers that name their own files still want the names to match the
    /// ones this service picks; the bulk export does exactly that.
    /// </remarks>
    /// <param name="value">Text to convert.</param>
    /// <returns>A lowercase, hyphenated slug.</returns>
    public static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && builder.Length > 0)
            {
                builder.Append('-');
                lastWasSeparator = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "export" : slug[..Math.Min(60, slug.Length)];
    }

    /// <summary>
    /// Reshapes a transcript so every message serialises as its own numbered entry with its
    /// speaker resolved.
    /// </summary>
    /// <param name="transcript">The transcript.</param>
    /// <returns>An anonymous shape suited to JSON.</returns>
    private static object Project(ConversationTranscript transcript) => new
    {
        transcript.ConversationId,
        transcript.Title,
        transcript.Speaker,
        transcript.ExportedAtUtc,
        transcript.StartedAtUtc,
        transcript.EndedAtUtc,
        MessageCount = transcript.Messages.Count,
        transcript.UserMessageCount,
        transcript.ReplyCount,
        Messages = transcript.Messages.Select((message, index) => new
        {
            Index = index + 1,
            Role = message.Role.ToString(),
            Speaker = transcript.SpeakerFor(message),
            message.SentAtUtc,
            message.WordCount,
            message.Text,
            message.FlaggedReason,
        }),
    };

    private static string RenderTranscriptText(ConversationTranscript transcript)
    {
        var builder = new StringBuilder();

        foreach (var (message, index) in transcript.Messages.Select(static (m, i) => (m, i)))
        {
            var stamp = message.SentAtUtc is { } at
                ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "no timestamp";

            builder.AppendLine($"[{index + 1:D3}] {transcript.SpeakerFor(message)} · {stamp}");
            builder.AppendLine(new string('-', 60));
            builder.AppendLine(message.Text);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string RenderPlainText(object value) => value switch
    {
        ConversationTranscript transcript => RenderTranscriptText(transcript),
        Chat chat => string.Join(
            Environment.NewLine,
            new[] { chat.Name, chat.Speaker, chat.LatestMessage }
                .Where(static s => !string.IsNullOrWhiteSpace(s))),
        _ => value.ToString() ?? string.Empty,
    };

    private static string RenderMarkdown(object value)
    {
        var builder = new StringBuilder();

        switch (value)
        {
            case ConversationTranscript transcript:
                builder.AppendLine("---");
                builder.AppendLine($"title: {YamlScalar(transcript.Title)}");
                builder.AppendLine($"conversationId: {YamlScalar(transcript.ConversationId)}");
                if (transcript.Speaker is { Length: > 0 } speaker)
                {
                    builder.AppendLine($"speaker: {YamlScalar(speaker)}");
                }

                builder.AppendLine($"messages: {transcript.Messages.Count}");
                builder.AppendLine($"fromYou: {transcript.UserMessageCount}");
                builder.AppendLine($"replies: {transcript.ReplyCount}");
                if (transcript.StartedAtUtc is { } started)
                {
                    builder.AppendLine($"started: {started.UtcDateTime:O}");
                }

                builder.AppendLine($"exported: {transcript.ExportedAtUtc.UtcDateTime:O}");
                builder.AppendLine("---");
                builder.AppendLine();
                builder.AppendLine($"# {transcript.Title}");

                foreach (var (message, index) in transcript.Messages.Select(static (m, i) => (m, i)))
                {
                    var stamp = message.SentAtUtc is { } at
                        ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                        : null;

                    builder.AppendLine();
                    builder.AppendLine(
                        $"## {index + 1}. {transcript.SpeakerFor(message)}"
                        + (stamp is null ? string.Empty : $" — {stamp}"));

                    if (message.FlaggedReason is { Length: > 0 } flagged)
                    {
                        builder.AppendLine();
                        builder.AppendLine($"> ⚑ Flagged: {flagged}");
                    }

                    builder.AppendLine();
                    builder.AppendLine(message.Text);
                }

                break;

            case Chat chat:
                builder.AppendLine("---");
                builder.AppendLine($"name: {YamlScalar(chat.Name)}");
                builder.AppendLine($"id: {YamlScalar(chat.Id)}");
                if (chat.LastMessageAtUtc is { } lastMessage)
                {
                    builder.AppendLine($"lastMessage: {lastMessage.UtcDateTime:O}");
                }

                if (chat.Speaker is { Length: > 0 } chatSpeaker)
                {
                    builder.AppendLine($"with: {YamlScalar(chatSpeaker)}");
                }

                builder.AppendLine("---");
                builder.AppendLine();
                builder.AppendLine($"# {chat.Name}");

                AppendSection(builder, "Latest message", chat.LatestMessage);
                break;

            default:
                builder.AppendLine(JsonSerializer.Serialize(value, value.GetType(), JsonOptions));
                break;
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string heading, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"## {heading}");
        builder.AppendLine();
        builder.AppendLine(body);
    }

    private static string YamlScalar(string value)
        => value.Contains(':') || value.Contains('#') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
}
