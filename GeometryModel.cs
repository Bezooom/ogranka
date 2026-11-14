using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeklaToStep;

/// <summary>Millimetre vector. Column-vector math, no hidden unit conversion.</summary>
public readonly struct Vec3 : IEquatable<Vec3>
{
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public Vec3(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double LengthSq => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSq);

    public Vec3 Normalized()
    {
        double l = Length;
        if (l < 1e-15) return new Vec3(0, 0, 0);
        return new Vec3(X / l, Y / l, Z / l);
    }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => a * s;

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
        a.Y * b.Z - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - a.Y * b.X);

    public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
    public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + X.GetHashCode();
            h = h * 31 + Y.GetHashCode();
            h = h * 31 + Z.GetHashCode();
            return h;
        }
    }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
}

/// <summary>
/// Column-vector 4x4. Translation lives in the last column.
/// <c>Multiply(local)</c> applies <paramref name="local"/> first.
/// </summary>
public readonly struct Mat4
{
    public readonly double M00, M01, M02, M03;
    public readonly double M10, M11, M12, M13;
    public readonly double M20, M21, M22, M23;
    public readonly double M30, M31, M32, M33;

    public Mat4(
        double m00, double m01, double m02, double m03,
        double m10, double m11, double m12, double m13,
        double m20, double m21, double m22, double m23,
        double m30, double m31, double m32, double m33)
    {
        M00 = m00; M01 = m01; M02 = m02; M03 = m03;
        M10 = m10; M11 = m11; M12 = m12; M13 = m13;
        M20 = m20; M21 = m21; M22 = m22; M23 = m23;
        M30 = m30; M31 = m31; M32 = m32; M33 = m33;
    }

    public static Mat4 Identity => new Mat4(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1);

    public static Mat4 FromAxes(Vec3 x, Vec3 y, Vec3 z, Vec3 origin) => new Mat4(
        x.X, y.X, z.X, origin.X,
        x.Y, y.Y, z.Y, origin.Y,
        x.Z, y.Z, z.Z, origin.Z,
        0, 0, 0, 1);

    /// <summary>IFC/STEP axis2 placement: Z = axis, X hint = ref direction, Y = Z × X.</summary>
    public static Mat4 FromAxis2(Vec3 origin, Vec3 axisZ, Vec3 refX)
    {
        var z = axisZ.LengthSq < 1e-24 ? new Vec3(0, 0, 1) : axisZ.Normalized();
        var x = refX - z * Vec3.Dot(refX, z);
        if (x.LengthSq < 1e-24)
        {
            var alt = Math.Abs(z.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            x = alt - z * Vec3.Dot(alt, z);
        }
        x = x.Normalized();
        var y = Vec3.Cross(z, x).Normalized();
        x = Vec3.Cross(y, z).Normalized();
        return FromAxes(x, y, z, origin);
    }

    public Vec3 TransformPoint(Vec3 p) => new Vec3(
        M00 * p.X + M01 * p.Y + M02 * p.Z + M03,
        M10 * p.X + M11 * p.Y + M12 * p.Z + M13,
        M20 * p.X + M21 * p.Y + M22 * p.Z + M23);

    public Vec3 TransformVector(Vec3 v) => new Vec3(
        M00 * v.X + M01 * v.Y + M02 * v.Z,
        M10 * v.X + M11 * v.Y + M12 * v.Z,
        M20 * v.X + M21 * v.Y + M22 * v.Z);

    public Mat4 Multiply(Mat4 b) => new Mat4(
        M00 * b.M00 + M01 * b.M10 + M02 * b.M20 + M03 * b.M30,
        M00 * b.M01 + M01 * b.M11 + M02 * b.M21 + M03 * b.M31,
        M00 * b.M02 + M01 * b.M12 + M02 * b.M22 + M03 * b.M32,
        M00 * b.M03 + M01 * b.M13 + M02 * b.M23 + M03 * b.M33,
        M10 * b.M00 + M11 * b.M10 + M12 * b.M20 + M13 * b.M30,
        M10 * b.M01 + M11 * b.M11 + M12 * b.M21 + M13 * b.M31,
        M10 * b.M02 + M11 * b.M12 + M12 * b.M22 + M13 * b.M32,
        M10 * b.M03 + M11 * b.M13 + M12 * b.M23 + M13 * b.M33,
        M20 * b.M00 + M21 * b.M10 + M22 * b.M20 + M23 * b.M30,
        M20 * b.M01 + M21 * b.M11 + M22 * b.M21 + M23 * b.M31,
        M20 * b.M02 + M21 * b.M12 + M22 * b.M22 + M23 * b.M32,
        M20 * b.M03 + M21 * b.M13 + M22 * b.M23 + M23 * b.M33,
        M30 * b.M00 + M31 * b.M10 + M32 * b.M20 + M33 * b.M30,
        M30 * b.M01 + M31 * b.M11 + M32 * b.M21 + M33 * b.M31,
        M30 * b.M02 + M31 * b.M12 + M32 * b.M22 + M33 * b.M32,
        M30 * b.M03 + M31 * b.M13 + M32 * b.M23 + M33 * b.M33);

    public bool TryInverse(out Mat4 inverse)
    {
        var a = new double[4, 8];
        double[] src =
        {
            M00, M01, M02, M03,
            M10, M11, M12, M13,
            M20, M21, M22, M23,
            M30, M31, M32, M33
        };
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++) a[r, c] = src[r * 4 + c];
            a[r, r + 4] = 1;
        }
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            double max = Math.Abs(a[col, col]);
            for (int r = col + 1; r < 4; r++)
            {
                double v = Math.Abs(a[r, col]);
                if (v > max)
                {
                    max = v;
                    pivot = r;
                }
            }
            if (max < 1e-14)
            {
                inverse = Identity;
                return false;
            }
            if (pivot != col)
            {
                for (int c = 0; c < 8; c++)
                {
                    double tmp = a[col, c];
                    a[col, c] = a[pivot, c];
                    a[pivot, c] = tmp;
                }
            }
            double div = a[col, col];
            for (int c = 0; c < 8; c++) a[col, c] /= div;
            for (int r = 0; r < 4; r++)
            {
                if (r == col) continue;
                double f = a[r, col];
                if (f == 0) continue;
                for (int c = 0; c < 8; c++) a[r, c] -= f * a[col, c];
            }
        }
        inverse = new Mat4(
            a[0, 4], a[0, 5], a[0, 6], a[0, 7],
            a[1, 4], a[1, 5], a[1, 6], a[1, 7],
            a[2, 4], a[2, 5], a[2, 6], a[2, 7],
            a[3, 4], a[3, 5], a[3, 6], a[3, 7]);
        return true;
    }
}

