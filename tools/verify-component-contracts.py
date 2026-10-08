# -*- coding: utf-8 -*-
"""Rhino-hosted component contract/archive gate (IronPython 2.7 compatible).

Run with tools/verify-rhino-contracts.ps1, not CPython. Pure comparison is
importable by tests/tooling. Default contract mode never solves. Optional probes
solve only their offline test definitions; no mode connects or opens a console.
"""
from __future__ import print_function
import io
import json
import os
import traceback


def compare_contracts(expected, actual, allowed_additions=(), allowed_removals=(), allowed_changes=None):
    # allowed_removals: GUIDs that may be missing from `actual` without error (e.g. a
    # component retired on purpose). allowed_changes: dict of GUID -> set of record keys
    # that may differ, or the string "all" to allow any key on that record, for a
    # component whose socket layout changed on purpose (e.g. dropped inputs).
    if allowed_changes is None:
        allowed_changes = {}
    errors = []
    indexed = []
    for label, records in (("baseline", expected), ("actual", actual)):
        table = {}
        for record in records:
            guid = record["guid"]
            if guid in table:
                errors.append("duplicate GUID in " + label + ": " + guid)
            table[guid] = record
        indexed.append(table)
    before, after = indexed
    for guid in sorted(before):
        if guid not in after:
            if guid not in allowed_removals:
                errors.append("missing component: " + guid)
            continue
        allowed_keys = allowed_changes.get(guid)
        for key in sorted(before[guid]):
            if before[guid][key] != after[guid].get(key):
                if allowed_keys == "all" or (allowed_keys and key in allowed_keys):
                    continue
                errors.append(guid + ": changed " + key)
    for guid in sorted(set(after) - set(before) - set(allowed_additions)):
        errors.append("unexpected component: " + guid)
    return errors


def save_json(path, value):
    # Reports/baselines are generated artifacts, always invariant UTF-8 JSON.
    with io.open(path, "w", encoding="utf-8") as stream:
        stream.write(unicode_json(value))


def unicode_json(value):
    value = json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True, separators=(",", ": ")) + "\n"
    try:
        return unicode(value)
    except NameError:
        return value


def parameter_contract(param):
    return {"name": param.Name, "nickname": param.NickName,
            "type": param.GetType().FullName, "access": str(param.Access),
            "optional": param.Optional}


def component_contract(component, base_type, flags):
    specs = base_type.GetProperty("InputSpecs", flags).GetValue(component, None)
    groups = base_type.GetProperty("ToggleGroups", flags).GetValue(component, None)
    return {"guid": str(component.ComponentGuid), "class": component.GetType().FullName,
            "name": component.Name, "nickname": component.NickName,
            "category": component.Category, "subcategory": component.SubCategory,
            "exposure": str(component.Exposure),
            "active_groups": sorted(base_type.GetField("Active", flags).GetValue(component)),
            "inputs": [parameter_contract(p) for p in component.Params.Input],
            "outputs": [parameter_contract(p) for p in component.Params.Output],
            "all_input_specs": [{"name": s.Name, "nickname": s.Nick, "type": str(s.Type),
                                  "access": str(s.Access), "group": s.Group} for s in specs],
            "groups": [{"group": g.Item1, "label": g.Item2} for g in groups]}


