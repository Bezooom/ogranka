using System;
using System.Collections.Generic;

namespace TeklaToStep;

/// <summary>
/// Planar profile builders and the two swept solids Tekla exports besides a straight prism.
/// Fillets are quarter-circle polylines, not analytic STEP circles.
/// </summary>
public static class ProfileShapes
{
    public const int ArcSegments = 8;

    public static List<Vec3> IShape(double width, double depth, double web, double flange, double fillet, double edgeRadius)
    {
        double hw = width / 2, hd = depth / 2, hweb = web / 2, yf = hd - flange;
        var pts = new List<Vec3>
        {
            new Vec3(-hw, -hd, 0), new Vec3(hw, -hd, 0), new Vec3(hw, -yf, 0), new Vec3(hweb, -yf, 0),
            new Vec3(hweb, yf, 0), new Vec3(hw, yf, 0), new Vec3(hw, hd, 0), new Vec3(-hw, hd, 0),
            new Vec3(-hw, yf, 0), new Vec3(-hweb, yf, 0), new Vec3(-hweb, -yf, 0), new Vec3(-hw, -yf, 0)
        };
        if (fillet > 1e-6) pts = Fillet(pts, fillet, concave: true, ArcSegments);
        if (edgeRadius > 1e-6) pts = Fillet(pts, edgeRadius, concave: false, ArcSegments);
        return pts;
    }

    /// <summary>
    /// Cold-formed C. Girth is the lip length, same reading as IfcOpenShell.
    /// Opening faces +X, web sits at -X, profile is centred.
    /// </summary>
    public static List<Vec3> CShape(double depth, double width, double wall, double girth, double fillet)
    {
        double hw = width / 2, hd = depth / 2;
        double lip = girth;
        if (lip < wall) lip = wall;
        if (lip > hd) lip = hd;
        var pts = new List<Vec3>
        {
            new Vec3(-hw, -hd, 0),
            new Vec3(hw, -hd, 0),
            new Vec3(hw, -hd + lip, 0),
            new Vec3(hw - wall, -hd + lip, 0),
            new Vec3(hw - wall, -hd + wall, 0),
            new Vec3(-hw + wall, -hd + wall, 0),
            new Vec3(-hw + wall, hd - wall, 0),
            new Vec3(hw - wall, hd - wall, 0),
            new Vec3(hw - wall, hd - lip, 0),
            new Vec3(hw, hd - lip, 0),
            new Vec3(hw, hd, 0),
            new Vec3(-hw, hd, 0)
        };
        if (fillet > 1e-6) pts = Fillet(pts, fillet, concave: true, ArcSegments);
        return pts;
    }

    public static List<Vec3> RoundedRect(double xDim, double yDim, double radius)
    {
        double hx = xDim / 2, hy = yDim / 2;
        var pts = new List<Vec3>
        {
            new Vec3(-hx, -hy, 0), new Vec3(hx, -hy, 0), new Vec3(hx, hy, 0), new Vec3(-hx, hy, 0)
        };
        if (radius <= 1e-6) return pts;
        return Fillet(pts, radius, concave: false, ArcSegments);
    }

