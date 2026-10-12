using Forgekeeper.Core.Services;

namespace Forgekeeper.Api.Endpoints;

/// <summary>
/// Library path settings: <c>Library:LinuxRoot</c> (default: first <c>Storage:BasePaths</c>) and
/// <c>Library:WindowsPathPrefix</c> (env <c>Library__WindowsPathPrefix</c>).
/// </summary>
public sealed record LibraryPathSettings(string LinuxRoot, string? WindowsPathPrefix, IReadOnlyList<string> AllowedRoots)
{
    public static LibraryPathSettings From(IConfiguration config)
    {
        var basePaths = config.GetSection("Storage:BasePaths").Get<string[]>() ?? [];
        var root = config["Library:LinuxRoot"];
        if (string.IsNullOrWhiteSpace(root)) root = basePaths.FirstOrDefault() ?? "/library";
        var prefix = config["Library:WindowsPathPrefix"];
        var allowed = basePaths.Append(root).Distinct().ToList();
        return new LibraryPathSettings(root, string.IsNullOrWhiteSpace(prefix) ? null : prefix, allowed);
    }

    public string? ToWindows(string? linuxPath) => LibraryPathMapper.ToWindowsPath(linuxPath, LinuxRoot, WindowsPathPrefix);
}
