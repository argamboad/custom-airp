using Microsoft.Extensions.Logging.Abstractions;
using Airp.Application.Options;
using Airp.Domain.Conversations;
using Airp.Infrastructure.Storage;
using Shouldly;

namespace Airp.Tests;

public class JsonConfigurationServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ourdream-tests", Guid.NewGuid().ToString("n"));

    private readonly string _path;
    private readonly StaticOptionsMonitor<AirpOptions> _options = TestOptions.Default();
    private readonly JsonConfigurationService _service;

    public JsonConfigurationServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "airp.json");
        _service = new JsonConfigurationService(
            _options,
            NullLogger<JsonConfigurationService>.Instance,
            _path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EnsureExistsAsync_WritesDefaultsOnlyOnce()
    {
        (await _service.EnsureExistsAsync()).ShouldBeTrue();
        File.Exists(_path).ShouldBeTrue();

        (await _service.EnsureExistsAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_WritesTheSectionAndSerialisesEnumsByName()
    {
        await _service.SaveAsync(new AirpOptions { Theme = ThemeName.Light, AutoRefreshSeconds = 15 });

        var json = await File.ReadAllTextAsync(_path);

        json.ShouldContain("\"Airp\"");
        json.ShouldContain("\"theme\": \"Light\"");
        json.ShouldContain("\"autoRefreshSeconds\": 15");
    }

    [Fact]
    public async Task SaveAsync_SaysWhatTheEnumKeysWillAccept()
    {
        // A palette or a keyboard dialect is a closed set nobody can guess from the file, and
        // the list comes from the enums so adding one adds it to the file's own documentation.
        await _service.EnsureExistsAsync();

        var json = await File.ReadAllTextAsync(_path);

        json.ShouldContain("// one of: Dark, Light, HighContrast, Monochrome");
        json.ShouldContain("// one of: Standard, Vim");

        var themeAt = json.IndexOf("\"theme\":", StringComparison.Ordinal);
        var commentAt = json.IndexOf("Dark, Light, HighContrast", StringComparison.Ordinal);

        commentAt.ShouldBeLessThan(themeAt, "the comment goes above the key it is about");
    }

    [Fact]
    public async Task SaveAsync_DoesNotChokeOnAFileThatHasCommentsInIt()
    {
        // Without this the annotations written above would come back as an unparseable file on
        // the next save, and the whole of the user's settings would be replaced by defaults.
        await File.WriteAllTextAsync(
            _path,
            """
            {
              "Airp": {
                // one of: Dark, Light, HighContrast, Monochrome
                "theme": "Light",
                "somethingThisVersionHasNeverHeardOf": 7,
              }
            }
            """);

        await _service.SaveAsync(new AirpOptions { AutoRefreshSeconds = 15 });

        var json = await File.ReadAllTextAsync(_path);

        json.ShouldContain("somethingThisVersionHasNeverHeardOf");
        json.ShouldContain("\"autoRefreshSeconds\": 15");
    }

    [Fact]
    public async Task RewriteAsync_AddsWhatIsMissingAndTouchesNothingElse()
    {
        // Nothing else ever looks inside a file that is already there, so a settings file
        // written by an older version keeps its shape through any number of reinstalls — it
        // lives in the application data directory, not beside the binary.
        await File.WriteAllTextAsync(
            _path,
            """
            {
              "Airp": {
                "theme": "Light",
                "exportDirectory": "./exports",
                "model": { "contextBudget": 60000 }
              }
            }
            """);

        var added = await _service.RewriteAsync();

        added.ShouldContain("transcriptWidthPercent");

        var json = await File.ReadAllTextAsync(_path);

        json.ShouldContain("\"theme\": \"Light\"");
        json.ShouldContain("\"contextBudget\": 60000");
        json.ShouldContain("// one of: Dark, Light, HighContrast, Monochrome");

        // The effective options have been post-configured, so writing back what is *in effect*
        // would bake this machine's absolute export directory into a portable file.
        json.ShouldContain("\"exportDirectory\": \"./exports\"");
    }

    [Fact]
    public async Task RewriteAsync_OnAFileThatIsAlreadyCurrentOnlyPutsTheCommentsBack()
    {
        await _service.EnsureExistsAsync();

        (await _service.RewriteAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAsync_PreservesAHandWrittenSection()
    {
        await File.WriteAllTextAsync(
            _path,
            """{ "Airp": { "Model": { "Name": "mine/custom" } } }""");

        await _service.SaveAsync(new AirpOptions { Theme = ThemeName.Light });

        var json = await File.ReadAllTextAsync(_path);

        // A deliberate override must survive a settings write.
        json.ShouldContain("mine/custom");
        json.ShouldContain("\"theme\": \"Light\"");
    }

    [Fact]
    public async Task SaveAsync_PreservesUnrelatedTopLevelSections()
    {
        await File.WriteAllTextAsync(_path, """{ "Logging": { "LogLevel": { "Default": "Debug" } } }""");

        await _service.SaveAsync(new AirpOptions());

        var json = await File.ReadAllTextAsync(_path);

        json.ShouldContain("Logging");
        json.ShouldContain("Debug");
        json.ShouldContain("Airp");
    }

    [Fact]
    public async Task SaveAsync_ReplacesAnUnparseableFileRatherThanFailing()
    {
        await File.WriteAllTextAsync(_path, "{ not json at all");

        await Should.NotThrowAsync(() => _service.SaveAsync(new AirpOptions()));

        (await File.ReadAllTextAsync(_path)).ShouldContain("Airp");
    }

    [Fact]
    public void Current_ReflectsTheBoundOptions()
    {
        _options.CurrentValue.DefaultPersona = "allan";

        _service.Current.DefaultPersona.ShouldBe("allan");
    }

    [Fact]
    public async Task SaveAsync_RejectsNull()
        => await Should.ThrowAsync<ArgumentNullException>(() => _service.SaveAsync(null!));
}