    /// <summary>
    /// Replace sharp corners with a circular arc. CCW polygon: a right turn is concave.
    /// </summary>
    public static List<Vec3> Fillet(IReadOnlyList<Vec3> poly, double radius, bool concave, int segments)
    {
        int n = poly.Count;
        if (n < 3 || radius <= 1e-6) return new List<Vec3>(poly);
        if (segments < 2) segments = 2;
        var result = new List<Vec3>();
        for (int i = 0; i < n; i++)
        {
            var prev = poly[(i + n - 1) % n];
            var corner = poly[i];
            var next = poly[(i + 1) % n];
            var into = corner - prev;
            var outDir = next - corner;
            double lenIn = into.Length;
            double lenOut = outDir.Length;
            if (lenIn < 1e-9 || lenOut < 1e-9)
            {
                result.Add(corner);
                continue;
            }
            into = into * (1.0 / lenIn);
            outDir = outDir * (1.0 / lenOut);
            double cross = into.X * outDir.Y - into.Y * outDir.X;
            bool isConcave = cross < 0;
            if (Math.Abs(cross) < 1e-8 || isConcave != concave)
            {
                result.Add(corner);
                continue;
            }
            double turn = Math.Atan2(cross, Vec3.Dot(into, outDir));
            double half = Math.Abs(turn) * 0.5;
            if (half < 1e-4 || half > Math.PI * 0.49)
            {
                result.Add(corner);
                continue;
            }
            double tanLen = radius / Math.Tan(half);
            double maxTan = Math.Min(lenIn, lenOut) * 0.45;
            if (tanLen > maxTan) tanLen = maxTan;
            if (tanLen < 1e-6)
            {
                result.Add(corner);
                continue;
            }
            double r = tanLen * Math.Tan(half);
            var p1 = corner - into * tanLen;
            var p2 = corner + outDir * tanLen;
            var left = new Vec3(-into.Y, into.X, 0);
            var center = isConcave ? p1 - left * r : p1 + left * r;
            double a0 = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
            double a1 = Math.Atan2(p2.Y - center.Y, p2.X - center.X);
            double sweep = a1 - a0;
            while (sweep > Math.PI) sweep -= 2 * Math.PI;
            while (sweep < -Math.PI) sweep += 2 * Math.PI;
            result.Add(p1);
            for (int s = 1; s < segments; s++)
            {
                double a = a0 + sweep * s / segments;
                result.Add(new Vec3(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a), corner.Z));
            }
            result.Add(p2);
        }
        return result.Count >= 3 ? result : new List<Vec3>(poly);
    }

    public static List<Facet> Revolve(
        IReadOnlyList<Vec3> outer,
        IReadOnlyList<IReadOnlyList<Vec3>>? holes,
        Vec3 axisOrigin,
        Vec3 axisDir,
        double angleRad,
        int steps)
    {
        var faces = new List<Facet>();
        var ring = new List<Vec3>(outer);
        if (ring.Count < 3 || Math.Abs(angleRad) < 1e-8) return faces;
        if (MeshOps.SignedAreaXY(ring) < 0) ring.Reverse();
        bool full = Math.Abs(Math.Abs(angleRad) - 2 * Math.PI) < 1e-3 || Math.Abs(angleRad) > 2 * Math.PI - 1e-3;
        double sweep = full ? (angleRad < 0 ? -1 : 1) * 2 * Math.PI : angleRad;
        int n = Math.Max(12, steps);
        var axis = axisDir.LengthSq < 1e-18 ? new Vec3(0, 0, 1) : axisDir.Normalized();
        int stations = full ? n : n + 1;
        AddRevolvedLoop(faces, ring, axisOrigin, axis, sweep, n, stations, full, outwardRight: true);
        if (holes != null)
        {
            for (int h = 0; h < holes.Count; h++)
            {
                var hole = new List<Vec3>(holes[h]);
                if (hole.Count < 3) continue;
                if (MeshOps.SignedAreaXY(hole) > 0) hole.Reverse();
                AddRevolvedLoop(faces, hole, axisOrigin, axis, sweep, n, stations, full, outwardRight: true);
            }
        }
        return faces;
    }

    public static List<Facet> SweptDisk(IReadOnlyList<Vec3> pathIn, double radius, double innerRadius, int segments)
    {
        var faces = new List<Facet>();
        var path = new List<Vec3>();
        for (int i = 0; i < pathIn.Count; i++)
        {
            if (path.Count == 0 || (path[path.Count - 1] - pathIn[i]).LengthSq > 1e-8)
                path.Add(pathIn[i]);
        }
        if (path.Count < 2 || radius <= 1e-6) return faces;
        int segs = Math.Max(8, segments);
        var tangent = new Vec3[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            var back = i == 0 ? path[1] - path[0] : path[i] - path[i - 1];
            var fwd = i == path.Count - 1 ? back : path[i + 1] - path[i];
            var t = back + fwd;
            tangent[i] = t.LengthSq < 1e-18 ? back.Normalized() : t.Normalized();
        }
        var frameX = new Vec3[path.Count];
        var helper = Math.Abs(tangent[0].Z) < 0.9 ? new Vec3(0, 0, 1) : new Vec3(0, 1, 0);
        frameX[0] = Vec3.Cross(tangent[0], helper).Normalized();
        for (int i = 1; i < path.Count; i++)
        {
            var rotAxis = Vec3.Cross(tangent[i - 1], tangent[i]);
            if (rotAxis.LengthSq < 1e-16)
                frameX[i] = frameX[i - 1];
            else
            {
                double ang = Math.Atan2(rotAxis.Length, Vec3.Dot(tangent[i - 1], tangent[i]));
                frameX[i] = RotateDirection(frameX[i - 1], rotAxis.Normalized(), ang);
            }
            var y = Vec3.Cross(tangent[i], frameX[i]);
            if (y.LengthSq < 1e-16) y = Vec3.Cross(tangent[i], helper);
            y = y.Normalized();
            frameX[i] = Vec3.Cross(y, tangent[i]).Normalized();
        }

        bool tube = innerRadius > 1e-6 && innerRadius < radius - 1e-6;
        var outer = new List<Vec3>[path.Count];
        var inner = tube ? new List<Vec3>[path.Count] : null;
        for (int i = 0; i < path.Count; i++)
        {
            var y = Vec3.Cross(tangent[i], frameX[i]).Normalized();
            outer[i] = Circle(path[i], frameX[i], y, radius, segs);
            if (inner != null) inner[i] = Circle(path[i], frameX[i], y, innerRadius, segs);
        }
        for (int i = 0; i < path.Count - 1; i++)
        {
            AddTubeWall(faces, outer[i], outer[i + 1], awayFromCenter: true);
            if (inner != null) AddTubeWall(faces, inner[i], inner[i + 1], awayFromCenter: false);
        }
        AddDiskCap(faces, outer[0], inner == null ? null : inner[0], path[1] - path[0], atStart: true);
        AddDiskCap(faces, outer[path.Count - 1], inner == null ? null : inner[path.Count - 1], path[path.Count - 1] - path[path.Count - 2], atStart: false);
        return faces;
    }

    public static List<Facet> Transform(IReadOnlyList<Facet> faces, Mat4 m)
    {
        var dst = new List<Facet>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            var outer = new List<Vec3>(f.Outer.Count);
            for (int k = 0; k < f.Outer.Count; k++) outer.Add(m.TransformPoint(f.Outer[k]));
            var facet = new Facet(outer, m.TransformVector(f.Normal));
            for (int h = 0; h < f.Holes.Count; h++)
            {
                var hole = new List<Vec3>(f.Holes[h].Count);
                for (int k = 0; k < f.Holes[h].Count; k++) hole.Add(m.TransformPoint(f.Holes[h][k]));
                facet.Holes.Add(hole);
            }
            dst.Add(facet);
        }
        return dst;
    }

    static void AddRevolvedLoop(
        List<Facet> faces,
        List<Vec3> loop,
        Vec3 axisOrigin,
        Vec3 axis,
        double sweep,
        int steps,
        int stations,
        bool full,
        bool outwardRight)
    {
        var rings = new List<Vec3>[stations];
        for (int s = 0; s < stations; s++)
        {
            double a = full ? sweep * s / steps : sweep * s / steps;
            rings[s] = new List<Vec3>(loop.Count);
            for (int i = 0; i < loop.Count; i++)
                rings[s].Add(RotateAround(loop[i], axisOrigin, axis, a));
        }
        int spans = full ? steps : steps;
        for (int s = 0; s < spans; s++)
        {
            int s2 = (s + 1) % stations;
            double a = sweep * s / steps;
            for (int i = 0; i < loop.Count; i++)
            {
                int j = (i + 1) % loop.Count;
                var edge = loop[j] - loop[i];
                var right = new Vec3(edge.Y, -edge.X, 0);
                if (!outwardRight) right = -right;
                var want = RotateDirection(right, axis, a);
                var quad = new List<Vec3> { rings[s][i], rings[s][j], rings[s2][j], rings[s2][i] };
                if (MeshOps.NewellRaw(quad).LengthSq < 1e-12) continue;
                if (Vec3.Dot(MeshOps.Newell(quad), want) < 0) quad.Reverse();
                faces.Add(new Facet(quad, MeshOps.Newell(quad)));
            }
        }
        if (full) return;
        AddCapAway(faces, rings[0], rings[1]);
        AddCapAway(faces, rings[stations - 1], rings[stations - 2]);
    }

    static void AddCapAway(List<Facet> faces, List<Vec3> cap, List<Vec3> other)
    {
        var loop = new List<Vec3>(cap);
        var into = other[0] - cap[0];
        if (Vec3.Dot(MeshOps.Newell(loop), into) > 0) loop.Reverse();
        faces.Add(new Facet(loop, MeshOps.Newell(loop)));
    }

    static void AddTubeWall(List<Facet> faces, List<Vec3> a, List<Vec3> b, bool awayFromCenter)
    {
        var mid = Centroid(a);
        int n = a.Count;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var quad = new List<Vec3> { a[i], a[j], b[j], b[i] };
            var radial = (a[i] + a[j]) * 0.5 - mid;
            bool away = Vec3.Dot(MeshOps.Newell(quad), radial) > 0;
            if (away != awayFromCenter) quad.Reverse();
            faces.Add(new Facet(quad, MeshOps.Newell(quad)));
        }
    }

    static void AddDiskCap(List<Facet> faces, List<Vec3> outer, List<Vec3>? hole, Vec3 travel, bool atStart)
    {
        var loop = new List<Vec3>(outer);
        var want = atStart ? -travel : travel;
        if (Vec3.Dot(MeshOps.Newell(loop), want) < 0) loop.Reverse();
        var facet = new Facet(loop, MeshOps.Newell(loop));
        if (hole != null)
        {
            var h = new List<Vec3>(hole);
            if (Vec3.Dot(MeshOps.Newell(h), facet.Normal) > 0) h.Reverse();
            facet.Holes.Add(h);
        }
        faces.Add(facet);
    }

    static List<Vec3> Circle(Vec3 center, Vec3 x, Vec3 y, double radius, int segs)
    {
        var pts = new List<Vec3>(segs);
        for (int i = 0; i < segs; i++)
        {
            double a = i * 2 * Math.PI / segs;
            pts.Add(center + x * (radius * Math.Cos(a)) + y * (radius * Math.Sin(a)));
        }
        return pts;
    }

    static Vec3 Centroid(IReadOnlyList<Vec3> pts)
    {
        var s = new Vec3(0, 0, 0);
        for (int i = 0; i < pts.Count; i++) s += pts[i];
        return s * (1.0 / pts.Count);
    }

    public static Vec3 RotateAround(Vec3 p, Vec3 origin, Vec3 axis, double ang)
    {
        var k = axis.LengthSq < 1e-18 ? new Vec3(0, 0, 1) : axis.Normalized();
        var v = p - origin;
        double c = Math.Cos(ang);
        double s = Math.Sin(ang);
        return origin + v * c + Vec3.Cross(k, v) * s + k * Vec3.Dot(k, v) * (1 - c);
    }

    static Vec3 RotateDirection(Vec3 v, Vec3 axis, double ang)
    {
        var k = axis.Normalized();
        double c = Math.Cos(ang);
        double s = Math.Sin(ang);
        return v * c + Vec3.Cross(k, v) * s + k * Vec3.Dot(k, v) * (1 - c);
    }
}
