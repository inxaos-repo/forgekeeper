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

# Install stl-thumb + system dependencies + Chromium for Playwright
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    wget ca-certificates curl \
    libegl1 libgl1 libxkbcommon0 \
    # Chromium and Playwright browser deps
    chromium \
    libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 \
    libcups2 libdrm2 libxcomposite1 libxdamage1 \
    libxfixes3 libxrandr2 libgbm1 libpango-1.0-0 \
    libcairo2 libx11-xcb1 libxcb-dri3-0 && \
    # Install Node.js (Playwright .NET SDK needs node to drive the browser)
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    # Install stl-thumb for 3D model thumbnail generation
    wget -q https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb_0.5.0_amd64.deb -O /tmp/stl-thumb.deb && \
    dpkg -i /tmp/stl-thumb.deb || apt-get install -f -y && \
    rm -f /tmp/stl-thumb.deb && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

# Copy published application
COPY --from=build /app/publish .

# Copy plugins
COPY --from=plugins-build /app/plugins /app/plugins

# Copy frontend build output
COPY --from=frontend-build /src/Forgekeeper.Api/wwwroot ./wwwroot/

# Create directories for runtime data
RUN mkdir -p /app/plugins /data

# Install Playwright driver (node-based protocol driver, uses system Chromium)
RUN npx playwright install --with-deps chromium 2>/dev/null || true

# Environment defaults — Playwright uses system Chromium
ENV PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/chromium

ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    Forgekeeper__PluginsDirectory=/app/plugins \
    Storage__ThumbnailDir=.thumbnails \
    Thumbnails__Enabled=true \
    Thumbnails__Renderer=stl-thumb \
    Thumbnails__Size=256 \
    Thumbnails__Format=webp

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "Forgekeeper.Api.dll"]

# Cache bust: 2026-04-19
ARG CACHE_BUST=20260419
