# Plugin Development Guide

Forgekeeper uses a plugin system for scraping external library platforms (MyMiniFactory, Thangs, Cults3D, etc.). Plugins implement the `ILibraryScraper` interface from the `Forgekeeper.PluginSdk` NuGet package.

## ILibraryScraper Interface

Every plugin must implement this interface:

```csharp
public interface ILibraryScraper
{
    // Identity
    string SourceSlug { get; }           // URL-safe slug (e.g., "mmf", "thangs")
    string SourceName { get; }           // Display name (e.g., "MyMiniFactory")
    string Description { get; }          // Brief description
    string Version { get; }              // SemVer version string

    // Configuration
    IReadOnlyList<PluginConfigField> ConfigSchema { get; }
    bool RequiresBrowserAuth { get; }    // True if OAuth/cookie-based auth needed

    // Core methods
    Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct);
    Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(PluginContext context, Stream? uploadedManifest, CancellationToken ct);
    Task<ScrapeResult> ScrapeModelAsync(PluginContext context, ScrapedModel model, CancellationToken ct);
    Task<AuthResult> HandleAuthCallbackAsync(PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct);
    string? GetAdminPageHtml(PluginContext context);  // Optional custom admin UI
}
```

## PluginContext

The host provides a `PluginContext` to every plugin method:

| Field | Type | Description |
|-------|------|-------------|
| `SourceDirectory` | `string` | Root directory for this source's files (e.g., `/mnt/3dprinting/sources/mmf/`) |
| `ModelDirectory` | `string?` | Working directory for the current model (set per-model during `ScrapeModelAsync`) |
| `Config` | `IReadOnlyDictionary<string, string>` | Plugin config values from database, matching `ConfigSchema` keys |
| `HttpClient` | `HttpClient` | Pre-configured HTTP client (host manages timeouts/handlers) |
| `Logger` | `ILogger` | Logger scoped to this plugin |
| `TokenStore` | `ITokenStore` | Persistent encrypted token storage |
| `Progress` | `IProgress<ScrapeProgress>` | Progress reporter for long-running operations |

### ITokenStore

Persistent token storage for auth tokens, scoped per-plugin and encrypted in the database:

```csharp
public interface ITokenStore
{
    Task<string?> GetTokenAsync(string key, CancellationToken ct);
    Task SaveTokenAsync(string key, string value, CancellationToken ct);
    Task DeleteTokenAsync(string key, CancellationToken ct);
}
```

## Supporting Types

### ScrapedModel

Represents a model discovered during manifest fetch:

```csharp
public class ScrapedModel
{
    public required string ExternalId { get; init; }   // Source platform ID
    public required string Name { get; init; }         // Model name
    public string? CreatorName { get; init; }          // Creator display name
    public string? CreatorId { get; init; }            // Creator external ID
    public DateTime? UpdatedAt { get; init; }          // Last updated on source
    public string? Type { get; init; }                 // Category (miniature, terrain, bust)
    public Dictionary<string, object>? Extra { get; init; }  // Source-specific data
}
```

### ScrapeResult

Returned from `ScrapeModelAsync`:

```csharp
public class ScrapeResult
{
    public bool Success { get; init; }
    public string? MetadataFile { get; init; }        // Relative path to metadata.json
    public IReadOnlyList<DownloadedFile> Files { get; init; }
    public string? Error { get; init; }

    public static ScrapeResult Failure(string error);
    public static ScrapeResult Ok(string metadataFile, IReadOnlyList<DownloadedFile> files);
}
```

### DownloadedFile

Represents a file downloaded during scraping:

```csharp
public class DownloadedFile
{
    public required string Filename { get; init; }    // Just the filename
    public required string LocalPath { get; init; }   // Full local path
    public long Size { get; init; }                   // File size in bytes
    public string? Variant { get; init; }             // "supported", "unsupported", "presupported"
    public bool IsArchive { get; init; }              // ZIP/RAR that should be extracted
}
```

### AuthResult

Returned from authentication methods:

```csharp
public class AuthResult
{
    public bool Authenticated { get; init; }
    public string? AuthUrl { get; init; }             // URL for browser-based auth
    public string? Message { get; init; }

    public static AuthResult Success(string? message = null);
    public static AuthResult NeedsBrowser(string authUrl, string? message = null);
    public static AuthResult Failed(string message);
}
```

### PluginConfigField

Defines a configuration field:

