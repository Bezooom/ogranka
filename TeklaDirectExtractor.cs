using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
#if !NETFRAMEWORK
using System.Runtime.Loader;
#endif

namespace TeklaToStep;

/// <summary>
/// Reads solids of the objects selected in a running Tekla Structures session.
/// The net48 build loads Tekla Open API assemblies from the Tekla bin folder, so one
/// executable works across Tekla 2022-2026 without a compile-time reference.
/// </summary>
public static class TeklaDirectExtractor
{
    static string? _bin;
    static bool _hooked;

    public static IReadOnlyList<SolidMesh> ExtractSelected(string? teklaBin, IList<string>? warnings = null)
    {
        var list = warnings ?? new List<string>();
#if TEKLA
        string? bin = FindTeklaBin(teklaBin);
        if (string.IsNullOrEmpty(bin))
        {
            list.Add("Не найден каталог bin Tekla. Укажите --tekla-bin или переменную TEKLA_BIN.");
            return new List<SolidMesh>();
        }
        HookResolve(bin);
        return ExtractTyped(list);
#else
        return ExtractReflective(teklaBin, list);
#endif
    }

    public static string? FindTeklaBin(string? explicitBin)
    {
        string? found = NormalizeBin(explicitBin);
        if (found != null) return found;
        found = NormalizeBin(Environment.GetEnvironmentVariable("TEKLA_BIN"));
        if (found != null) return found;
        found = NormalizeBin(Environment.GetEnvironmentVariable("XSBIN"));
        if (found != null) return found;
        found = FromRunningProcess();
        if (found != null) return found;
        found = FromRegistry();
        if (found != null) return found;
        return FromProgramFiles();
    }

#if TEKLA
    static IReadOnlyList<SolidMesh> ExtractTyped(IList<string> warnings)
    {
        var result = new List<SolidMesh>();
        Tekla.Structures.Model.Model model;
        try
        {
            model = new Tekla.Structures.Model.Model();
        }
        catch (Exception ex)
        {
            warnings.Add("Не удалось создать Tekla.Structures.Model.Model: " + ex.Message);
            return result;
        }
        if (!model.GetConnectionStatus())
        {
            warnings.Add("Tekla Structures не подключена. Откройте модель и повторите --live.");
            return result;
        }
        var selected = model.GetModelObjectSelector().GetSelectedObjects();
        int seen = 0;
        while (selected.MoveNext())
        {
            seen++;
            object current = selected.Current;
            if (current is Tekla.Structures.Model.Part part)
                AddTypedPart(part, result, warnings);
            else if (current is Tekla.Structures.Model.Assembly assembly)
            {
                AddTypedPart(assembly.GetMainPart() as Tekla.Structures.Model.Part, result, warnings);
                foreach (object secondary in assembly.GetSecondaries())
                    AddTypedPart(secondary as Tekla.Structures.Model.Part, result, warnings);
            }
            else
                AddReflected(current, result, warnings);
        }
        if (seen == 0)
            warnings.Add("В Tekla ничего не выбрано.");
        return result;
    }

    static void AddTypedPart(Tekla.Structures.Model.Part? part, List<SolidMesh> result, IList<string> warnings)
    {
        if (part == null) return;
        Tekla.Structures.Model.Solid? solid;
        try
        {
            solid = part.GetSolid(Tekla.Structures.Model.Solid.SolidCreationTypeEnum.NORMAL);
        }
        catch (Exception ex)
        {
            warnings.Add("GetSolid не удался для " + part.GetType().Name + ": " + ex.Message);
            return;
        }
        if (solid == null)
        {
            warnings.Add(part.GetType().Name + " не дал Solid.");
            return;
        }
        var mesh = MeshFromSolid(solid, TypedName(part), warnings);
        if (mesh != null) result.Add(mesh);
    }

    static string TypedName(Tekla.Structures.Model.Part part)
    {
        string name = part.Name;
        if (string.IsNullOrEmpty(name)) name = part.GetType().Name;
        return name + "-" + part.Identifier.ID.ToString();
    }
#endif

