# MySourceScraper — Forgekeeper Plugin

A Forgekeeper library scraper plugin for **My Source**.

## Getting Started

1. Install dependencies:
   ```bash
   dotnet restore
   ```

2. Implement the scraping logic in `MySourceScraperPlugin.cs`:
   - `AuthenticateAsync` — Validate API keys or perform OAuth flow
   - `FetchManifestAsync` — Fetch the user's model library
   - `ScrapeModelAsync` — Download files for a single model

3. Build:
   ```bash
   dotnet build -c Release
   ```

4. Deploy: Copy the output DLL to Forgekeeper's `plugins/MySourceScraper/` directory.

## Configuration

This plugin requires the following configuration (set via Forgekeeper's plugin admin UI):

| Key | Description | Required |
|-----|-------------|----------|
| `API_KEY` | Your My Source API key | Yes |
| `USERNAME` | Your My Source username | Yes |

## Plugin Lifecycle

1. **Discovery** — Forgekeeper finds this DLL in the plugins directory
2. **Authentication** — `AuthenticateAsync()` validates credentials
3. **Manifest** — `FetchManifestAsync()` lists available models
4. **Scrape** — `ScrapeModelAsync()` downloads each model's files and writes `metadata.json`

## metadata.json

Each scraped model MUST produce a `metadata.json` file. This is the integration contract between plugins and Forgekeeper. See the Plugin SDK documentation for the full schema.

## License

MIT
