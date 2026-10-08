# -*- coding: utf-8 -*-
"""Offline settings apply, undo/redo, source protection and folder refresh probe."""
import json
import os
import System
from System import Array, Object, Decimal
from System.IO import Directory, File
from Grasshopper.Kernel import GH_Document
from Grasshopper.Kernel.Special import GH_NumberSlider, GH_Panel
from Grasshopper.Kernel.Types import GH_String, GH_Boolean


def run(assembly, report):
    old_solutions = GH_Document.EnableSolutions
    documents = []
    checks = []
    core = System.Reflection.Assembly.LoadFrom(os.path.join(os.path.dirname(assembly.Location), "Lugworm.Core.dll"))
    parse = core.GetType("Lugworm.Core.Settings.ComponentSettings", True).GetMethod("Parse")
    io = assembly.GetType("Lugworm.Ui.SettingsIo", True)
    apply = io.GetMethod("Apply")

    def document():
        doc = GH_Document()
        doc.Enabled = True
        documents.append(doc)
        return doc

    def component(doc, name):
        comp = System.Activator.CreateInstance(assembly.GetType("Lugworm.Components." + name + "Component", True))
        comp.CreateAttributes()
        doc.AddObject(comp, False)
        return comp

    def param(comp, name):
        return next(p for p in comp.Params.Input if p.Name == name)

    def slider(doc, comp, name, value):
        sl = GH_NumberSlider()
        sl.CreateAttributes()
        sl.Slider.Minimum = Decimal(0)
        sl.Slider.Maximum = Decimal(2000)
        sl.Slider.DecimalPlaces = 3
        sl.SetSliderValue(Decimal(value))
        doc.AddObject(sl, False)
        param(comp, name).AddSource(sl)
        return sl

    def text(key, inputs, groups=()):
        return json.dumps({"iamlab-settings": 1, "component": key, "inputs": inputs, "groups": list(groups)})

    def load(comp, inputs, groups=()):
        settings = parse.Invoke(None, Array[Object]([text(comp.SettingsKey, inputs, groups)]))
        return apply.Invoke(None, Array[Object]([comp, settings, "probe"]))

    def solve(doc, comp):
        GH_Document.EnableSolutions = True
        comp.ExpireSolution(False)
        doc.NewSolution(False)
        GH_Document.EnableSolutions = False

    def output(comp, name):
        p = next(p for p in comp.Params.Output if p.Name == name)
        return "\n".join(str(g) for g in p.VolatileData.AllData(True))

    try:
        GH_Document.EnableSolutions = False
        doc = document()
        comp = component(doc, "Slicer")
        original_id = comp.InstanceGuid
        sl = slider(doc, comp, "layerHeight", 2)
        sl.Slider.Maximum = Decimal(5)
        slider_id = sl.InstanceGuid
        before = set(o.InstanceGuid for o in doc.Objects)
        undo_count = doc.UndoServer.UndoCount
        load(comp, {"layerHeight": 40.125, "brickShift": 0.25}, ["bricklayer"])
        assert float(sl.CurrentValue) == 40.125, "Existing slider was not updated"
        assert float(sl.Slider.Maximum) == 40.125, "Slider range was not widened"
        assert doc.UndoServer.UndoCount == undo_count + 1, "Load must record exactly one undo"
        after = set(o.InstanceGuid for o in doc.Objects)
        assert len(after) > len(before), "No new inputs created"
        doc.UndoServer.PerformUndo()
        assert set(o.InstanceGuid for o in doc.Objects) == before, "Undo left created inputs on the canvas"
        comp = doc.FindObject(original_id, False)
        sl = doc.FindObject(slider_id, False)
        assert float(sl.CurrentValue) == 2, "Undo did not restore the existing slider"
        assert float(sl.Slider.Maximum) == 5, "Undo did not restore the slider range"
        assert not any(p.Name == "brickShift" for p in comp.Params.Input), "Undo did not restore groups"
        doc.UndoServer.PerformRedo()
        comp = doc.FindObject(original_id, False)
        sl = doc.FindObject(slider_id, False)
        assert set(o.InstanceGuid for o in doc.Objects) == after, "Redo did not restore created inputs"
        assert float(sl.CurrentValue) == 40.125, "Redo did not restore slider value"
        assert param(comp, "brickShift").SourceCount == 1, "Redo lost the group input wire"
        checks.append("single apply restores objects, wires, groups and slider values in one undo/redo")

        doc = document()
        first = component(doc, "PrintBed")
        second = component(doc, "PrintBed")
        sl = slider(doc, first, "width", 600)
        param(second, "width").AddSource(sl)
        result = load(first, {"width": 800})
        assert float(sl.CurrentValue) == 600, "Single apply changed an outside recipient"
        assert any("shared source" in s for s in result.Skipped), "Missing shared-source explanation"
        panel = GH_Panel()
        panel.CreateAttributes()
        panel.UserText = "original"
        doc.AddObject(panel, False)
        upstream = GH_Panel()
        upstream.CreateAttributes()
        upstream.UserText = "700"
        doc.AddObject(upstream, False)
        panel.AddSource(upstream)
        param(first, "depth").RemoveAllSources()
        param(first, "depth").AddSource(panel)
        result = load(first, {"depth": 900})
        assert panel.UserText == "original", "A panel carrying upstream data was overwritten"
        assert any("upstream data" in s for s in result.Skipped), "Missing upstream-data explanation"
        checks.append("shared controls and panels with incoming wires are protected")

        folder = os.path.join(os.path.dirname(os.environ["IAM_CONTRACT_RESULT"]), "settings-fixtures")
        Directory.CreateDirectory(folder)
        path = os.path.join(folder, "bed.lwset.json")
        File.WriteAllText(path, text("printbed", {"width": 800}))
        loader = component(doc, "LoadSettings")
        param(loader, "folder").PersistentData.Append(GH_String(folder))
        solve(doc, loader)
        # The loader is now current. Edit disk without expiring it, then click Apply.
        File.WriteAllText(path, text("printbed", {"width": 1234.5}))
        before = set(o.InstanceGuid for o in doc.Objects)
        old_undo_count = doc.UndoServer.UndoCount
        loader.OnCapsulePressed("apply")
        assert float(sl.CurrentValue) == 1234.5, "Apply used the stale folder index"
        assert doc.UndoServer.UndoCount == old_undo_count + 1, "Canvas apply is not one undo"
        # Solve again to prove diagnostics survive runtime-message clearing.
        solve(doc, loader)
        assert "Last apply:" in output(loader, "info"), "Apply report disappeared on solve"
        assert "upstream data" in output(loader, "info"), "Canvas report lost skip reasons"
        assert "updated" in output(loader, "info"), "Canvas report lost per-input actions"
        doc.UndoServer.PerformUndo()
        assert set(o.InstanceGuid for o in doc.Objects) == before, "Canvas undo left newly added objects"
        sl = doc.FindObject(sl.InstanceGuid, False)
        assert float(sl.CurrentValue) == 600, "Canvas undo did not restore shared control"
        doc.UndoServer.PerformRedo()
        sl = doc.FindObject(sl.InstanceGuid, False)
        assert float(sl.CurrentValue) == 1234.5, "Canvas redo did not restore shared control"
        checks.append("canvas apply snapshots targets, refreshes disk, updates agreeing recipients and retains diagnostics")

        # Conflicting values for two inputs fed by one source must have no winner.
        first = doc.FindObject(first.InstanceGuid, False)
        second = doc.FindObject(second.InstanceGuid, False)
        param(first, "depth").RemoveAllSources()
        param(first, "depth").AddSource(sl)
        param(second, "depth").RemoveAllSources()
        param(second, "depth").AddSource(sl)
        File.WriteAllText(path, text("printbed", {"width": 900, "depth": 1000}))
        loader.OnCapsulePressed("apply")
        assert float(sl.CurrentValue) == 1234.5, "Conflicting requests picked an arbitrary winner"
        checks.append("conflicting shared-source requests leave the source unchanged")

        # Frozen scans retain their values; the next watched apply refreshes them.
        param(loader, "watch").PersistentData.Clear()
        param(loader, "watch").PersistentData.Append(GH_Boolean(False))
        solve(doc, loader)
        File.WriteAllText(path, text("printbed", {"width": 1100, "depth": 1100}))
        loader.OnCapsulePressed("apply")
        assert float(sl.CurrentValue) == 1234.5, "watch=false unexpectedly refreshed the index"
        param(loader, "watch").PersistentData.Clear()
        param(loader, "watch").PersistentData.Append(GH_Boolean(True))
        solve(doc, loader)
        loader.OnCapsulePressed("apply")
        assert float(sl.CurrentValue) == 1100, "watch=true did not refresh after frozen scan"
        checks.append("watch=false retains the scan and watch=true resumes refresh")
        report["settings_checks"] = checks
    finally:
        for doc in documents:
            doc.Dispose()
        GH_Document.EnableSolutions = old_solutions