    static IReadOnlyList<SolidMesh> ExtractReflective(string? teklaBin, IList<string> warnings)
    {
        var result = new List<SolidMesh>();
        if (!IsWindows())
        {
            warnings.Add("Tekla Open API есть только в Windows. Для этой машины используйте --ifc.");
            return result;
        }
        string? bin = FindTeklaBin(teklaBin);
        if (bin == null)
        {
            warnings.Add("Не найден каталог bin Tekla. Укажите --tekla-bin или переменную TEKLA_BIN.");
            return result;
        }
        try
        {
            HookResolve(bin);
            string modelPath = Path.Combine(bin, "Tekla.Structures.Model.dll");
            string corePath = Path.Combine(bin, "Tekla.Structures.dll");
            if (!File.Exists(modelPath) || !File.Exists(corePath))
            {
                warnings.Add("В " + bin + " нет Tekla.Structures.Model.dll.");
                return result;
            }
            Assembly.LoadFrom(corePath);
            var modelAsm = Assembly.LoadFrom(modelPath);
            var modelType = modelAsm.GetType("Tekla.Structures.Model.Model");
            if (modelType == null)
            {
                warnings.Add("В сборке нет типа Tekla.Structures.Model.Model.");
                return result;
            }
            object? model = Activator.CreateInstance(modelType);
            if (model == null)
            {
                warnings.Add("Не удалось создать Model.");
                return result;
            }
            var connected = modelType.GetMethod("GetConnectionStatus");
            if (connected == null || !(bool)connected.Invoke(model, null)!)
            {
                warnings.Add("Tekla Structures не подключена. Откройте модель и повторите --live.");
                return result;
            }
            var selector = modelType.GetMethod("GetModelObjectSelector")?.Invoke(model, null);
            var selected = selector?.GetType().GetMethod("GetSelectedObjects")?.Invoke(selector, null);
            if (selected == null)
            {
                warnings.Add("GetSelectedObjects недоступен в этой версии Open API.");
                return result;
            }
            int seen = WalkEnumerator(selected, delegate(object current) { AddReflected(current, result, warnings); });
            if (seen == 0)
                warnings.Add("В Tekla ничего не выбрано.");
        }
        catch (Exception ex)
        {
            warnings.Add("Ошибка подключения к Tekla: " + ex.GetType().Name + ": " + ex.Message);
            warnings.Add("Для --live запускайте сборку net48 x64 на машине с открытой моделью Tekla.");
        }
        return result;
    }

    static void AddReflected(object? current, List<SolidMesh> result, IList<string> warnings)
    {
        if (current == null) return;
        string typeName = current.GetType().Name;
        if (typeName == "Assembly")
        {
            var main = current.GetType().GetMethod("GetMainPart")?.Invoke(current, null);
            AddReflected(main, result, warnings);
            var secondaries = current.GetType().GetMethod("GetSecondaries")?.Invoke(current, null) as IEnumerable;
            if (secondaries == null) return;
            foreach (object secondary in secondaries)
                AddReflected(secondary, result, warnings);
            return;
        }
        object? solid = TryGetSolid(current);
        if (solid == null)
        {
            if (typeName.IndexOf("ReferenceModel", StringComparison.Ordinal) >= 0)
                warnings.Add(typeName + ": выберите отдельные объекты ссылочной модели, у контейнера нет GetSolid().");
            else
                warnings.Add(typeName + " пропущен: нет Solid.");
            return;
        }
        var mesh = MeshFromSolid(solid, ReflectedName(current), warnings);
        if (mesh != null) result.Add(mesh);
    }

    static object? TryGetSolid(object current)
    {
        var type = current.GetType();
        foreach (var method in type.GetMethods())
        {
            if (method.Name != "GetSolid") continue;
            var parameters = method.GetParameters();
            try
            {
                if (parameters.Length == 0)
                    return method.Invoke(current, null);
                if (parameters.Length == 1 && parameters[0].ParameterType.IsEnum)
                {
                    object normal = Enum.Parse(parameters[0].ParameterType, "NORMAL");
                    return method.Invoke(current, new[] { normal });
                }
            }
            catch (TargetInvocationException)
            {
                return null;
            }
        }
        return null;
    }

    static SolidMesh? MeshFromSolid(object solid, string name, IList<string> warnings)
    {
        object? faces = solid.GetType().GetMethod("GetFaceEnumerator")?.Invoke(solid, null);
        if (faces == null)
        {
            warnings.Add(name + ": Solid без FaceEnumerator.");
            return null;
        }
        var mesh = new SolidMesh(name);
        WalkEnumerator(faces, delegate(object face)
        {
            var facet = new Facet();
            facet.Normal = ReadXyz(face.GetType().GetProperty("Normal")?.GetValue(face, null));
            object? loops = face.GetType().GetMethod("GetLoopEnumerator")?.Invoke(face, null);
            if (loops == null) return;
            bool first = true;
            WalkEnumerator(loops, delegate(object loop)
            {
                object? vertices = loop.GetType().GetMethod("GetVertexEnumerator")?.Invoke(loop, null);
                if (vertices == null) return;
                var pts = new List<Vec3>();
                WalkEnumerator(vertices, delegate(object point) { pts.Add(ReadXyz(point)); });
                if (pts.Count < 3) return;
                if (first)
                {
                    facet.Outer = pts;
                    first = false;
                }
                else facet.Holes.Add(pts);
            });
            if (facet.Outer.Count >= 3) mesh.Faces.Add(facet);
        });
        if (mesh.Faces.Count == 0)
        {
            warnings.Add(name + ": пустой Solid.");
            return null;
        }
        return mesh;
    }

    static int WalkEnumerator(object enumerator, Action<object> visit)
    {
        var move = enumerator.GetType().GetMethod("MoveNext");
        var current = enumerator.GetType().GetProperty("Current");
        if (move == null || current == null) return 0;
        int count = 0;
        while ((bool)move.Invoke(enumerator, null)!)
        {
            object? item = current.GetValue(enumerator, null);
            if (item == null) continue;
            count++;
            visit(item);
        }
        return count;
    }

