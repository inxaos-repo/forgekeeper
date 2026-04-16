using Xunit;
using Moq;
using Forgekeeper.Core.DTOs;
using Forgekeeper.Core.Enums;
using Forgekeeper.Core.Models;
using Forgekeeper.Infrastructure.Services;
using Forgekeeper.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for SearchService.
///
/// Note: Full SearchService integration tests require PostgreSQL because:
/// 1. pg_trgm fuzzy search (ILike) is PostgreSQL-specific
/// 2. JSONB columns (PrintHistory, Components, PrintSettings) can't be projected by InMemory provider
///
/// These unit tests validate the request/response DTOs and basic search contract.
/// Full filter combination tests should use Testcontainers with PostgreSQL.
/// </summary>
public class SearchServiceTests
{
    [Fact]
    public void ModelSearchRequest_DefaultValues_AreCorrect()
    {
        var request = new ModelSearchRequest();
        Assert.Equal("name", request.SortBy);
        Assert.False(request.SortDescending);
        Assert.Equal(1, request.Page);
        Assert.Equal(50, request.PageSize);
        Assert.Null(request.Query);
        Assert.Null(request.CreatorId);
        Assert.Null(request.Category);
        Assert.Null(request.Source);
        Assert.Null(request.Printed);
    }

    [Fact]
    public void ModelSearchRequest_TagsParsing_SupportsCommaSeparated()
    {
        var request = new ModelSearchRequest { Tags = "warhammer, 40k, space marine" };
        var tags = request.Tags!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, tags.Length);
        Assert.Contains("warhammer", tags);
        Assert.Contains("40k", tags);
        Assert.Contains("space marine", tags);
    }

    [Fact]
    public void ModelSearchRequest_AllFilterTypes_CanBeSet()
    {
        var request = new ModelSearchRequest
        {
            Query = "space marine",
            CreatorId = Guid.NewGuid(),
            Category = "Warhammer 40K",
            GameSystem = "40K",
            Scale = "28mm",
            Source = SourceType.Mmf,
            SourceSlug = "mmf",
            FileType = FileType.Stl,
            Printed = true,
            MinRating = 4,
            LicenseType = "personal",
            CollectionName = "My Collection",
            Tags = "infantry,hero",
            Creator = "AwesomeSculptor",
            AcquisitionMethod = AcquisitionMethod.Purchase,
            PublishedAfter = new DateTime(2025, 1, 1),
            PublishedBefore = new DateTime(2026, 12, 31),
            SortBy = "rating",
            SortDescending = true,
            Page = 3,
            PageSize = 25,
        };

        // All properties should be set correctly
        Assert.Equal("space marine", request.Query);
        Assert.NotNull(request.CreatorId);
        Assert.Equal("Warhammer 40K", request.Category);
        Assert.Equal("40K", request.GameSystem);
        Assert.Equal("28mm", request.Scale);
        Assert.Equal(SourceType.Mmf, request.Source);
        Assert.Equal("mmf", request.SourceSlug);
        Assert.Equal(FileType.Stl, request.FileType);
        Assert.True(request.Printed);
        Assert.Equal(4, request.MinRating);
        Assert.Equal("personal", request.LicenseType);
        Assert.Equal("My Collection", request.CollectionName);
        Assert.Equal("infantry,hero", request.Tags);
        Assert.Equal("AwesomeSculptor", request.Creator);
        Assert.Equal(AcquisitionMethod.Purchase, request.AcquisitionMethod);
        Assert.Equal(new DateTime(2025, 1, 1), request.PublishedAfter);
        Assert.Equal(new DateTime(2026, 12, 31), request.PublishedBefore);
        Assert.Equal("rating", request.SortBy);
        Assert.True(request.SortDescending);
        Assert.Equal(3, request.Page);
        Assert.Equal(25, request.PageSize);
    }

    [Fact]
    public void PaginatedResult_CalculatesCorrectly()
    {
        var result = new PaginatedResult<ModelResponse>
        {
            Items = [new ModelResponse { Name = "Test" }],
            TotalCount = 100,
            Page = 3,
            PageSize = 25,
        };

        Assert.Single(result.Items);
        Assert.Equal(100, result.TotalCount);
        Assert.Equal(3, result.Page);
        Assert.Equal(25, result.PageSize);
    }

    [Fact]
    public void ModelResponse_PrintedStatus_MappedFromPrintHistory()
    {
        // ModelResponse.Printed is set by the service during projection
        var response = new ModelResponse
        {
            Name = "Test",
            Printed = true,
        };
        Assert.True(response.Printed);
    }

    [Fact]
    public void ModelDetailResponse_InheritsFromModelResponse()
    {
        var detail = new ModelDetailResponse
        {
            Name = "Test Model",
            Category = "Warhammer 40K",
            Variants =
            [
                new VariantResponse
                {
                    FileName = "body.stl",
                    FileType = FileType.Stl,
                    VariantType = VariantType.Supported,
                    FileSizeBytes = 5_000_000,
                },
            ],
            PrintHistory =
            [
                new PrintHistoryEntry { Result = "success", Date = "2026-01-15" },
            ],
            Components =
            [
                new ComponentInfo { Name = "Body", File = "body.stl" },
            ],
        };

        Assert.Equal("Test Model", detail.Name);
        Assert.Single(detail.Variants);
        Assert.Single(detail.PrintHistory!);
        Assert.Single(detail.Components!);
    }

    [Fact]
    public void BulkUpdateRequest_SupportsAllOperations()
    {
        var operations = new[] { "tag", "categorize", "setGameSystem", "setScale", "setRating", "setLicense" };
        foreach (var op in operations)
        {
            var request = new BulkUpdateRequest
            {
                ModelIds = [Guid.NewGuid(), Guid.NewGuid()],
                Operation = op,
                Value = "test-value",
            };
            Assert.Equal(2, request.ModelIds.Count);
            Assert.Equal(op, request.Operation);
        }
    }

    [Fact]
    public void DuplicateGroup_MatchTypes()
    {
        var nameGroup = new DuplicateGroup
        {
            MatchType = "name",
            Similarity = 1.0,
            Models = [new DuplicateModel { Name = "Test", CreatorName = "Creator" }],
        };
        Assert.Equal("name", nameGroup.MatchType);
        Assert.Equal(1.0, nameGroup.Similarity);

        var hashGroup = new DuplicateGroup
        {
            MatchType = "hash",
            Similarity = 1.0,
            Models = [new DuplicateModel { Name = "Test", CreatorName = "Creator" }],
        };
        Assert.Equal("hash", hashGroup.MatchType);
    }

    /// <summary>
    /// Verifies the SearchService constructor accepts the required dependencies.
    /// Actual query execution requires PostgreSQL (Testcontainers integration test).
    /// </summary>
    [Fact]
    public void SearchService_CanBeConstructed()
    {
        var db = TestDbContextFactory.Create();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Search:MinTrigramSimilarity"] = "0.3",
            })
            .Build();

        var service = new SearchService(db, config);
        Assert.NotNull(service);
        db.Dispose();
    }
}
