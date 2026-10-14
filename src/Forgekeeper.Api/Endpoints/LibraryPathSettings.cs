using Forgekeeper.Core.Services;

namespace Forgekeeper.Api.Endpoints;

/// <summary>
/// Library path settings: <c>Library:LinuxRoot</c> (default: first <c>Storage:BasePaths</c>) and
/// <c>Library:WindowsPathPrefix</c> (env <c>Library__WindowsPathPrefix</c>).
/// <c>Library:EnableExplorerProtocol</c> (env <c>Library__EnableExplorerProtocol</c>, default <c>false</c>) gates the
/// unsupported <c>forgekeeper-open://</c> Explorer links and <c>file://</c> links. When off, the API leaves those URLs
/// null and the UI hides the Explorer / file:// buttons.
/// </summary>
public sealed record LibraryPathSettings(string LinuxRoot, string? WindowsPathPrefix, IReadOnlyList<string> AllowedRoots, bool EnableExplorerProtocol = false)
{
    public static LibraryPathSettings From(IConfiguration config)
    {
        var basePaths = config.GetSection("Storage:BasePaths").Get<string[]>() ?? [];
        var root = config["Library:LinuxRoot"];
        if (string.IsNullOrWhiteSpace(root)) root = basePaths.FirstOrDefault() ?? "/library";
        var prefix = config["Library:WindowsPathPrefix"];
        var allowed = basePaths.Append(root).Distinct().ToList();
        var explorer = config.GetValue("Library:EnableExplorerProtocol", false);
        return new LibraryPathSettings(root, string.IsNullOrWhiteSpace(prefix) ? null : prefix, allowed, explorer);
    }

    public string? ToWindows(string? linuxPath) => LibraryPathMapper.ToWindowsPath(linuxPath, LinuxRoot, WindowsPathPrefix);
}