    static Vec3 ReadXyz(object? point)
    {
        if (point == null) return new Vec3(0, 0, 0);
        var type = point.GetType();
        double x = Convert.ToDouble(type.GetProperty("X")?.GetValue(point, null) ?? 0.0);
        double y = Convert.ToDouble(type.GetProperty("Y")?.GetValue(point, null) ?? 0.0);
        double z = Convert.ToDouble(type.GetProperty("Z")?.GetValue(point, null) ?? 0.0);
        return new Vec3(x, y, z);
    }

    static string ReflectedName(object current)
    {
        string? name = current.GetType().GetProperty("Name")?.GetValue(current, null) as string;
        if (string.IsNullOrEmpty(name))
        {
            var report = current.GetType().GetMethod("GetReportProperty", new[] { typeof(string), typeof(string).MakeByRefType() });
            if (report != null)
            {
                object?[] args = { "NAME", string.Empty };
                try
                {
                    report.Invoke(current, args);
                    name = args[1] as string;
                }
                catch (TargetInvocationException)
                {
                    name = null;
                }
            }
        }
        object? id = current.GetType().GetProperty("Identifier")?.GetValue(current, null);
        object? idValue = id?.GetType().GetProperty("ID")?.GetValue(id, null);
        if (string.IsNullOrEmpty(name)) name = current.GetType().Name;
        return idValue == null ? name! : name + "-" + idValue;
    }

    static void HookResolve(string bin)
    {
        _bin = bin;
        if (_hooked) return;
        _hooked = true;
#if NETFRAMEWORK
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
        {
            string? name = new AssemblyName(args.Name).Name;
            return name == null ? null : LoadSibling(name);
        };
#else
        AssemblyLoadContext.Default.Resolving += delegate(AssemblyLoadContext context, AssemblyName name)
        {
            return name.Name == null ? null : LoadSibling(name.Name);
        };
#endif
    }

    static Assembly? LoadSibling(string simpleName)
    {
        if (_bin == null) return null;
        string path = Path.Combine(_bin, simpleName + ".dll");
        if (!File.Exists(path)) return null;
        return Assembly.LoadFrom(path);
    }

    static string? FromRunningProcess()
    {
        if (!IsWindows()) return null;
        try
        {
            var processes = Process.GetProcessesByName("TeklaStructures");
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    string? file = processes[i].MainModule?.FileName;
                    if (file == null) continue;
                    string? bin = NormalizeBin(Path.GetDirectoryName(file));
                    if (bin != null) return bin;
                }
                catch (Exception)
                {
                    // A protected Tekla process does not expose MainModule. Keep scanning.
                }
            }
        }
        catch (Exception)
        {
            return null;
        }
        return null;
    }

    static string? FromRegistry()
    {
        if (!IsWindows()) return null;
        try
        {
            var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Tekla\Structures");
            if (root == null) return null;
            using (root)
            {
                string? best = null;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using (var key = root.OpenSubKey(sub))
                    {
                        if (key == null) continue;
                        foreach (string valueName in new[] { "MainDir", "InstallDir", "InstallPath", "ProgramDirectory" })
                        {
                            string? dir = key.GetValue(valueName) as string;
                            string? bin = NormalizeBin(dir);
                            if (bin != null) best = bin;
                        }
                    }
                }
                return best;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string? FromProgramFiles()
    {
        if (!IsWindows()) return null;
        var roots = new List<string>();
        AddIfSet(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        AddIfSet(roots, Environment.GetEnvironmentVariable("ProgramFiles(x86)"));
        AddIfSet(roots, @"C:\Program Files");
        AddIfSet(roots, @"D:\TeklaStructures");
        string? best = null;
        string bestName = string.Empty;
        for (int i = 0; i < roots.Count; i++)
        {
            string parent = Path.Combine(roots[i], "Tekla Structures");
            if (!Directory.Exists(parent)) continue;
            string[] versions;
            try { versions = Directory.GetDirectories(parent); }
            catch (IOException) { continue; }
            for (int v = 0; v < versions.Length; v++)
            {
                string? bin = NormalizeBin(versions[v]);
                if (bin == null) continue;
                string name = Path.GetFileName(versions[v]);
                if (best == null || string.Compare(name, bestName, StringComparison.Ordinal) > 0)
                {
                    best = bin;
                    bestName = name;
                }
            }
        }
        return best;
    }

    static void AddIfSet(List<string> roots, string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) roots.Add(path);
    }

    public static string? NormalizeBin(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (File.Exists(Path.Combine(path, "Tekla.Structures.Model.dll"))) return path;
        string bin = Path.Combine(path, "bin");
        if (File.Exists(Path.Combine(bin, "Tekla.Structures.Model.dll"))) return bin;
        string nt = Path.Combine(path, "nt", "bin");
        if (File.Exists(Path.Combine(nt, "Tekla.Structures.Model.dll"))) return nt;
        return null;
    }

    static bool IsWindows()
    {
#if NETFRAMEWORK
        return true;
#else
        return OperatingSystem.IsWindows();
#endif
    }
}
