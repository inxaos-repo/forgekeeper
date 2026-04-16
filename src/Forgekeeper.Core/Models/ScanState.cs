namespace Forgekeeper.Core.Models;

public class ScanState
{
    public Guid Id { get; set; }
    public string DirectoryPath { get; set; } = string.Empty;
    public DateTime LastScannedAt { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public int FileCount { get; set; }
}
