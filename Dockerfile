FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Forgekeeper.sln ./
COPY src/Forgekeeper.Core/Forgekeeper.Core.csproj src/Forgekeeper.Core/
COPY src/Forgekeeper.Infrastructure/Forgekeeper.Infrastructure.csproj src/Forgekeeper.Infrastructure/
COPY src/Forgekeeper.Api/Forgekeeper.Api.csproj src/Forgekeeper.Api/
RUN dotnet restore

# Copy source and build
COPY src/ src/
RUN dotnet publish src/Forgekeeper.Api/Forgekeeper.Api.csproj -c Release -o /app/publish --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install stl-thumb for thumbnail generation
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget \
    && rm -rf /var/lib/apt/lists/*

# stl-thumb would be installed here — placeholder for actual binary
# RUN wget -qO /usr/local/bin/stl-thumb https://github.com/unlimitedbacon/stl-thumb/releases/download/v0.5.0/stl-thumb-linux-x86_64 \
#     && chmod +x /usr/local/bin/stl-thumb

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "Forgekeeper.Api.dll"]