/// <summary>Plane with outward normal of a removal volume (tool interior is the negative side).</summary>
public readonly struct Plane
{
    public readonly Vec3 Origin;
    public readonly Vec3 Outward;

    public Plane(Vec3 origin, Vec3 outward)
    {
        Origin = origin;
        Outward = outward.LengthSq < 1e-24 ? new Vec3(0, 0, 1) : outward.Normalized();
    }

    public double Signed(Vec3 p) => Vec3.Dot(p - Origin, Outward);
}

/// <summary>One planar face. Outer is the boundary; holes wind opposite the outward normal.</summary>
public sealed class Facet
{
    public Vec3 Normal;
    public List<Vec3> Outer;
    public List<List<Vec3>> Holes;

    public Facet()
    {
        Outer = new List<Vec3>();
        Holes = new List<List<Vec3>>();
    }

    public Facet(IEnumerable<Vec3> outer, Vec3 normal)
    {
        Outer = new List<Vec3>(outer);
        Holes = new List<List<Vec3>>();
        Normal = normal;
    }
}

/// <summary>Named solid. <see cref="Voids"/> are internal cavities, not through-holes.</summary>
public sealed class SolidMesh
{
    public string Name;
    public List<Facet> Faces;
    public List<List<Facet>> Voids;

    public SolidMesh(string name)
    {
        Name = name ?? string.Empty;
        Faces = new List<Facet>();
        Voids = new List<List<Facet>>();
    }
}

/// <summary>Mesh constructors and the convex half-space subtraction used for IFC clipping.</summary>
public static class MeshOps
{
    public const double Eps = 1e-4;