```csharp
public class PluginConfigField
{
    public required string Key { get; init; }         // Config key (e.g., "API_KEY")
    public required string Label { get; init; }       // UI label
    public required PluginConfigFieldType Type { get; init; }
    public string? DefaultValue { get; init; }
    public bool Required { get; init; }
    public string? HelpText { get; init; }
}

public enum PluginConfigFieldType
{
    String,     // Plain text input
    Secret,     // Password field — stored encrypted, masked in API responses
    Url,        // URL input
    Number,     // Numeric input
    Bool        // Toggle/checkbox
}
```

### ScrapeProgress

Report progress during long-running operations:

```csharp
public class ScrapeProgress
{
    public required string Status { get; init; }  // "authenticating", "fetching_manifest", "downloading", "complete", "error"
    public int Current { get; init; }             // Current item index (0-based)
    public int Total { get; init; }               // Total items (0 if unknown)
    public string? CurrentItem { get; init; }     // Description of current item
}
```

## Step-by-Step: Creating a New Plugin

### 1. Create from Template

```bash
# Install the template (if not already installed)
dotnet new install ./templates/Forgekeeper.Scraper.Template

# Create a new plugin project
dotnet new forgekeeper-scraper -n Forgekeeper.Scraper.MySource \
  --sourceSlug mysource \
  --sourceName "My Source"
```

Or create manually:

```bash
mkdir plugins/Forgekeeper.Scraper.MySource
cd plugins/Forgekeeper.Scraper.MySource
```

### 2. Create the Project File

`Forgekeeper.Scraper.MySource.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Forgekeeper.PluginSdk/Forgekeeper.PluginSdk.csproj" />
  </ItemGroup>
</Project>
```

### 3. Implement the Plugin

```csharp
using Forgekeeper.PluginSdk;

public class MySourceScraperPlugin : ILibraryScraper
{
    public string SourceSlug => "mysource";
    public string SourceName => "My Source";
    public string Description => "Scrapes 3D model libraries from My Source.";
    public string Version => "1.0.0";
    public bool RequiresBrowserAuth => false;

    public IReadOnlyList<PluginConfigField> ConfigSchema =>
    [
        new PluginConfigField
        {
            Key = "API_KEY",
            Label = "API Key",
            Type = PluginConfigFieldType.Secret,
            Required = true,
            HelpText = "Your My Source API key",
        },
    ];

    public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct)
    {
        var apiKey = context.Config.GetValueOrDefault("API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return AuthResult.Failed("API_KEY not configured");

        // Validate the key
        var response = await context.HttpClient.GetAsync("https://api.mysource.com/me", ct);
        return response.IsSuccessStatusCode
            ? AuthResult.Success()
            : AuthResult.Failed("Invalid API key");
    }

    public async Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(
        PluginContext context, Stream? uploadedManifest, CancellationToken ct)
    {
        var models = new List<ScrapedModel>();

        // Paginate through the user's library
        var page = 1;
        while (true)
        {
            var response = await context.HttpClient.GetAsync(
                $"https://api.mysource.com/library?page={page}", ct);
            // ... parse response, add ScrapedModel entries
            break;
        }

        return models;
    }

    public async Task<ScrapeResult> ScrapeModelAsync(
        PluginContext context, ScrapedModel model, CancellationToken ct)
    {
        try
        {
            var files = new List<DownloadedFile>();

            // Download model files to context.ModelDirectory
            // ...

            // Write metadata.json (REQUIRED)
            var metadata = new
            {
                metadataVersion = 1,
                source = SourceSlug,
                externalId = model.ExternalId,
                name = model.Name,
                creator = new { displayName = model.CreatorName },
                dates = new { downloaded = DateTime.UtcNow },
            };
            var json = System.Text.Json.JsonSerializer.Serialize(metadata,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(
                Path.Combine(context.ModelDirectory!, "metadata.json"), json, ct);

            return ScrapeResult.Ok("metadata.json", files);
        }
        catch (Exception ex)
        {
            return ScrapeResult.Failure(ex.Message);
        }
    }

    public Task<AuthResult> HandleAuthCallbackAsync(
        PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct)
        => Task.FromResult(AuthResult.Failed("Not implemented"));

    public string? GetAdminPageHtml(PluginContext context) => null;
}
```

### 4. Build the Plugin

```bash
dotnet publish plugins/Forgekeeper.Scraper.MySource \
  -c Release -o plugins/output/Forgekeeper.Scraper.MySource
```

### 5. Deploy

Copy the output DLLs to the plugins directory configured in Forgekeeper:
- Docker: mount to `/app/plugins`
- Kubernetes: copy to the plugins PVC

Restart Forgekeeper to load the new plugin. Verify with:

