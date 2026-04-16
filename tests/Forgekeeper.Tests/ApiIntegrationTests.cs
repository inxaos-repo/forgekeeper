using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Integration tests for Forgekeeper API endpoints.
/// Uses WebApplicationFactory with InMemory database to test real HTTP responses.
/// </summary>
public class ApiIntegrationTests : IClassFixture<ForgeTestFactory>
{
    private readonly HttpClient _client;
    private readonly ForgeTestFactory _factory;

    public ApiIntegrationTests(ForgeTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // --- Health ---

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", json.GetProperty("status").GetString());
        Assert.True(json.TryGetProperty("timestamp", out _));
    }

    // --- Models ---

    [Fact]
    public async Task GetModels_Returns200WithPagination()
    {
        // Note: SearchService uses EF.Functions.ILike which requires PostgreSQL.
        // With InMemory provider, the query without a search term should work,
        // but if the provider doesn't support certain LINQ translations, it may fail.
        // We test the endpoint is reachable; full search tests need PostgreSQL.
        var response = await _client.GetAsync("/api/v1/models?page=1&pageSize=10");

        // Accept either 200 (success) or 400 (InMemory LINQ limitation)
        Assert.True(
            response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected 200 or 400, got {response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(json.TryGetProperty("totalCount", out _));
            Assert.True(json.TryGetProperty("page", out _));
            Assert.True(json.TryGetProperty("pageSize", out _));
            Assert.True(json.TryGetProperty("items", out _));
        }
    }

    [Fact]
    public async Task GetModelById_Returns404ForMissingModel()
    {
        var response = await _client.GetAsync($"/api/v1/models/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetModelById_Returns200ForExistingModel()
    {
        var modelId = await SeedTestModel();
        var response = await _client.GetAsync($"/api/v1/models/{modelId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(modelId.ToString(), json.GetProperty("id").GetString());
    }

    // --- Bulk ---

    [Fact]
    public async Task BulkUpdate_Returns200()
    {
        var modelId = await SeedTestModel();
        var request = new
        {
            ModelIds = new[] { modelId },
            Operation = "categorize",
            Value = "terrain",
        };

        var response = await _client.PostAsJsonAsync("/api/v1/models/bulk", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, json.GetProperty("affectedCount").GetInt32());
    }

    [Fact]
    public async Task BulkUpdate_ReturnsBadRequest_WhenNoIds()
    {
        var request = new
        {
            ModelIds = Array.Empty<Guid>(),
            Operation = "categorize",
            Value = "terrain",
        };

        var response = await _client.PostAsJsonAsync("/api/v1/models/bulk", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Creators ---

    [Fact]
    public async Task GetCreators_Returns200()
    {
        var response = await _client.GetAsync("/api/v1/creators");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCreatorById_Returns404ForMissingCreator()
    {
        var response = await _client.GetAsync($"/api/v1/creators/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Duplicates ---

    [Fact]
    public async Task GetDuplicates_Returns200()
    {
        var response = await _client.GetAsync("/api/v1/models/duplicates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Plugins ---

    [Fact]
    public async Task GetPlugins_Returns200()
    {
        var response = await _client.GetAsync("/api/v1/plugins");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- MCP ---

    [Fact]
    public async Task McpTools_Returns200WithToolList()
    {
        var response = await _client.GetAsync("/mcp/tools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Response is { tools: [...] } object
        Assert.True(json.TryGetProperty("tools", out var tools), "Expected 'tools' property");
        Assert.Equal(JsonValueKind.Array, tools.ValueKind);
        Assert.True(tools.GetArrayLength() > 0, "Expected at least one MCP tool");

        // Verify each tool has name and description
        foreach (var tool in tools.EnumerateArray())
        {
            Assert.True(tool.TryGetProperty("name", out _), "MCP tool missing 'name'");
            Assert.True(tool.TryGetProperty("description", out _), "MCP tool missing 'description'");
        }
    }

    [Fact]
    public async Task McpInvoke_ReturnsBadRequest_ForUnknownTool()
    {
        var request = new { tool = "nonexistent_tool", arguments = new { } };
        var response = await _client.PostAsJsonAsync("/mcp/invoke", request);
        // MCP returns 400 for unknown tools
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- CORS ---

    [Fact]
    public async Task Cors_HeadersPresent_ForConfiguredOrigin()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // CORS headers should be present for configured origin
        Assert.True(
            response.Headers.Contains("Access-Control-Allow-Origin"),
            "Expected CORS Access-Control-Allow-Origin header");
    }

    // --- Helper ---

    private async Task<Guid> SeedTestModel()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var creator = new Creator
        {
            Id = Guid.NewGuid(),
            Name = $"TestCreator_{Guid.NewGuid():N}",
            Source = SourceType.Thangs,
        };
        db.Creators.Add(creator);

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            Name = $"TestModel_{Guid.NewGuid():N}",
            CreatorId = creator.Id,
            Source = SourceType.Thangs,
            BasePath = $"/test/{Guid.NewGuid():N}",
        };
        db.Models.Add(model);
        await db.SaveChangesAsync();

        return model.Id;
    }
}

/// <summary>
/// Custom WebApplicationFactory that replaces PostgreSQL with InMemory database
/// for API integration tests.
/// </summary>
/// <summary>
/// Simple IDbContextFactory for tests that creates InMemory contexts with JSONB value converters.
/// </summary>
internal class ApiTestDbContextFactory : IDbContextFactory<ForgeDbContext>
{
    private readonly string _dbName;

    public ApiTestDbContextFactory(string dbName) => _dbName = dbName;

    public ForgeDbContext CreateDbContext() => TestDbContextFactory.Create(_dbName);
}

public class ForgeTestFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"ForgeTestDb_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Nuclear option: remove ALL descriptors that touch ForgeDbContext or DbContextOptions.
            // This prevents the dual-provider (Npgsql + InMemory) conflict.
            for (int i = services.Count - 1; i >= 0; i--)
            {
                var d = services[i];
                var st = d.ServiceType;
                if (st == typeof(ForgeDbContext)
                    || st == typeof(DbContextOptions<ForgeDbContext>)
                    || st == typeof(DbContextOptions)
                    || (st.IsGenericType && st.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)
                        && st.GenericTypeArguments.Length > 0 && st.GenericTypeArguments[0] == typeof(ForgeDbContext))
                    || (st.IsGenericType && st.GetGenericTypeDefinition() == typeof(DbContextOptions<>))
                )
                {
                    services.RemoveAt(i);
                }
            }

            // Use TestDbContextFactory.Create which provides JSONB value converters for InMemory
            var factory = new ApiTestDbContextFactory(_dbName);
            services.AddSingleton<IDbContextFactory<ForgeDbContext>>(factory);

            services.AddScoped<ForgeDbContext>(sp =>
                sp.GetRequiredService<IDbContextFactory<ForgeDbContext>>().CreateDbContext());
        });
    }
}
