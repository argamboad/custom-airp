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
