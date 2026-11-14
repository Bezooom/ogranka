using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TeklaToStep;

/// <summary>CLI and the self-contained smoke test. No Tekla session is required for --self-test.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args ?? new string[0]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        var rest = new List<string>();
        string? teklaBin = null;
        bool faceted = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--tekla-bin" && i + 1 < args.Length)
            {
                teklaBin = args[++i];
                continue;
            }
            if (args[i] == "--faceted")
            {
                faceted = true;
                continue;
            }
            rest.Add(args[i]);
        }
        if (rest.Count == 0 || rest[0] == "--self-test")
            return SelfTest(rest.Count > 1 ? rest[1] : "test_output.stp");
        if (rest[0] == "--help" || rest[0] == "-h" || rest[0] == "/?")
        {
            PrintHelp();
            return 0;
        }
        if (rest[0] == "--smoke")
            return WriteSmoke(rest.Count > 1 ? rest[1] : "test_output.stp");
        if (rest[0] == "--live")
        {
            if (rest.Count < 2)
            {
                Console.Error.WriteLine("Нужен путь: TeklaToStep --live output.stp");
                return 1;
            }
            return Live(rest[1], teklaBin, faceted);
        }
        if (rest[0] == "--ifc")
        {
            if (rest.Count < 3)
            {
                Console.Error.WriteLine("Нужны пути: TeklaToStep --ifc input.ifc output.stp");
                return 1;
            }
            return ConvertIfc(rest[1], rest[2], faceted);
        }
        Console.Error.WriteLine("Неизвестный аргумент: " + rest[0]);
        PrintHelp();
        return 1;
    }

    static void PrintHelp()
    {
        Console.WriteLine("TeklaToStep – геометрия Tekla / IFC в STEP AP214 (мм), без сторонних CAD-библиотек.");
        Console.WriteLine("  TeklaToStep --live output.stp [--tekla-bin \"C:\\Program Files\\Tekla Structures\\2024.0\\bin\"]");
        Console.WriteLine("  TeklaToStep --ifc input.ifc output.stp [--faceted]");
        Console.WriteLine("  --faceted: FACETED_BREP и POLY_LOOP вместо рёберной схемы (файл в разы меньше)");
        Console.WriteLine("  TeklaToStep --smoke [test_output.stp]");
        Console.WriteLine("  TeklaToStep --self-test");
        Console.WriteLine("Windows: для --live собирайте net48 x64 и запускайте при открытой модели Tekla.");
        Console.WriteLine("  dotnet build -c Release -f net48");
        Console.WriteLine("IFC-режим работает на любой ОС, где есть этот runtime.");
    }

    static int Live(string output, string? teklaBin, bool faceted = false)
    {
        var warnings = new List<string>();
        var solids = TeklaDirectExtractor.ExtractSelected(teklaBin, warnings);
        for (int i = 0; i < warnings.Count; i++) Console.Error.WriteLine(warnings[i]);
        if (solids.Count == 0) return 2;
        var writer = new StepAsciiWriter();
        writer.Write(output, solids, faceted);
        for (int i = 0; i < writer.Warnings.Count; i++) Console.Error.WriteLine(writer.Warnings[i]);
        Console.WriteLine("Записано тел: " + solids.Count.ToString(CultureInfo.InvariantCulture) + " → " + output);
        return 0;
    }

    static int ConvertIfc(string input, string output, bool faceted = false)
    {
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseFile(input, warnings);
        for (int i = 0; i < warnings.Count; i++) Console.Error.WriteLine(warnings[i]);
        if (solids.Count == 0) return 2;
        var writer = new StepAsciiWriter();
        writer.Write(output, solids, faceted);
        for (int i = 0; i < writer.Warnings.Count; i++) Console.Error.WriteLine(writer.Warnings[i]);
        Console.WriteLine("IFC тел: " + solids.Count.ToString(CultureInfo.InvariantCulture) + " → " + output);
        return 0;
    }

    static int WriteSmoke(string path)
    {
        var mesh = BuildWebHoleBeam();
        var writer = new StepAsciiWriter();
        writer.Write(path, new[] { mesh });
        var errors = ValidateStep(File.ReadAllText(path), true);
        if (writer.Warnings.Count > 0 || errors.Count > 0)
        {
            for (int i = 0; i < writer.Warnings.Count; i++) Console.Error.WriteLine(writer.Warnings[i]);
            for (int i = 0; i < errors.Count; i++) Console.Error.WriteLine(errors[i]);
            return 1;
        }
        Console.WriteLine(path);
        return 0;
    }

    static int SelfTest(string stepPath)
    {
        var rows = new List<string[]>();
        bool ok = true;
        ok &= Check(rows, "Матрица: обратная восстанавливает точку", MatrixRoundtrip());
        ok &= Check(rows, "Куб 10 мм: объём 1000", Near(MeshOps.SignedVolume(MeshOps.Box("box", 0, 0, 0, 10, 10, 10)), 1000, 1e-4));
        ok &= Check(rows, "Клиппинг куба плоскостью z=7: объём 700", ClipBox());
        ok &= Check(rows, "Прямоугольный проём: объём 840 и замкнутый STEP", RectangularOpening());
        ok &= Check(rows, "IFC-проём IfcRelVoidsElement: объём 840", IfcOpening());
        var beam = BuildWebHoleBeam();
        double beamVolume = MeshOps.SignedVolume(beam);
        ok &= Check(rows, "Двутавр с отверстием: объём 1123200", Near(beamVolume, 1123200, 1));
        var writer = new StepAsciiWriter();
        writer.Write(stepPath, new[] { beam });
        string step = File.ReadAllText(stepPath);
        var stepErrors = ValidateStep(step, true);
        ok &= Check(rows, "STEP: заголовок, мм, ссылки, manifold", stepErrors.Count == 0, stepErrors.Count == 0 ? "граф замкнут" : string.Join("; ", stepErrors.ToArray()));
        ok &= Check(rows, "STEP: 18 граней и FACE_BOUND", Count(step, "ADVANCED_FACE") == 18 && step.IndexOf("FACE_BOUND", StringComparison.Ordinal) >= 0);
        ok &= Check(rows, "STEP: 32 вершины после дедупа", Count(step, "VERTEX_POINT") == 32);
        ok &= Check(rows, "Писатель не открыл оболочку", writer.Warnings.Count == 0, writer.Warnings.Count == 0 ? "0 предупреждений" : string.Join("; ", writer.Warnings.ToArray()));

        ok &= Check(rows, "IFC куб, сдвиг X=100, объём 1000", IfcCube());
        ok &= Check(rows, "IFC выдавливание 10x20x30, объём 6000", IfcRect());
        ok &= Check(rows, "IFC двутавр, объём 1142400", IfcIShape());
        ok &= Check(rows, "IFC клиппинг z>7, объём 700", IfcClip());
        ok &= Check(rows, "IFC в метрах: куб 1 м = 1e9 мм³", IfcMetreCube());
        ok &= Check(rows, "IFC mapped item, сдвиг 50 мм", IfcMapped());
        ok &= Check(rows, "IFC двутавр с галтелью 10: объём больше острого", IfcIShapeFillet());
        ok &= Check(rows, "IFC швеллер с губами: объём 380000", IfcCShape());
        ok &= Check(rows, "IFC скруглённый прямоугольник", IfcRoundedRect());
        ok &= Check(rows, "IFC вращение кольца", IfcRevolve());
        ok &= Check(rows, "IFC диск по прямой", IfcSweptDisk());
        ok &= Check(rows, "IFC вычитание уголка: объём 3200", IfcConcaveTool());
        ok &= Check(rows, "FACETED_BREP: POLY_LOOP без рёбер и дублей точек", FacetedStep());
        ok &= Check(rows, "IFC швеллер с гиртом меньше стенки: U без отгибов", IfcSmallGirth());
        ok &= Check(rows, "IFC диск по замкнутой направляющей: тор без крышек", IfcClosedSweep());
        ok &= Check(rows, "IFC вращение на 90° с отверстием профиля", IfcRevolveHole());

        Console.WriteLine("| Требование | Вердикт | Факт |");
        Console.WriteLine("| --- | --- | --- |");
        for (int i = 0; i < rows.Count; i++)
            Console.WriteLine("| " + rows[i][0] + " | " + rows[i][1] + " | " + rows[i][2] + " |");
        Console.WriteLine(ok ? "SELF-TEST PASS " + stepPath : "SELF-TEST FAIL");
        return ok ? 0 : 1;
    }

    static bool Check(List<string[]> rows, string name, bool pass)
    {
        return Check(rows, name, pass, pass ? "совпало" : "не совпало");
    }

    static bool Check(List<string[]> rows, string name, bool pass, string evidence)
    {
        rows.Add(new[] { name, pass ? "PASS" : "FAIL", evidence.Replace("|", "/") });
        return pass;
    }

    static bool MatrixRoundtrip()
    {
        var m = Mat4.FromAxes(new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0), new Vec3(5, 6, 7));
        Mat4 inv;
        if (!m.TryInverse(out inv)) return false;
        var back = inv.TransformPoint(m.TransformPoint(new Vec3(1, 2, 3)));
        return (back - new Vec3(1, 2, 3)).Length < 1e-9;
    }

    static bool ClipBox()
    {
        var box = MeshOps.Box("box", 0, 0, 0, 10, 10, 10);
        var planes = new List<Plane> { new Plane(new Vec3(0, 0, 7), new Vec3(0, 0, -1)) };
        List<Facet> cut;
        string error;
        if (!MeshOps.TrySubtract(box.Faces, planes, out cut, out error)) return false;
        return Near(MeshOps.SignedVolume(cut), 700, 1e-3);
    }

    static bool HollowExtrusion()
    {
        var outer = new List<Vec3>
        {
            new Vec3(-5, -5, 0), new Vec3(5, -5, 0), new Vec3(5, 5, 0), new Vec3(-5, 5, 0)
        };
        var hole = new List<Vec3>
        {
            new Vec3(-2, -2, 0), new Vec3(-2, 2, 0), new Vec3(2, 2, 0), new Vec3(2, -2, 0)
        };
        var holes = new List<IReadOnlyList<Vec3>> { hole };
        var faces = MeshOps.Extrude(outer, holes, Mat4.Identity, new Vec3(0, 0, 1), 10);
        return Near(MeshOps.SignedVolume(faces), 840, 1e-3);
    }
    static bool RectangularOpening()
    {
        var box = MeshOps.Box("box", 0, 0, 0, 10, 10, 10);
        var planes = new List<Plane>
        {
            new Plane(new Vec3(3, 0, 0), new Vec3(-1, 0, 0)),
            new Plane(new Vec3(7, 0, 0), new Vec3(1, 0, 0)),
            new Plane(new Vec3(0, 3, 0), new Vec3(0, -1, 0)),
            new Plane(new Vec3(0, 7, 0), new Vec3(0, 1, 0)),
            new Plane(new Vec3(0, 0, -1), new Vec3(0, 0, -1)),
            new Plane(new Vec3(0, 0, 11), new Vec3(0, 0, 1))
        };
        List<Facet> cut;
        string error;
        if (!MeshOps.TrySubtract(box.Faces, planes, out cut, out error)) return false;
        if (!Near(MeshOps.SignedVolume(cut), 840, 1e-2)) return false;
        var mesh = new SolidMesh("opening");
        mesh.Faces = cut;
        var writer = new StepAsciiWriter();
        string path = Path.Combine(Path.GetTempPath(), "tekla-opening-check.stp");
        writer.Write(path, new[] { mesh });
        var stepErrors = ValidateStep(File.ReadAllText(path), true);
        File.Delete(path);
        return writer.Warnings.Count == 0 && stepErrors.Count == 0;
    }

    static bool IfcOpening()
    {
        string data = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCRECTANGLEPROFILEDEF(.AREA.,'R',#12,10.,10.);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,10.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Beam',$,$,#18,#17,$);
#20=IFCCARTESIANPOINT((0.,0.,-1.));
#21=IFCAXIS2PLACEMENT3D(#20,$,$);
#22=IFCRECTANGLEPROFILEDEF(.AREA.,'H',#12,4.,4.);
#23=IFCEXTRUDEDAREASOLID(#22,#21,#14,12.);
#24=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#23));
#25=IFCPRODUCTDEFINITIONSHAPE($,$,(#24));
#26=IFCLOCALPLACEMENT($,#21);
#27=IFCOPENINGELEMENT('o',$,'Hole',$,$,#26,#25,$);
#28=IFCRELVOIDSELEMENT('v',$,$,$,#19,#27);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + data + Footer(), warnings);
        return solids.Count == 1 && Near(MeshOps.SignedVolume(solids[0]), 840, 1);
    }


    static bool IfcCube()
    {
        string ifc = Header() + @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCCARTESIANPOINT((10.,0.,0.));
#12=IFCCARTESIANPOINT((10.,10.,0.));
#13=IFCCARTESIANPOINT((0.,10.,0.));
#14=IFCCARTESIANPOINT((0.,0.,10.));
#15=IFCCARTESIANPOINT((10.,0.,10.));
#16=IFCCARTESIANPOINT((10.,10.,10.));
#17=IFCCARTESIANPOINT((0.,10.,10.));
#20=IFCPOLYLOOP((#10,#13,#12,#11));
#21=IFCPOLYLOOP((#14,#15,#16,#17));
#22=IFCPOLYLOOP((#10,#11,#15,#14));
#23=IFCPOLYLOOP((#13,#17,#16,#12));
#24=IFCPOLYLOOP((#10,#14,#17,#13));
#25=IFCPOLYLOOP((#11,#12,#16,#15));
#30=IFCFACEOUTERBOUND(#20,.T.);
#31=IFCFACEOUTERBOUND(#21,.T.);
#32=IFCFACEOUTERBOUND(#22,.T.);
#33=IFCFACEOUTERBOUND(#23,.T.);
#34=IFCFACEOUTERBOUND(#24,.T.);
#35=IFCFACEOUTERBOUND(#25,.T.);
#40=IFCFACE((#30));
#41=IFCFACE((#31));
#42=IFCFACE((#32));
#43=IFCFACE((#33));
#44=IFCFACE((#34));
#45=IFCFACE((#35));
#50=IFCCLOSEDSHELL((#40,#41,#42,#43,#44,#45));
#51=IFCFACETEDBREP(#50);
#52=IFCSHAPEREPRESENTATION(#2,'Body','Brep',(#51));
#53=IFCPRODUCTDEFINITIONSHAPE($,$,(#52));
#60=IFCCARTESIANPOINT((100.,0.,0.));
#61=IFCAXIS2PLACEMENT3D(#60,$,$);
#62=IFCLOCALPLACEMENT($,#61);
#70=IFCBEAM('beam',$,'Cube',$,$,#62,#53,$);
" + Footer();
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(ifc, warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        if (!Near(MeshOps.SignedVolume(solids[0]), 1000, 1e-3)) return false;
        double minX = double.MaxValue;
        for (int i = 0; i < solids[0].Faces.Count; i++)
            for (int k = 0; k < solids[0].Faces[i].Outer.Count; k++)
                if (solids[0].Faces[i].Outer[k].X < minX) minX = solids[0].Faces[i].Outer[k].X;
        return Near(minX, 100, 1e-6);
    }

    static bool IfcRect()
    {
        var solids = Parse(RectBody(10, 20, 30, "IFCEXTRUDEDAREASOLID(#13,#11,#14,30.)"));
        return solids != null && Near(MeshOps.SignedVolume(solids[0]), 6000, 1e-2);
    }

    static bool IfcIShape()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCISHAPEPROFILEDEF(.AREA.,'I',#12,100.,200.,8.,12.,$,$,$);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,300.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'I',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        return solids.Count == 1 && Near(MeshOps.SignedVolume(solids[0]), 1142400, 1);
    }
    static bool IfcIShapeFillet()
    {
        var poly = ProfileShapes.IShape(100, 200, 8, 12, 10, 0);
        double expected = Math.Abs(MeshOps.SignedAreaXY(poly)) * 300;
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCISHAPEPROFILEDEF(.AREA.,'I',#12,100.,200.,8.,12.,10.,$,$);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,300.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'I',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        double volume = MeshOps.SignedVolume(solids[0]);
        bool filletPass = solids.Count == 1 && warnings.Count == 0 && volume > 1142400 && Near(volume, expected, 1);
        if (!filletPass)
            Console.Error.WriteLine("FILLET vol=" + volume.ToString("0.###") + " expected=" + expected.ToString("0.###") + " solids=" + solids.Count + " warns=" + warnings.Count + " " + string.Join(" | ", warnings.ToArray()));
        return filletPass;
    }

    static bool IfcCShape()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCCSHAPEPROFILEDEF(.AREA.,'C',#12,200.,80.,10.,30.,$);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,100.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'C',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        return solids.Count == 1 && warnings.Count == 0 && Near(MeshOps.SignedVolume(solids[0]), 380000, 1)
            && StepClosed(solids[0], "test_c_shape.stp");
    }

    static bool IfcRoundedRect()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCROUNDEDRECTANGLEPROFILEDEF(.AREA.,'R',#12,20.,10.,2.);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,10.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'R',$,$,#18,#17,$);
";
        var poly = ProfileShapes.RoundedRect(20, 10, 2);
        double expected = Math.Abs(MeshOps.SignedAreaXY(poly)) * 10;
        double analytic = (200 - (4 - Math.PI) * 4) * 10;
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        double volume = MeshOps.SignedVolume(solids[0]);
        return Near(volume, expected, 1) && volume < 2000 && volume > analytic - 5;
    }

    static bool IfcRevolve()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#20=IFCCARTESIANPOINT((25.,0.,0.));
#21=IFCAXIS2PLACEMENT2D(#20,$);
#13=IFCRECTANGLEPROFILEDEF(.AREA.,'R',#21,10.,10.);
#30=IFCCARTESIANPOINT((0.,0.,0.));
#31=IFCDIRECTION((0.,1.,0.));
#32=IFCAXIS1PLACEMENT(#30,#31);
#15=IFCREVOLVEDAREASOLID(#13,#11,#32,6.28318530718);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Ring',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        return Near(MeshOps.SignedVolume(solids[0]), 2 * Math.PI * 25 * 100, 200)
            && StepClosed(solids[0], "test_revolve.stp");
    }

    static bool IfcSweptDisk()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#40=IFCCARTESIANPOINT((0.,0.,100.));
#41=IFCPOLYLINE((#10,#40));
#15=IFCSWEPTDISKSOLID(#41,5.,$);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Bar',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        double poly = 0.5 * 32 * 25 * Math.Sin(2 * Math.PI / 32) * 100;
        return Near(MeshOps.SignedVolume(solids[0]), poly, 1)
            && StepClosed(solids[0], "test_disk.stp");
    }

    static bool IfcConcaveTool()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCRECTANGLEPROFILEDEF(.AREA.,'B',#12,20.,20.);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,10.);
#50=IFCCARTESIANPOINT((-6.,-6.));
#51=IFCCARTESIANPOINT((6.,-6.));
#52=IFCCARTESIANPOINT((6.,-2.));
#53=IFCCARTESIANPOINT((-2.,-2.));
#54=IFCCARTESIANPOINT((-2.,6.));
#55=IFCCARTESIANPOINT((-6.,6.));
#56=IFCPOLYLINE((#50,#51,#52,#53,#54,#55,#50));
#57=IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,'L',#56);
#58=IFCCARTESIANPOINT((0.,0.,-1.));
#59=IFCAXIS2PLACEMENT3D(#58,$,$);
#60=IFCEXTRUDEDAREASOLID(#57,#59,#14,12.);
#61=IFCBOOLEANCLIPPINGRESULT(.DIFFERENCE.,#15,#60);
#16=IFCSHAPEREPRESENTATION(#2,'Body','CSG',(#61));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Cut',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        if (!Near(MeshOps.SignedVolume(solids[0]), 3200, 1)) return false;
        var writer = new StepAsciiWriter();
        writer.Write("test_concave.stp", new[] { solids[0] });
        var stepErrors = ValidateStep(File.ReadAllText("test_concave.stp"), true);
        bool ok = writer.Warnings.Count == 0 && stepErrors.Count == 0;
        if (!ok)
            Console.Error.WriteLine("CONCAVE warn=" + string.Join(" | ", writer.Warnings.ToArray()) + " errors=" + string.Join(" | ", stepErrors.ToArray()));
        return ok;
    }


    static bool IfcClip()
    {
        string geom = "IFCBOOLEANCLIPPINGRESULT(.DIFFERENCE.,#15,#33)";
        string extra = @"
#30=IFCCARTESIANPOINT((0.,0.,7.));
#31=IFCAXIS2PLACEMENT3D(#30,#14,$);
#32=IFCPLANE(#31);
#33=IFCHALFSPACESOLID(#32,.F.);
";
        var solids = Parse(RectBody(10, 10, 10, geom) .Replace("#19=IFCBEAM", extra + "#19=IFCBEAM"));
        return solids != null && Near(MeshOps.SignedVolume(solids[0]), 700, 1e-2);
    }

    static bool IfcMetreCube()
    {
        string ifc = Header() + @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCCARTESIANPOINT((1.,0.,0.));
#12=IFCCARTESIANPOINT((1.,1.,0.));
#13=IFCCARTESIANPOINT((0.,1.,0.));
#14=IFCCARTESIANPOINT((0.,0.,1.));
#15=IFCCARTESIANPOINT((1.,0.,1.));
#16=IFCCARTESIANPOINT((1.,1.,1.));
#17=IFCCARTESIANPOINT((0.,1.,1.));
#20=IFCPOLYLOOP((#10,#13,#12,#11));
#21=IFCPOLYLOOP((#14,#15,#16,#17));
#22=IFCPOLYLOOP((#10,#11,#15,#14));
#23=IFCPOLYLOOP((#13,#17,#16,#12));
#24=IFCPOLYLOOP((#10,#14,#17,#13));
#25=IFCPOLYLOOP((#11,#12,#16,#15));
#30=IFCFACEOUTERBOUND(#20,.T.);
#31=IFCFACEOUTERBOUND(#21,.T.);
#32=IFCFACEOUTERBOUND(#22,.T.);
#33=IFCFACEOUTERBOUND(#23,.T.);
#34=IFCFACEOUTERBOUND(#24,.T.);
#35=IFCFACEOUTERBOUND(#25,.T.);
#40=IFCFACE((#30));
#41=IFCFACE((#31));
#42=IFCFACE((#32));
#43=IFCFACE((#33));
#44=IFCFACE((#34));
#45=IFCFACE((#35));
#50=IFCCLOSEDSHELL((#40,#41,#42,#43,#44,#45));
#51=IFCFACETEDBREP(#50);
#52=IFCSHAPEREPRESENTATION(#2,'Body','Brep',(#51));
#53=IFCPRODUCTDEFINITIONSHAPE($,$,(#52));
#61=IFCAXIS2PLACEMENT3D(#10,$,$);
#62=IFCLOCALPLACEMENT($,#61);
#70=IFCBEAM('m',$,'Metre',$,$,#62,#53,$);
" + Footer();
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(ifc, warnings);
        return solids.Count == 1 && Near(MeshOps.SignedVolume(solids[0]), 1e9, 1);
    }

    static bool IfcMapped()
    {
        string body = RectBody(10, 10, 10, "IFCMAPPEDITEM(#80,#83)");
        string extra = @"
#80=IFCREPRESENTATIONMAP(#11,#81);
#81=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#82=IFCCARTESIANPOINT((50.,0.,0.));
#83=IFCCARTESIANTRANSFORMATIONOPERATOR3D($,$,#82,$,$);
";
        var solids = Parse(body.Replace("#19=IFCBEAM", extra + "#19=IFCBEAM"));
        if (solids == null) return false;
        double minX = double.MaxValue;
        for (int i = 0; i < solids[0].Faces.Count; i++)
            for (int k = 0; k < solids[0].Faces[i].Outer.Count; k++)
                if (solids[0].Faces[i].Outer[k].X < minX) minX = solids[0].Faces[i].Outer[k].X;
        return Near(minX, 45, 1e-4) && Near(MeshOps.SignedVolume(solids[0]), 1000, 1e-2);
    }

    static bool StepClosed(SolidMesh solid, string path)
    {
        var writer = new StepAsciiWriter();
        writer.Write(path, new[] { solid });
        if (writer.Warnings.Count > 0) return false;
        return ValidateStep(File.ReadAllText(path), true).Count == 0;
    }

    static bool FacetedStep()
    {
        var box = MeshOps.Box("box", 0, 0, 0, 10, 10, 10);
        var writer = new StepAsciiWriter();
        writer.Write("test_faceted.stp", new[] { box }, true);
        if (writer.Warnings.Count > 0) return false;
        var errors = ValidateFacetedStep(File.ReadAllText("test_faceted.stp"));
        if (errors.Count > 0)
            Console.Error.WriteLine("FACETED: " + string.Join("; ", errors.ToArray()));
        return errors.Count == 0;
    }

    static bool IfcSmallGirth()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCCSHAPEPROFILEDEF(.AREA.,'C',#12,200.,80.,10.,5.,$);
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14,100.);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'C',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        return solids.Count == 1 && warnings.Count == 1 && Near(MeshOps.SignedVolume(solids[0]), 340000, 1)
            && StepClosed(solids[0], "test_u_shape.stp");
    }

    static bool IfcClosedSweep()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#40=IFCCARTESIANPOINT((100.,0.,0.));