def run_host(report):
    import clr
    import System
    import Rhino
    from System.Reflection import BindingFlags
    from System.Runtime.Versioning import TargetFrameworkAttribute
    clr.AddReference("Grasshopper")
    clr.AddReference("GH_IO")
    import Grasshopper
    from Grasshopper.Kernel import GH_Component, GH_Document, GH_ExternalFile
    from GH_IO.Serialization import GH_Archive

    report["phase"] = "host-imports-loaded"
    save_json(os.environ["IAM_CONTRACT_RESULT"] + ".progress.json", report)

    expected_path = os.path.abspath(os.environ["IAM_CONTRACT_ASSEMBLY"])
    # Load the exact file, rather than silently testing an installed older plugin.
    server = Grasshopper.Instances.ComponentServer
    assembly = System.Reflection.Assembly.LoadFrom(expected_path)
    assert os.path.normcase(assembly.Location) == os.path.normcase(expected_path), "Wrong plugin assembly loaded"
    # The installed Rhino 8 API documents this loader but marks it internal.
    # Reflection is confined to the test harness; do not load every installed GHA.
    loader = server.GetType().GetMethod("LoadExternalFile", BindingFlags.Instance | BindingFlags.NonPublic)
    assert loader is not None, "Unsupported GH single-file loader API"
    assert loader.Invoke(server, System.Array[System.Object]([GH_ExternalFile(expected_path), False])), "Plugin registration failed"
    tfm = assembly.GetCustomAttributes(clr.GetClrType(TargetFrameworkAttribute), False)[0].FrameworkName
    report.update({"assembly": assembly.Location, "module_id": str(assembly.ManifestModule.ModuleVersionId),
                   "target_framework": tfm, "clr_version": str(System.Environment.Version),
                   "rhino_version": str(Rhino.RhinoApp.Version)})
    assert tfm == os.environ["IAM_CONTRACT_TFM"], "Wrong target framework loaded"
    assert (System.Environment.Version.Major == 4) == (".NETFramework" in tfm), "Wrong Rhino CLR mode"

    flags = BindingFlags.Instance | BindingFlags.NonPublic
    base_type = assembly.GetType("Lugworm.Ui.IamComponentBase", True)
    component_type = clr.GetClrType(GH_Component)
    types = sorted([t for t in assembly.GetExportedTypes()
                    if not t.IsAbstract and component_type.IsAssignableFrom(t)], key=lambda t: t.FullName)
    # 28 concrete components since Version (...0028) was removed on 2026-09-21
    assert len(types) >= 28, "Incomplete component enumeration"
    defaults, expanded, documents = [], [], []
    old_solutions = GH_Document.EnableSolutions
    GH_Document.EnableSolutions = False
    try:
        for expand in (False, True):
            document = GH_Document()
            document.Enabled = False
            documents.append(document)
            contracts = []
            for component_class in types:
                component = System.Activator.CreateInstance(component_class)
                component.CreateAttributes()
                document.AddObject(component, False)
                if expand:
                    active = base_type.GetField("Active", flags).GetValue(component)
                    for spec in base_type.GetProperty("InputSpecs", flags).GetValue(component, None):
                        if spec.Group != "core":
                            active.Add(spec.Group)
                    # SyncInputs(bool recordUndo = true, bool recompute = true): reflection
                    # will not fill optional parameters, so passing None raises "Parameter
                    # count mismatch". Both are passed explicitly as False on purpose - this
                    # probe records no undo events and must never trigger a solve.
                    base_type.GetMethod("SyncInputs", flags).Invoke(
                        component, System.Array[System.Object]([False, False]))
                contracts.append(component_contract(component, base_type, flags))
            if expand:
                expanded = contracts
            else:
                defaults = contracts

            # Wire every registered socket without ever evaluating the definition.
            from Grasshopper.Kernel.Parameters import Param_GenericObject
            wiring = []
            for component in list(document.Objects):
                if not component_type.IsAssignableFrom(component.GetType()):
                    continue
                for direction, parameters in (("in", component.Params.Input), ("out", component.Params.Output)):
                    for index, param in enumerate(parameters):
                        endpoint = Param_GenericObject()
                        endpoint.CreateAttributes()
                        document.AddObject(endpoint, False)
                        if direction == "in":
                            param.AddSource(endpoint)
                        else:
                            endpoint.AddSource(param)
                        wiring.append((component.InstanceGuid, direction, index, endpoint.InstanceGuid))
            archive = GH_Archive()
            assert archive.AppendObject(document, "Definition"), "Archive write failed"
            payload = archive.Serialize_Binary()
            assert payload.Length > 0, "Empty archive"
            restored = GH_Document()
            restored.Enabled = False
            documents.append(restored)
            read_archive = GH_Archive()
            assert read_archive.Deserialize_Binary(payload), "Archive decode failed"
            assert read_archive.ExtractObject(restored, "Definition"), "Archive read failed"
            restored_contracts = []
            for obj in restored.Objects:
                if obj.GetType().Assembly == assembly and component_type.IsAssignableFrom(obj.GetType()):
                    restored_contracts.append(component_contract(obj, base_type, flags))
            errors = compare_contracts(contracts, restored_contracts)
            assert not errors, "Archive contract drift: " + "; ".join(errors)
            for component_id, direction, index, endpoint_id in wiring:
                component = restored.FindObject(component_id, False)
                endpoint = restored.FindObject(endpoint_id, False)
                assert component is not None and endpoint is not None, "Missing wired object"
                if direction == "in":
                    assert component.Params.Input[index].Sources.Count == 1, "Lost input wire"
                    assert component.Params.Input[index].Sources[0].InstanceGuid == endpoint_id, "Wrong input source"
                else:
                    assert endpoint.Sources.Count == 1, "Lost output wire"
                    assert endpoint.Sources[0].InstanceGuid == component.Params.Output[index].InstanceGuid, "Wrong output source"
            report.setdefault("roundtrips", []).append({"expanded": expand, "components": len(contracts),
                                                        "wires": len(wiring), "archive_bytes": payload.Length})
    finally:
        for document in reversed(documents):
            document.Dispose()
        GH_Document.EnableSolutions = old_solutions

    current = {"schema": 1, "default": defaults, "expanded": expanded}
    baseline_path = os.environ["IAM_CONTRACT_BASELINE"]
    if os.environ.get("IAM_CONTRACT_CAPTURE") == "1":
        assert not os.path.exists(baseline_path), "Refusing to overwrite compatibility baseline"
        save_json(baseline_path, current)
    else:
        with io.open(baseline_path, encoding="utf-8") as stream:
            baseline = json.load(stream)
        additions = [x for x in os.environ.get("IAM_CONTRACT_ADDITIONS", "").split(",") if x]
        removals = [x for x in os.environ.get("IAM_CONTRACT_REMOVALS", "").split(",") if x]
        changes = {}
        for entry in os.environ.get("IAM_CONTRACT_CHANGES", "").split(";"):
            entry = entry.strip()
            if not entry:
                continue
            guid, _, keys = entry.partition(":")
            changes[guid] = "all" if keys == "all" else set(k for k in keys.split(",") if k)
        for mode in ("default", "expanded"):
            errors = compare_contracts(baseline[mode], current[mode], additions, removals, changes)
            assert not errors, mode + ": " + "; ".join(errors)
    # Deliberately failing assertion verifies the launcher does not treat process
    # startup or an old result file as a pass. Does not touch machine state.
    assert os.environ.get("IAM_CONTRACT_INJECT_FAILURE") != "1", "Injected host assertion failure"
    probe_name = os.environ.get("IAM_CONTRACT_PROBE", "contracts")
    if probe_name in ("pareto-selection", "interlayer-time", "settings"):
        import imp
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        module_name = "iam_" + probe_name.replace("-", "_")
        probe = imp.load_source(module_name, os.path.join(root, "tests", "grasshopper", probe_name + "-roundtrip.py"
                                                            if probe_name == "pareto-selection" else probe_name + ".py"))
        probe.run(assembly, report)
    report["passed"] = True


def main():
    report = {"passed": False, "run_id": os.environ["IAM_CONTRACT_RUN_ID"]}
    try:
        report["phase"] = "script-started"
        save_json(os.environ["IAM_CONTRACT_RESULT"] + ".progress.json", report)
        run_host(report)
    except Exception:
        report["error"] = traceback.format_exc()
    finally:
        save_json(os.environ["IAM_CONTRACT_RESULT"], report)


if __name__ == "__main__":
    main()
