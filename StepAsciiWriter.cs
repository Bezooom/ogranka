using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeklaToStep;

/// <summary>
/// ISO 10303-21 writer for planar solids.
/// Emits AP214 <c>AUTOMOTIVE_DESIGN</c> with <c>MANIFOLD_SOLID_BREP</c> /
/// <c>ADVANCED_FACE</c> / <c>EDGE_LOOP</c>. A literal <c>FACETED_BREP</c> cannot carry
/// <c>EDGE_CURVE</c> (ISO 10303-42 requires <c>POLY_LOOP</c>) and SolidWorks rejects it
/// inconsistently. The entity chain below is the one FreeCAD and SolidWorks both open.
/// Units are millimetres.
/// </summary>
public sealed class StepAsciiWriter
{
    public const double VertexToleranceMm = 1e-4;

    public List<string> Warnings { get; } = new List<string>();

    public void Write(string path, IReadOnlyList<SolidMesh> solids)
    {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            Write(writer, solids, Path.GetFileName(path));
    }

    public void Write(TextWriter writer, IReadOnlyList<SolidMesh> solids, string fileLabel)
    {
        if (writer == null) throw new ArgumentNullException(nameof(writer));
        if (solids == null) throw new ArgumentNullException(nameof(solids));
        Warnings.Clear();
        WriteHeader(writer, string.IsNullOrEmpty(fileLabel) ? "model.stp" : fileLabel);
        writer.WriteLine("DATA;");

        int id = 1;
        var points = new Dictionary<QuantKey, int>();
        var vertices = new Dictionary<int, int>();
        var closed = new List<int>();
        var openShells = new List<int>();

        for (int s = 0; s < solids.Count; s++)
        {
            var solid = solids[s];
            if (solid == null || solid.Faces.Count == 0) continue;
            var edges = new Dictionary<EdgeKey, EdgeRec>();
            var faceIds = WriteShellFaces(writer, ref id, solid.Faces, points, vertices, edges);
            if (faceIds.Count == 0)
            {
                Warnings.Add("Тело \"" + solid.Name + "\" не дало ни одной грани.");
                continue;
            }
            var voidIds = new List<int>();
            bool voidsClosed = true;
            for (int v = 0; v < solid.Voids.Count; v++)
            {
                var voidEdges = new Dictionary<EdgeKey, EdgeRec>();
                var voidFaces = WriteShellFaces(writer, ref id, solid.Voids[v], points, vertices, voidEdges);
                if (voidFaces.Count == 0) continue;
                if (!IsManifold(voidEdges))
                {
                    voidsClosed = false;
                    Warnings.Add("Полость тела \"" + solid.Name + "\" не замкнута и пропущена.");
                    continue;
                }
                int shell = Emit(writer, ref id, "CLOSED_SHELL", "''", IdList(voidFaces));
                int oriented = EmitRaw(writer, ref id, "ORIENTED_CLOSED_SHELL('',*,#" + shell + ",.F.)");
                voidIds.Add(oriented);
            }

            bool manifold = IsManifold(edges) && voidsClosed;
            int outer = Emit(writer, ref id, manifold ? "CLOSED_SHELL" : "OPEN_SHELL", "''", IdList(faceIds));
            if (!manifold)
            {
                Warnings.Add("Тело \"" + solid.Name + "\" записано как OPEN_SHELL: ребро не имеет ровно двух противоположных полурёбер.");
                openShells.Add(outer);
                continue;
            }
            string name = StepString(string.IsNullOrEmpty(solid.Name) ? "Solid" : solid.Name);
            int brep;
            if (voidIds.Count == 0)
                brep = Emit(writer, ref id, "MANIFOLD_SOLID_BREP", name, "#" + outer);
            else
                brep = Emit(writer, ref id, "BREP_WITH_VOIDS", name, "#" + outer, IdList(voidIds));
            closed.Add(brep);
        }

        WriteProduct(writer, ref id, closed, openShells, fileLabel);
        writer.WriteLine("ENDSEC;");
        writer.WriteLine("END-ISO-10303-21;");
    }

