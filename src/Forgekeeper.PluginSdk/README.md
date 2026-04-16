# Forgekeeper.PluginSdk

Plugin SDK for building **Forgekeeper** library scraper plugins.

Forgekeeper is a self-hosted 3D print file manager — think "Plex for STL files." This SDK provides the interfaces and types needed to create custom source integrations for platforms like MyMiniFactory, Thangs, Cults3D, Patreon, and more.

## Quick Start

1. Install the SDK:
   ```bash
   dotnet add package Forgekeeper.PluginSdk
   ```

2. Implement `ILibraryScraper`:
   ```csharp
   public class MySourcePlugin : ILibraryScraper
   {
       public string SourceSlug => "mysource";
       public string SourceName => "My Source";
       // ... implement all interface members
   }
   ```

3. Build as a class library and drop the DLL into Forgekeeper's `plugins/` directory.

## Key Interfaces

- **`ILibraryScraper`** — Core plugin interface. Implement this to scrape a source.
- **`ITokenStore`** — Persistent token storage provided by the host.
- **`PluginContext`** — Runtime context with config, HTTP client, logger, and token store.
- **`ScrapedModel`** — Model summary returned from manifest fetching.
- **`ScrapeResult`** — Result of scraping a single model.

## Plugin Lifecycle

1. **Discovery** — Forgekeeper scans the `plugins/` directory for DLLs implementing `ILibraryScraper`.
2. **Authentication** — `AuthenticateAsync()` is called before any scraping.
3. **Manifest Fetch** — `FetchManifestAsync()` returns the user's library.
4. **Model Scrape** — `ScrapeModelAsync()` downloads files and produces `metadata.json`.

## Template

Use the `dotnet new` template for quick scaffolding:
```bash
dotnet new forgekeeper-scraper -n MySource
```

## License

MIT