#41=IFCCARTESIANPOINT((100.,100.,0.));
#42=IFCCARTESIANPOINT((0.,100.,0.));
#43=IFCPOLYLINE((#10,#40,#41,#42,#10));
#15=IFCSWEPTDISKSOLID(#43,5.,$);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Loop',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        double section = 0.5 * 32 * 25 * Math.Sin(2 * Math.PI / 32);
        double straight = section * 400;
        double volume = MeshOps.SignedVolume(solids[0]);
        return volume > straight * 0.6 && volume < straight && StepClosed(solids[0], "test_torus.stp");
    }

    static bool IfcRevolveHole()
    {
        string body = @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#50=IFCCARTESIANPOINT((20.,-5.));
#51=IFCCARTESIANPOINT((30.,-5.));
#52=IFCCARTESIANPOINT((30.,5.));
#53=IFCCARTESIANPOINT((20.,5.));
#54=IFCPOLYLINE((#50,#51,#52,#53,#50));
#55=IFCCARTESIANPOINT((23.,-2.));
#56=IFCCARTESIANPOINT((23.,2.));
#57=IFCCARTESIANPOINT((27.,2.));
#58=IFCCARTESIANPOINT((27.,-2.));
#59=IFCPOLYLINE((#55,#56,#57,#58,#55));
#13=IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,'R',#54,(#59));
#30=IFCCARTESIANPOINT((0.,0.,0.));
#31=IFCDIRECTION((0.,1.,0.));
#32=IFCAXIS1PLACEMENT(#30,#31);
#15=IFCREVOLVEDAREASOLID(#13,#11,#32,1.57079632679);
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#15));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Ring',$,$,#18,#17,$);
";
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + body + Footer(), warnings);
        if (solids.Count != 1 || warnings.Count != 0) return false;
        double expected = Math.PI / 2 * (100 * 25 - 16 * 25);
        return Near(MeshOps.SignedVolume(solids[0]), expected, expected * 0.01)
            && StepClosed(solids[0], "test_revolve_hole.stp");
    }

    static List<SolidMesh>? Parse(string data)
    {
        var warnings = new List<string>();
        var solids = TeklaIfcAsciiParser.ParseText(Header() + data + Footer(), warnings);
        if (warnings.Count > 0 && solids.Count == 0) return null;
        return solids.Count == 0 ? null : new List<SolidMesh>(solids);
    }

    static string RectBody(double x, double y, double depth, string item)
    {
        return @"
#5=IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.);
#6=IFCUNITASSIGNMENT((#5));
#10=IFCCARTESIANPOINT((0.,0.,0.));
#11=IFCAXIS2PLACEMENT3D(#10,$,$);
#12=IFCAXIS2PLACEMENT2D(#10,$);
#13=IFCRECTANGLEPROFILEDEF(.AREA.,'R',#12," + F(x) + "," + F(y) + @");
#14=IFCDIRECTION((0.,0.,1.));
#15=IFCEXTRUDEDAREASOLID(#13,#11,#14," + F(depth) + @");
#16=IFCSHAPEREPRESENTATION(#2,'Body','SweptSolid',(#" + ItemRef(item) + @"));
#17=IFCPRODUCTDEFINITIONSHAPE($,$,(#16));
#18=IFCLOCALPLACEMENT($,#11);
#19=IFCBEAM('b',$,'Rect',$,$,#18,#17,$);
".Replace("#" + ItemRef(item), item.StartsWith("IFC", StringComparison.Ordinal) ? "#90" : "#" + ItemRef(item))
         .Replace("(#90)", "(#90)") + (item.StartsWith("IFC", StringComparison.Ordinal) ? "#90=" + item + ";\n" : string.Empty);
    }

    static string ItemRef(string item)
    {
        return item.StartsWith("IFC", StringComparison.Ordinal) ? "90" : "15";
    }

    static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    static string Header()
    {
        return "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('test'),'2;1');\n" +
               "FILE_NAME('t.ifc','2026-11-14T00:00:00',(''),(''),'TeklaToStep','TeklaToStep','');\n" +
               "FILE_SCHEMA(('IFC2X3'));\nENDSEC;\nDATA;\n#2=IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-5,#4,$);\n#4=IFCAXIS2PLACEMENT3D(#10,$,$);\n";
    }

    static string Footer() => "ENDSEC;\nEND-ISO-10303-21;\n";

    public static SolidMesh BuildWebHoleBeam()
    {
        var mesh = new SolidMesh("I-beam-web-hole");
        var yz = new[]
        {
            new Vec3(0, -50, -100), new Vec3(0, 50, -100), new Vec3(0, 50, -88), new Vec3(0, 4, -88),
            new Vec3(0, 4, 88), new Vec3(0, 50, 88), new Vec3(0, 50, 100), new Vec3(0, -50, 100),
            new Vec3(0, -50, 88), new Vec3(0, -4, 88), new Vec3(0, -4, -88), new Vec3(0, -50, -88)
        };
        var start = new List<Vec3>(yz.Length);
        var end = new List<Vec3>(yz.Length);
        for (int i = 0; i < yz.Length; i++)
        {
            start.Add(yz[i]);
            end.Add(new Vec3(300, yz[i].Y, yz[i].Z));
        }
        mesh.Faces.Add(new Facet(end, new Vec3(1, 0, 0)));
        var startRev = new List<Vec3>(start);
        startRev.Reverse();
        mesh.Faces.Add(new Facet(startRev, new Vec3(-1, 0, 0)));
        for (int i = 0; i < start.Count; i++)
        {
            int j = (i + 1) % start.Count;
            var quad = new List<Vec3> { start[i], start[j], end[j], end[i] };
            var desired = Vec3.Cross(start[j] - start[i], new Vec3(300, 0, 0));
            var facet = new Facet(quad, desired.Normalized());
            if (i == 3)
            {
                facet.Holes.Add(new List<Vec3>
                {
                    new Vec3(130, 4, -30), new Vec3(170, 4, -30), new Vec3(170, 4, 30), new Vec3(130, 4, 30)
                });
            }
            else if (i == 9)
            {
                facet.Holes.Add(new List<Vec3>
                {
                    new Vec3(130, -4, -30), new Vec3(130, -4, 30), new Vec3(170, -4, 30), new Vec3(170, -4, -30)
                });
            }
            mesh.Faces.Add(facet);
        }
        mesh.Faces.Add(new Facet(new[]
        {
            new Vec3(130, -4, -30), new Vec3(130, 4, -30), new Vec3(130, 4, 30), new Vec3(130, -4, 30)
        }, new Vec3(1, 0, 0)));
        mesh.Faces.Add(new Facet(new[]
        {
            new Vec3(170, 4, -30), new Vec3(170, -4, -30), new Vec3(170, -4, 30), new Vec3(170, 4, 30)
        }, new Vec3(-1, 0, 0)));
        mesh.Faces.Add(new Facet(new[]
        {
            new Vec3(130, -4, -30), new Vec3(170, -4, -30), new Vec3(170, 4, -30), new Vec3(130, 4, -30)
        }, new Vec3(0, 0, 1)));
        mesh.Faces.Add(new Facet(new[]
        {
            new Vec3(130, 4, 30), new Vec3(170, 4, 30), new Vec3(170, -4, 30), new Vec3(130, -4, 30)
        }, new Vec3(0, 0, -1)));
        return mesh;
    }

    /// <summary>Structural check of the compact form: faces carry POLY_LOOP, no edge entities.</summary>
    public static List<string> ValidateFacetedStep(string text)
    {
        var errors = new List<string>();
        if (text.IndexOf("ISO-10303-21;", StringComparison.Ordinal) < 0) errors.Add("нет ISO-10303-21");
        if (text.IndexOf("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));", StringComparison.Ordinal) < 0) errors.Add("нет AUTOMOTIVE_DESIGN");
        if (text.IndexOf("SI_UNIT(.MILLI.,.METRE.)", StringComparison.Ordinal) < 0) errors.Add("нет миллиметров");
        if (text.IndexOf("FACETED_BREP", StringComparison.Ordinal) < 0) errors.Add("нет FACETED_BREP");
        if (text.IndexOf("ADVANCED_BREP_SHAPE_REPRESENTATION", StringComparison.Ordinal) < 0) errors.Add("нет ADVANCED_BREP_SHAPE_REPRESENTATION");
        if (text.IndexOf("END-ISO-10303-21;", StringComparison.Ordinal) < 0) errors.Add("нет конца файла");
        if (text.IndexOf("EDGE_CURVE(", StringComparison.Ordinal) >= 0) errors.Add("есть EDGE_CURVE: схема не компактная");
        if (text.IndexOf("VERTEX_POINT(", StringComparison.Ordinal) >= 0) errors.Add("есть VERTEX_POINT: схема не компактная");

        var defined = new HashSet<int>();
        var refs = new HashSet<int>();
        int faces = Count(text, "ADVANCED_FACE");
        int loops = Count(text, "POLY_LOOP");
        var pointCoords = new Dictionary<int, string>();
        var loopRefs = new List<int>();
        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            int id;
            if (!int.TryParse(line.Substring(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) continue;
            if (!defined.Add(id)) errors.Add("повторный #" + id.ToString(CultureInfo.InvariantCulture));
            CollectRefs(line, eq + 1, refs);
            int cp = line.IndexOf("=CARTESIAN_POINT(", StringComparison.Ordinal);
            if (cp >= 0) pointCoords[id] = line.Substring(cp + 17).Trim();
            int pl = line.IndexOf("=POLY_LOOP(", StringComparison.Ordinal);
            if (pl >= 0)
            {
                var local = new HashSet<int>();
                CollectRefs(line, pl, local);
                foreach (int r in local) loopRefs.Add(r);
            }
        }
        var idsByCoord = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < loopRefs.Count; i++)
        {
            string coords;
            if (!pointCoords.TryGetValue(loopRefs[i], out coords)) continue;
            int existing;
            if (idsByCoord.TryGetValue(coords, out existing) && existing != loopRefs[i])
                errors.Add("одна вершина записана дважды: " + coords);
            else
                idsByCoord[coords] = loopRefs[i];
        }
        foreach (int r in refs)
            if (!defined.Contains(r)) errors.Add("ссылка на несуществующий #" + r.ToString(CultureInfo.InvariantCulture));
        if (faces == 0) errors.Add("нет граней");
        if (loops != faces) errors.Add("POLY_LOOP " + loops.ToString(CultureInfo.InvariantCulture) + " != граней " + faces.ToString(CultureInfo.InvariantCulture));
        return errors;
    }

    public static List<string> ValidateStep(string text, bool requireManifold)
    {
        var errors = new List<string>();
        if (text.IndexOf("ISO-10303-21;", StringComparison.Ordinal) < 0) errors.Add("нет ISO-10303-21");
        if (text.IndexOf("FILE_SCHEMA(('AUTOMOTIVE_DESIGN'));", StringComparison.Ordinal) < 0) errors.Add("нет AUTOMOTIVE_DESIGN");
        if (text.IndexOf("SI_UNIT(.MILLI.,.METRE.)", StringComparison.Ordinal) < 0) errors.Add("нет миллиметров");
        if (text.IndexOf("MANIFOLD_SOLID_BREP", StringComparison.Ordinal) < 0) errors.Add("нет MANIFOLD_SOLID_BREP");
        if (text.IndexOf("ADVANCED_BREP_SHAPE_REPRESENTATION", StringComparison.Ordinal) < 0) errors.Add("нет ADVANCED_BREP_SHAPE_REPRESENTATION");
        if (text.IndexOf("END-ISO-10303-21;", StringComparison.Ordinal) < 0) errors.Add("нет конца файла");

        var defined = new HashSet<int>();
        var refs = new HashSet<int>();
        var curves = new HashSet<int>();
        var uses = new Dictionary<int, List<bool>>();
        var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] != '#') continue;
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            int id;
            if (!int.TryParse(line.Substring(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                continue;
            if (!defined.Add(id)) errors.Add("повторный #" + id.ToString(CultureInfo.InvariantCulture));
            CollectRefs(line, eq + 1, refs);
            if (line.IndexOf("=EDGE_CURVE(", StringComparison.Ordinal) >= 0) curves.Add(id);
            if (line.IndexOf("=ORIENTED_EDGE(", StringComparison.Ordinal) >= 0)
            {
                int hash = line.LastIndexOf('#');
                int comma = line.IndexOf(',', hash);
                int edgeId;
                if (hash > 0 && comma > hash && int.TryParse(line.Substring(hash + 1, comma - hash - 1), out edgeId))
                {
                    List<bool> flags;
                    if (!uses.TryGetValue(edgeId, out flags))
                    {
                        flags = new List<bool>();
                        uses[edgeId] = flags;
                    }
                    flags.Add(line.IndexOf(".T.)", StringComparison.Ordinal) >= 0 && line.IndexOf(".F.)", StringComparison.Ordinal) < 0);
                }
            }
        }
        foreach (int r in refs)
        {
            if (!defined.Contains(r)) errors.Add("висячая ссылка #" + r.ToString(CultureInfo.InvariantCulture));
        }
        if (requireManifold)
        {
            foreach (int curve in curves)
            {
                List<bool> flags;
                if (!uses.TryGetValue(curve, out flags) || flags.Count != 2 || flags[0] == flags[1])
                    errors.Add("ребро #" + curve.ToString(CultureInfo.InvariantCulture) + " не имеет двух противоположных ORIENTED_EDGE");
            }
        }
        return errors;
    }

    static void CollectRefs(string line, int from, HashSet<int> refs)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] != '#') continue;
            int j = i + 1;
            int start = j;
            while (j < line.Length && char.IsDigit(line[j])) j++;
            int id;
            if (j > start && int.TryParse(line.Substring(start, j - start), out id)) refs.Add(id);
            i = j - 1;
        }
    }

    static int Count(string text, string token)
    {
        int n = 0;
        int i = 0;
        while (true)
        {
            i = text.IndexOf(token, i, StringComparison.Ordinal);
            if (i < 0) return n;
            n++;
            i += token.Length;
        }
    }

    static bool Near(double value, double expected, double tol) => Math.Abs(value - expected) <= tol;
}
