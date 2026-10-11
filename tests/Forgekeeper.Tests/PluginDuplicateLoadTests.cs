using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Scraper.Mmf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Regression tests for #29 (MMF plugin loaded twice from /app/plugins and /data/plugins)
/// and #32 (manifest.json not shipped with the plugin build output).
/// </summary>
public class PluginDuplicateLoadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fk-dup-{Guid.NewGuid():N}");
    private string Builtin => Path.Combine(_root, "app-plugins");
    private string Installed => Path.Combine(_root, "data-plugins");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private static string RepoManifest()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Forgekeeper.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "plugins", "Forgekeeper.Scraper.Mmf", "manifest.json");
    }

    private static void CopyPlugin(string target, bool withManifest)
    {
        Directory.CreateDirectory(target);
        var dll = typeof(MmfScraperPlugin).Assembly.Location;
        File.Copy(dll, Path.Combine(target, Path.GetFileName(dll)));
        if (withManifest)
            File.Copy(RepoManifest(), Path.Combine(target, "manifest.json"));
    }

    private (PluginHostService svc, Mock<ILogger<PluginHostService>> log) Create(bool preferBundled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Forgekeeper:BuiltinPluginsDirectory"] = Builtin,
            ["Forgekeeper:PluginsDirectory"] = Installed,
            ["Forgekeeper:SourcesDirectory"] = Path.Combine(_root, "sources"),
            ["Plugins:PreferBundled"] = preferBundled ? "true" : "false",
            ["Plugins:WarnOnVersionDrift"] = "false",
        }).Build();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ForgeDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddLogging();
        var log = new Mock<ILogger<PluginHostService>>();
        var svc = new PluginHostService(services.BuildServiceProvider(), log.Object, config,
            new ManifestValidationService(Mock.Of<ILogger<ManifestValidationService>>()),
            new SdkCompatibilityChecker(Mock.Of<ILogger<SdkCompatibilityChecker>>()));
        return (svc, log);
    }

    private static int CountLogs(Mock<ILogger<PluginHostService>> log, string fragment) =>
        log.Invocations.Count(i => i.Method.Name == "Log" &&
            i.Arguments.Count > 2 && i.Arguments[2]?.ToString()?.Contains(fragment) == true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mmf_InBothDirsWithDifferentDirNames_LoadsExactlyOnce(bool preferBundled)
    {
        // Production layout: bundled copy has a manifest, stale /data copy has none.
        CopyPlugin(Path.Combine(Builtin, "Forgekeeper.Scraper.Mmf"), withManifest: true);
        CopyPlugin(Path.Combine(Installed, "mmf"), withManifest: false);

        var (svc, log) = Create(preferBundled);
        await svc.DiscoverPluginsAsync(CancellationToken.None);

        Assert.Single(svc.Plugins);
        var p = svc.Plugins["mmf"];
        var expectedDir = preferBundled ? Builtin : Installed;
        Assert.StartsWith(expectedDir, p.SourceDirectory);
        Assert.Equal(1, CountLogs(log, "Loaded plugin: "));
        Assert.Equal(1, CountLogs(log, "already loaded from"));
        if (preferBundled)
        {
            Assert.NotNull(p.Manifest);
            Assert.Equal(0, CountLogs(log, "No manifest.json found"));
        }
    }

    [Fact]
    public async Task Mmf_BundledWithManifest_LoadsWithManifestAndNoLegacyWarning()
    {
        CopyPlugin(Path.Combine(Builtin, "Forgekeeper.Scraper.Mmf"), withManifest: true);
        var (svc, log) = Create(preferBundled: true);
        await svc.DiscoverPluginsAsync(CancellationToken.None);

        Assert.Single(svc.Plugins);
        Assert.Equal("mmf", svc.Plugins["mmf"].Manifest!.Slug);
        Assert.Equal(0, CountLogs(log, "No manifest.json found"));
    }

    [Fact]
    public void MmfBuildOutput_ContainsManifestJson()
    {
        // #32: csproj copies manifest.json to output; referencing test project receives it too.
        var dllDir = Path.GetDirectoryName(typeof(MmfScraperPlugin).Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(dllDir, "manifest.json")),
            $"manifest.json missing next to {dllDir}");
    }
}
