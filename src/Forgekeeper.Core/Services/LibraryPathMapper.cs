namespace Forgekeeper.Core.Services;

/// <summary>
/// Maps container paths under the library root (e.g. <c>/library</c>) to the matching Windows path
/// (a UNC share or mapped drive), and builds <c>file://</c> and <c>forgekeeper-open://</c> links.
/// Also does the "is this path inside an allowed root" check used by file-serving endpoints.
/// </summary>
/// <remarks>
/// Configure with <c>Library:WindowsPathPrefix</c> (env <c>Library__WindowsPathPrefix</c>), e.g.
/// <c>\\samba.k8s.inxaos.com\warehousepool\3d printing</c>. <c>Library:LinuxRoot</c> defaults to the first
/// <c>Storage:BasePaths</c> entry. With no prefix set, nothing is mapped and the UI copies the Linux path.
/// </remarks>
public static class LibraryPathMapper
{
    public const string ProtocolScheme = "forgekeeper-open";

    /// <summary>Windows path for <paramref name="linuxPath"/>, or null if it isn't under <paramref name="linuxRoot"/> or no prefix is set.</summary>
    public static string? ToWindowsPath(string? linuxPath, string? linuxRoot, string? windowsPrefix)
    {
        if (string.IsNullOrWhiteSpace(linuxPath) || string.IsNullOrWhiteSpace(linuxRoot) || string.IsNullOrWhiteSpace(windowsPrefix))
            return null;

        var root = NormalizeLinux(linuxRoot);
        var path = NormalizeLinux(linuxPath);
        if (path is null || root is null) return null;

        string rest;
        if (path == root) rest = "";
        else if (path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal))
            rest = path[(root == "/" ? 1 : root.Length + 1)..];
        else return null;

        var prefix = windowsPrefix.Trim().Replace('/', '\\').TrimEnd('\\');
        if (prefix.Length == 0) return null;
        // keep a bare drive as "W:\"
        if (rest.Length == 0) return prefix.EndsWith(':') ? prefix + "\\" : prefix;
        return prefix + "\\" + rest.Replace('/', '\\');
    }

    /// <summary>
    /// <c>file://</c> URL for a Windows path. UNC <c>\\host\share\a b</c> → <c>file://host/share/a%20b</c>;
    /// drive <c>W:\x</c> → <c>file:///W:/x</c>. Chrome won't follow these from an https page, so the UI offers copy too.
    /// </summary>
    public static string? ToFileUrl(string? windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        var p = windowsPath.Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            return "file://" + parts[0] + (parts.Length > 1 ? "/" + string.Join('/', parts.Skip(1).Select(Uri.EscapeDataString)) : "");
        }
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
        {
            var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return "file:///" + char.ToUpperInvariant(p[0]) + ":/" + string.Join('/', parts.Select(Uri.EscapeDataString));
        }
        return null;
    }

    /// <summary><c>forgekeeper-open://open?path=…</c> URL for the optional Windows protocol handler (docs/tools/windows-open-handler).</summary>
    public static string? ToProtocolUrl(string? windowsPath, bool select)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return null;
        return $"{ProtocolScheme}://{(select ? "select" : "open")}?path={Uri.EscapeDataString(windowsPath)}";
    }

    /// <summary>True when <paramref name="path"/> resolves inside one of <paramref name="roots"/> (no traversal out via "..").</summary>
    public static bool IsWithinRoots(string? path, IEnumerable<string?> roots)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }

        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            string root;
            try { root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)); } catch { continue; }
            if (full.Equals(root, StringComparison.Ordinal)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string? NormalizeLinux(string p)
    {
        p = p.Trim().Replace('\\', '/');
        if (!p.StartsWith('/')) return null;
        // collapse "." and "..", refuse to climb above "/"
        var stack = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count == 0) return null; stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return "/" + string.Join('/', stack);
    }
}
