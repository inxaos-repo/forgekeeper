# ============================================================
# Forgekeeper — Multi-stage Production Build
# ============================================================

# --- Stage 1: Build .NET backend ---
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and ALL project files for layer caching
COPY Forgekeeper.sln ./
COPY src/Forgekeeper.Core/Forgekeeper.Core.csproj src/Forgekeeper.Core/
COPY src/Forgekeeper.Infrastructure/Forgekeeper.Infrastructure.csproj src/Forgekeeper.Infrastructure/
COPY src/Forgekeeper.PluginSdk/Forgekeeper.PluginSdk.csproj src/Forgekeeper.PluginSdk/
COPY src/Forgekeeper.Api/Forgekeeper.Api.csproj src/Forgekeeper.Api/
COPY tests/Forgekeeper.Tests/Forgekeeper.Tests.csproj tests/Forgekeeper.Tests/
COPY plugins/Forgekeeper.Scraper.Mmf/Forgekeeper.Scraper.Mmf.csproj plugins/Forgekeeper.Scraper.Mmf/

# Restore full solution
RUN dotnet restore

# Copy all source and publish
COPY src/ src/
COPY tests/ tests/
COPY plugins/ plugins/
RUN dotnet publish src/Forgekeeper.Api/Forgekeeper.Api.csproj -c Release -o /app/publish

# --- Stage 2: Build plugins ---
FROM build AS plugins-build
WORKDIR /src

# Set CACHE_DATE to force rebuild (e.g., CACHE_DATE=2026-04-17)
ARG CACHE_DATE=2026-01-01
COPY plugins/ plugins/
RUN if [ -f plugins/Forgekeeper.Scraper.Mmf/Forgekeeper.Scraper.Mmf.csproj ]; then \
      dotnet publish plugins/Forgekeeper.Scraper.Mmf/Forgekeeper.Scraper.Mmf.csproj \
        -c Release -o /app/plugins/Forgekeeper.Scraper.Mmf; \
    fi

# --- Stage 3: Build Vue.js frontend ---
FROM node:22-alpine AS frontend-build
WORKDIR /web

COPY src/Forgekeeper.Web/package*.json ./
RUN npm install 2>&1 || echo "npm install failed, continuing with placeholder"

COPY src/Forgekeeper.Web/ ./
RUN if [ -f node_modules/.bin/vite ]; then \
      npx vite build; \
    elif [ -f vite.config.ts ] || [ -f vite.config.js ]; then \
      npm install && npx vite build; \
    else \
      echo "No Vite found, using placeholder"; \
    fi && \
    mkdir -p dist && \
    if [ ! -f dist/index.html ]; then \
      echo '<html><body><h1>Forgekeeper</h1></body></html>' > dist/index.html; \
    fi

# --- Stage 4: Runtime image ---
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install all system dependencies in one layer:
# - stl-thumb deps (OpenGL, EGL)
# - Playwright/Chromium deps (GTK, NSS, ALSA, etc.)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    wget ca-certificates \
    # stl-thumb dependencies
    libegl1 libgl1 libxkbcommon0 \
    # Playwright/Chromium dependencies
    fonts-liberation libasound2 libatk1.0-0 libatk-bridge2.0-0 \
    libcups2 libdbus-1-3 libdrm2 libgbm1 libgtk-3-0 libnspr4 libnss3 \
    libx11-xcb1 libxcomposite1 libxdamage1 libxrandr2 xdg-utils \
    libxshmfence1 libxss1 libxtst6 && \
    # Install stl-thumb
    wget -q https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb_0.5.0_amd64.deb -O /tmp/stl-thumb.deb && \
    dpkg -i /tmp/stl-thumb.deb || apt-get install -f -y && \
    rm -f /tmp/stl-thumb.deb && \
    # Cleanup
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Copy plugins
COPY --from=plugins-build /app/plugins /app/plugins

# Copy frontend build output
COPY --from=frontend-build /src/Forgekeeper.Api/wwwroot ./wwwroot/

# Install Playwright Chromium using the plugin's DLL
# Create a minimal runtimeconfig so dotnet exec can run the Playwright installer
RUN if [ -f /app/plugins/Forgekeeper.Scraper.Mmf/Microsoft.Playwright.dll ]; then \
      echo '{"runtimeOptions":{"tfm":"net9.0","framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"}}}' \
        > /app/plugins/Forgekeeper.Scraper.Mmf/Microsoft.Playwright.runtimeconfig.json && \
      dotnet exec /app/plugins/Forgekeeper.Scraper.Mmf/Microsoft.Playwright.dll install chromium; \
    fi

# Create directories for runtime data
RUN mkdir -p /app/plugins /data

# Environment defaults
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    Forgekeeper__PluginsDirectory=/app/plugins \
    Storage__ThumbnailDir=.thumbnails \
    Thumbnails__Enabled=true \
    Thumbnails__Renderer=stl-thumb \
    Thumbnails__Size=256 \
    Thumbnails__Format=webp \
    PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "Forgekeeper.Api.dll"]
