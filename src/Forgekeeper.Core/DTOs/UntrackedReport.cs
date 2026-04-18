namespace Forgekeeper.Core.DTOs;

public class UntrackedReport
{
    public int TotalOrphans { get; set; }
    public long OrphanSizeBytes { get; set; }
    public List<UntrackedItem> Items { get; set; } = [];
}

public class UntrackedItem
{
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}
