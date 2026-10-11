using System.Diagnostics;
using Forgekeeper.Infrastructure.Data;
using Forgekeeper.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Forgekeeper.Tests;

/// <summary>#58: a renderer that hangs must be killed at the timeout, not left running.</summary>
public class ThumbnailTimeoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fk-thumb-" + Guid.NewGuid().ToString("N"));
    public ThumbnailTimeoutTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task HangingRenderer_IsKilledAtTimeout()
    {
        if (!OperatingSystem.IsLinux()) return;
        var pidFile = Path.Combine(_dir, "pid");
        var script = Path.Combine(_dir, "fake-renderer.sh");
        File.WriteAllText(script, $"#!/bin/sh\n[ \"$1\" = --version ] && exit 0\necho $$ > '{pidFile}'\nexec sleep 60\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var model = Path.Combine(_dir, "big.obj");
        File.WriteAllText(model, "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Thumbnails:Renderer"] = script,
            ["Thumbnails:TimeoutSeconds"] = "1",
        }).Build();
        var svc = new ThumbnailService(Mock.Of<IDbContextFactory<ForgeDbContext>>(), config,
            NullLogger<ThumbnailService>.Instance, Mock.Of<IServiceScopeFactory>());

        var sw = Stopwatch.StartNew();
        await svc.GenerateThumbnailAsync(model, Path.Combine(_dir, "out.webp"));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));

        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        await Task.Delay(300);
        Assert.False(Directory.Exists($"/proc/{pid}") && !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z "),
            "renderer process still running after timeout");
    }
}

public class ObjToStlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fk-obj-" + Guid.NewGuid().ToString("N"));
    public ObjToStlTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Convert_QuadAndTriangle_WritesBinaryStl()
    {
        var obj = Path.Combine(_dir, "m.obj");
        // CRLF + comments + v/vt/vn face syntax + negative index, like the Uranium exports in the library
        File.WriteAllText(obj, "# URANIUM OBJ EXPORT\r\nv 0 0 0\r\nv 1 0 0\r\nv 1 1 0\r\nv 0 1 0\r\nvn 0 0 1\r\nf 1//1 2//1 3//1 4//1\r\nf -4 -3 -1\r\n");
        var stl = Path.Combine(_dir, "m.stl");
        Assert.Equal(3, ObjToStl.Convert(obj, stl));
        var bytes = File.ReadAllBytes(stl);
        Assert.Equal(84 + 3 * 50, bytes.Length);
        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 80));
        Assert.Equal(1f, BitConverter.ToSingle(bytes, 84 + 8)); // normal z of first triangle
    }

    [Fact]
    public void Convert_NoFaces_ReturnsZero_AndWritesNothing()
    {
        var obj = Path.Combine(_dir, "pts.obj");
        File.WriteAllText(obj, "v 0 0 0\nv 1 0 0\n");
        var stl = Path.Combine(_dir, "pts.stl");
        Assert.Equal(0, ObjToStl.Convert(obj, stl));
        Assert.False(File.Exists(stl));
    }
}
