# -*- coding: utf-8 -*-
"""Offline GH probe for Pareto Select: real component, real archive, no window.

Run through verify-rhino-contracts.ps1 -Probe pareto-selection. The front is the
plan's hand-derived A/B/C fixture; nothing here calls the optimizer.
"""
import math
import System
from System.Reflection import BindingFlags
from GH_IO.Serialization import GH_Archive
from Grasshopper.Kernel import GH_Document
from Grasshopper.Kernel.Data import GH_Path
from Grasshopper.Kernel.Types import GH_Number, GH_String, GH_Integer


FLAGS = BindingFlags.Instance | BindingFlags.NonPublic
S18 = math.sqrt(0.18)
FRONT = [[10.0, 0.2, 0.9, S18], [20.0, 0.6, 0.6, 0.6], [30.0, 0.9, 0.2, S18]]


def close(a, b, label, tolerance=1e-9):
    assert abs(a - b) <= tolerance, "%s: expected %.12g, got %.12g" % (label, b, a)


def values(component, output_name):
    param = next(p for p in component.Params.Output if p.Name == output_name)
    return [goo.Value for goo in param.VolatileData.AllData(True)]


def set_list(component, name, items):
    param = next(p for p in component.Params.Input if p.Name == name)
    param.PersistentData.Clear()
    for item in items:
        param.PersistentData.Append(item)


def set_tree(component, name, rows):
    param = next(p for p in component.Params.Input if p.Name == name)
    param.PersistentData.Clear()
    for i, row in enumerate(rows):
        for v in row:
            param.PersistentData.Append(GH_Number(v), GH_Path(i))


def create(assembly, document):
    component = System.Activator.CreateInstance(assembly.GetType("Lugworm.Components.ParetoSelectComponent", True))
    component.CreateAttributes()
    document.AddObject(component, False)
    return component


def field(component, name):
    return component.GetType().GetField(name, FLAGS).GetValue(component)


def run(assembly, report):
    original_solutions = GH_Document.EnableSolutions
    document = GH_Document()
    document.Enabled = True
    try:
        GH_Document.EnableSolutions = False
        picker = create(assembly, document)
        set_tree(picker, "front", FRONT)
        set_list(picker, "factorNames", [GH_String("feed")])
        set_list(picker, "objectiveNames", [GH_String("width desirability"), GH_String("speed desirability")])
        GH_Document.EnableSolutions = True
        document.NewSolution(False)
        assert values(picker, "settings") == [], "no selection must emit empty settings"
        assert values(picker, "current") == [False], "no selection is not current"
        assert field(picker, "_form") is None, "solve must not open a window"

        # graphical path: the same SelectRow every UI path uses (row 1 = B)
        picker.GetType().GetMethod("SelectRow", FLAGS).Invoke(picker, System.Array[System.Object]([1]))
        document.NewSolution(False)
        close(values(picker, "settings")[0], 20.0, "selected feed")
        di = values(picker, "di")
        close(di[0], 0.6, "d1"); close(di[1], 0.6, "d2")
        close(values(picker, "D")[0], 0.6, "D")
        assert values(picker, "current") == [True], "selection on the live front is current"
        key = values(picker, "key")[0]
        assert key.startswith("iamps1:"), "key token"

        # reorder the front: still B, same key
        set_tree(picker, "front", [FRONT[2], FRONT[0], FRONT[1]])
        picker.ExpireSolution(False)
        document.NewSolution(False)
        assert values(picker, "current") == [True], "reordering keeps the selection"
        assert values(picker, "key")[0] == key, "reordering keeps the key"

        # archive round-trip: destroy, recreate, read with NO upstream data
        archive = GH_Archive()
        chunk = archive.CreateTopLevelNode("probe")
        assert picker.Write(chunk), "Write"
        document.RemoveObject(picker, False)
        GH_Document.EnableSolutions = False
        restored = create(assembly, document)
        assert restored.Read(chunk), "Read"
        GH_Document.EnableSolutions = True
        document.NewSolution(False)
        close(values(restored, "settings")[0], 20.0, "restored feed")
        rdi = values(restored, "di")
        close(rdi[0], 0.6, "restored d1"); close(rdi[1], 0.6, "restored d2")
        close(values(restored, "D")[0], 0.6, "restored D")
        assert values(restored, "current") == [False], "restored without a front is not current"
        assert "current front unavailable" in values(restored, "status")[0], "restored status"
        assert field(restored, "_archived") is True, "archived plot flag"
        assert field(restored, "_form") is None, "Read must not open a window"
        assert field(restored, "_strip") is not None, "archived front still draws"

        # a replaced front makes the stored recipe stale, and headless key wins over index
        set_tree(restored, "front", [FRONT[0], [21.0, 0.61, 0.59, math.sqrt(0.3599)], FRONT[2]])
        set_list(restored, "factorNames", [GH_String("feed")])
        set_list(restored, "objectiveNames", [GH_String("width desirability"), GH_String("speed desirability")])
        restored.ExpireSolution(False)
        document.NewSolution(False)
        assert values(restored, "current") == [False], "replaced front is stale"
        close(values(restored, "settings")[0], 20.0, "stale recipe keeps its numbers")
        set_list(restored, "selectionKey", [GH_String(key)])
        set_list(restored, "selectionIndex", [GH_Integer(0)])
        restored.ExpireSolution(False)
        document.NewSolution(False)
        assert values(restored, "current") == [False], "a key from another front is refused, index not applied"
        assert "different front" in values(restored, "status")[0], "refusal names the reason"
        close(values(restored, "settings")[0], 20.0, "no fallback to the index")

        # copy/paste = Write + Read into a second instance: independent arrays, no shared form
        chunk2 = GH_Archive().CreateTopLevelNode("copy")
        assert restored.Write(chunk2)
        GH_Document.EnableSolutions = False
        copy = create(assembly, document)
        assert copy.Read(chunk2)
        GH_Document.EnableSolutions = True
        document.NewSolution(False)
        assert field(copy, "_state") is not field(restored, "_state"), "copies do not share state"
        assert field(copy, "_form") is None
        # Additive reliability payload: feasible, predictions, half widths, exact PI bounds,
        # leverage and extrapolation. The archive retains it without an upstream solve.
        rich = [row + [float('nan'), 7.8, 2.1, 0.4, 0.9, 7.4, 1.2, 8.2, 3.0, 0.8, 1.0] for row in FRONT]
        set_list(restored, "selectionKey", [])
        set_list(restored, "selectionIndex", [GH_Integer(1)])
        set_tree(restored, "front", rich)
        restored.ExpireSolution(False)
        document.NewSolution(False)
        reliability = field(restored, "_state").Recipe.Reliability
        assert reliability is not None and reliability.ExtrapolationFlag
        close(reliability.PiHalf[0], 0.4, "PI half width")
        rich_chunk = GH_Archive().CreateTopLevelNode("reliability")
        assert restored.Write(rich_chunk)
        assert copy.Read(rich_chunk)
        assert field(copy, "_state").Recipe.Reliability.ExtrapolationFlag
        report.setdefault("pareto_selection", []).append({"passed": True, "key": key})
    finally:
        document.Dispose()
        GH_Document.EnableSolutions = original_solutions