    List<int> WriteShellFaces(
        TextWriter writer,
        ref int id,
        IReadOnlyList<Facet> faces,
        Dictionary<QuantKey, int> points,
        Dictionary<int, int> vertices,
        Dictionary<EdgeKey, EdgeRec> edges)
    {
        var faceIds = new List<int>();
        for (int i = 0; i < faces.Count; i++)
        {
            int faceId = WriteFace(writer, ref id, faces[i], points, vertices, edges);
            if (faceId != 0) faceIds.Add(faceId);
        }
        return faceIds;
    }

    int WriteFace(
        TextWriter writer,
        ref int id,
        Facet facet,
        Dictionary<QuantKey, int> points,
        Dictionary<int, int> vertices,
        Dictionary<EdgeKey, EdgeRec> edges)
    {
        var outer = Clean(facet.Outer);
        if (outer.Count < 3) return 0;
        var newell = MeshOps.Newell(outer);
        var normal = facet.Normal.LengthSq < 1e-24 ? newell : facet.Normal.Normalized();
        if (normal.LengthSq < 1e-24) return 0;
        if (newell.LengthSq > 1e-24 && Vec3.Dot(newell, normal) < 0)
        {
            outer.Reverse();
            newell = -newell;
        }

        var holes = new List<List<Vec3>>();
        for (int h = 0; h < facet.Holes.Count; h++)
        {
            var hole = Clean(facet.Holes[h]);
            if (hole.Count < 3) continue;
            var hn = MeshOps.Newell(hole);
            if (hn.LengthSq > 1e-24 && Vec3.Dot(hn, normal) > 0)
                hole.Reverse();
            holes.Add(hole);
        }

        var outerEdges = WriteLoop(writer, ref id, outer, points, vertices, edges);
        if (outerEdges.Count < 3) return 0;
        int loop = Emit(writer, ref id, "EDGE_LOOP", "''", IdList(outerEdges));
        int outerBound = Emit(writer, ref id, "FACE_OUTER_BOUND", "''", "#" + loop, ".T.");
        var bounds = new List<int> { outerBound };
        for (int h = 0; h < holes.Count; h++)
        {
            var holeEdges = WriteLoop(writer, ref id, holes[h], points, vertices, edges);
            if (holeEdges.Count < 3) continue;
            int holeLoop = Emit(writer, ref id, "EDGE_LOOP", "''", IdList(holeEdges));
            bounds.Add(Emit(writer, ref id, "FACE_BOUND", "''", "#" + holeLoop, ".T."));
        }

        int originId = Cartesian(writer, ref id, outer[0], points);
        var hint = outer[1] - outer[0];
        var x = hint - normal * Vec3.Dot(hint, normal);
        if (x.LengthSq < 1e-24)
        {
            var alt = Math.Abs(normal.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            x = alt - normal * Vec3.Dot(alt, normal);
        }
        x = x.Normalized();
        int zId = Emit(writer, ref id, "DIRECTION", "''", Coord(normal));
        int xId = Emit(writer, ref id, "DIRECTION", "''", Coord(x));
        int axis = Emit(writer, ref id, "AXIS2_PLACEMENT_3D", "''", "#" + originId, "#" + zId, "#" + xId);
        int plane = Emit(writer, ref id, "PLANE", "''", "#" + axis);
        return Emit(writer, ref id, "ADVANCED_FACE", "''", IdList(bounds), "#" + plane, ".T.");
    }

    List<int> WriteLoop(
        TextWriter writer,
        ref int id,
        List<Vec3> loop,
        Dictionary<QuantKey, int> points,
        Dictionary<int, int> vertices,
        Dictionary<EdgeKey, EdgeRec> edges)
    {
        var ids = new List<int>();
        var vids = new int[loop.Count];
        for (int i = 0; i < loop.Count; i++)
            vids[i] = Vertex(writer, ref id, loop[i], points, vertices);
        for (int i = 0; i < loop.Count; i++)
        {
            int j = (i + 1) % loop.Count;
            if (vids[i] == vids[j]) continue;
            ids.Add(OrientedEdge(writer, ref id, vids[i], vids[j], loop[i], loop[j], points, edges));
        }
        return ids;
    }

    int OrientedEdge(
        TextWriter writer,
        ref int id,
        int v1,
        int v2,
        Vec3 p1,
        Vec3 p2,
        Dictionary<QuantKey, int> points,
        Dictionary<EdgeKey, EdgeRec> edges)
    {
        var key = new EdgeKey(v1, v2);
        EdgeRec rec;
        if (!edges.TryGetValue(key, out rec))
        {
            int start = v1 < v2 ? v1 : v2;
            Vec3 ps = v1 < v2 ? p1 : p2;
            Vec3 pe = v1 < v2 ? p2 : p1;
            int line = Line(writer, ref id, ps, pe, points);
            int curve = Emit(writer, ref id, "EDGE_CURVE", "''", "#" + start, "#" + (start == v1 ? v2 : v1), "#" + line, ".T.");
            rec = new EdgeRec(curve, start, start == v1 ? v2 : v1);
            edges[key] = rec;
        }
        bool forward = rec.V1 == v1 && rec.V2 == v2;
        if (forward) rec.Forward++;
        else rec.Backward++;
        rec.Uses++;
        edges[key] = rec;
        return EmitRaw(writer, ref id, "ORIENTED_EDGE('',*,*,#" + rec.Curve + "," + (forward ? ".T." : ".F.") + ")");
    }

    int Line(TextWriter writer, ref int id, Vec3 start, Vec3 end, Dictionary<QuantKey, int> points)
    {
        var dir = (end - start).Normalized();
        if (dir.LengthSq < 0.5) dir = new Vec3(1, 0, 0);
        int cp = Cartesian(writer, ref id, start, points);
        int d = Emit(writer, ref id, "DIRECTION", "''", Coord(dir));
        int vec = Emit(writer, ref id, "VECTOR", "''", "#" + d, "1.0");
        return Emit(writer, ref id, "LINE", "''", "#" + cp, "#" + vec);
    }

    int Cartesian(TextWriter writer, ref int id, Vec3 p, Dictionary<QuantKey, int> points)
    {
        var key = new QuantKey(p);
        int existing;
        if (points.TryGetValue(key, out existing)) return existing;
        int cid = Emit(writer, ref id, "CARTESIAN_POINT", "''", Coord(p));
        points[key] = cid;
        return cid;
    }

    int Vertex(TextWriter writer, ref int id, Vec3 p, Dictionary<QuantKey, int> points, Dictionary<int, int> vertices)
    {
        int cid = Cartesian(writer, ref id, p, points);
        int vid;
        if (vertices.TryGetValue(cid, out vid)) return vid;
        vid = Emit(writer, ref id, "VERTEX_POINT", "''", "#" + cid);
        vertices[cid] = vid;
        return vid;
    }

    static bool IsManifold(Dictionary<EdgeKey, EdgeRec> edges)
    {
        if (edges.Count == 0) return false;
        foreach (var pair in edges)
        {
            var rec = pair.Value;
            if (rec.Uses != 2 || rec.Forward != 1 || rec.Backward != 1) return false;
        }
        return true;
    }

    void WriteProduct(TextWriter writer, ref int id, List<int> closed, List<int> openShells, string fileLabel)
    {
        int app = Emit(writer, ref id, "APPLICATION_CONTEXT", "'core data for automotive mechanical design processes'");
        Emit(writer, ref id, "APPLICATION_PROTOCOL_DEFINITION", "'committee draft'", "'automotive_design'", "1997", "#" + app);
        int mech = Emit(writer, ref id, "MECHANICAL_CONTEXT", "''", "#" + app, "'mechanical'");
        string productName = StepString(string.IsNullOrEmpty(fileLabel) ? "TeklaExport" : Path.GetFileNameWithoutExtension(fileLabel));
        int product = Emit(writer, ref id, "PRODUCT", productName, productName, "''", "(#" + mech + ")");
        int formation = Emit(writer, ref id, "PRODUCT_DEFINITION_FORMATION", "''", "''", "#" + product);
        int pdContext = Emit(writer, ref id, "PRODUCT_DEFINITION_CONTEXT", "'part definition'", "#" + app, "'design'");
        int definition = Emit(writer, ref id, "PRODUCT_DEFINITION", "'design'", "''", "#" + formation, "#" + pdContext);
        int shape = Emit(writer, ref id, "PRODUCT_DEFINITION_SHAPE", "''", "''", "#" + definition);

        int length = EmitRaw(writer, ref id, "( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) )");
        int angle = EmitRaw(writer, ref id, "( NAMED_UNIT(*) PLANE_ANGLE_UNIT() SI_UNIT($,.RADIAN.) )");
        int solid = EmitRaw(writer, ref id, "( NAMED_UNIT(*) SOLID_ANGLE_UNIT() SI_UNIT($,.STERADIAN.) )");
        int uncertainty = Emit(
            writer, ref id, "UNCERTAINTY_MEASURE_WITH_UNIT",
            "LENGTH_MEASURE(0.0001)", "#" + length, "'distance_accuracy_value'", "'confusion accuracy'");
        int context = EmitRaw(
            writer,
            ref id,
            "( GEOMETRIC_REPRESENTATION_CONTEXT(3) GLOBAL_UNCERTAINTY_ASSIGNED_CONTEXT((#" + uncertainty +
            ")) GLOBAL_UNIT_ASSIGNED_CONTEXT((#" + length + ",#" + angle + ",#" + solid +
            ")) REPRESENTATION_CONTEXT('','3D') )");

        int origin = Emit(writer, ref id, "CARTESIAN_POINT", "''", "(0.0,0.0,0.0)");
        int z = Emit(writer, ref id, "DIRECTION", "''", "(0.0,0.0,1.0)");
        int x = Emit(writer, ref id, "DIRECTION", "''", "(1.0,0.0,0.0)");
        int placement = Emit(writer, ref id, "AXIS2_PLACEMENT_3D", "''", "#" + origin, "#" + z, "#" + x);

        int advanced = 0;
        if (closed.Count > 0)
        {
            var items = new List<int> { placement };
            items.AddRange(closed);
            advanced = Emit(writer, ref id, "ADVANCED_BREP_SHAPE_REPRESENTATION", "''", IdList(items), "#" + context);
        }
        int surface = 0;
        if (openShells.Count > 0)
        {
            int model = Emit(writer, ref id, "SHELL_BASED_SURFACE_MODEL", "''", IdList(openShells));
            surface = Emit(
                writer, ref id, "MANIFOLD_SURFACE_SHAPE_REPRESENTATION", "''",
                "(#" + placement + ",#" + model + ")", "#" + context);
        }
        int primary = advanced != 0 ? advanced : surface;
        if (primary == 0)
            primary = Emit(writer, ref id, "ADVANCED_BREP_SHAPE_REPRESENTATION", "''", "(#" + placement + ")", "#" + context);
        Emit(writer, ref id, "SHAPE_DEFINITION_REPRESENTATION", "#" + shape, "#" + primary);
        if (advanced != 0 && surface != 0)
            Emit(writer, ref id, "SHAPE_REPRESENTATION_RELATIONSHIP", "''", "''", "#" + advanced, "#" + surface);
    }

    static void WriteHeader(TextWriter writer, string fileLabel)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        writer.WriteLine("ISO-10303-21;");
        writer.WriteLine("HEADER;");
        writer.WriteLine("FILE_DESCRIPTION(('Tekla geometry as AP214 planar manifold solid'),'2;1');");
        writer.WriteLine(
            "FILE_NAME(" + StepString(fileLabel) + ",'" + stamp +
            "',('TeklaToStep'),('TeklaToStep'),'TeklaToStep STEP writer','TeklaToStep','');");
        writer.WriteLine("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));");
        writer.WriteLine("ENDSEC;");
    }

