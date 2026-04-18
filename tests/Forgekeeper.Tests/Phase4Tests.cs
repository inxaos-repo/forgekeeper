using Forgekeeper.Infrastructure.Services;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>
/// Phase 4 tests: fuzzy directory matching, name normalization, creator skip list.
/// Tests the PluginHostService's directory matching logic.
/// </summary>
public class Phase4Tests : IDisposable
{
    private readonly string _tempDir;

    public Phase4Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"forgekeeper-p4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // --- Name Normalization ---

    [Theory]
    [InlineData("Artisan Guild", "artisanguild")]
    [InlineData("artisan-guild", "artisanguild")]
    [InlineData("Artisan_Guild", "artisanguild")]
    [InlineData("ARTISAN GUILD", "artisanguild")]
    [InlineData("  Artisan  Guild  ", "artisanguild")]
    [InlineData("Dragon Knight (Champion)", "dragonknightchampion")]
    [InlineData("Model v2.1", "modelv21")]
    public void NormalizeName_StripsPunctuationAndSpaces(string input, string expected)
    {
        // Use reflection to test the private static method
        var method = typeof(PluginHostService).GetMethod("NormalizeName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { input }) as string;
        Assert.Equal(expected, result);
    }

    // --- Fuzzy Directory Matching ---

    [Fact]
    public void FindFuzzyDir_ExactMatch()
    {
        var target = Path.Combine(_tempDir, "Artisan Guild");
        Directory.CreateDirectory(target);

        var method = typeof(PluginHostService).GetMethod("FindFuzzyDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild" }) as string;
        Assert.Equal(target, result);
    }

    [Fact]
    public void FindFuzzyDir_CaseInsensitiveMatch()
    {
        var target = Path.Combine(_tempDir, "artisan guild");
        Directory.CreateDirectory(target);

        var method = typeof(PluginHostService).GetMethod("FindFuzzyDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild" }) as string;
        Assert.Equal(target, result);
    }

    [Fact]
    public void FindFuzzyDir_NormalizedMatch()
    {
        var target = Path.Combine(_tempDir, "Artisan-Guild");
        Directory.CreateDirectory(target);

        var method = typeof(PluginHostService).GetMethod("FindFuzzyDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild" }) as string;
        Assert.Equal(target, result);
    }

    [Fact]
    public void FindFuzzyDir_DashPrefixMatch()
    {
        // MiniDownloader sometimes creates "ID - Model Name" folders
        var target = Path.Combine(_tempDir, "12345 - Dragon Knight");
        Directory.CreateDirectory(target);

        var method = typeof(PluginHostService).GetMethod("FindFuzzyDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Dragon Knight" }) as string;
        Assert.Equal(target, result);
    }

    [Fact]
    public void FindFuzzyDir_NoMatch_ReturnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "SomeOtherCreator"));

        var method = typeof(PluginHostService).GetMethod("FindFuzzyDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild" }) as string;
        Assert.Null(result);
    }

    // --- Model Directory Matching ---

    [Fact]
    public void FindExistingModelDir_ExactMatch()
    {
        var creatorDir = Path.Combine(_tempDir, "Artisan Guild");
        var modelDir = Path.Combine(creatorDir, "Dragon Knight");
        Directory.CreateDirectory(modelDir);

        var method = typeof(PluginHostService).GetMethod("FindExistingModelDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild", "Dragon Knight" }) as string;
        Assert.Equal(modelDir, result);
    }

    [Fact]
    public void FindExistingModelDir_FuzzyCreatorAndModel()
    {
        var creatorDir = Path.Combine(_tempDir, "artisan-guild");
        var modelDir = Path.Combine(creatorDir, "Dragon_Knight_Champion");
        Directory.CreateDirectory(modelDir);

        var method = typeof(PluginHostService).GetMethod("FindExistingModelDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "Artisan Guild", "Dragon Knight Champion" }) as string;
        Assert.Equal(modelDir, result);
    }

    [Fact]
    public void FindExistingModelDir_NoMatch_ReturnsNull()
    {
        var method = typeof(PluginHostService).GetMethod("FindExistingModelDir",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { _tempDir, "NonExistent", "Model" }) as string;
        Assert.Null(result);
    }
}