    public static Vec3 NewellRaw(IReadOnlyList<Vec3> pts)
    {
        double x = 0, y = 0, z = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            x += (a.Y - b.Y) * (a.Z + b.Z);
            y += (a.Z - b.Z) * (a.X + b.X);
            z += (a.X - b.X) * (a.Y + b.Y);
        }
        return new Vec3(x, y, z);
    }

    public static Vec3 Newell(IReadOnlyList<Vec3> pts)
    {
        var raw = NewellRaw(pts);
        return raw.LengthSq < 1e-24 ? new Vec3(0, 0, 0) : raw.Normalized();
    }

    /// <summary>Signed volume. Positive when face loops wind with the outward normal.</summary>
    public static double SignedVolume(IEnumerable<Facet> faces)
    {
        double v = 0;
        foreach (var f in faces)
        {
            if (f.Outer.Count >= 3) v += Vec3.Dot(f.Outer[0], NewellRaw(f.Outer));
            foreach (var h in f.Holes)
            {
                if (h.Count >= 3) v += Vec3.Dot(h[0], NewellRaw(h));
            }
        }
        return v / 6.0;
    }

    public static double SignedVolume(SolidMesh solid)
    {
        double v = SignedVolume(solid.Faces);
        foreach (var shell in solid.Voids)
            v += SignedVolume(shell);
        return v;
    }

    public static double SignedAreaXY(IReadOnlyList<Vec3> pts)
    {
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % pts.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return a * 0.5;
    }

    public static bool IsConvexXY(IReadOnlyList<Vec3> pts)
    {
        int n = pts.Count;
        if (n < 3) return false;
        int sign = 0;
        for (int i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            var c = pts[(i + 2) % n];
            double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= 1e-8) continue;
            int s = cross > 0 ? 1 : -1;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return sign != 0;
    }

    public static SolidMesh Box(string name, double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var s = new SolidMesh(name);
        s.Faces.Add(new Facet(new[] { V(x0, y0, z0), V(x0, y1, z0), V(x1, y1, z0), V(x1, y0, z0) }, new Vec3(0, 0, -1)));
        s.Faces.Add(new Facet(new[] { V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1) }, new Vec3(0, 0, 1)));
        s.Faces.Add(new Facet(new[] { V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1) }, new Vec3(0, -1, 0)));
        s.Faces.Add(new Facet(new[] { V(x0, y1, z0), V(x0, y1, z1), V(x1, y1, z1), V(x1, y1, z0) }, new Vec3(0, 1, 0)));
        s.Faces.Add(new Facet(new[] { V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1), V(x0, y1, z0) }, new Vec3(-1, 0, 0)));
        s.Faces.Add(new Facet(new[] { V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1), V(x1, y0, z1) }, new Vec3(1, 0, 0)));
        return s;
    }

    /// <summary>
    /// Extrude a local XY profile through <paramref name="position"/>.
    /// Outer must be CCW in XY (corrected if not). Holes are stored CW.
    /// </summary>
    public static List<Facet> Extrude(
        IReadOnlyList<Vec3> outerLocal,
        IReadOnlyList<IReadOnlyList<Vec3>>? holesLocal,
        Mat4 position,
        Vec3 extrudeDirLocal,
        double depth)
    {
        var faces = new List<Facet>();
        if (outerLocal.Count < 3 || Math.Abs(depth) < 1e-12) return faces;
        var ring = new List<Vec3>(outerLocal);
        if (SignedAreaXY(ring) < 0) ring.Reverse();
        var dir = extrudeDirLocal.LengthSq < 1e-24 ? new Vec3(0, 0, 1) : extrudeDirLocal.Normalized();
        var shift = position.TransformVector(dir * depth);
        if (shift.LengthSq < 1e-18) return faces;

        var bottom = new List<Vec3>(ring.Count);
        var top = new List<Vec3>(ring.Count);
        for (int i = 0; i < ring.Count; i++)
        {
            var b = position.TransformPoint(ring[i]);
            bottom.Add(b);
            top.Add(b + shift);
        }

        var zWorld = position.TransformVector(new Vec3(0, 0, 1));
        bool topUsesRing = Vec3.Dot(zWorld, shift) >= 0;
        var topLoop = new List<Vec3>(top);
        var botLoop = new List<Vec3>(bottom);
        if (!topUsesRing) topLoop.Reverse();
        else botLoop.Reverse();

        var topFacet = new Facet(topLoop, shift.Normalized());
        var botFacet = new Facet(botLoop, (-shift).Normalized());
        AddWalls(faces, bottom, top);

        if (holesLocal != null)
        {
            foreach (var src in holesLocal)
            {
                if (src.Count < 3) continue;
                var hole = new List<Vec3>(src);
                if (SignedAreaXY(hole) > 0) hole.Reverse();
                var hb = new List<Vec3>(hole.Count);
                var ht = new List<Vec3>(hole.Count);
                for (int i = 0; i < hole.Count; i++)
                {
                    var b = position.TransformPoint(hole[i]);
                    hb.Add(b);
                    ht.Add(b + shift);
                }
                var htLoop = new List<Vec3>(ht);
                var hbLoop = new List<Vec3>(hb);
                if (!topUsesRing) htLoop.Reverse();
                else hbLoop.Reverse();
                topFacet.Holes.Add(htLoop);
                botFacet.Holes.Add(hbLoop);
                AddWalls(faces, hb, ht);
            }
        }
        faces.Add(topFacet);
        faces.Add(botFacet);
        return faces;
    }

    /// <summary>
    /// Subtract the intersection of half-spaces. A point is inside the tool when every plane
    /// signed distance is negative. Returns false if a holed face is cut (caller keeps the original).
    /// </summary>
    public static bool TrySubtract(IReadOnlyList<Facet> subject, IReadOnlyList<Plane> planes, out List<Facet> result, out string error)
    {
        result = new List<Facet>();
        error = string.Empty;
        if (planes == null || planes.Count == 0)
        {
            foreach (var f in subject) result.Add(Clone(f));
            return true;
        }
        foreach (var f in subject)
        {
            if (f.Holes.Count > 0 && FaceHits(f, planes))
            {
                error = "вычитание пересекает грань с отверстием; оставлен исходный операнд";
                return false;
            }
        }

        foreach (var face in subject)
        {
            if (face.Outer.Count < 3) continue;
            if (!FaceHits(face, planes))
            {
                result.Add(Clone(face));
                continue;
            }
            var pieces = new List<Piece> { new Piece(new List<Vec3>(face.Outer), false) };
            for (int pi = 0; pi < planes.Count; pi++)
            {
                var next = new List<Piece>();
                foreach (var piece in pieces)
                {
                    List<List<Vec3>> inn;
                    List<List<Vec3>> outp;
                    List<(Vec3 a, Vec3 b)> cuts;
                    bool coplanar;
                    if (!SplitPolygon(piece.Poly, planes[pi], out inn, out outp, out cuts, out coplanar))
                    {
                        error = "не удалось разрезать грань плоскостью инструмента";
                        return false;
                    }
                    if (coplanar)
                    {
                        next.Add(new Piece(piece.Poly, piece.Outside || CentroidSign(piece.Poly, planes[pi]) > Eps));
                        continue;
                    }
                    for (int i = 0; i < outp.Count; i++)
                        if (outp[i].Count >= 3) next.Add(new Piece(outp[i], true));
                    for (int i = 0; i < inn.Count; i++)
                        if (inn[i].Count >= 3) next.Add(new Piece(inn[i], piece.Outside));
                }
                pieces = next;
            }
            for (int i = 0; i < pieces.Count; i++)
            {
                var poly = pieces[i].Poly;
                if (poly.Count < 3) continue;
                if (!pieces[i].Outside && AllInside(poly, planes)) continue;
                var n = Newell(poly);
                if (n.LengthSq < 1e-18) continue;
                if (face.Normal.LengthSq > 1e-18 && Vec3.Dot(n, face.Normal) < 0)
                {
                    poly.Reverse();
                    n = -n;
                }
                result.Add(new Facet(poly, n));
            }
        }

        var caps = BuildCaps(subject, planes);
        for (int i = 0; i < caps.Count; i++) result.Add(caps[i]);
        return true;
    }

    static List<Facet> BuildCaps(IReadOnlyList<Facet> subject, IReadOnlyList<Plane> planes)
    {
        var caps = new List<Facet>();
        for (int pi = 0; pi < planes.Count; pi++)
        {
            var segs = new List<(Vec3 a, Vec3 b)>();
            for (int f = 0; f < subject.Count; f++)
            {
                if (subject[f].Outer.Count < 3) continue;
                List<List<Vec3>> inn;
                List<List<Vec3>> outp;
                List<(Vec3 a, Vec3 b)> cuts;
                bool coplanar;
                if (!SplitPolygon(subject[f].Outer, planes[pi], out inn, out outp, out cuts, out coplanar) || coplanar)
                    continue;
                for (int c = 0; c < cuts.Count; c++) segs.Add(cuts[c]);
            }
            var loops = ChainLoops(segs);
            var others = new List<Plane>();
            for (int j = 0; j < planes.Count; j++)
                if (j != pi) others.Add(planes[j]);
            var desired = -planes[pi].Outward;
            for (int i = 0; i < loops.Count; i++)
            {
                var clipped = ClipToInside(loops[i], others);
                for (int k = 0; k < clipped.Count; k++)
                {
                    var loop = clipped[k];
                    if (loop.Count < 3) continue;
                    var n = Newell(loop);
                    if (n.LengthSq < 1e-18) continue;
                    if (Vec3.Dot(n, desired) < 0)
                    {
                        loop.Reverse();
                        n = -n;
                    }
                    caps.Add(new Facet(loop, n));
                }
            }
        }
        return caps;
    }

    static List<List<Vec3>> ClipToInside(List<Vec3> poly, List<Plane> planes)
    {
        var current = new List<List<Vec3>> { new List<Vec3>(poly) };
        for (int i = 0; i < planes.Count; i++)
        {
            var next = new List<List<Vec3>>();
            for (int p = 0; p < current.Count; p++)
            {
                List<List<Vec3>> inn;
                List<List<Vec3>> outp;
                List<(Vec3 a, Vec3 b)> cuts;
                bool coplanar;
                if (!SplitPolygon(current[p], planes[i], out inn, out outp, out cuts, out coplanar))
                    continue;
                if (coplanar)
                {
                    if (CentroidSign(current[p], planes[i]) <= Eps) next.Add(current[p]);
                    continue;
                }
                for (int k = 0; k < inn.Count; k++)
                    if (inn[k].Count >= 3) next.Add(inn[k]);
            }
            current = next;
        }
        return current;
    }

    static Vec3 V(double x, double y, double z) => new Vec3(x, y, z);

    static void AddWalls(List<Facet> faces, List<Vec3> bottom, List<Vec3> top)
    {
        int n = bottom.Count;
        if (n < 2 || top.Count != n) return;
        var shift = top[0] - bottom[0];
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            var a0 = bottom[i];
            var b0 = bottom[j];
            var quad = new List<Vec3> { a0, b0, top[j], top[i] };
            var desired = Vec3.Cross(b0 - a0, shift);
            if (desired.LengthSq < 1e-16) continue;
            if (Vec3.Dot(NewellRaw(quad), desired) < 0) quad.Reverse();
            faces.Add(new Facet(quad, desired.Normalized()));
        }
    }

    sealed class Piece
    {
        public List<Vec3> Poly;
        public bool Outside;
        public Piece(List<Vec3> poly, bool outside)
        {
            Poly = poly;
            Outside = outside;
        }
    }

    static Facet Clone(Facet f)
    {
        var c = new Facet(f.Outer, f.Normal);
        for (int i = 0; i < f.Holes.Count; i++) c.Holes.Add(new List<Vec3>(f.Holes[i]));
        return c;
    }

    static bool FaceHits(Facet f, IReadOnlyList<Plane> planes)
    {
        if (AllInside(f.Outer, planes) || AnyInside(f.Outer, planes)) return true;
        for (int i = 0; i < f.Outer.Count; i++)
        {
            var a = f.Outer[i];
            var b = f.Outer[(i + 1) % f.Outer.Count];
            for (int p = 0; p < planes.Count; p++)
            {
                double da = planes[p].Signed(a);
                double db = planes[p].Signed(b);
                if (da > Eps && db < -Eps || da < -Eps && db > Eps) return true;
            }
        }
        return false;
    }

    static bool AnyInside(IReadOnlyList<Vec3> poly, IReadOnlyList<Plane> planes)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            if (InsidePoint(poly[i], planes)) return true;
        }
        return false;
    }

    static bool AllInside(IReadOnlyList<Vec3> poly, IReadOnlyList<Plane> planes)
    {
        for (int i = 0; i < poly.Count; i++)
        {
            if (!InsidePoint(poly[i], planes)) return false;
        }
        return poly.Count > 0;
    }

    static bool InsidePoint(Vec3 p, IReadOnlyList<Plane> planes)
    {
        for (int i = 0; i < planes.Count; i++)
        {
            if (planes[i].Signed(p) > Eps) return false;
        }
        return true;
    }

    static double CentroidSign(IReadOnlyList<Vec3> poly, Plane plane)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            x += poly[i].X;
            y += poly[i].Y;
            z += poly[i].Z;
        }
        double n = poly.Count;
        return plane.Signed(new Vec3(x / n, y / n, z / n));
    }

    static int Sign(double d)
    {
        if (d > Eps) return 1;
        if (d < -Eps) return -1;
        return 0;
    }

    static Vec3 Lerp(Vec3 a, Vec3 b, double da, double db)
    {
        double t = da / (da - db);
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return a + (b - a) * t;
    }

    static bool SplitPolygon(
        IReadOnlyList<Vec3> poly,
        Plane plane,
        out List<List<Vec3>> inside,
        out List<List<Vec3>> outside,
        out List<(Vec3 a, Vec3 b)> cuts,
        out bool coplanar)
    {
        inside = new List<List<Vec3>>();
        outside = new List<List<Vec3>>();
        cuts = new List<(Vec3, Vec3)>();
        coplanar = false;
        int n = poly.Count;
        if (n < 3) return true;
        var d = new double[n];
        bool anyIn = false, anyOut = false;
        for (int i = 0; i < n; i++)
        {
            d[i] = plane.Signed(poly[i]);
            if (d[i] > Eps) anyOut = true;
            else if (d[i] < -Eps) anyIn = true;
        }
        if (!anyIn && !anyOut)
        {
            coplanar = true;
            return true;
        }
        if (!anyIn)
        {
            outside.Add(new List<Vec3>(poly));
            return true;
        }
        if (!anyOut)
        {
            inside.Add(new List<Vec3>(poly));
            return true;
        }

        var inEdges = new List<(Vec3 a, Vec3 b)>();
        var outEdges = new List<(Vec3 a, Vec3 b)>();
        var events = new List<(Vec3 p, bool leave)>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            int sa = Sign(d[i]);
            int sb = Sign(d[j]);
            var a = poly[i];
            var b = poly[j];
            if (sa < 0 && sb > 0)
            {
                var x = Lerp(a, b, d[i], d[j]);
                AddEdge(inEdges, a, x);
                AddEdge(outEdges, x, b);
                events.Add((x, true));
            }
            else if (sa > 0 && sb < 0)
            {
                var x = Lerp(a, b, d[i], d[j]);
                AddEdge(outEdges, a, x);
                AddEdge(inEdges, x, b);
                events.Add((x, false));
            }
            else
            {
                if (sa <= 0 && sb <= 0) AddEdge(inEdges, a, b);
                if (sa >= 0 && sb >= 0) AddEdge(outEdges, a, b);
                if (sa == 0 && sb > 0) events.Add((a, true));
                else if (sa == 0 && sb < 0) events.Add((a, false));
            }
        }

        int leaves = 0, enters = 0;
        for (int i = 0; i < events.Count; i++)
        {
            if (events[i].leave) leaves++;
            else enters++;
        }
        if (leaves != enters || leaves == 0)
            return false;

        for (int i = 0; i < events.Count; i++)
        {
            if (!events[i].leave) continue;
            int k = -1;
            for (int step = 1; step <= events.Count; step++)
            {
                int j = (i + step) % events.Count;
                if (!events[j].leave)
                {
                    k = j;
                    break;
                }
            }
            if (k < 0) return false;
            var leave = events[i].p;
            var enter = events[k].p;
            AddEdge(inEdges, leave, enter);
            AddEdge(outEdges, enter, leave);
            if ((leave - enter).LengthSq > Eps * Eps)
                cuts.Add((leave, enter));
        }

        inside = ChainLoops(inEdges);
        outside = ChainLoops(outEdges);
        return true;
    }

    static void AddEdge(List<(Vec3 a, Vec3 b)> edges, Vec3 a, Vec3 b)
    {
        if ((a - b).LengthSq <= Eps * Eps) return;
        edges.Add((a, b));
    }

    static List<List<Vec3>> ChainLoops(List<(Vec3 a, Vec3 b)> segs)
    {
        var loops = new List<List<Vec3>>();
        var unused = new List<(Vec3 a, Vec3 b)>(segs);
        while (unused.Count > 0)
        {
            var seg = unused[unused.Count - 1];
            unused.RemoveAt(unused.Count - 1);
            var loop = new List<Vec3> { seg.a, seg.b };
            bool closed = false;
            int guard = 0;
            while (guard++ < segs.Count + 2)
            {
                if (loop.Count >= 4 && (loop[loop.Count - 1] - loop[0]).LengthSq <= Eps * Eps)
                {
                    loop.RemoveAt(loop.Count - 1);
                    closed = true;
                    break;
                }
                int found = -1;
                var end = loop[loop.Count - 1];
                for (int i = 0; i < unused.Count; i++)
                {
                    if ((unused[i].a - end).LengthSq <= Eps * Eps)
                    {
                        found = i;
                        break;
                    }
                    if ((unused[i].b - end).LengthSq <= Eps * Eps)
                    {
                        unused[i] = (unused[i].b, unused[i].a);
                        found = i;
                        break;
                    }
                }
                if (found < 0) break;
                var next = unused[found];
                unused.RemoveAt(found);
                loop.Add(next.b);
            }
            if (closed && loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }
}
