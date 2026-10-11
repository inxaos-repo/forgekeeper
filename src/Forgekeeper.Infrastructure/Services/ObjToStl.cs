using System.Globalization;

namespace Forgekeeper.Infrastructure.Services;

/// <summary>
/// #58: stl-thumb 0.5.0 (the version in our image) does not read Wavefront OBJ at all. It parses
/// every input as STL and fails with "failed to fill whole buffer", even on a one-triangle
/// OBJ. Before rendering, OBJ files are converted to a temporary binary STL: geometry only
/// (v/f), with polygons fan-triangulated and negative (relative) indices supported.
/// </summary>
public static class ObjToStl
{
    /// <summary>Convert <paramref name="objPath"/> to binary STL at <paramref name="stlPath"/>. Returns triangle count (0 = nothing usable, no file written).</summary>
    public static int Convert(string objPath, string stlPath)
    {
        var verts = new List<(float X, float Y, float Z)>();
        var tris = new List<(int A, int B, int C)>();
        var face = new List<int>();

        foreach (var rawLine in File.ReadLines(objPath))
        {
            var line = rawLine.AsSpan().Trim();
            if (line.Length < 2) continue;
            if (line[0] == 'v' && line[1] == ' ')
            {
                var p = line[2..].ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3
                    && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    verts.Add((x, y, z));
                else
                    verts.Add((0, 0, 0)); // keep indices aligned
            }
            else if (line[0] == 'f' && line[1] == ' ')
            {
                face.Clear();
                foreach (var tok in line[2..].ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var slash = tok.IndexOf('/');
                    if (!int.TryParse(slash < 0 ? tok : tok[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx) || idx == 0)
                    { face.Clear(); break; }
                    face.Add(idx > 0 ? idx - 1 : verts.Count + idx);
                }
                if (face.Count < 3 || face.Any(i => i < 0 || i >= verts.Count)) continue;
                for (int i = 1; i + 1 < face.Count; i++)
                    tris.Add((face[0], face[i], face[i + 1]));
            }
        }

        if (tris.Count == 0) return 0;

        using var fs = File.Create(stlPath);
        using var w = new BinaryWriter(fs);
        w.Write(new byte[80]);
        w.Write((uint)tris.Count);
        foreach (var (a, b, c) in tris)
        {
            var (p0, p1, p2) = (verts[a], verts[b], verts[c]);
            float ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z;
            float vx = p2.X - p0.X, vy = p2.Y - p0.Y, vz = p2.Z - p0.Z;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            var len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 0) { nx /= len; ny /= len; nz /= len; }
            w.Write(nx); w.Write(ny); w.Write(nz);
            w.Write(p0.X); w.Write(p0.Y); w.Write(p0.Z);
            w.Write(p1.X); w.Write(p1.Y); w.Write(p1.Z);
            w.Write(p2.X); w.Write(p2.Y); w.Write(p2.Z);
            w.Write((ushort)0);
        }
        return tris.Count;
    }
}