    static int Emit(TextWriter writer, ref int id, string type, params string[] args)
    {
        int mine = id++;
        writer.Write("#");
        writer.Write(mine);
        writer.Write("=");
        writer.Write(type);
        writer.Write("(");
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) writer.Write(",");
            writer.Write(args[i]);
        }
        writer.WriteLine(");");
        return mine;
    }

    static int EmitRaw(TextWriter writer, ref int id, string body)
    {
        int mine = id++;
        writer.Write("#");
        writer.Write(mine);
        writer.Write("=");
        writer.Write(body);
        writer.WriteLine(";");
        return mine;
    }

    static string IdList(IReadOnlyList<int> ids)
    {
        var sb = new StringBuilder();
        sb.Append("(");
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("#");
            sb.Append(ids[i].ToString(CultureInfo.InvariantCulture));
        }
        sb.Append(")");
        return sb.ToString();
    }

    static string Coord(Vec3 p) => "(" + Fmt(p.X) + "," + Fmt(p.Y) + "," + Fmt(p.Z) + ")";

    public static string Fmt(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v))
            throw new InvalidDataException("Некорректная координата STEP.");
        if (Math.Abs(v) < 1e-12) v = 0;
        return v.ToString("0.0######", CultureInfo.InvariantCulture);
    }

    public static string StepString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "''";
        var sb = new StringBuilder();
        sb.Append("'");
        int n = 0;
        foreach (char c in value)
        {
            if (n >= 200) break;
            if (c == '\'')
            {
                sb.Append("''");
                n++;
            }
            else if (c >= 32 && c < 127)
            {
                sb.Append(c);
                n++;
            }
            else
            {
                sb.Append("_");
                n++;
            }
        }
        sb.Append("'");
        return sb.ToString();
    }

    static List<Vec3> Clean(IReadOnlyList<Vec3> pts)
    {
        var result = new List<Vec3>(pts.Count);
        double tol2 = VertexToleranceMm * VertexToleranceMm;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z) ||
                double.IsInfinity(p.X) || double.IsInfinity(p.Y) || double.IsInfinity(p.Z))
                throw new InvalidDataException("NaN или бесконечная координата.");
            if (result.Count == 0 || (p - result[result.Count - 1]).LengthSq > tol2)
                result.Add(p);
        }
        if (result.Count > 1 && (result[0] - result[result.Count - 1]).LengthSq <= tol2)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    readonly struct QuantKey : IEquatable<QuantKey>
    {
        public readonly long X;
        public readonly long Y;
        public readonly long Z;

        public QuantKey(Vec3 p)
        {
            X = (long)Math.Round(p.X / VertexToleranceMm, MidpointRounding.AwayFromZero);
            Y = (long)Math.Round(p.Y / VertexToleranceMm, MidpointRounding.AwayFromZero);
            Z = (long)Math.Round(p.Z / VertexToleranceMm, MidpointRounding.AwayFromZero);
        }

        public bool Equals(QuantKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is QuantKey k && Equals(k);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)(X ^ (X >> 32));
                h = h * 31 + (int)(Y ^ (Y >> 32));
                h = h * 31 + (int)(Z ^ (Z >> 32));
                return h;
            }
        }
    }

    readonly struct EdgeKey : IEquatable<EdgeKey>
    {
        public readonly int Lo;
        public readonly int Hi;
        public EdgeKey(int a, int b)
        {
            if (a < b) { Lo = a; Hi = b; }
            else { Lo = b; Hi = a; }
        }
        public bool Equals(EdgeKey other) => Lo == other.Lo && Hi == other.Hi;
        public override bool Equals(object? obj) => obj is EdgeKey k && Equals(k);
        public override int GetHashCode() => Lo * 397 ^ Hi;
    }

    struct EdgeRec
    {
        public int Curve;
        public int V1;
        public int V2;
        public int Uses;
        public int Forward;
        public int Backward;
        public EdgeRec(int curve, int v1, int v2)
        {
            Curve = curve;
            V1 = v1;
            V2 = v2;
            Uses = 0;
            Forward = 0;
            Backward = 0;
        }
    }
}