```bash
curl http://localhost:5000/api/v1/plugins
```

## Authentication Patterns

### API Key / Token-Based

The simplest pattern — user provides an API key, stored as a Secret config field:

```csharp
public bool RequiresBrowserAuth => false;

public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct)
{
    var apiKey = context.Config.GetValueOrDefault("API_KEY");
    if (string.IsNullOrEmpty(apiKey))
        return AuthResult.Failed("API_KEY not configured");

    context.HttpClient.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    return AuthResult.Success();
}
```

### OAuth / Browser-Based

For platforms requiring OAuth or browser login:

```csharp
public bool RequiresBrowserAuth => true;

public async Task<AuthResult> AuthenticateAsync(PluginContext context, CancellationToken ct)
{
    // Check for existing token
    var token = await context.TokenStore.GetTokenAsync("access_token", ct);
    if (!string.IsNullOrEmpty(token))
        return AuthResult.Success("Using stored token");

    // Redirect user to OAuth flow
    var clientId = context.Config.GetValueOrDefault("CLIENT_ID");
    var authUrl = $"https://mysource.com/oauth/authorize?client_id={clientId}&redirect_uri=...";
    return AuthResult.NeedsBrowser(authUrl, "Please authorize in your browser");
}

public async Task<AuthResult> HandleAuthCallbackAsync(
    PluginContext context, IDictionary<string, string> callbackParams, CancellationToken ct)
{
    if (callbackParams.TryGetValue("access_token", out var token))
    {
        await context.TokenStore.SaveTokenAsync("access_token", token, ct);
        return AuthResult.Success("Authorization complete");
    }
    return AuthResult.Failed("No access token in callback");
}
```

The host serves an auth callback page at `/auth/{slug}/callback` that extracts OAuth fragment parameters and forwards them to `HandleAuthCallbackAsync`.

### Manifest Upload

For platforms where scraping the library API isn't possible, plugins can accept an uploaded manifest file (e.g., exported library data):

```csharp
public async Task<IReadOnlyList<ScrapedModel>> FetchManifestAsync(
    PluginContext context, Stream? uploadedManifest, CancellationToken ct)
{
    if (uploadedManifest != null)
    {
        // Parse the uploaded file
        using var reader = new StreamReader(uploadedManifest);
        var content = await reader.ReadToEndAsync(ct);
        // ... parse and return models
    }

    // Fallback: fetch from API
    // ...
}
```

Upload via the API:

```bash
curl -X POST http://localhost:5000/api/v1/plugins/mysource/manifest \
  -F "manifest=@library-export.json"
```

## metadata.json Format

Every scraped model **must** produce a `metadata.json` file. See the [Architecture](architecture.md#metadatajson-contract) page for the full specification.

Minimal required fields:

```json
{
  "metadataVersion": 1,
  "source": "mysource",
  "externalId": "12345",
  "name": "Model Name",
  "creator": {
    "displayName": "Creator Name"
  },
  "dates": {
    "downloaded": "2026-04-15T18:00:00Z"
  }
}
```

## Testing Plugins

### Unit Tests

Reference `Forgekeeper.PluginSdk` and mock the `PluginContext`:

```csharp
[Fact]
public async Task AuthenticateAsync_WithValidKey_ReturnsSuccess()
{
    var plugin = new MySourceScraperPlugin();
    var context = new PluginContext
    {
        SourceDirectory = "/tmp/test",
        Config = new Dictionary<string, string> { ["API_KEY"] = "test-key" },
        HttpClient = new HttpClient(new MockHttpHandler()),
        Logger = NullLogger.Instance,
        TokenStore = new InMemoryTokenStore(),
        Progress = new Progress<ScrapeProgress>(),
    };

    var result = await plugin.AuthenticateAsync(context, CancellationToken.None);
    Assert.True(result.Authenticated);
}
```

### Integration Tests

Use the dev Docker Compose environment with a real database:

```bash
docker compose -f docker-compose.dev.yml up -d
dotnet test tests/Forgekeeper.Tests --filter "Category=Plugin"
```

### Verify Plugin Loading

After deploying, check that the plugin appears:

```bash
curl http://localhost:5000/api/v1/plugins | jq
```

Test configuration:

```bash
# Set config
curl -X PUT http://localhost:5000/api/v1/plugins/mysource/config \
  -H "Content-Type: application/json" \
  -d '{"API_KEY": "test-key"}'

# Trigger sync
curl -X POST http://localhost:5000/api/v1/plugins/mysource/sync

# Monitor progress
curl http://localhost:5000/api/v1/plugins/mysource/status
```
