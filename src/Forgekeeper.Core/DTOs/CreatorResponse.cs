using Forgekeeper.Core.Enums;

namespace Forgekeeper.Core.DTOs;

public class CreatorResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public SourceType Source { get; set; }
    public string? SourceUrl { get; set; }
    public string? AvatarUrl { get; set; }
    public int ModelCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreatorDetailResponse : CreatorResponse
{
    public long TotalSizeBytes { get; set; }
    public int TotalFileCount { get; set; }
    public List<ModelResponse> Models { get; set; } = [];
}
