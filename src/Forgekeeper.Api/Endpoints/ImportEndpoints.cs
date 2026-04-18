using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Interfaces;
using Forgekeeper.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Forgekeeper.Api.Endpoints;

public static class ImportEndpoints
{
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".3mf", ".lys", ".ctb", ".cbddlp", ".gcode", ".sl1", ".zip"
    };

    public static void MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/import").WithTags("Import");

        group.MapPost("/process", async (
            IImportService importService,
            CancellationToken ct) =>
        {
            var results = await importService.ProcessUnsortedAsync(ct);
            return Results.Ok(results);
        }).WithName("ProcessUnsorted");

        group.MapGet("/queue", async (
            [FromQuery] ImportStatus? status,
            IImportService importService,
            CancellationToken ct) =>
        {
            var items = await importService.GetQueueAsync(status, ct);
            return Results.Ok(items);
        }).WithName("GetImportQueue");

        group.MapPost("/queue/{id:guid}/confirm", async (
            Guid id,
            [FromBody] ImportConfirmRequest request,
            IImportService importService,
            CancellationToken ct) =>
        {
            try
            {
                await importService.ConfirmImportAsync(id, request, ct);
                return Results.Ok(new { message = "Import confirmed" });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("ConfirmImport");

        group.MapDelete("/queue/{id:guid}", async (
            Guid id,
            IImportService importService,
            CancellationToken ct) =>
        {
            try
            {
                await importService.DismissAsync(id, ct);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("DismissImport");

        // --- Folder Scan (manual import discovery) ---

        group.MapPost("/scan", async (
            [FromBody] ImportScanRequest request,
            ForgeDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Path))
                return Results.BadRequest(new { message = "path is required" });

            if (!Directory.Exists(request.Path))
                return Results.BadRequest(new { message = $"Directory not found: {request.Path}" });

            var result = new ImportScanResult
            {
                ScannedPath = request.Path,
                ScannedAt = DateTime.UtcNow,
            };

            // Build lookups
            var existingPathSet = await db.Models
                .Select(m => new ModelPathEntry { Id = m.Id, BasePath = m.BasePath, Name = m.Name })
                .ToListAsync(ct);

            var pathLookup = existingPathSet.ToDictionary(
                m => m.BasePath.TrimEnd(Path.DirectorySeparatorChar),
                m => m,
                StringComparer.OrdinalIgnoreCase);

            var existingByName = existingPathSet
                .GroupBy(m => m.Name.ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First());

            var creatorNames = await db.Creators
                .Select(c => c.Name)
                .ToListAsync(ct);

            // Gather existing name+creator combos for fuzzy match
            var existingNameCreator = await db.Models
                .Select(m => new ModelNameCreator { Id = m.Id, Name = m.Name, CreatorName = m.Creator.Name })
                .ToListAsync(ct);

            var dirsScanned = 0;
            var detected = new List<DetectedModelEntry>();

            ScanDirectory(
                request.Path, request.Path, 0,
                request.MaxDepth > 0 ? request.MaxDepth : int.MaxValue,
                request.Recursive,
                pathLookup, existingNameCreator, creatorNames,
                detected, ref dirsScanned, ct);

            result.TotalDirectoriesScanned = dirsScanned;
            result.DetectedModels = detected.Count;
            result.AlreadyInLibrary = detected.Count(d => d.AlreadyInLibrary);
            result.Models = detected;

            return Results.Ok(result);
        }).WithName("ScanForImport");
    }

    // -------- helpers --------

    private sealed record ModelPathEntry { public Guid Id { get; set; } public string BasePath { get; set; } = ""; public string Name { get; set; } = ""; }
    private sealed record ModelNameCreator { public Guid Id { get; set; } public string Name { get; set; } = ""; public string CreatorName { get; set; } = ""; }

    private static void ScanDirectory(
        string rootPath,
        string currentPath,
        int depth,
        int maxDepth,
        bool recursive,
        Dictionary<string, ModelPathEntry> pathLookup,
        List<ModelNameCreator> existingModels,
        List<string> creatorNames,
        List<DetectedModelEntry> results,
        ref int dirsScanned,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        dirsScanned++;

        // Does this directory directly contain model files?
        bool hasModels;
        try
        {
            hasModels = Directory.EnumerateFiles(currentPath, "*", SearchOption.AllDirectories)
                .Any(f => ModelExtensions.Contains(Path.GetExtension(f)));
        }
        catch { return; }

        if (hasModels)
        {
            var entry = BuildDetectedEntry(rootPath, currentPath, pathLookup, existingModels, creatorNames);
            if (entry != null)
                results.Add(entry);
            // Don't recurse further — this folder IS the model
        }
        else if (recursive && depth < maxDepth)
        {
            foreach (var subDir in Directory.EnumerateDirectories(currentPath))
                ScanDirectory(rootPath, subDir, depth + 1, maxDepth, recursive,
                    pathLookup, existingModels, creatorNames, results, ref dirsScanned, ct);
        }
    }

    private static DetectedModelEntry? BuildDetectedEntry(
        string rootPath,
        string folderPath,
        Dictionary<string, ModelPathEntry> pathLookup,
        List<ModelNameCreator> existingModels,
        List<string> creatorNames)
    {
        List<string> allFiles;
        try
        {
            allFiles = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
                .Where(f => ModelExtensions.Contains(Path.GetExtension(f)))
                .ToList();
        }
        catch { return null; }

        if (allFiles.Count == 0) return null;

        var folderName = Path.GetFileName(folderPath) ?? folderPath;
        var parentName = Path.GetFileName(Path.GetDirectoryName(folderPath) ?? rootPath) ?? "";

        // Try to match parent folder to a known creator
        string? detectedCreator = creatorNames.FirstOrDefault(c =>
            string.Equals(c, parentName, StringComparison.OrdinalIgnoreCase)
            || parentName.Contains(c, StringComparison.OrdinalIgnoreCase));
        detectedCreator ??= parentName;

        // Check if path is already in library
        var normalizedPath = folderPath.TrimEnd(Path.DirectorySeparatorChar);
        bool alreadyInLibrary = pathLookup.TryGetValue(normalizedPath, out var existingByPath);
        Guid? existingId = alreadyInLibrary ? existingByPath!.Id : null;

        // Fuzzy match: same name + creator name
        if (!alreadyInLibrary)
        {
            var fuzzy = existingModels.FirstOrDefault(m =>
                string.Equals(m.Name, folderName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.CreatorName, detectedCreator, StringComparison.OrdinalIgnoreCase));
            if (fuzzy != null)
            {
                alreadyInLibrary = true;
                existingId = fuzzy.Id;
            }
        }

        var detectedFiles = allFiles.Select(f =>
        {
            var rel = Path.GetRelativePath(folderPath, f);
            long size = 0;
            try { size = new FileInfo(f).Length; } catch { }
            return new DetectedVariantFile
            {
                RelativePath = rel,
                FileName = Path.GetFileName(f),
                FileType = Path.GetExtension(f).TrimStart('.').ToUpperInvariant(),
                SizeBytes = size,
                DetectedVariant = DetectVariant(rel),
            };
        }).ToList();

        return new DetectedModelEntry
        {
            FolderPath = folderPath,
            DetectedModelName = folderName,
            DetectedCreatorName = detectedCreator,
            AlreadyInLibrary = alreadyInLibrary,
            ExistingModelId = existingId,
            Files = detectedFiles,
            TotalFiles = detectedFiles.Count,
            TotalSizeBytes = detectedFiles.Sum(f => f.SizeBytes),
            HasMetadataJson = File.Exists(Path.Combine(folderPath, "metadata.json")),
        };
    }

    private static string? DetectVariant(string relativePath)
    {
        var pathLower = relativePath.Replace('\\', '/').ToLowerInvariant();
        var segments = pathLower.Split('/');

        foreach (var segment in segments)
        {
            if (segment is "supported" or "sup") return "supported";
            if (segment is "unsupported" or "unsup" or "nosup") return "unsupported";
            if (segment is "presupported" or "pre-supported" or "presup") return "presupported";
            if (segment is "lychee") return "lychee";
        }

        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".lys" => "lychee",
            ".ctb" or ".cbddlp" => "chitubox",
            ".gcode" => "gcode",
            _ => null
        };
    }
}
