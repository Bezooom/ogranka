using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TeklaToStep;

/// <summary>
/// Offline reader for Tekla IFC2x3 / IFC4. Resolves placements, faceted breps,
/// extrusions, mapped items, triangulated facesets and half-space clipping.
/// Coordinates are converted to millimetres.
/// </summary>
public static class TeklaIfcAsciiParser
{
    public static IReadOnlyList<SolidMesh> ParseFile(string path, IList<string>? warnings = null)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
        return ParseText(File.ReadAllText(path), warnings);
    }

    public static IReadOnlyList<SolidMesh> ParseText(string text, IList<string>? warnings = null)
    {
        if (text == null) throw new ArgumentNullException(nameof(text));
        var list = warnings ?? new List<string>();
        var file = P21File.Parse(text);
        if (file.Entities.Count == 0)
            throw new InvalidDataException("В IFC нет секции DATA.");
        return new Converter(file, list).Convert();
    }

    sealed class Converter
    {
        readonly P21File _file;
        readonly IList<string> _warnings;
        readonly double _scale;
        readonly Dictionary<int, List<int>> _voids = new Dictionary<int, List<int>>();
        bool _filletWarned;

        public Converter(P21File file, IList<string> warnings)
        {
            _file = file;
            _warnings = warnings;
            _scale = ReadScale();
        }

        public List<SolidMesh> Convert()
        {
            IndexVoids();
            var result = new List<SolidMesh>();
            foreach (var entity in _file.Entities.Values)
            {
                if (SkipProduct(entity.Type)) continue;
                P21Entity? shape = FindArgType(entity, "IFCPRODUCTDEFINITIONSHAPE");
                if (shape == null) continue;
                var items = BodyItems(shape, out bool baked);
                if (items.Count == 0) continue;
                var world = PlacementOf(entity);
                var faces = new List<Facet>();
                var voids = new List<List<Facet>>();
                foreach (var item in items)
                    ConvertItem(item, world, faces, voids);
                if (!baked && _voids.ContainsKey(entity.Id))
                    ApplyOpenings(entity.Id, faces);
                if (faces.Count == 0)
                {
                    Warn("У \"" + NameOf(entity) + "\" нет разобранной геометрии.");
                    continue;
                }
                var mesh = new SolidMesh(NameOf(entity));
                mesh.Faces = faces;
                mesh.Voids = voids;
                result.Add(mesh);
            }
            if (result.Count == 0)
                Warn("IFC не содержит тел, которые парсер умеет читать.");
            return result;
        }

        void ApplyOpenings(int elementId, List<Facet> faces)
        {
            List<int> openings;
            if (!_voids.TryGetValue(elementId, out openings)) return;
            for (int i = 0; i < openings.Count; i++)
            {
                var opening = _file.Get(openings[i]);
                if (opening == null) continue;
                var shape = FindArgType(opening, "IFCPRODUCTDEFINITIONSHAPE");
                if (shape == null) continue;
                var items = BodyItems(shape, out _);
                var place = PlacementOf(opening);
                for (int k = 0; k < items.Count; k++)
                {
                    string why;
                    List<Facet> cut;
                    if (!TrySubtractItem(items[k], place, faces, out cut, out why))
                    {
                        Warn("Проём #" + openings[i] + " не вычтен: " + why);
                        continue;
                    }
                    faces.Clear();
                    faces.AddRange(cut);
                }
            }
        }

        void ConvertItem(P21Entity item, Mat4 world, List<Facet> dest, List<List<Facet>> voids)
        {
            switch (item.Type)
            {
                case "IFCFACETEDBREP":
                case "IFCADVANCEDBREP":
                case "IFCMANIFOLDSOLIDBREP":
                    AddShellRef(Arg(item, 0), world, dest);
                    break;
                case "IFCFACETEDBREPWITHVOIDS":
                case "IFCADVANCEDBREPWITHVOIDS":
                    AddShellRef(Arg(item, 0), world, dest);
                    AddVoidShells(item, world, voids);
                    break;
                case "IFCEXTRUDEDAREASOLID":
                case "IFCEXTRUDEDAREASOLIDTAPERED":
                    if (item.Type == "IFCEXTRUDEDAREASOLIDTAPERED")
                        Warn("IFCEXTRUDEDAREASOLIDTAPERED аппроксимирован призмой (#" + item.Id + ").");
                    ConvertExtrusion(item, world, dest);
                    break;
                case "IFCREVOLVEDAREASOLID":
                    ConvertRevolved(item, world, dest);
                    break;
                case "IFCSWEPTDISKSOLID":
                    ConvertSweptDisk(item, world, dest);
                    break;
                case "IFCMAPPEDITEM":
                    ConvertMapped(item, world, dest, voids);
                    break;
                case "IFCBOOLEANCLIPPINGRESULT":
                case "IFCBOOLEANRESULT":
                    ConvertBoolean(item, world, dest, voids);
                    break;
                case "IFCTRIANGULATEDFACESET":
                case "IFCTRIANGULATEDIRREGULARNETWORK":
                case "IFCPOLYGONALFACESET":
                    ConvertTessellation(item, world, dest);
                    break;
                case "IFCSHELLBASEDSURFACEMODEL":
                case "IFCFACEBASEDSURFACEMODEL":
                case "IFCCONNECTEDFACESET":
                case "IFCCLOSEDSHELL":
                case "IFCOPENSHELL":
                    AddShell(item, world, dest);
                    break;
                case "IFCBOUNDINGBOX":
                    break;
                default:
                    Warn("Пропуск геометрии " + item.Type + " (#" + item.Id + ").");
                    break;
            }
        }

        void ConvertBoolean(P21Entity item, Mat4 world, List<Facet> dest, List<List<Facet>> voids)
        {
            string op = EnumOf(Arg(item, 0)) ?? string.Empty;
            var first = Resolve(Arg(item, 1));
            var second = Resolve(Arg(item, 2));
            if (op != "DIFFERENCE")
            {
                Warn("Булева " + op + " не сливается, берётся первый операнд #" + item.Id + ".");
                if (first != null) ConvertItem(first, world, dest, voids);
                return;
            }
            var subject = new List<Facet>();
            var ignored = new List<List<Facet>>();
            if (first != null) ConvertItem(first, world, subject, ignored);
            if (subject.Count == 0) return;
            string why = string.Empty;
            List<Facet> cut;
            if (second == null || !TrySubtractItem(second, world, subject, out cut, out why))
            {
                Warn((why.Length == 0 ? "клиппинг не разобран" : why) + " (#" + item.Id + "). Оставлен первый операнд.");
                dest.AddRange(subject);
                return;
            }
            dest.AddRange(cut);
        }

        bool TryTool(P21Entity tool, Mat4 world, out List<Plane> planes, out string why)
        {
            planes = new List<Plane>();
            why = string.Empty;
            if (tool.Type == "IFCHALFSPACESOLID" || tool.Type == "IFCPOLYGONALBOUNDEDHALFSPACE" || tool.Type == "IFCBOXEDHALFSPACE")
                return HalfSpacePlanes(tool, world, planes, out why);
            if (tool.Type == "IFCEXTRUDEDAREASOLID" || tool.Type == "IFCEXTRUDEDAREASOLIDTAPERED")
            {
                var faces = new List<Facet>();
                ConvertExtrusion(tool, world, faces);
                return TryToolFromFaces(faces, out planes, out why);
            }
            if (tool.Type == "IFCFACETEDBREP" || tool.Type == "IFCADVANCEDBREP" || tool.Type == "IFCMANIFOLDSOLIDBREP")
            {
                var faces = new List<Facet>();
                AddShellRef(Arg(tool, 0), world, faces);
                return TryToolFromFaces(faces, out planes, out why);
            }
            if (tool.Type == "IFCBOOLEANCLIPPINGRESULT" || tool.Type == "IFCBOOLEANRESULT")
            {
                why = "вложенный булев инструмент не выпуклый";
                return false;
            }
            why = "инструмент " + tool.Type + " не поддержан";
            return false;
        }

        bool TryToolFromFaces(List<Facet> faces, out List<Plane> planes, out string why)
        {
            planes = new List<Plane>();
            why = string.Empty;
            if (faces.Count < 4)
            {
                why = "у инструмента меньше 4 граней";
                return false;
            }
            var verts = new List<Vec3>();
            for (int i = 0; i < faces.Count; i++)
            {
                if (faces[i].Holes.Count > 0)
                {
                    why = "инструмент с отверстиями не выпуклый";
                    return false;
                }
                for (int k = 0; k < faces[i].Outer.Count; k++) verts.Add(faces[i].Outer[k]);
            }
            for (int i = 0; i < faces.Count; i++)
            {
                var n = faces[i].Normal.LengthSq > 1e-18 ? faces[i].Normal.Normalized() : MeshOps.Newell(faces[i].Outer);
                if (n.LengthSq < 1e-18)
                {
                    why = "вырожденная грань инструмента";
                    return false;
                }
                var origin = faces[i].Outer[0];
                for (int v = 0; v < verts.Count; v++)
                {
                    if (Vec3.Dot(verts[v] - origin, n) > MeshOps.Eps * 10)
                    {
                        why = "невыпуклый инструмент булевой операции";
                        return false;
                    }
                }
                planes.Add(new Plane(origin, n));
            }
            return true;
        }

        bool HalfSpacePlanes(P21Entity hs, Mat4 world, List<Plane> planes, out string why)
        {
            why = string.Empty;
            var surface = Resolve(Arg(hs, 0));
            if (surface == null || surface.Type != "IFCPLANE")
            {
                why = "HalfSpace без IfcPlane";
                return false;
            }
            bool agreement = BoolOf(Arg(hs, 1), true);
            var pos = Resolve(Arg(surface, 0));
            var local = pos != null ? Axis3(pos) : Mat4.Identity;
            var origin = world.TransformPoint(local.TransformPoint(new Vec3(0, 0, 0)));
            var normal = world.TransformVector(local.TransformVector(new Vec3(0, 0, 1))).Normalized();
            planes.Add(new Plane(origin, agreement ? normal : -normal));
            if (hs.Type != "IFCPOLYGONALBOUNDEDHALFSPACE") return true;

            var prismPos = Resolve(Arg(hs, 2));
            var boundary = Resolve(Arg(hs, 3));
            var pts = boundary == null ? null : CurvePoints(boundary);
            if (pts == null || pts.Count < 3)
            {
                why = "PolygonalBoundedHalfSpace без контура";
                return false;
            }
            var pm = prismPos != null ? Axis3(prismPos) : Mat4.Identity;
            var localPts = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                localPts.Add(new Vec3(pts[i].X, pts[i].Y, 0));
            if (MeshOps.SignedAreaXY(localPts) < 0) localPts.Reverse();
            if (!MeshOps.IsConvexXY(localPts))
            {
                why = "вогнутый PolygonalBoundedHalfSpace";
                return false;
            }
            var worldPts = new List<Vec3>(localPts.Count);
            for (int i = 0; i < localPts.Count; i++)
                worldPts.Add(world.TransformPoint(pm.TransformPoint(localPts[i])));
            var z = world.TransformVector(pm.TransformVector(new Vec3(0, 0, 1))).Normalized();
            int n = worldPts.Count;
            if (n > 1 && (worldPts[0] - worldPts[n - 1]).LengthSq <= MeshOps.Eps * MeshOps.Eps)
            {
                worldPts.RemoveAt(n - 1);
                n--;
            }
            for (int i = 0; i < n; i++)
            {
                var a = worldPts[i];
                var b = worldPts[(i + 1) % n];
                var outward = Vec3.Cross(b - a, z);
                if (outward.LengthSq < 1e-16) continue;
                planes.Add(new Plane(a, outward));
            }
            return true;
        }

        void ConvertExtrusion(P21Entity item, Mat4 world, List<Facet> dest)
        {
            var area = Resolve(Arg(item, 0));
            var posEnt = Resolve(Arg(item, 1));
            var dir = DirOf(Arg(item, 2)) ?? new Vec3(0, 0, 1);
            double depth = LengthOf(Arg(item, 3));
            if (area == null || depth == 0)
            {
                Warn("Пустое выдавливание #" + item.Id + ".");
                return;
            }
            List<Vec3> outer;
            List<List<Vec3>> holes;
            string why;
            if (!TryProfile(area, out outer, out holes, out why))
            {
                Warn(why + " #" + item.Id + ".");
                return;
            }
            var position = world.Multiply(posEnt != null ? Axis3(posEnt) : Mat4.Identity);
            dest.AddRange(MeshOps.Extrude(outer, holes, position, dir, depth));
        }

        void ConvertRevolved(P21Entity item, Mat4 world, List<Facet> dest)
        {
            var area = Resolve(Arg(item, 0));
            var posEnt = Resolve(Arg(item, 1));
            var axisEnt = Resolve(Arg(item, 2));
            double angle = NumOf(Arg(item, 3), 0);
            if (area == null || Math.Abs(angle) < 1e-9)
            {
                Warn("Пустое вращение #" + item.Id + ".");
                return;
            }
            List<Vec3> outer;
            List<List<Vec3>> holes;
            string why;
            if (!TryProfile(area, out outer, out holes, out why))
            {
                Warn(why + " #" + item.Id + ".");
                return;
            }
            var origin = new Vec3(0, 0, 0);
            var axis = new Vec3(0, 0, 1);
            if (axisEnt != null)
            {
                var loc = Point3(Arg(axisEnt, 0));
                if (loc != null) origin = loc.Value;
                var ax = DirOf(Arg(axisEnt, 1));
                if (ax != null) axis = ax.Value;
            }
            var local = ProfileShapes.Revolve(outer, holes, origin, axis, angle, 32);
            var position = world.Multiply(posEnt != null ? Axis3(posEnt) : Mat4.Identity);
            dest.AddRange(ProfileShapes.Transform(local, position));
        }

        void ConvertSweptDisk(P21Entity item, Mat4 world, List<Facet> dest)
        {
            var curve = Resolve(Arg(item, 0));
            double radius = LengthOf(Arg(item, 1));
            double inner = LengthOf(Arg(item, 2));
            if (curve == null || radius <= 0)
            {
                Warn("Пустой IfcSweptDiskSolid #" + item.Id + ".");
                return;
            }
            var local = CurvePoints(curve);
            if (local == null || local.Count < 2)
            {
                Warn("Направляющая IfcSweptDiskSolid не разобрана #" + item.Id + ".");
                return;
            }
            var path = new List<Vec3>(local.Count);
            for (int i = 0; i < local.Count; i++) path.Add(world.TransformPoint(local[i]));
            dest.AddRange(ProfileShapes.SweptDisk(path, radius, inner, 32));
        }

        bool TrySubtractItem(P21Entity item, Mat4 world, List<Facet> subject, out List<Facet> cut, out string why)
        {
            cut = subject;
            why = string.Empty;
            if (item.Type == "IFCEXTRUDEDAREASOLID" || item.Type == "IFCEXTRUDEDAREASOLIDTAPERED")
                return TrySubtractExtrusion(item, world, subject, out cut, out why);
            List<Plane> planes;
            if (TryTool(item, world, out planes, out why) && MeshOps.TrySubtract(subject, planes, out cut, out why))
                return true;
            if (item.Type == "IFCFACETEDBREP" || item.Type == "IFCADVANCEDBREP" || item.Type == "IFCMANIFOLDSOLIDBREP")
                why = "невыпуклый BREP-инструмент не вычитается";
            return false;
        }

        bool TrySubtractExtrusion(P21Entity item, Mat4 world, List<Facet> subject, out List<Facet> cut, out string why)
        {
            cut = subject;
            why = string.Empty;
            var area = Resolve(Arg(item, 0));
            var posEnt = Resolve(Arg(item, 1));
            var dir = DirOf(Arg(item, 2)) ?? new Vec3(0, 0, 1);
            double depth = LengthOf(Arg(item, 3));
            if (area == null || Math.Abs(depth) < 1e-9)
            {
                why = "пустое выдавливание";
                return false;
            }
            List<Vec3> outer;
            List<List<Vec3>> holes;
            if (!TryProfile(area, out outer, out holes, out why)) return false;
            var position = world.Multiply(posEnt != null ? Axis3(posEnt) : Mat4.Identity);
            if (MeshOps.IsConvexXY(outer))
            {
                var faces = MeshOps.Extrude(outer, holes, position, dir, depth);
                return TrySubtractFaces(faces, subject, out cut, out why);
            }
            return TrySubtractConcaveExtrusion(outer, holes, position, dir, depth, subject, out cut, out why);
        }

        /// <summary>
        /// Concave profile cut in one pass: the profile's edge planes partition the subject,
        /// and the exact point-in-profile test decides which fragments die. Caps are not built,
        /// so the tool must pass clean through the subject.
        /// </summary>
        bool TrySubtractConcaveExtrusion(
            List<Vec3> outer,
            List<List<Vec3>> holes,
            Mat4 position,
            Vec3 dir,
            double depth,
            List<Facet> subject,
            out List<Facet> cut,
            out string why)
        {
            cut = subject;
            why = string.Empty;
            if (holes.Count > 0)
            {
                why = "вогнутый инструмент с отверстиями не вычитается";
                return false;
            }
            var axis = dir.LengthSq < 1e-18 ? new Vec3(0, 0, 1) : dir.Normalized();
            if (Math.Abs(axis.Z) < 0.99)
            {
                why = "вогнутый вырез с осью не по нормали профиля не поддержан";
                return false;
            }
            Mat4 inverse;
            if (!position.TryInverse(out inverse))
            {
                why = "обратная матрица инструмента не построена";
                return false;
            }
            double lo = Math.Min(0, depth);
            double hi = Math.Max(0, depth);
            var planes = new List<Plane>(outer.Count);
            var wallBoxes = new List<IReadOnlyList<Plane>>(outer.Count);
            for (int i = 0; i < outer.Count; i++)
            {
                var a = outer[i];
                var b = outer[(i + 1) % outer.Count];
                var edge = b - a;
                var outward = new Vec3(edge.Y, -edge.X, 0);
                if (outward.LengthSq < 1e-16) continue;
                var mid = new Vec3((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, 0);
                planes.Add(new Plane(position.TransformPoint(mid), position.TransformVector(outward.Normalized())));
                var along = position.TransformVector(edge.Normalized());
                wallBoxes.Add(new List<Plane>
                {
                    new Plane(position.TransformPoint(a), -along),
                    new Plane(position.TransformPoint(b), along),
                    new Plane(position.TransformPoint(a + axis * lo), -position.TransformVector(axis)),
                    new Plane(position.TransformPoint(a + axis * hi), position.TransformVector(axis))
                });
            }
            for (int f = 0; f < subject.Count; f++)
            {
                for (int k = 0; k < subject[f].Outer.Count; k++)
                {
                    var local = inverse.TransformPoint(subject[f].Outer[k]);
                    double t = Vec3.Dot(local, axis);
                    if (t <= lo + 1e-6 || t >= hi - 1e-6)
                    {
                        why = "вогнутый вырез не прорезает тело насквозь";
                        return false;
                    }
                }
            }
            Func<Vec3, bool> insideTool = p =>
            {
                var local = inverse.TransformPoint(p);
                double t = Vec3.Dot(local, axis);
                if (t < lo || t > hi) return false;
                var flat = local - axis * t;
                return MeshOps.PointInPolygonXY(flat, outer);
            };
            return MeshOps.TrySubtractInterior(subject, planes, insideTool, wallBoxes, out cut, out why);
        }

        bool TrySubtractFaces(List<Facet> toolFaces, List<Facet> subject, out List<Facet> cut, out string why)
        {
            List<Plane> planes;
            if (!TryToolFromFaces(toolFaces, out planes, out why))
            {
                cut = subject;
                return false;
            }
            return MeshOps.TrySubtract(subject, planes, out cut, out why);
        }

        void ConvertMapped(P21Entity item, Mat4 world, List<Facet> dest, List<List<Facet>> voids)
        {
            var map = Resolve(Arg(item, 0));
            var target = Resolve(Arg(item, 1));
            if (map == null || map.Type != "IFCREPRESENTATIONMAP")
            {
                Warn("IfcMappedItem без IfcRepresentationMap #" + item.Id + ".");
                return;
            }
            var originEnt = Resolve(Arg(map, 0));
            var mappedRep = Resolve(Arg(map, 1));
            var origin = originEnt != null && originEnt.Type == "IFCAXIS2PLACEMENT3D" ? Axis3(originEnt) : Mat4.Identity;
            Mat4 inv;
            if (!origin.TryInverse(out inv))
            {
                Warn("Не удалось инвертировать MappingOrigin #" + map.Id + ".");
                inv = Mat4.Identity;
            }
            var op = target != null ? Operator3D(target) : Mat4.Identity;
            var xform = world.Multiply(op).Multiply(inv);
            if (mappedRep == null) return;
            var items = LastList(mappedRep);
            for (int i = 0; i < items.Count; i++)
            {
                var g = Resolve(items[i]);
                if (g != null) ConvertItem(g, xform, dest, voids);
            }
        }

        void ConvertTessellation(P21Entity item, Mat4 world, List<Facet> dest)
        {
            P21Entity? coords = null;
            for (int i = 0; i < item.Args.Count; i++)
            {
                var e = Resolve(item.Args[i]);
                if (e != null && (e.Type == "IFCCARTESIANPOINTLIST3D" || e.Type == "IFCCARTESIANPOINTLIST2D"))
                    coords = e;
            }
            if (coords == null)
            {
                Warn("Tessellation без списка точек #" + item.Id + ".");
                return;
            }
            var pts = PointList(coords);
            if (item.Type == "IFCPOLYGONALFACESET")
            {
                for (int i = 0; i < item.Args.Count; i++)
                {
                    var list = item.Args[i].AsList();
                    if (list == null) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var face = Resolve(list[k]);
                        if (face == null) continue;
                        if (face.Type != "IFCINDEXEDPOLYGONALFACE" && face.Type != "IFCINDEXEDPOLYGONALFACEWITHVOIDS")
                            continue;
                        var poly = Indices(AsList(Arg(face, 0)), pts, world);
                        if (poly.Count < 3) continue;
                        var facet = new Facet(poly, MeshOps.Newell(poly));
                        if (face.Type == "IFCINDEXEDPOLYGONALFACEWITHVOIDS")
                        {
                            var holes = AsList(Arg(face, 1));
                            for (int h = 0; h < holes.Count; h++)
                            {
                                var hp = Indices(holes[h].AsList(), pts, world);
                                if (hp.Count >= 3) facet.Holes.Add(hp);
                            }
                        }
                        dest.Add(facet);
                    }
                }
                return;
            }
            var triples = FindTriples(item);
            for (int i = 0; i < triples.Count; i++)
            {
                var tri = triples[i];
                int i0 = (int)NumAt(tri, 0) - 1;
                int i1 = (int)NumAt(tri, 1) - 1;
                int i2 = (int)NumAt(tri, 2) - 1;
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= pts.Count || i1 >= pts.Count || i2 >= pts.Count)
                    continue;
                var a = world.TransformPoint(pts[i0]);
                var b = world.TransformPoint(pts[i1]);
                var c = world.TransformPoint(pts[i2]);
                dest.Add(new Facet(new[] { a, b, c }, MeshOps.Newell(new[] { a, b, c })));
            }
        }

        void AddVoidShells(P21Entity item, Mat4 world, List<List<Facet>> voids)
        {
            var list = AsList(Arg(item, 1));
            for (int i = 0; i < list.Count; i++)
            {
                var shell = Resolve(list[i]);
                if (shell == null) continue;
                var faces = new List<Facet>();
                AddShell(shell, world, faces);
                if (faces.Count > 0) voids.Add(faces);
            }
        }

        void AddShellRef(P21Value? value, Mat4 world, List<Facet> dest)
        {
            var shell = Resolve(value);
            if (shell != null) AddShell(shell, world, dest);
        }

        void AddShell(P21Entity shell, Mat4 world, List<Facet> dest)
        {
            if (shell.Type == "IFCSHELLBASEDSURFACEMODEL" || shell.Type == "IFCFACEBASEDSURFACEMODEL")
            {
                var kids = LastList(shell);
                for (int i = 0; i < kids.Count; i++)
                {
                    var child = Resolve(kids[i]);
                    if (child != null) AddShell(child, world, dest);
                }
                return;
            }
            var faces = AsList(Arg(shell, 0));
            if (faces.Count == 0) faces = LastList(shell);
            for (int i = 0; i < faces.Count; i++)
            {
                var face = Resolve(faces[i]);
                if (face == null) continue;
                if (face.Type == "IFCCLOSEDSHELL" || face.Type == "IFCOPENSHELL" || face.Type == "IFCCONNECTEDFACESET")
                {
                    AddShell(face, world, dest);
                    continue;
                }
                var facet = ConvertFace(face, world);
                if (facet != null) dest.Add(facet);
            }
        }

        Facet? ConvertFace(P21Entity face, Mat4 world)
        {
            var bounds = AsList(Arg(face, 0));
            List<Vec3>? outer = null;
            var holes = new List<List<Vec3>>();
            for (int i = 0; i < bounds.Count; i++)
            {
                var bound = Resolve(bounds[i]);
                if (bound == null) continue;
                bool ori = BoolOf(Arg(bound, 1), true);
                var loop = Resolve(Arg(bound, 0));
                var pts = LoopPoints(loop, world);
                if (pts == null || pts.Count < 3) continue;
                if (!ori) pts.Reverse();
                if (bound.Type == "IFCFACEOUTERBOUND" || outer == null)
                {
                    if (outer != null) holes.Add(outer);
                    outer = pts;
                }
                else holes.Add(pts);
            }
            if (outer == null) return null;
            if (face.Type == "IFCADVANCEDFACE" && !BoolOf(Arg(face, 2), true))
                outer.Reverse();
            var facet = new Facet(outer, MeshOps.Newell(outer));
            facet.Holes.AddRange(holes);
            return facet;
        }

        List<Vec3>? LoopPoints(P21Entity? loop, Mat4 world)
        {
            if (loop == null) return null;
            if (loop.Type == "IFCPOLYLOOP")
            {
                var pts = new List<Vec3>();
                var list = AsList(Arg(loop, 0));
                for (int i = 0; i < list.Count; i++)
                {
                    var p = Point3(list[i]);
                    if (p != null) pts.Add(world.TransformPoint(p.Value));
                }
                return pts;
            }
            if (loop.Type == "IFCEDGELOOP")
            {
                Warn("IfcEdgeLoop не разворачивается в точки (#" + loop.Id + ").");
                return null;
            }
            return null;
        }

        bool TryProfile(P21Entity profile, out List<Vec3> outer, out List<List<Vec3>> holes, out string why)
        {
            outer = new List<Vec3>();
            holes = new List<List<Vec3>>();
            why = string.Empty;
            switch (profile.Type)
            {
                case "IFCRECTANGLEPROFILEDEF":
                case "IFCROUNDEDRECTANGLEPROFILEDEF":
                    double rounding = profile.Type == "IFCROUNDEDRECTANGLEPROFILEDEF" ? LengthOf(Arg(profile, 5)) : 0;
                    outer = Apply2D(ProfileShapes.RoundedRect(LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)), rounding), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCRECTANGLEHOLLOWPROFILEDEF":
                    WarnOnceFillet(profile.Type);
                    double x = LengthOf(Arg(profile, 3));
                    double y = LengthOf(Arg(profile, 4));
                    double wall = LengthOf(Arg(profile, 5));
                    var pos = Resolve(Arg(profile, 2));
                    outer = Apply2D(Rect(x, y), pos);
                    if (x > 2 * wall && y > 2 * wall)
                        holes.Add(Apply2D(RectCw(x - 2 * wall, y - 2 * wall), pos));
                    return outer.Count >= 3;
                case "IFCCIRCLEPROFILEDEF":
                    outer = Apply2D(Circle(LengthOf(Arg(profile, 3)), 32, true), Resolve(Arg(profile, 2)));
                    return true;
                case "IFCCIRCLEHOLLOWPROFILEDEF":
                    double radius = LengthOf(Arg(profile, 3));
                    double thick = LengthOf(Arg(profile, 4));
                    var cpos = Resolve(Arg(profile, 2));
                    outer = Apply2D(Circle(radius, 32, true), cpos);
                    if (radius > thick)
                        holes.Add(Apply2D(Circle(radius - thick, 32, false), cpos));
                    return true;
                case "IFCISHAPEPROFILEDEF":
                    outer = Apply2D(ProfileShapes.IShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)),
                        LengthOf(Arg(profile, 5)), LengthOf(Arg(profile, 6)),
                        LengthOf(Arg(profile, 7)), LengthOf(Arg(profile, 8))), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCLSHAPEPROFILEDEF":
                    outer = Apply2D(ProfileShapes.Fillet(LShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)), LengthOf(Arg(profile, 5))),
                        LengthOf(Arg(profile, 6)), true, ProfileShapes.ArcSegments), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCUSHAPEPROFILEDEF":
                    outer = Apply2D(ProfileShapes.Fillet(UShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)),
                        LengthOf(Arg(profile, 5)), LengthOf(Arg(profile, 6))),
                        LengthOf(Arg(profile, 7)), true, ProfileShapes.ArcSegments), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCCSHAPEPROFILEDEF":
                    if (LengthOf(Arg(profile, 6)) + 1e-6 < LengthOf(Arg(profile, 5)))
                        Warn("IfcCShapeProfileDef: girth меньше толщины, губа прижата к полке #" + profile.Id + ".");
                    outer = Apply2D(ProfileShapes.CShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)),
                        LengthOf(Arg(profile, 5)), LengthOf(Arg(profile, 6)),
                        LengthOf(Arg(profile, 7))), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCTSHAPEPROFILEDEF":
                    WarnOnceFillet(profile.Type);
                    outer = Apply2D(TShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)),
                        LengthOf(Arg(profile, 5)), LengthOf(Arg(profile, 6))), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCZSHAPEPROFILEDEF":
                    WarnOnceFillet(profile.Type);
                    outer = Apply2D(ZShape(
                        LengthOf(Arg(profile, 3)), LengthOf(Arg(profile, 4)),
                        LengthOf(Arg(profile, 5)), LengthOf(Arg(profile, 6))), Resolve(Arg(profile, 2)));
                    return outer.Count >= 3;
                case "IFCARBITRARYCLOSEDPROFILEDEF":
                    return CurveProfile(Resolve(Arg(profile, 2)), null, out outer, out holes, out why);
                case "IFCARBITRARYPROFILEDEFWITHVOIDS":
                    var inner = AsList(Arg(profile, 3));
                    var innerCurves = new List<P21Entity>();
                    for (int i = 0; i < inner.Count; i++)
                    {
                        var c = Resolve(inner[i]);
                        if (c != null) innerCurves.Add(c);
                    }
                    return CurveProfile(Resolve(Arg(profile, 2)), innerCurves, out outer, out holes, out why);
                case "IFCARBITRARYOPENPROFILEDEF":
                    why = "открытый профиль IfcArbitraryOpenProfileDef не образует тело";
                    return false;
                default:
                    why = "профиль " + profile.Type + " не поддержан";
                    return false;
            }
        }

        bool CurveProfile(P21Entity? outerCurve, List<P21Entity>? innerCurves, out List<Vec3> outer, out List<List<Vec3>> holes, out string why)
        {
            outer = new List<Vec3>();
            holes = new List<List<Vec3>>();
            why = string.Empty;
            var pts = outerCurve == null ? null : CurvePoints(outerCurve);
            if (pts == null || pts.Count < 3)
            {
                why = "контур профиля не разобран";
                return false;
            }
            outer = pts;
            if (outer.Count > 3 && (outer[0] - outer[outer.Count - 1]).LengthSq <= 1e-8)
                outer.RemoveAt(outer.Count - 1);
            if (MeshOps.SignedAreaXY(outer) < 0) outer.Reverse();
            if (innerCurves == null) return true;
            for (int i = 0; i < innerCurves.Count; i++)
            {
                var hp = CurvePoints(innerCurves[i]);
                if (hp == null || hp.Count < 3) continue;
                if (hp.Count > 3 && (hp[0] - hp[hp.Count - 1]).LengthSq <= 1e-8)
                    hp.RemoveAt(hp.Count - 1);
                if (MeshOps.SignedAreaXY(hp) > 0) hp.Reverse();
                holes.Add(hp);
            }
            return true;
        }

        List<Vec3>? CurvePoints(P21Entity curve)
        {
            if (curve.Type == "IFCPOLYLINE" || curve.Type == "IFCINDEXEDPOLYCURVE")
            {
                if (curve.Type == "IFCINDEXEDPOLYCURVE")
                    return PointList(Resolve(Arg(curve, 0)));
                var pts = new List<Vec3>();
                var list = AsList(Arg(curve, 0));
                for (int i = 0; i < list.Count; i++)
                {
                    var p = Point3(list[i]);
                    if (p != null) pts.Add(p.Value);
                }
                return pts;
            }
            if (curve.Type == "IFCCOMPOSITECURVE")
            {
                var pts = new List<Vec3>();
                var segs = AsList(Arg(curve, 0));
                for (int i = 0; i < segs.Count; i++)
                {
                    var seg = Resolve(segs[i]);
                    if (seg == null) continue;
                    bool same = BoolOf(Arg(seg, 1), true);
                    var parent = Resolve(Arg(seg, 2));
                    var part = parent == null ? null : CurvePoints(parent);
                    if (part == null) continue;
                    if (!same) part.Reverse();
                    if (pts.Count > 0 && part.Count > 0 && (pts[pts.Count - 1] - part[0]).LengthSq < 1e-8)
                        part.RemoveAt(0);
                    pts.AddRange(part);
                }
                return pts;
            }
            if (curve.Type == "IFCTRIMMEDCURVE")
            {
                var basis = Resolve(Arg(curve, 0));
                var a = TrimPoint(Arg(curve, 1), basis);
                var b = TrimPoint(Arg(curve, 2), basis);
                if (a != null && b != null) return new List<Vec3> { a.Value, b.Value };
                if (basis != null && (basis.Type == "IFCPOLYLINE" || basis.Type == "IFCCOMPOSITECURVE"))
                    return CurvePoints(basis);
                Warn("IfcTrimmedCurve не разобран #" + curve.Id + ".");
                return null;
            }
            Warn("Кривая " + curve.Type + " не поддержана #" + curve.Id + ".");
            return null;
        }

        Vec3? TrimPoint(P21Value? value, P21Entity? basis)
        {
            if (value == null) return null;
            var list = value.AsList();
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var p = Point3(list[i]);
                    if (p != null) return p;
                }
            }
            var direct = Point3(value);
            if (direct != null) return direct;
            if (basis != null && basis.Type == "IFCLINE")
            {
                var origin = Point3(Arg(basis, 0));
                var vector = Resolve(Arg(basis, 1));
                var dir = vector == null ? null : DirOf(Arg(vector, 0));
                double mag = vector == null ? 1 : NumOf(Arg(vector, 1), 1);
                double param = NumOf(value, double.NaN);
                if (origin != null && dir != null && !double.IsNaN(param))
                    return origin.Value + dir.Value * (mag * param);
            }
            return null;
        }

        List<Vec3> Apply2D(List<Vec3> pts, P21Entity? pos)
        {
            if (pos == null || pos.Type != "IFCAXIS2PLACEMENT2D") return pts;
            var origin = Point3(Arg(pos, 0)) ?? new Vec3(0, 0, 0);
            var rd = DirOf(Arg(pos, 1));
            var x = rd == null ? new Vec3(1, 0, 0) : new Vec3(rd.Value.X, rd.Value.Y, 0).Normalized();
            if (x.LengthSq < 0.5) x = new Vec3(1, 0, 0);
            var y = new Vec3(-x.Y, x.X, 0);
            var result = new List<Vec3>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
                result.Add(origin + x * pts[i].X + y * pts[i].Y);
            return result;
        }

        static List<Vec3> Rect(double x, double y)
        {
            double hx = x / 2, hy = y / 2;
            return new List<Vec3>
            {
                new Vec3(-hx, -hy, 0), new Vec3(hx, -hy, 0), new Vec3(hx, hy, 0), new Vec3(-hx, hy, 0)
            };
        }

        static List<Vec3> RectCw(double x, double y)
        {
            var r = Rect(x, y);
            r.Reverse();
            return r;
        }

        static List<Vec3> Circle(double radius, int n, bool ccw)
        {
            var pts = new List<Vec3>(n);
            for (int i = 0; i < n; i++)
            {
                double a = (ccw ? 1 : -1) * i * 2 * Math.PI / n;
                pts.Add(new Vec3(radius * Math.Cos(a), radius * Math.Sin(a), 0));
            }
            return pts;
        }

        static List<Vec3> IShape(double width, double depth, double web, double flange)
        {
            double hw = width / 2, hd = depth / 2, hweb = web / 2, yf = hd - flange;
            return new List<Vec3>
            {
                new Vec3(-hw, -hd, 0), new Vec3(hw, -hd, 0), new Vec3(hw, -yf, 0), new Vec3(hweb, -yf, 0),
                new Vec3(hweb, yf, 0), new Vec3(hw, yf, 0), new Vec3(hw, hd, 0), new Vec3(-hw, hd, 0),
                new Vec3(-hw, yf, 0), new Vec3(-hweb, yf, 0), new Vec3(-hweb, -yf, 0), new Vec3(-hw, -yf, 0)
            };
        }

        static List<Vec3> LShape(double depth, double width, double thickness)
        {
            return new List<Vec3>
            {
                new Vec3(0, 0, 0), new Vec3(width, 0, 0), new Vec3(width, thickness, 0),
                new Vec3(thickness, thickness, 0), new Vec3(thickness, depth, 0), new Vec3(0, depth, 0)
            };
        }

        static List<Vec3> UShape(double depth, double flangeWidth, double web, double flange)
        {
            double hw = flangeWidth / 2, hd = depth / 2;
            return new List<Vec3>
            {
                new Vec3(-hw, -hd, 0), new Vec3(hw, -hd, 0), new Vec3(hw, -hd + flange, 0),
                new Vec3(-hw + web, -hd + flange, 0), new Vec3(-hw + web, hd - flange, 0),
                new Vec3(hw, hd - flange, 0), new Vec3(hw, hd, 0), new Vec3(-hw, hd, 0)
            };
        }

        static List<Vec3> TShape(double depth, double flangeWidth, double web, double flange)
        {
            double hw = flangeWidth / 2, hd = depth / 2, hweb = web / 2, yf = hd - flange;
            return new List<Vec3>
            {
                new Vec3(-hweb, -hd, 0), new Vec3(hweb, -hd, 0), new Vec3(hweb, yf, 0), new Vec3(hw, yf, 0),
                new Vec3(hw, hd, 0), new Vec3(-hw, hd, 0), new Vec3(-hw, yf, 0), new Vec3(-hweb, yf, 0)
            };
        }

        static List<Vec3> ZShape(double depth, double flangeWidth, double web, double flange)
        {
            double hw = flangeWidth / 2, hd = depth / 2, hweb = web / 2;
            return new List<Vec3>
            {
                new Vec3(-hw, -hd, 0), new Vec3(hweb, -hd, 0), new Vec3(hweb, hd - flange, 0), new Vec3(hw, hd - flange, 0),
                new Vec3(hw, hd, 0), new Vec3(-hweb, hd, 0), new Vec3(-hweb, -hd + flange, 0), new Vec3(-hw, -hd + flange, 0)
            };
        }

        List<P21Entity> BodyItems(P21Entity shape, out bool baked)
        {
            baked = false;
            var found = new List<P21Entity>();
            var reps = LastList(shape);
            for (int i = 0; i < reps.Count; i++)
            {
                var rep = Resolve(reps[i]);
                if (rep == null) continue;
                if (rep.Type != "IFCSHAPEREPRESENTATION" && rep.Type != "IFCTOPOLOGYREPRESENTATION") continue;
                string ident = StringOf(Arg(rep, 1)) ?? string.Empty;
                if (IsNonBody(ident)) continue;
                var items = LastList(rep);
                for (int k = 0; k < items.Count; k++)
                {
                    var g = Resolve(items[k]);
                    if (g == null) continue;
                    found.Add(g);
                    if (g.Type.IndexOf("BOOLEAN", StringComparison.Ordinal) >= 0 ||
                        g.Type.IndexOf("BREP", StringComparison.Ordinal) >= 0 ||
                        g.Type.IndexOf("FACESET", StringComparison.Ordinal) >= 0)
                        baked = true;
                }
            }
            return found;
        }

        static bool IsNonBody(string ident)
        {
            if (ident.Length == 0) return false;
            return ident.Equals("Axis", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Box", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("FootPrint", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Profile", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Reference", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Clearance", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Lighting", StringComparison.OrdinalIgnoreCase)
                || ident.Equals("Annotation", StringComparison.OrdinalIgnoreCase);
        }

        void IndexVoids()
        {
            foreach (var entity in _file.Entities.Values)
            {
                if (entity.Type != "IFCRELVOIDSELEMENT" && entity.Type != "IFCRELVOIDSELEMENTS") continue;
                var refs = new List<int>();
                for (int i = 0; i < entity.Args.Count; i++)
                {
                    int? id = entity.Args[i].AsRef();
                    if (id != null) refs.Add(id.Value);
                }
                if (refs.Count < 2) continue;
                int element = refs[refs.Count - 2];
                int opening = refs[refs.Count - 1];
                List<int> list;
                if (!_voids.TryGetValue(element, out list))
                {
                    list = new List<int>();
                    _voids[element] = list;
                }
                list.Add(opening);
            }
        }

        Mat4 PlacementOf(P21Entity product)
        {
            for (int i = 0; i < product.Args.Count; i++)
            {
                var e = Resolve(product.Args[i]);
                if (e != null && (e.Type == "IFCLOCALPLACEMENT" || e.Type == "IFCAXIS2PLACEMENT3D"))
                    return PlacementMatrix(e.Id, new HashSet<int>());
            }
            return Mat4.Identity;
        }

        Mat4 PlacementMatrix(int id, HashSet<int> stack)
        {
            if (!stack.Add(id)) return Mat4.Identity;
            var e = _file.Get(id);
            if (e == null) return Mat4.Identity;
            if (e.Type == "IFCLOCALPLACEMENT")
            {
                var parent = Mat4.Identity;
                var rel = Resolve(Arg(e, 0));
                if (rel != null) parent = PlacementMatrix(rel.Id, stack);
                var axis = Resolve(Arg(e, 1));
                var local = axis != null ? Axis3(axis) : Mat4.Identity;
                return parent.Multiply(local);
            }
            if (e.Type == "IFCAXIS2PLACEMENT3D") return Axis3(e);
            return Mat4.Identity;
        }

        Mat4 Axis3(P21Entity e)
        {
            var loc = Point3(Arg(e, 0)) ?? new Vec3(0, 0, 0);
            var z = DirOf(Arg(e, 1)) ?? new Vec3(0, 0, 1);
            var x = DirOf(Arg(e, 2)) ?? new Vec3(1, 0, 0);
            return Mat4.FromAxis2(loc, z, x);
        }

        Mat4 Operator3D(P21Entity e)
        {
            var xu = DirOf(Arg(e, 0)) ?? new Vec3(1, 0, 0);
            var yu = DirOf(Arg(e, 1)) ?? new Vec3(0, 1, 0);
            var origin = Point3(Arg(e, 2)) ?? new Vec3(0, 0, 0);
            double s1 = NumOf(Arg(e, 3), 1);
            if (Math.Abs(s1) < 1e-15) s1 = 1;
            var zu = DirOf(Arg(e, 4));
            xu = xu.Normalized();
            yu = yu.Normalized();
            if (zu == null) zu = Vec3.Cross(xu, yu);
            zu = zu.Value.Normalized();
            double s2 = s1, s3 = s1;
            if (e.Type == "IFCCARTESIANTRANSFORMATIONOPERATOR3DNONUNIFORM")
            {
                s2 = NumOf(Arg(e, 5), s1);
                s3 = NumOf(Arg(e, 6), s1);
            }
            return Mat4.FromAxes(xu * s1, yu * s2, zu.Value * s3, origin);
        }

        List<Vec3> PointList(P21Entity? e)
        {
            var pts = new List<Vec3>();
            if (e == null) return pts;
            var rows = AsList(Arg(e, 0));
            for (int i = 0; i < rows.Count; i++)
            {
                var coords = rows[i].AsList();
                if (coords == null) continue;
                double x = NumAt(coords, 0) * _scale;
                double y = NumAt(coords, 1) * _scale;
                double z = coords.Count > 2 ? NumAt(coords, 2) * _scale : 0;
                pts.Add(new Vec3(x, y, z));
            }
            return pts;
        }

        static List<Vec3> Indices(IReadOnlyList<P21Value>? idx, List<Vec3> pts, Mat4 world)
        {
            var poly = new List<Vec3>();
            if (idx == null) return poly;
            for (int i = 0; i < idx.Count; i++)
            {
                int n = (int)NumAt(idx, i) - 1;
                if (n < 0 || n >= pts.Count) continue;
                poly.Add(world.TransformPoint(pts[n]));
            }
            return poly;
        }

        List<IReadOnlyList<P21Value>> FindTriples(P21Entity item)
        {
            var found = new List<IReadOnlyList<P21Value>>();
            for (int i = 0; i < item.Args.Count; i++)
            {
                var list = item.Args[i].AsList();
                if (list == null || list.Count == 0) continue;
                var first = list[0].AsList();
                if (first != null && first.Count == 3 && first[0].AsNumber() != null)
                {
                    for (int k = 0; k < list.Count; k++)
                    {
                        var row = list[k].AsList();
                        if (row != null && row.Count == 3) found.Add(row);
                    }
                    if (found.Count > 0) return found;
                }
            }
            return found;
        }

        Vec3? Point3(P21Value? value)
        {
            if (value == null || value.IsNull) return null;
            if (value is P21Typed typed && typed.Type == "IFCCARTESIANPOINT")
                return Coords(typed.Inner);
            var e = Resolve(value);
            if (e == null || e.Type != "IFCCARTESIANPOINT") return null;
            return Coords(Arg(e, 0));
        }

        Vec3 Coords(P21Value? listVal)
        {
            var list = AsList(listVal);
            double x = NumAt(list, 0) * _scale;
            double y = NumAt(list, 1) * _scale;
            double z = list.Count > 2 ? NumAt(list, 2) * _scale : 0;
            return new Vec3(x, y, z);
        }

        Vec3? DirOf(P21Value? value)
        {
            var e = Resolve(value);
            if (e == null || e.Type != "IFCDIRECTION") return null;
            var list = AsList(Arg(e, 0));
            double x = NumAt(list, 0);
            double y = list.Count > 1 ? NumAt(list, 1) : 0;
            double z = list.Count > 2 ? NumAt(list, 2) : 0;
            var d = new Vec3(x, y, z);
            return d.LengthSq < 1e-24 ? (Vec3?)null : d.Normalized();
        }

        double LengthOf(P21Value? value) => NumOf(value, 0) * _scale;

        double NumOf(P21Value? value, double fallback)
        {
            if (value == null || value.IsNull) return fallback;
            double? n = value.AsNumber();
            if (n != null) return n.Value;
            var list = value.AsList();
            if (list != null && list.Count == 1) return NumOf(list[0], fallback);
            return fallback;
        }

        static double NumAt(IReadOnlyList<P21Value>? list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return 0;
            double? n = list[index].AsNumber();
            return n ?? 0;
        }

        string NameOf(P21Entity e)
        {
            if (e.Args.Count > 2)
            {
                string? s = StringOf(e.Args[2]);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return e.Type + "-" + e.Id.ToString(CultureInfo.InvariantCulture);
        }

        static string? StringOf(P21Value? value)
        {
            if (value == null) return null;
            return value.AsString();
        }

        static string? EnumOf(P21Value? value)
        {
            if (value == null) return null;
            return value.AsEnum();
        }

        static bool BoolOf(P21Value? value, bool fallback)
        {
            if (value == null || value.IsNull) return fallback;
            bool? b = value.AsBool();
            if (b != null) return b.Value;
            string? en = value.AsEnum();
            if (en == "T") return true;
            if (en == "F") return false;
            return fallback;
        }

        P21Entity? FindArgType(P21Entity entity, string type)
        {
            for (int i = 0; i < entity.Args.Count; i++)
            {
                var e = Resolve(entity.Args[i]);
                if (e != null && e.Type == type) return e;
            }
            return null;
        }

        P21Value? Arg(P21Entity e, int index)
        {
            if (index < 0 || index >= e.Args.Count) return null;
            return e.Args[index];
        }

        P21Entity? Resolve(P21Value? value) => _file.Resolve(value);

        static IReadOnlyList<P21Value> AsList(P21Value? value)
        {
            if (value == null) return Empty;
            var list = value.AsList();
            return list ?? Empty;
        }

        List<P21Value> LastList(P21Entity e)
        {
            for (int i = e.Args.Count - 1; i >= 0; i--)
            {
                var list = e.Args[i].AsList();
                if (list != null) return new List<P21Value>(list);
            }
            return new List<P21Value>();
        }

        double ReadScale()
        {
            P21Entity? assignment = null;
            foreach (var e in _file.Entities.Values)
            {
                if (e.Type == "IFCUNITASSIGNMENT")
                {
                    assignment = e;
                    break;
                }
            }
            if (assignment == null)
            {
                Warn("Нет IfcUnitAssignment, приняты миллиметры.");
                return 1;
            }
            var units = AsList(Arg(assignment, 0));
            for (int i = 0; i < units.Count; i++)
            {
                var unit = Resolve(units[i]);
                if (unit == null || !IsLength(unit)) continue;
                if (unit.Type == "IFCSIUNIT") return SiToMm(unit);
                if (unit.Type == "IFCCONVERSIONBASEDUNIT") return ConversionToMm(unit);
            }
            Warn("Нет единицы длины, приняты миллиметры.");
            return 1;
        }

        double ConversionToMm(P21Entity unit)
        {
            P21Entity? factor = null;
            for (int i = 0; i < unit.Args.Count; i++)
            {
                var e = Resolve(unit.Args[i]);
                if (e != null && e.Type == "IFCMEASUREWITHUNIT") factor = e;
            }
            if (factor == null) return 1;
            double value = NumOf(Arg(factor, 0), 1);
            var component = Resolve(Arg(factor, 1));
            double unitMm = component != null && component.Type == "IFCSIUNIT" ? SiToMm(component) : 1000;
            return value * unitMm;
        }

        double SiToMm(P21Entity unit)
        {
            string prefix = string.Empty;
            string name = string.Empty;
            for (int i = 0; i < unit.Args.Count; i++)
            {
                string? en = unit.Args[i].AsEnum();
                if (en == null || en == "LENGTHUNIT") continue;
                if (en == "METRE" || en == "METER") name = en;
                else prefix = en;
            }
            if (name != "METRE" && name != "METER") return 1;
            switch (prefix)
            {
                case "MILLI": return 1;
                case "CENTI": return 10;
                case "DECI": return 100;
                case "KILO": return 1e6;
                case "MICRO": return 1e-3;
                case "": return 1000;
                default:
                    Warn("Неизвестный префикс длины " + prefix + ", приняты метры.");
                    return 1000;
            }
        }

        static bool IsLength(P21Entity unit)
        {
            for (int i = 0; i < unit.Args.Count; i++)
            {
                if (unit.Args[i].AsEnum() == "LENGTHUNIT") return true;
            }
            return false;
        }

        static bool SkipProduct(string type)
        {
            return type == "IFCOPENINGELEMENT"
                || type == "IFCOPENINGSTANDARDCASE"
                || type == "IFCSPACE"
                || type == "IFCANNOTATION"
                || type == "IFCGRID"
                || type == "IFCGRIDAXIS"
                || type == "IFCPROJECT"
                || type == "IFCSITE"
                || type == "IFCBUILDING"
                || type == "IFCBUILDINGSTOREY"
                || type.StartsWith("IFCREL", StringComparison.Ordinal);
        }

        void WarnOnceFillet(string type)
        {
            if (_filletWarned) return;
            _filletWarned = true;
            Warn("Скругления профиля заменены острыми кромками (" + type + ").");
        }

        void Warn(string message) => _warnings.Add(message);

        static readonly P21Value[] Empty = new P21Value[0];
    }
}
