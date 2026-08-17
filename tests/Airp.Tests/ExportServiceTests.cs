using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Abstractions;
using Airp.Application.Services;
using Airp.Domain.Conversations;
using Shouldly;

namespace Airp.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ourdream-tests", Guid.NewGuid().ToString("n"));

    private readonly ExportService _service;

    public ExportServiceTests()
        => _service = new ExportService(
            TestOptions.Default(o => o.ExportDirectory = _directory),
            NullLogger<ExportService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static Chat SampleChat => new()
    {
        Id = "prof-1",
        Name = "The Professor",
        Speaker = "Professor",
        LatestMessage = "I settle into my office…",
        LastMessageAtUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Render_Markdown_ForAChat_EmitsFrontMatterAndSections()
    {
        var markdown = _service.Render(SampleChat, ExportFormat.Markdown);

        markdown.ShouldStartWith("---");
        markdown.ShouldContain("name: The Professor");
        markdown.ShouldContain("with: Professor");
        markdown.ShouldContain("# The Professor");
            }

    [Fact]
    public void Render_Markdown_EscapesScalarsThatWouldBreakTheFrontMatter()
    {
        var chat = SampleChat with { Name = "Doctor: Who" };

        _service.Render(chat, ExportFormat.Markdown).ShouldContain("name: \"Doctor: Who\"");
    }

    [Fact]
    public void Render_Json_IsValidAndCarriesTheChatsFields()
    {
        var json = _service.Render(SampleChat, ExportFormat.Json);

        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("Name").GetString().ShouldBe("The Professor");
        document.RootElement.GetProperty("Speaker").GetString().ShouldBe("Professor");
    }

    [Fact]
    public void Render_RejectsAnUnknownFormat()
        => Should.Throw<ArgumentOutOfRangeException>(() => _service.Render(SampleChat, (ExportFormat)99));

    [Fact]
    public void Render_RejectsNull()
        => Should.Throw<ArgumentNullException>(() => _service.Render(null!, ExportFormat.Json));

    [Fact]
    public async Task ExportAsync_WritesToTheConfiguredDirectoryWithADerivedName()
    {
        var path = await _service.ExportAsync(SampleChat, ExportFormat.Markdown);

        File.Exists(path).ShouldBeTrue();
        Path.GetFileName(path).ShouldStartWith("the-professor-");
        Path.GetExtension(path).ShouldBe(".md");
        (await File.ReadAllTextAsync(path)).ShouldContain("# The Professor");
    }

    [Fact]
    public async Task ExportAsync_HonoursAnExplicitPathAndCreatesMissingDirectories()
    {
        var path = Path.Combine(_directory, "nested", "deeper", "out.json");

        var written = await _service.ExportAsync(SampleChat, ExportFormat.Json, path);

        written.ShouldBe(Path.GetFullPath(path));
        File.Exists(path).ShouldBeTrue();
    }

    [Theory]
    [InlineData("The Professor", "the-professor")]
    [InlineData("  Story   Ideas!! ", "story-ideas")]
    [InlineData("!!!", "export")]
    [InlineData("", "export")]
    public void Slug_ProducesFileSafeNames(string input, string expected)
        => ExportService.Slug(input).ShouldBe(expected);

    [Fact]
    public void Slug_TruncatesVeryLongNames()
        => ExportService.Slug(new string('a', 200)).Length.ShouldBeLessThanOrEqualTo(60);

    [Theory]
    [InlineData(ExportFormat.Markdown, ".md")]
    [InlineData(ExportFormat.Json, ".json")]
    [InlineData(ExportFormat.PlainText, ".txt")]
    public void BuildDefaultPath_UsesTheRightExtension(ExportFormat format, string extension)
        => Path.GetExtension(_service.BuildDefaultPath(SampleChat, format)).ShouldBe(extension);
}
