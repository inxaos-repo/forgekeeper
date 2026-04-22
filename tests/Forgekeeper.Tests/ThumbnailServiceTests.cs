using Forgekeeper.Infrastructure.Services;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Tests for <see cref="ThumbnailService.IsMacOSJunkFile(string?)"/> — the pre-filter that
/// skips macOS-fork sidecar files before they reach stl-thumb (which panics on them).
///
/// Context: a single scan pass on a library containing macOS-zipped creator uploads
/// generated ~2,212 panic-warn log lines from stl-thumb on 2026-04-21. The filter
/// eliminates the entire class without touching real STL handling.
/// </summary>
public class ThumbnailServiceTests
{
    [Theory]
    // Real STLs — must NOT be filtered.
    [InlineData("/library/sources/mmf/creator/model/part.stl")]
    [InlineData("/foo/bar.stl")]
    // Single-underscore prefixes are not the AppleDouble convention — NOT filtered.
    [InlineData("/library/sources/mmf/_creator/bar.stl")]
    [InlineData("/_foo/bar.stl")]
    // Dotfile pattern in a PARENT directory does not trigger — the rule is strictly the basename.
    [InlineData("/foo/._hidden/real.stl")]
    [InlineData("/creator/.__something/real.stl")]
    public void IsMacOSJunkFile_LeavesRealStls(string path)
    {
        Assert.False(ThumbnailService.IsMacOSJunkFile(path),
            $"Expected '{path}' to be treated as a real STL, but it was filtered out.");
    }

    [Theory]
    // __MACOSX/ directory anywhere in the path — filter.
    [InlineData("/library/sources/mmf/unknown/Foo/__MACOSX/bar.stl")]
    [InlineData("/__MACOSX/top-level.stl")]
    [InlineData("/a/b/c/__MACOSX/d.stl")]
    // Basename starts with `._` — filter.
    [InlineData("/library/sources/mmf/unknown/Foo/._part.stl")]
    [InlineData("/foo/._bar.stl")]
    // Basename starts with `.__` — filter.
    [InlineData("/library/sources/mmf/unknown/Foo/.__part.stl")]
    [InlineData("/foo/.__bar.stl")]
    // Case matters for __MACOSX — exact macOS convention is all-caps.
    // (We don't match __macosx lowercase — that's a fallthrough real STL case.)
    public void IsMacOSJunkFile_FiltersKnownJunkPatterns(string path)
    {
        Assert.True(ThumbnailService.IsMacOSJunkFile(path),
            $"Expected '{path}' to be filtered as macOS junk, but it passed through.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsMacOSJunkFile_NullOrWhitespace_ReturnsFalse(string? path)
    {
        // Whitespace/empty paths are NOT treated as junk — they'll fall through to
        // whatever error handling the caller already has. Prevents the filter from
        // silently swallowing bad input it didn't cause.
        Assert.False(ThumbnailService.IsMacOSJunkFile(path));
    }

    [Fact]
    public void IsMacOSJunkFile_WindowsStyleSeparators_MatchesMacosxDirectory()
    {
        // If this code ever runs on Windows against a Windows-format path, we still
        // catch the __MACOSX/ rule. The basename rule is already separator-agnostic
        // because Path.GetFileName handles both.
        Assert.True(ThumbnailService.IsMacOSJunkFile(@"C:\library\Foo\__MACOSX\bar.stl"));
    }

    [Fact]
    public void IsMacOSJunkFile_LowercaseMacosx_DoesNotMatch()
    {
        // __MACOSX/ is strictly the all-caps convention macOS emits; a folder called
        // __macosx is something else entirely. Sanity-check we're not being greedy.
        Assert.False(ThumbnailService.IsMacOSJunkFile("/foo/__macosx/bar.stl"));
    }
}
