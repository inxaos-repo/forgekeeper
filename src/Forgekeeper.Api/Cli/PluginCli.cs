using System.Text.Json;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Forgekeeper.Api.Cli;

/// <summary>
/// CLI handler for the `forgekeeper plugin` command group.
/// Intercepts the application before the web server starts and runs as a one-shot command.
/// </summary>
public static class PluginCli
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            PrintHelp();
            return 0;
        }

        return args[0] switch
        {
            "list" => await RunListAsync(args[1..], configuration),
            "info" => await RunInfoAsync(args[1..], configuration),
            "install" => await RunInstallAsync(args[1..], configuration),
            "update" => await RunUpdateAsync(args[1..], configuration),
            "remove" or "rm" or "uninstall" => await RunRemoveAsync(args[1..], configuration),
            "reload" => RunReload(),
            _ => UnknownCommand(args[0]),
        };
    }

    // ─── list ────────────────────────────────────────────────────────────────

    private static async Task<int> RunListAsync(string[] args, IConfiguration configuration)
    {
        bool json = args.Contains("--json");

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: forgekeeper plugin list [--json]");
            Console.WriteLine();
            Console.WriteLine("List all installed plugins and their status.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --json    Output as JSON array");
            return 0;
        }

        var pluginsDir = GetPluginsDir(configuration);
        var plugins = ScanPlugins(pluginsDir, configuration);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(plugins, JsonOpts));
            return 0;
        }

        if (plugins.Count == 0)
        {
            Console.WriteLine($"No plugins found in {pluginsDir}");
            return 0;
        }

        // Table: SLUG  VERSION  AUTHOR  SDK  STATUS
        const int slugW = 20, verW = 10, authW = 20, sdkW = 8;
        Console.WriteLine(
            $"{"SLUG",-20}  {"VERSION",-10}  {"AUTHOR",-20}  {"SDK",-8}  STATUS");
        Console.WriteLine(new string('-', slugW + verW + authW + sdkW + 20));

        foreach (var p in plugins)
        {
            var status = p.ManifestValid == false ? "⚠️  Invalid manifest"
                : p.SdkCompatLevel == "MajorMismatch" ? "❌ SDK incompatible"
                : p.SdkCompatLevel == "MinorMismatch" ? "⚠️  SDK warning"
                : "✅ Compatible";

            Console.WriteLine(
                $"{Truncate(p.Slug, slugW),-20}  "
                + $"{Truncate(p.Version ?? "-", verW),-10}  "
                + $"{Truncate(p.Author ?? "-", authW),-20}  "
                + $"{Truncate(p.SdkVersion ?? "-", sdkW),-8}  "
                + status);
        }

        Console.WriteLine();
        Console.WriteLine($"{plugins.Count} plugin(s) installed in {pluginsDir}");

        return await Task.FromResult(0);
    }

    // ─── info ────────────────────────────────────────────────────────────────

    private static async Task<int> RunInfoAsync(string[] args, IConfiguration configuration)
    {
        bool json = args.Contains("--json");
        var slug = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (args.Contains("--help") || args.Contains("-h") || string.IsNullOrEmpty(slug))
        {
            Console.WriteLine("Usage: forgekeeper plugin info <slug> [--json]");
            Console.WriteLine();
            Console.WriteLine("Show detailed information about an installed plugin.");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  slug    Plugin slug (e.g., mmf)");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --json    Output as JSON");
            return string.IsNullOrEmpty(slug) ? 1 : 0;
        }

        var pluginsDir = GetPluginsDir(configuration);
        var pluginDir = Path.Combine(pluginsDir, slug);

        if (!Directory.Exists(pluginDir))
        {
            Console.Error.WriteLine($"Plugin '{slug}' not found in {pluginsDir}");
            return 1;
        }

        var validator = BuildMinimalValidator();
        var manifest = validator.LoadManifest(pluginDir);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                slug,
                pluginDir,
                manifest,
                manifestFound = manifest is not null,
            }, JsonOpts));
            return 0;
        }

        if (manifest is null)
        {
            Console.WriteLine($"Plugin: {slug}");
            Console.WriteLine($"Directory: {pluginDir}");
            Console.WriteLine("⚠️  No manifest.json found");
            Console.WriteLine();
            Console.WriteLine("DLL files:");
            foreach (var dll in Directory.GetFiles(pluginDir, "*.dll"))
                Console.WriteLine($"  {Path.GetFileName(dll)}");
            return 0;
        }

        Console.WriteLine($"Plugin: {manifest.Name} ({manifest.Slug})");
        Console.WriteLine($"Version: {manifest.Version}");
        if (!string.IsNullOrEmpty(manifest.Author))
            Console.WriteLine($"Author: {manifest.Author}{(manifest.Email is not null ? $" <{manifest.Email}>" : "")}");
        if (!string.IsNullOrEmpty(manifest.Description))
            Console.WriteLine($"Description: {manifest.Description}");
        if (!string.IsNullOrEmpty(manifest.Homepage))
            Console.WriteLine($"Homepage: {manifest.Homepage}");
        if (!string.IsNullOrEmpty(manifest.SourceUrl))
            Console.WriteLine($"Source: {manifest.SourceUrl}");
        if (!string.IsNullOrEmpty(manifest.License))
            Console.WriteLine($"License: {manifest.License}");
        if (manifest.Tags.Count > 0)
            Console.WriteLine($"Tags: {string.Join(", ", manifest.Tags)}");
        Console.WriteLine();
        Console.WriteLine($"SDK Version: {manifest.SdkVersion}");
        if (!string.IsNullOrEmpty(manifest.MinSdkVersion))
            Console.WriteLine($"Min SDK: {manifest.MinSdkVersion}");
        if (!string.IsNullOrEmpty(manifest.MaxSdkVersion))
            Console.WriteLine($"Max SDK: {manifest.MaxSdkVersion}");
        Console.WriteLine($"Entry Assembly: {manifest.EntryAssembly}");
        Console.WriteLine();
        Console.WriteLine($"Directory: {pluginDir}");

        // Run SDK compat check
        var sdkChecker = BuildMinimalSdkChecker();
        var compat = sdkChecker.CheckCompatibility(manifest);
        Console.WriteLine($"SDK Compatibility: {compat.Level}" + (compat.Reason is not null ? $" — {compat.Reason}" : ""));

        return await Task.FromResult(0);
    }

    // ─── install ─────────────────────────────────────────────────────────────

    private static async Task<int> RunInstallAsync(string[] args, IConfiguration configuration)
    {
        bool json = args.Contains("--json");
        var positional = args.Where(a => !a.StartsWith('-')).ToArray();
        var source = positional.ElementAtOrDefault(0);
        var version = positional.ElementAtOrDefault(1);

        if (args.Contains("--help") || args.Contains("-h") || string.IsNullOrEmpty(source))
        {
            Console.WriteLine("Usage: forgekeeper plugin install <source> [version] [--json]");
            Console.WriteLine();
            Console.WriteLine("Install a plugin from a GitHub repository.");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  source     GitHub URL or owner/repo (e.g., https://github.com/org/Forgekeeper.Plugin.Mmf)");
            Console.WriteLine("  version    Specific version to install (optional, default: latest)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  forgekeeper plugin install https://github.com/org/Forgekeeper.Plugin.Mmf");
            Console.WriteLine("  forgekeeper plugin install org/Forgekeeper.Plugin.Mmf 1.0.0");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --json    Output result as JSON");
            return string.IsNullOrEmpty(source) ? 1 : 0;
        }

        await using var sp = BuildServiceProvider(configuration);
        var installService = sp.GetRequiredService<IPluginInstallService>();

        if (!json) Console.WriteLine($"Installing plugin from '{source}' version '{version ?? "latest"}'...");

        var result = await installService.InstallAsync(source, version);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✅ {result.Message}");
            Console.ResetColor();
            Console.WriteLine($"   Slug: {result.Slug}");
            Console.WriteLine($"   Version: {result.InstalledVersion}");
            if (result.PreviousVersion is not null)
                Console.WriteLine($"   Previous: {result.PreviousVersion}");
            Console.WriteLine();
            Console.WriteLine("Restart Forgekeeper or use POST /api/v1/plugins/reload to activate the plugin.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"❌ Installation failed: {result.Message}");
            Console.ResetColor();
        }

        return result.Success ? 0 : 1;
    }

    // ─── update ──────────────────────────────────────────────────────────────

    private static async Task<int> RunUpdateAsync(string[] args, IConfiguration configuration)
    {
        bool json = args.Contains("--json");
        bool checkOnly = args.Contains("--check");
        var slug = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: forgekeeper plugin update [<slug>|--check] [--json]");
            Console.WriteLine();
            Console.WriteLine("Update an installed plugin to the latest version.");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  slug     Plugin slug to update (omit to update all)");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --check   Check for updates without installing");
            Console.WriteLine("  --json    Output result as JSON");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  forgekeeper plugin update mmf");
            Console.WriteLine("  forgekeeper plugin update --check");
            return 0;
        }

        var pluginsDir = GetPluginsDir(configuration);

        // --check: compare installed versions against resolved GitHub releases
        if (checkOnly)
        {
            return await RunUpdateCheckAsync(pluginsDir, configuration, json);
        }

        if (string.IsNullOrEmpty(slug))
        {
            Console.Error.WriteLine("Specify a plugin slug to update, or use --check to see available updates.");
            return 1;
        }

        await using var sp = BuildServiceProvider(configuration);
        var installService = sp.GetRequiredService<IPluginInstallService>();

        if (!json) Console.WriteLine($"Updating plugin '{slug}'...");

        var result = await installService.UpdateAsync(slug);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOpts));
            return result.Success ? 0 : 1;
        }

        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✅ {result.Message}");
            Console.ResetColor();
            if (result.PreviousVersion is not null && result.InstalledVersion is not null)
                Console.WriteLine($"   {result.PreviousVersion} → {result.InstalledVersion}");
            Console.WriteLine("Restart Forgekeeper or use POST /api/v1/plugins/reload to apply the update.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"❌ Update failed: {result.Message}");
            Console.ResetColor();
        }

        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunUpdateCheckAsync(string pluginsDir, IConfiguration configuration, bool json)
    {
        var plugins = ScanPlugins(pluginsDir, configuration);
        var updateablePlugins = plugins
            .Where(p => !string.IsNullOrEmpty(p.SourceUrl) && p.SourceUrl!.Contains("github.com"))
            .ToList();

        if (!json && updateablePlugins.Count == 0)
        {
            Console.WriteLine("No plugins with GitHub source URLs found. Cannot check for updates.");
            return 0;
        }

        await using var sp = BuildServiceProvider(configuration);
        var resolver = sp.GetRequiredService<IGitHubReleaseResolver>();

        var updates = new List<object>();

        foreach (var plugin in updateablePlugins)
        {
            if (!json) Console.Write($"Checking {plugin.Slug}... ");
            try
            {
                var release = await resolver.ResolveAsync(plugin.SourceUrl!);
                if (release is null)
                {
                    if (!json) Console.WriteLine("(could not resolve)");
                    continue;
                }

                var hasUpdate = !string.Equals(release.Version, plugin.Version, StringComparison.OrdinalIgnoreCase);
                updates.Add(new { slug = plugin.Slug, current = plugin.Version, available = release.Version, hasUpdate });

                if (!json)
                {
                    if (hasUpdate)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"{plugin.Version} → {release.Version} available");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"v{plugin.Version} (up to date)");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!json) Console.WriteLine($"(error: {ex.Message})");
            }
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(updates, JsonOpts));
        }
        else
        {
            var available = updates.OfType<dynamic>().Count(u => (bool)((dynamic)u).hasUpdate);
            Console.WriteLine();
            Console.WriteLine(available > 0
                ? $"{available} update(s) available. Run 'forgekeeper plugin update <slug>' to install."
                : "All plugins are up to date.");
        }

        return 0;
    }

    // ─── remove ──────────────────────────────────────────────────────────────

    private static async Task<int> RunRemoveAsync(string[] args, IConfiguration configuration)
    {
        bool json = args.Contains("--json");
        bool yes = args.Contains("--yes") || args.Contains("-y");
        var slug = args.FirstOrDefault(a => !a.StartsWith('-'));

        if (args.Contains("--help") || args.Contains("-h") || string.IsNullOrEmpty(slug))
        {
            Console.WriteLine("Usage: forgekeeper plugin remove <slug> [--yes] [--json]");
            Console.WriteLine();
            Console.WriteLine("Uninstall an installed plugin.");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  slug     Plugin slug to remove");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --yes, -y   Skip confirmation prompt");
            Console.WriteLine("  --json      Output result as JSON");
            return string.IsNullOrEmpty(slug) ? 1 : 0;
        }

        var pluginsDir = GetPluginsDir(configuration);
        var pluginDir = Path.Combine(pluginsDir, slug);

        if (!Directory.Exists(pluginDir))
        {
            var msg = $"Plugin '{slug}' not found in {pluginsDir}";
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, message = msg }, JsonOpts));
            else
                Console.Error.WriteLine($"❌ {msg}");
            return 1;
        }

        // Read manifest to show version in prompt
        var validator = BuildMinimalValidator();
        var manifest = validator.LoadManifest(pluginDir);
        var version = manifest?.Version ?? "unknown version";

        if (!yes && !json)
        {
            Console.Write($"Remove plugin '{slug}' v{version} from {pluginDir}? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer != "y" && answer != "yes")
            {
                Console.WriteLine("Aborted.");
                return 0;
            }
        }

        await using var sp = BuildServiceProvider(configuration);
        var installService = sp.GetRequiredService<IPluginInstallService>();

        var success = await installService.RemoveAsync(slug);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success,
                slug,
                message = success ? $"Plugin '{slug}' removed" : $"Failed to remove plugin '{slug}'"
            }, JsonOpts));
            return success ? 0 : 1;
        }

        if (success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"✅ Plugin '{slug}' v{version} removed");
            Console.ResetColor();
            Console.WriteLine("Restart Forgekeeper or use POST /api/v1/plugins/reload to apply changes.");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"❌ Failed to remove plugin '{slug}'");
            Console.ResetColor();
        }

        return success ? 0 : 1;
    }

    // ─── reload ──────────────────────────────────────────────────────────────

    private static int RunReload()
    {
        Console.WriteLine("Hot-reload is only available via the API while the server is running.");
        Console.WriteLine();
        Console.WriteLine("To reload plugins:");
        Console.WriteLine("  • All plugins:     POST /api/v1/plugins/reload");
        Console.WriteLine("  • Single plugin:   POST /api/v1/plugins/{slug}/reload");
        Console.WriteLine();
        Console.WriteLine("Note: hot_reload_enabled must be set to true in config.");
        return 0;
    }

    // ─── Help ─────────────────────────────────────────────────────────────────

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: forgekeeper plugin <subcommand> [options]");
        Console.WriteLine();
        Console.WriteLine("Manage Forgekeeper plugins.");
        Console.WriteLine();
        Console.WriteLine("Subcommands:");
        Console.WriteLine("  list              List all installed plugins");
        Console.WriteLine("  info <slug>       Show details for a plugin");
        Console.WriteLine("  install <source>  Install a plugin from GitHub");
        Console.WriteLine("  update <slug>     Update a plugin to the latest version");
        Console.WriteLine("  update --check    Check for available updates (no install)");
        Console.WriteLine("  remove <slug>     Uninstall a plugin");
        Console.WriteLine("  reload            Show how to trigger hot-reload via API");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  --help, -h        Show this help");
        Console.WriteLine("  --json            Machine-readable JSON output");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  forgekeeper plugin list");
        Console.WriteLine("  forgekeeper plugin install https://github.com/org/Forgekeeper.Plugin.Mmf");
        Console.WriteLine("  forgekeeper plugin update mmf");
        Console.WriteLine("  forgekeeper plugin remove mmf --yes");
    }

    private static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"Unknown subcommand: '{cmd}'");
        Console.Error.WriteLine("Run 'forgekeeper plugin --help' for usage.");
        return 1;
    }

    // ─── Scanning ─────────────────────────────────────────────────────────────

    private static List<PluginScanResult> ScanPlugins(string pluginsDir, IConfiguration configuration)
    {
        var results = new List<PluginScanResult>();
        if (!Directory.Exists(pluginsDir)) return results;

        var validator = BuildMinimalValidator();
        var sdkChecker = BuildMinimalSdkChecker();

        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var slug = Path.GetFileName(dir);
            var manifest = validator.LoadManifest(dir);

            SdkCompatResult? compat = null;
            ManifestValidationResult? validation = null;
            if (manifest is not null)
            {
                validation = validator.Validate(manifest);
                compat = sdkChecker.CheckCompatibility(manifest);
            }

            results.Add(new PluginScanResult
            {
                Slug = manifest?.Slug ?? slug,
                Name = manifest?.Name,
                Version = manifest?.Version,
                Author = manifest?.Author,
                Description = manifest?.Description,
                SdkVersion = manifest?.SdkVersion,
                SourceUrl = manifest?.SourceUrl ?? manifest?.Homepage,
                ManifestValid = manifest is null ? null : validation?.IsValid,
                SdkCompatLevel = compat?.Level.ToString(),
                PluginDir = dir,
            });
        }

        return results;
    }

    // ─── Service Provider ────────────────────────────────────────────────────

    private static ServiceProvider BuildServiceProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // Logging — suppress debug noise in CLI context
        services.AddLogging(b => b
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));

        services.AddHttpClient();
        services.AddSingleton(configuration);

        // Plugin system services
        services.AddSingleton<ManifestValidationService>();
        services.AddSingleton<SdkCompatibilityChecker>();
        services.AddSingleton<IGitHubReleaseResolver, GitHubReleaseResolver>();

        // DB — optional (needed for PluginConfig cleanup on remove)
        var connectionString = configuration.GetConnectionString("ForgeDb");
        if (!string.IsNullOrEmpty(connectionString))
        {
            try
            {
                var npgsqlDataSource = new Npgsql.NpgsqlDataSourceBuilder(connectionString)
                    .EnableDynamicJson()
                    .Build();
                services.AddDbContextFactory<ForgeDbContext>(options =>
                    options.UseNpgsql(npgsqlDataSource)
                        .UseSnakeCaseNamingConvention()
                        .ConfigureWarnings(w => w.Ignore(
                            Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
            }
            catch (Exception)
            {
                // DB config error — PluginInstallService will handle missing factory gracefully
            }
        }

        services.AddScoped<IPluginInstallService, PluginInstallService>();

        return services.BuildServiceProvider();
    }

    private static ManifestValidationService BuildMinimalValidator()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None));
        return new ManifestValidationService(loggerFactory.CreateLogger<ManifestValidationService>());
    }

    private static SdkCompatibilityChecker BuildMinimalSdkChecker()
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.None));
        return new SdkCompatibilityChecker(loggerFactory.CreateLogger<SdkCompatibilityChecker>());
    }

    private static string GetPluginsDir(IConfiguration configuration) =>
        configuration["Forgekeeper:PluginsDirectory"] ?? "/data/plugins";

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..(maxLen - 1)] + "…";

    /// <summary>Lightweight scan result for CLI list/info.</summary>
    private sealed class PluginScanResult
    {
        public string Slug { get; set; } = "";
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Author { get; set; }
        public string? Description { get; set; }
        public string? SdkVersion { get; set; }
        public string? SourceUrl { get; set; }
        public bool? ManifestValid { get; set; }
        public string? SdkCompatLevel { get; set; }
        public string? PluginDir { get; set; }
    }
}
