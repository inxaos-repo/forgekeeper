using Xunit;
using Forgekeeper.Core.Enums;
using Forgekeeper.Infrastructure.Services;

namespace Forgekeeper.Tests;

public class VariantDetectionTests
{
    [Theory]
    [InlineData("supported/model.stl", ".stl", VariantType.Supported)]
    [InlineData("sup/model.stl", ".stl", VariantType.Supported)]
    [InlineData("unsupported/model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("unsup/model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("nosup/model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("presupported/model.stl", ".stl", VariantType.Presupported)]
    [InlineData("pre-supported/model.stl", ".stl", VariantType.Presupported)]
    [InlineData("presup/model.stl", ".stl", VariantType.Presupported)]
    [InlineData("lychee/model.lys", ".lys", VariantType.LycheeProject)]
    [InlineData("chitubox/model.ctb", ".ctb", VariantType.ChituboxProject)]
    [InlineData("images/preview.png", ".png", VariantType.PreviewImage)]
    public void DetectVariantType_FolderBased_ReturnsCorrectType(string relativePath, string ext, VariantType expected)
    {
        var result = FileScannerService.DetectVariantType(relativePath, ext);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("model.lys", ".lys", VariantType.LycheeProject)]
    [InlineData("model.ctb", ".ctb", VariantType.ChituboxProject)]
    [InlineData("model.cbddlp", ".cbddlp", VariantType.ChituboxProject)]
    [InlineData("model.gcode", ".gcode", VariantType.Gcode)]
    [InlineData("model.3mf", ".3mf", VariantType.PrintProject)]
    public void DetectVariantType_ExtensionBased_ReturnsCorrectType(string relativePath, string ext, VariantType expected)
    {
        var result = FileScannerService.DetectVariantType(relativePath, ext);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("model.stl", ".stl", VariantType.Unsupported)]
    [InlineData("model.obj", ".obj", VariantType.Unsupported)]
    public void DetectVariantType_RootFile_DefaultsToUnsupported(string relativePath, string ext, VariantType expected)
    {
        var result = FileScannerService.DetectVariantType(relativePath, ext);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(".stl", FileType.Stl)]
    [InlineData(".obj", FileType.Obj)]
    [InlineData(".3mf", FileType.Threemf)]
    [InlineData(".lys", FileType.Lys)]
    [InlineData(".ctb", FileType.Ctb)]
    [InlineData(".cbddlp", FileType.Cbddlp)]
    [InlineData(".gcode", FileType.Gcode)]
    [InlineData(".sl1", FileType.Sl1)]
    [InlineData(".png", FileType.Png)]
    [InlineData(".jpg", FileType.Jpg)]
    [InlineData(".jpeg", FileType.Jpg)]
    [InlineData(".webp", FileType.Webp)]
    [InlineData(".xyz", FileType.Other)]
    public void DetectFileType_ReturnsCorrectType(string ext, FileType expected)
    {
        var result = FileScannerService.DetectFileType(ext);
        Assert.Equal(expected, result);
    }
}
