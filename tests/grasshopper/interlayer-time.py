# -*- coding: utf-8 -*-
"""Offline repeated Interlayer solves, cache identity and colored layer geometry."""
import System
import Rhino
from System import Array, Byte, String
from System.Collections.Generic import List
from System.Drawing import Color
from System.IO import File, FileInfo, Path
from System.Reflection import BindingFlags
from Grasshopper.Kernel import GH_Document
from Grasshopper.Kernel.Types import GH_String, GH_Integer

FLAGS = BindingFlags.Instance | BindingFlags.NonPublic
PUBLIC = BindingFlags.Instance | BindingFlags.Public


def field(obj, name):
    return obj.GetType().GetField(name, FLAGS | PUBLIC).GetValue(obj)


def set_input(component, name, values):
    param = next(p for p in component.Params.Input if p.Name == name)
    param.PersistentData.Clear()
    for value in values:
        param.PersistentData.Append(value)


def values(component, name):
    param = next(p for p in component.Params.Output if p.Name == name)
    return [goo.Value for goo in param.VolatileData.AllData(True)]


def solve(component, document):
    component.ExpireSolution(False)
    document.NewSolution(False)


def run(assembly, report):
    rhino_doc = Rhino.RhinoDoc.ActiveDoc
    if rhino_doc is None:
        assert Rhino.RhinoApp.RunScript("_-New _None", False)
        rhino_doc = Rhino.RhinoDoc.ActiveDoc
    original_units = rhino_doc.ModelUnitSystem
    original_solutions = GH_Document.EnableSolutions
    document = GH_Document()
    document.Enabled = True
    source_file = Path.GetTempFileName()
    try:
        GH_Document.EnableSolutions = False
        component_type = assembly.GetType("Lugworm.Components.InterlayerTimeComponent", True)
        component = System.Activator.CreateInstance(component_type)
        component.CreateAttributes()
        base_type = assembly.GetType("Lugworm.Ui.IamComponentBase", True)
        active = base_type.GetField("Active", FLAGS).GetValue(component)
        active.Add("display")
        base_type.GetMethod("SyncInputs", FLAGS).Invoke(component, Array[System.Object]([False, False]))
        document.AddObject(component, False)
        icon = component_type.GetProperty("Icon", FLAGS).GetValue(component, None)
        assert icon is not None and icon.Width == 24 and icon.Height == 24, "Interlayer icon is not 24 px"
        lines = ["; LAYERHEIGHT=2", "; LINEWIDTH=8", "G21", "G90", "M83", "G0 X0 Y0 Z0 F3000"]
        for layer in (1, 2):
            z = (layer - 1) * 2
            lines.append("; layer %d/2" % layer)
            if layer > 1:
                lines.append("G1 Z%d F600" % z)
            for x, y in ((10, 0), (10, 10), (0, 10), (0, 0)):
                lines.append("G1 X%d Y%d Z%d F600 E1" % (x, y, z))
        set_input(component, "gcode", [GH_String("\n".join(lines))])
        set_input(component, "display", [GH_Integer(1)])
        GH_Document.EnableSolutions = True
        reference_times = None
        for unit, scale in ((Rhino.UnitSystem.Millimeters, 1.0), (Rhino.UnitSystem.Inches, 25.4)):
            rhino_doc.ModelUnitSystem = unit
            solve(component, document)
            paths = values(component, "path")
            assert len(paths) == 2 and values(component, "layers") == [2], "Expected two extrusion runs/layers"
            assert abs(paths[0].GetLength() * scale - 40.0) < 1e-6, "Cached path ignored document unit change"
            times = values(component, "times")
            assert len(times) == len(values(component, "colors")), "Color/time vertex correspondence lost"
            if reference_times is None:
                reference_times = times
            assert times == reference_times, "Document units changed physical interlayer seconds"
            draw = field(component, "_draw")
            layers = field(draw, "Layers")
            for layer_draw in layers.GetType().GetProperty("Values").GetValue(layers, None):
                mesh = field(layer_draw, "LayerMesh")
                assert mesh is not None and mesh.IsValid, "Missing/invalid layer mesh"
                assert mesh.VertexColors.Count == mesh.Vertices.Count, "Uncolored mesh vertices"
        # Simulate the completed background reader result. The actual Compute path
        # must distinguish equal decoded lines with different exact file offsets.
        source_type = component_type.GetNestedType("SourceRead", BindingFlags.NonPublic)
        reader = field(component, "_gcodeReader")
        previous_analysis = None
        for newline, bom in (("\n", False), ("\r\n", False), ("\r\n", True)):
            data = System.Text.Encoding.UTF8.GetBytes(newline.join(lines) + newline)
            if bom:
                data = Array[Byte](list(System.Text.Encoding.UTF8.GetPreamble()) + list(data))
            File.WriteAllBytes(source_file, data)
            info = FileInfo(source_file)
            read_key = "%s|%d|%d" % (info.FullName, info.Length, info.LastWriteTimeUtc.Ticks)
            source = System.Activator.CreateInstance(source_type, True)
            source_type.GetField("Bytes", PUBLIC).SetValue(source, data)
            source_type.GetField("Lines", PUBLIC).SetValue(source, List[String](Array[String](lines)))
            reader.GetType().GetField("_resultKey", FLAGS).SetValue(reader, read_key)
            reader.GetType().GetField("_result", FLAGS).SetValue(reader, source)
            set_input(component, "gcodePath", [GH_String(source_file)])
            solve(component, document)
            analysis = field(component, "_analysis")
            assert analysis is not None
            if previous_analysis is not None:
                assert not System.Object.ReferenceEquals(previous_analysis, analysis), "Exact bytes reused stale analysis"
            previous_analysis = analysis
        # Closed colored seams represent different deposition times at one point.
        from Lugworm.Core.Geometry import Vec3
        pts = List[Vec3]()
        for x, y in ((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)):
            pts.Add(Vec3(float(x), float(y), 0.0))
        colors = List[Color](Array[Color]([Color.Red, Color.Green, Color.Blue, Color.Yellow, Color.Purple]))
        preview = assembly.GetType("Lugworm.Ui.ExtrusionPreview", True)
        method = next(m for m in preview.GetMethods() if m.Name == "BuildLayer" and len(m.GetParameters()) == 4)
        mesh = method.Invoke(None, Array[System.Object]([pts, 8.0, 2.0, colors]))
        try:
            assert mesh.IsValid and mesh.VertexColors.Count == mesh.Vertices.Count
            assert mesh.VertexColors[0].ToArgb() == Color.Red.ToArgb()
            assert mesh.VertexColors[mesh.VertexColors.Count - 1].ToArgb() == Color.Purple.ToArgb(), "Closed seam lost endpoint color"
        finally:
            mesh.Dispose()
        # Dimension changes must invalidate even a stationary, decimated preview.
        cache_type = preview.GetNestedType("Cache")
        cache = System.Activator.CreateInstance(cache_type)
        cached = preview.GetMethod("BuildCached")
        straight = List[Vec3]()
        flags = List[System.Boolean]()
        for i in range(100):
            straight.Add(Vec3(float(i), 0.0, 0.0))
            flags.Add(i > 0)
        previous = None
        for width, height, budget in ((8.0, 2.0, 10), (12.0, 2.0, 10),
                                      (12.0, 0.5, 10), (12.0, 0.5, 20)):
            args = Array[System.Object]([cache, "same-path", straight, flags,
                                       System.Int32(99), width, height,
                                       System.Int32(budget), System.Int32(400000)])
            layer = cached.Invoke(None, args)
            assert layer is not None and layer.IsValid and layer.IsClosed
            assert layer.SolidOrientation() == 1, "Layer normals face inward"
            bounds = layer.GetBoundingBox(True)
            assert abs(bounds.Max.Y - bounds.Min.Y - width) < 1e-5
            assert abs(bounds.Max.Z - bounds.Min.Z - height) < 1e-5, "Height clamped or stale"
            assert not System.Object.ReferenceEquals(previous, layer), "Dimension/budget change reused stale mesh"
            assert System.Object.ReferenceEquals(layer, cached.Invoke(None, args)), "Unchanged input rebuilt mesh"
            # Each lateral profile ring lies on the requested ellipse.
            for i in range(24):
                v = layer.Vertices[i]
                assert abs((v.Y / (width / 2)) ** 2 + (v.Z / (height / 2)) ** 2 - 1) < 1e-5
            previous = layer
        set_input(component, "gcodePath", [])
        set_input(component, "gcode", [GH_String("G21")])
        solve(component, document)
        assert not values(component, "path") and field(component, "_draw") is None, "Invalid input retained stale preview"
        report["interlayer_time"] = {"passed": True, "unit_scales": [1, 25.4], "exact_byte_variants": 3,
                                     "colored_closed_seam": True, "invalid_input_clears_preview": True}
    finally:
        document.Dispose()
        File.Delete(source_file)
        rhino_doc.ModelUnitSystem = original_units
        GH_Document.EnableSolutions = original_solutions
