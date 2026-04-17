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

COPY plugins/ plugins/
RUN if [ -f plugins/Forgekeeper.Scraper.Mmf/Forgekeeper.Scraper.Mmf.csproj ]; then \
      dotnet publish plugins/Forgekeeper.Scraper.Mmf/Forgekeeper.Scraper.Mmf.csproj \
        -c Release -o /app/plugins/Forgekeeper.Scraper.Mmf; \
    fi

# --- Stage 3: Build Vue.js frontend ---
FROM node:22-alpine AS frontend-build
WORKDIR /web

COPY src/Forgekeeper.Web/package*.json ./
# Use npm install (not ci) since we may not have a lock file
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

# Install stl-thumb for thumbnail generation
# Using placeholder — replace with actual binary URL for your architecture
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget \
    libglib2.0-0 \
    libgl1-mesa-glx \
    libxrender1 \
    && rm -rf /var/lib/apt/lists/*

# stl-thumb installation (uncomment and set correct URL for your platform)
# AMD64:
# RUN wget -qO /tmp/stl-thumb.deb https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb_0.5.0_amd64.deb \
#     && dpkg -i /tmp/stl-thumb.deb || apt-get install -f -y \
#     && rm /tmp/stl-thumb.deb
# ARM64:
# RUN wget -qO /tmp/stl-thumb.deb https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb_0.5.0_arm64.deb \
#     && dpkg -i /tmp/stl-thumb.deb || apt-get install -f -y \
#     && rm /tmp/stl-thumb.deb

# Copy published application
COPY --from=build /app/publish .

# Copy plugins
COPY --from=plugins-build /app/plugins /app/plugins

# Copy frontend build output to wwwroot for static file serving
# vite.config.js outputs to ../../src/Forgekeeper.Api/wwwroot relative to /web
COPY --from=frontend-build /src/Forgekeeper.Api/wwwroot ./wwwroot/

# Create directories for runtime data
RUN mkdir -p /app/plugins /data

# Environment defaults
ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    Forgekeeper__PluginsDirectory=/app/plugins \
    Storage__ThumbnailDir=.thumbnails \
    Thumbnails__Enabled=true \
    Thumbnails__Renderer=stl-thumb \
    Thumbnails__Size=256x256 \
    Thumbnails__Format=webp

EXPOSE 5000

# Health check
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD wget -qO- http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "Forgekeeper.Api.dll"]
