using System.Text.Json;
using System.Text.Json.Serialization;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Mcp;

/// <summary>
/// MCP (Model Context Protocol) server for Forgekeeper.
/// Exposes read, write, and analysis tools that map to existing service methods.
/// Provides GET /mcp/tools and POST /mcp/invoke endpoints following MCP conventions.
/// The actual MCP SSE/stdio transport can be wired later when .NET MCP SDK is stable.
/// </summary>
public class ForgekeeperMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Returns all available MCP tool definitions.
    /// </summary>
    public static McpToolsListResponse GetToolDefinitions() => new()
    {
        Tools = [.. ReadTools(), .. WriteTools(), .. AnalysisTools()]
    };

    /// <summary>
    /// Invoke a tool by name with the provided arguments.
    /// </summary>
    public static async Task<McpInvokeResponse> InvokeAsync(
        McpInvokeRequest request,
        IServiceProvider services,
        CancellationToken ct)
    {
        try
        {
            var result = request.Tool switch
            {
                // Read tools
                "search" => await InvokeSearchAsync(request.Arguments, services, ct),
                "getModel" => await InvokeGetModelAsync(request.Arguments, services, ct),
                "getCreator" => await InvokeGetCreatorAsync(request.Arguments, services, ct),
                "listSources" => await InvokeListSourcesAsync(services, ct),
                "stats" => await InvokeStatsAsync(services, ct),
                "findDuplicates" => await InvokeFindDuplicatesAsync(request.Arguments, services, ct),
                "findUntagged" => await InvokeFindUntaggedAsync(request.Arguments, services, ct),
                "recent" => await InvokeRecentAsync(request.Arguments, services, ct),

                // Write tools
                "tagModel" => await InvokeTagModelAsync(request.Arguments, services, ct),
                "updateModel" => await InvokeUpdateModelAsync(request.Arguments, services, ct),
                "markPrinted" => await InvokeMarkPrintedAsync(request.Arguments, services, ct),
                "setComponents" => await InvokeSetComponentsAsync(request.Arguments, services, ct),
                "linkModels" => await InvokeLinkModelsAsync(request.Arguments, services, ct),
                "bulkUpdate" => await InvokeBulkUpdateAsync(request.Arguments, services, ct),
                "triggerSync" => await InvokeTriggerSyncAsync(request.Arguments, services, ct),

                // Analysis tools
                "collectionReport" => await InvokeCollectionReportAsync(services, ct),
                "healthCheck" => await InvokeHealthCheckAsync(services, ct),
                "printHistory" => await InvokePrintHistoryAsync(request.Arguments, services, ct),

                _ => throw new ArgumentException($"Unknown tool: {request.Tool}"),
            };

            return new McpInvokeResponse
            {
                Content = [new McpContent { Text = JsonSerializer.Serialize(result, JsonOptions) }],
            };
        }
        catch (Exception ex)
        {
            return new McpInvokeResponse
            {
                IsError = true,
                Content = [new McpContent { Text = ex.Message }],
            };
        }
    }

    // ======== READ TOOLS ========

    private static async Task<object> InvokeSearchAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ISearchService>();

        var request = new ModelSearchRequest
        {
            Query = GetString(args, "query"),
            Category = GetString(args, "category"),
            GameSystem = GetString(args, "gameSystem"),
            Creator = GetString(args, "creator"),
            Tags = GetString(args, "tags"),
            Scale = GetString(args, "scale"),
            Page = GetInt(args, "page", 1),
            PageSize = GetInt(args, "pageSize", 20),
            SortBy = GetString(args, "sortBy") ?? "name",
            SortDescending = GetBool(args, "sortDescending"),
        };

        if (GetString(args, "source") is { } sourceStr &&
            System.Enum.TryParse<SourceType>(sourceStr, true, out var source))
            request.Source = source;

        if (GetBool(args, "printed") is var printed && args.ContainsKey("printed"))
            request.Printed = printed;

        return await search.SearchAsync(request, ct);
    }

    private static async Task<object> InvokeGetModelAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");
        return await repo.GetByIdAsync(id, ct) ?? throw new KeyNotFoundException($"Model {id} not found");
    }

    private static async Task<object> InvokeGetCreatorAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ICreatorRepository>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");
        return await repo.GetByIdAsync(id, ct) ?? throw new KeyNotFoundException($"Creator {id} not found");
    }

    private static async Task<object> InvokeListSourcesAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        return await db.Sources.ToListAsync(ct);
    }

    private static async Task<object> InvokeStatsAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        return await repo.GetStatsAsync(ct);
    }

    private static async Task<object> InvokeFindDuplicatesAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var limit = GetInt(args, "limit", 50);

        var nameDupes = await db.Models
            .Include(m => m.Creator)
            .GroupBy(m => m.Name.ToLower())
            .Where(g => g.Count() > 1)
            .Take(limit)
            .Select(g => new
            {
                MatchType = "name",
                Models = g.Select(m => new { m.Id, m.Name, CreatorName = m.Creator.Name, m.BasePath }).ToList(),
            })
            .ToListAsync(ct);

        return nameDupes;
    }

    private static async Task<object> InvokeFindUntaggedAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var limit = GetInt(args, "limit", 50);

        return await db.Models
            .Include(m => m.Creator)
            .Include(m => m.Tags)
            .Where(m => !m.Tags.Any())
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Id, m.Name, CreatorName = m.Creator.Name, m.Category })
            .ToListAsync(ct);
    }

    private static async Task<object> InvokeRecentAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var limit = GetInt(args, "limit", 20);

        return await db.Models
            .Include(m => m.Creator)
            .OrderByDescending(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Id, m.Name, CreatorName = m.Creator.Name, m.Source, m.CreatedAt })
            .ToListAsync(ct);
    }

    // ======== WRITE TOOLS ========

    private static async Task<object> InvokeTagModelAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");
        var tagName = GetString(args, "tag")?.ToLowerInvariant().Trim()
            ?? throw new ArgumentException("tag is required");

        var model = await db.Models.Include(m => m.Tags).FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new KeyNotFoundException($"Model {id} not found");

        if (!model.Tags.Any(t => t.Name == tagName))
        {
            var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct)
                ?? new Tag { Id = Guid.NewGuid(), Name = tagName };
            if (!db.Entry(tag).IsKeySet || db.Entry(tag).State == EntityState.Detached)
                db.Tags.Add(tag);
            model.Tags.Add(tag);
            await db.SaveChangesAsync(ct);
        }

        return new { success = true, modelId = id, tag = tagName };
    }

    private static async Task<object> InvokeUpdateModelAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");

        var model = await repo.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Model {id} not found");

        if (GetString(args, "name") is { } name) model.Name = name;
        if (GetString(args, "category") is { } cat) model.Category = cat;
        if (GetString(args, "gameSystem") is { } gs) model.GameSystem = gs;
        if (GetString(args, "scale") is { } scale) model.Scale = scale;
        if (GetString(args, "notes") is { } notes) model.Notes = notes;
        if (args.TryGetValue("rating", out var ratingObj) && ratingObj != null)
        {
            if (int.TryParse(ratingObj.ToString(), out var rating))
                model.Rating = rating;
        }

        model.UpdatedAt = DateTime.UtcNow;
        await repo.UpdateAsync(model, ct);

        return new { success = true, modelId = id };
    }

    private static async Task<object> InvokeMarkPrintedAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");

        var model = await repo.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Model {id} not found");

        var entry = new PrintHistoryEntry
        {
            Id = Guid.NewGuid(),
            Date = GetString(args, "date") ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            Printer = GetString(args, "printer"),
            Material = GetString(args, "material"),
            Result = GetString(args, "result") ?? "success",
            Notes = GetString(args, "notes"),
        };

        model.PrintHistory ??= [];
        model.PrintHistory.Add(entry);
        await repo.UpdateAsync(model, ct);

        return new { success = true, modelId = id, printId = entry.Id };
    }

    private static async Task<object> InvokeSetComponentsAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        var id = GetGuid(args, "id") ?? throw new ArgumentException("id is required");

        var model = await repo.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Model {id} not found");

        if (args.TryGetValue("components", out var compObj) && compObj != null)
        {
            var json = compObj is JsonElement el ? el.GetRawText() : JsonSerializer.Serialize(compObj);
            model.Components = JsonSerializer.Deserialize<List<ComponentInfo>>(json, JsonOptions);
        }

        await repo.UpdateAsync(model, ct);
        return new { success = true, modelId = id, componentCount = model.Components?.Count ?? 0 };
    }

    private static async Task<object> InvokeLinkModelsAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var id = GetGuid(args, "modelId") ?? throw new ArgumentException("modelId is required");
        var relatedId = GetGuid(args, "relatedModelId") ?? throw new ArgumentException("relatedModelId is required");
        var relationType = GetString(args, "relationType") ?? "collection";

        var exists = await db.ModelRelations.AnyAsync(
            r => r.ModelId == id && r.RelatedModelId == relatedId && r.RelationType == relationType, ct);
        if (exists) return new { success = true, message = "Relation already exists" };

        db.ModelRelations.Add(new ModelRelation
        {
            Id = Guid.NewGuid(),
            ModelId = id,
            RelatedModelId = relatedId,
            RelationType = relationType,
        });
        await db.SaveChangesAsync(ct);

        return new { success = true, modelId = id, relatedModelId = relatedId, relationType };
    }

    private static async Task<object> InvokeBulkUpdateAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        List<Guid> modelIds;
        if (args.TryGetValue("modelIds", out var idsObj) && idsObj != null)
        {
            var json = idsObj is JsonElement el ? el.GetRawText() : JsonSerializer.Serialize(idsObj);
            modelIds = JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        else throw new ArgumentException("modelIds is required");

        var operation = GetString(args, "operation") ?? throw new ArgumentException("operation is required");
        var value = GetString(args, "value") ?? throw new ArgumentException("value is required");

        var models = await db.Models
            .Include(m => m.Tags)
            .Where(m => modelIds.Contains(m.Id))
            .ToListAsync(ct);

        switch (operation.ToLowerInvariant())
        {
            case "tag":
                var tagName = value.ToLowerInvariant().Trim();
                var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == tagName, ct)
                    ?? new Tag { Id = Guid.NewGuid(), Name = tagName };
                if (db.Entry(tag).State == EntityState.Detached)
                    db.Tags.Add(tag);
                foreach (var m in models)
                    if (!m.Tags.Any(t => t.Name == tagName)) m.Tags.Add(tag);
                break;
            case "categorize":
                foreach (var m in models) m.Category = value;
                break;
            case "setgamesystem":
                foreach (var m in models) m.GameSystem = value;
                break;
            case "setscale":
                foreach (var m in models) m.Scale = value;
                break;
            case "setlicense":
                foreach (var m in models) m.LicenseType = value;
                break;
            case "setcollection":
                foreach (var m in models) m.CollectionName = value;
                break;
            case "setprintstatus":
                foreach (var m in models) m.PrintStatus = value;
                break;
            case "removetag":
                var removeTagName = value.ToLowerInvariant().Trim();
                foreach (var m in models)
                {
                    var tagToRemove = m.Tags.FirstOrDefault(t => t.Name == removeTagName);
                    if (tagToRemove != null) m.Tags.Remove(tagToRemove);
                }
                break;
            case "setcreator":
                var creatorName = value.Trim();
                var newCreator = await db.Creators
                    .FirstOrDefaultAsync(c => c.Name.ToLower() == creatorName.ToLower(), ct);
                if (newCreator == null)
                    throw new ArgumentException($"Creator '{creatorName}' not found");
                foreach (var m in models) m.CreatorId = newCreator.Id;
                break;
            default:
                throw new ArgumentException($"Unknown operation: {operation}");
        }

        foreach (var m in models) m.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return new { success = true, affectedCount = models.Count, operation, value };
    }

    private static async Task<object> InvokeTriggerSyncAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        var pluginHost = services.GetRequiredService<PluginHostService>();
        var slug = GetString(args, "pluginSlug") ?? throw new ArgumentException("pluginSlug is required");
        await pluginHost.TriggerSyncAsync(slug, ct);
        return new { success = true, message = $"Sync triggered for plugin '{slug}'" };
    }

    // ======== ANALYSIS TOOLS ========

    private static async Task<object> InvokeCollectionReportAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var totalModels = await db.Models.CountAsync(ct);
        var totalCreators = await db.Creators.CountAsync(ct);
        var totalVariants = await db.Variants.CountAsync(ct);
        var totalSize = await db.Models.SumAsync(m => m.TotalSizeBytes, ct);
        var printedCount = await db.Models
            .Where(m => m.PrintHistory != null && m.PrintHistory.Count > 0)
            .CountAsync(ct);

        var bySource = await db.Models
            .GroupBy(m => m.Source)
            .Select(g => new { Source = g.Key.ToString(), Count = g.Count() })
            .ToListAsync(ct);

        var topCreators = await db.Creators
            .OrderByDescending(c => c.ModelCount)
            .Take(10)
            .Select(c => new { c.Name, c.ModelCount })
            .ToListAsync(ct);

        var byCategory = await db.Models
            .Where(m => m.Category != null)
            .GroupBy(m => m.Category!)
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(10)
            .ToListAsync(ct);

        return new
        {
            totalModels,
            totalCreators,
            totalVariants,
            totalSizeBytes = totalSize,
            totalSizeGb = Math.Round(totalSize / 1_073_741_824.0, 2),
            printedCount,
            unprintedCount = totalModels - printedCount,
            printedPercent = totalModels > 0 ? Math.Round(printedCount * 100.0 / totalModels, 1) : 0,
            bySource,
            topCreators,
            byCategory,
        };
    }

    private static async Task<object> InvokeHealthCheckAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();

        var issues = new List<string>();

        // Check for models with zero files
        var zeroFileModels = await db.Models.CountAsync(m => m.FileCount == 0, ct);
        if (zeroFileModels > 0)
            issues.Add($"{zeroFileModels} models have zero files");

        // Check for models without a creator
        var orphanModels = await db.Models.CountAsync(m => m.Creator == null!, ct);
        if (orphanModels > 0)
            issues.Add($"{orphanModels} models have no creator");

        // Check for untagged models
        var untagged = await db.Models.Include(m => m.Tags).CountAsync(m => !m.Tags.Any(), ct);
        if (untagged > 0)
            issues.Add($"{untagged} models have no tags");

        // Check for models without thumbnails
        var noThumbnail = await db.Models.CountAsync(m => m.ThumbnailPath == null, ct);

        // Check pending imports
        var pendingImports = await db.ImportQueue
            .CountAsync(q => q.Status == ImportStatus.Pending || q.Status == ImportStatus.AwaitingReview, ct);

        return new
        {
            status = issues.Count == 0 ? "healthy" : "issues_found",
            issueCount = issues.Count,
            issues,
            modelsWithoutThumbnails = noThumbnail,
            pendingImports,
            timestamp = DateTime.UtcNow,
        };
    }

    private static async Task<object> InvokePrintHistoryAsync(
        Dictionary<string, object?> args, IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ForgeDbContext>();
        var limit = GetInt(args, "limit", 50);

        var printed = await db.Models
            .Include(m => m.Creator)
            .Where(m => m.PrintHistory != null && m.PrintHistory.Count > 0)
            .OrderByDescending(m => m.UpdatedAt)
            .Take(limit)
            .Select(m => new
            {
                m.Id,
                m.Name,
                CreatorName = m.Creator.Name,
                PrintCount = m.PrintHistory!.Count,
                LastPrinted = m.PrintHistory.OrderByDescending(p => p.Date).Select(p => p.Date).FirstOrDefault(),
                m.PrintHistory,
            })
            .ToListAsync(ct);

        return printed;
    }

    // ======== TOOL DEFINITIONS ========

    private static List<McpToolDefinition> ReadTools() =>
    [
        new()
        {
            Name = "search",
            Description = "Search 3D models by text query, filters (category, gameSystem, creator, tags, source, scale, printed status), and pagination. Returns paginated results.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["query"] = new() { Description = "Full-text search query (fuzzy match on model name, creator name, tags)" },
                    ["category"] = new() { Description = "Filter by category (e.g., 'Warhammer 40K', 'Age of Sigmar', 'D&D')" },
                    ["gameSystem"] = new() { Description = "Filter by game system" },
                    ["creator"] = new() { Description = "Filter by creator name (substring match)" },
                    ["tags"] = new() { Description = "Comma-separated tag names — model must have ALL specified tags" },
                    ["source"] = new() { Description = "Filter by source", Enum = ["Mmf", "Thangs", "Cults3d", "Thingiverse", "Patreon", "Manual"] },
                    ["scale"] = new() { Description = "Filter by scale (e.g., '28mm', '32mm', '75mm')" },
                    ["printed"] = new() { Type = "boolean", Description = "Filter by print status" },
                    ["sortBy"] = new() { Description = "Sort field", Enum = ["name", "date", "rating", "filecount", "size", "creator"], Default = "name" },
                    ["sortDescending"] = new() { Type = "boolean", Description = "Sort in descending order" },
                    ["page"] = new() { Type = "integer", Description = "Page number (1-based)", Default = 1 },
                    ["pageSize"] = new() { Type = "integer", Description = "Results per page (1-200)", Default = 20 },
                },
            },
        },
        new()
        {
            Name = "getModel",
            Description = "Get full details of a 3D model by ID, including variants, print history, components, and related models.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new() { ["id"] = new() { Description = "Model UUID" } },
                Required = ["id"],
            },
        },
        new()
        {
            Name = "getCreator",
            Description = "Get details of a creator/sculptor by ID.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new() { ["id"] = new() { Description = "Creator UUID" } },
                Required = ["id"],
            },
        },
        new()
        {
            Name = "listSources",
            Description = "List all configured model sources (directories being scanned).",
            Category = "read",
            InputSchema = new McpInputSchema(),
        },
        new()
        {
            Name = "stats",
            Description = "Get collection statistics: total models, creators, files, sizes, top creators, models by source/category.",
            Category = "read",
            InputSchema = new McpInputSchema(),
        },
        new()
        {
            Name = "findDuplicates",
            Description = "Find potential duplicate models by name or file hash.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["limit"] = new() { Type = "integer", Description = "Max results to return", Default = 50 },
                },
            },
        },
        new()
        {
            Name = "findUntagged",
            Description = "Find models that have no tags assigned.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["limit"] = new() { Type = "integer", Description = "Max results to return", Default = 50 },
                },
            },
        },
        new()
        {
            Name = "recent",
            Description = "Get the most recently added models.",
            Category = "read",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["limit"] = new() { Type = "integer", Description = "Number of recent models to return", Default = 20 },
                },
            },
        },
    ];

    private static List<McpToolDefinition> WriteTools() =>
    [
        new()
        {
            Name = "tagModel",
            Description = "Add a tag to a model. Creates the tag if it doesn't exist.",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["id"] = new() { Description = "Model UUID" },
                    ["tag"] = new() { Description = "Tag name to add" },
                },
                Required = ["id", "tag"],
            },
        },
        new()
        {
            Name = "updateModel",
            Description = "Update model metadata: name, category, gameSystem, scale, notes, rating.",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["id"] = new() { Description = "Model UUID" },
                    ["name"] = new() { Description = "New model name" },
                    ["category"] = new() { Description = "Category (e.g., 'Warhammer 40K')" },
                    ["gameSystem"] = new() { Description = "Game system" },
                    ["scale"] = new() { Description = "Scale (e.g., '28mm')" },
                    ["notes"] = new() { Description = "Notes" },
                    ["rating"] = new() { Type = "integer", Description = "Rating 1-5" },
                },
                Required = ["id"],
            },
        },
        new()
        {
            Name = "markPrinted",
            Description = "Record that a model was printed. Adds a print history entry.",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["id"] = new() { Description = "Model UUID" },
                    ["date"] = new() { Description = "Print date (YYYY-MM-DD), defaults to today" },
                    ["printer"] = new() { Description = "Printer name" },
                    ["material"] = new() { Description = "Material used" },
                    ["result"] = new() { Description = "Print result", Enum = ["success", "failed", "partial"], Default = "success" },
                    ["notes"] = new() { Description = "Notes about the print" },
                },
                Required = ["id"],
            },
        },
        new()
        {
            Name = "setComponents",
            Description = "Set the component list for a multi-part model.",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["id"] = new() { Description = "Model UUID" },
                    ["components"] = new() { Type = "array", Description = "List of components", Items = new() { Type = "object", Description = "Component with name, file, required, group fields" } },
                },
                Required = ["id", "components"],
            },
        },
        new()
        {
            Name = "linkModels",
            Description = "Create a relationship between two models (collection, companion, remix, alternate, base).",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["modelId"] = new() { Description = "First model UUID" },
                    ["relatedModelId"] = new() { Description = "Related model UUID" },
                    ["relationType"] = new() { Description = "Relation type", Enum = ["collection", "companion", "remix", "alternate", "base"], Default = "collection" },
                },
                Required = ["modelId", "relatedModelId"],
            },
        },
        new()
        {
            Name = "bulkUpdate",
            Description = "Apply an operation to multiple models at once. Operations: tag (add tag), removetag (remove tag), categorize (set category), setGameSystem, setScale, setLicense (set licenseType), setCollection (set collectionName), setPrintStatus (set workflow print status), setCreator (reassign to creator by name).",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["modelIds"] = new() { Type = "array", Description = "List of model UUIDs", Items = new() { Description = "Model UUID" } },
                    ["operation"] = new() { Description = "Operation to perform", Enum = ["tag", "removetag", "categorize", "setgamesystem", "setscale", "setlicense", "setcollection", "setprintstatus", "setcreator"] },
                    ["value"] = new() { Description = "Value for the operation (tag name, category, creator name, etc.)" },
                },
                Required = ["modelIds", "operation", "value"],
            },
        },
        new()
        {
            Name = "triggerSync",
            Description = "Trigger a manual sync for a scraper plugin.",
            Category = "write",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["pluginSlug"] = new() { Description = "Plugin slug (e.g., 'mmf', 'thangs')" },
                },
                Required = ["pluginSlug"],
            },
        },
    ];

    private static List<McpToolDefinition> AnalysisTools() =>
    [
        new()
        {
            Name = "collectionReport",
            Description = "Generate a comprehensive collection report with statistics, source breakdown, top creators, and category distribution.",
            Category = "analysis",
            InputSchema = new McpInputSchema(),
        },
        new()
        {
            Name = "healthCheck",
            Description = "Run a health check on the collection: find orphan models, missing thumbnails, untagged models, pending imports.",
            Category = "analysis",
            InputSchema = new McpInputSchema(),
        },
        new()
        {
            Name = "printHistory",
            Description = "Get models with print history, sorted by most recent prints.",
            Category = "analysis",
            InputSchema = new McpInputSchema
            {
                Properties = new()
                {
                    ["limit"] = new() { Type = "integer", Description = "Max results", Default = 50 },
                },
            },
        },
    ];

    // ======== HELPERS ========

    private static string? GetString(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var val) || val == null) return null;
        if (val is JsonElement el) return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return val.ToString();
    }

    private static int GetInt(Dictionary<string, object?> args, string key, int defaultValue = 0)
    {
        var str = GetString(args, key);
        return int.TryParse(str, out var v) ? v : defaultValue;
    }

    private static bool GetBool(Dictionary<string, object?> args, string key, bool defaultValue = false)
    {
        var str = GetString(args, key);
        return bool.TryParse(str, out var v) ? v : defaultValue;
    }

    private static Guid? GetGuid(Dictionary<string, object?> args, string key)
    {
        var str = GetString(args, key);
        return Guid.TryParse(str, out var v) ? v : null;
    }
}
