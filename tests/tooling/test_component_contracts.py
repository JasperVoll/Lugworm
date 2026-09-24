"""Tests for the compatibility gate itself; no Rhino imports required."""
import copy
import importlib.util
from pathlib import Path
import unittest
from types import SimpleNamespace


TOOL = Path(__file__).resolve().parents[2] / "tools" / "verify-component-contracts.py"


class ContractGateTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(TOOL.is_file(), "Missing component compatibility gate")
        spec = importlib.util.spec_from_file_location("component_contracts", TOOL)
        self.gate = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.gate)
        self.baseline = [{"guid": "existing-guid", "name": "Existing", "inputs": [
            {"name": "a", "access": "item"}, {"name": "b", "access": "list"}],
            "outputs": [{"name": "result"}], "groups": ["optional"]}]

    def test_identical_contract_passes(self):
        self.assertEqual([], self.gate.compare_contracts(self.baseline, copy.deepcopy(self.baseline)))

    def test_removed_component_fails(self):
        self.assertIn("missing", " ".join(self.gate.compare_contracts(self.baseline, [])))

    def test_removed_component_passes_with_allowed_removal(self):
        self.assertEqual([], self.gate.compare_contracts(
            self.baseline, [], allowed_removals=["existing-guid"]))

    def test_socket_reorder_fails(self):
        actual = copy.deepcopy(self.baseline)
        actual[0]["inputs"].reverse()
        self.assertIn("inputs", " ".join(self.gate.compare_contracts(self.baseline, actual)))

    def test_changed_access_fails(self):
        actual = copy.deepcopy(self.baseline)
        actual[0]["inputs"][0]["access"] = "tree"
        self.assertIn("inputs", " ".join(self.gate.compare_contracts(self.baseline, actual)))

    def test_changed_key_passes_with_allowed_change(self):
        actual = copy.deepcopy(self.baseline)
        actual[0]["name"] = "Renamed"
        self.assertIn("changed", " ".join(self.gate.compare_contracts(self.baseline, actual)))
        self.assertEqual([], self.gate.compare_contracts(
            self.baseline, actual, allowed_changes={"existing-guid": {"name"}}))

    def test_changed_key_passes_with_allowed_change_all(self):
        actual = copy.deepcopy(self.baseline)
        actual[0]["name"] = "Renamed"
        actual[0]["inputs"] = []
        self.assertIn("changed", " ".join(self.gate.compare_contracts(self.baseline, actual)))
        self.assertEqual([], self.gate.compare_contracts(
            self.baseline, actual, allowed_changes={"existing-guid": "all"}))

    def test_duplicate_guid_fails_even_if_additions_allowed(self):
        actual = self.baseline + copy.deepcopy(self.baseline)
        self.assertIn("duplicate", " ".join(self.gate.compare_contracts(self.baseline, actual, ["existing-guid"])))

    def test_only_explicit_new_guid_allowed(self):
        actual = self.baseline + [{"guid": "new-guid", "name": "New"}]
        self.assertIn("unexpected", " ".join(self.gate.compare_contracts(self.baseline, actual)))
        self.assertEqual([], self.gate.compare_contracts(self.baseline, actual, ["new-guid"]))

    def test_optional_group_contract_fails(self):
        actual = copy.deepcopy(self.baseline)
        actual[0]["groups"] = []
        self.assertIn("groups", " ".join(self.gate.compare_contracts(self.baseline, actual)))

    def test_extraction_records_active_groups_not_just_declared_groups(self):
        component = SimpleNamespace(ComponentGuid="id", Name="name", NickName="nick",
            Category="cat", SubCategory="sub", Exposure="primary",
            GetType=lambda: SimpleNamespace(FullName="Component"),
            Params=SimpleNamespace(Input=[], Output=[]))
        base_type = SimpleNamespace(
            GetProperty=lambda name, flags: SimpleNamespace(GetValue=lambda obj, args: []),
            GetField=lambda name, flags: SimpleNamespace(GetValue=lambda obj: ["z", "a"]))
        actual = self.gate.component_contract(component, base_type, None)
        self.assertIn("active_groups", actual, "Archive gate must check enabled toggle state")
        self.assertEqual(["a", "z"], actual["active_groups"])
        changed = copy.deepcopy(actual)
        changed["active_groups"] = []
        self.assertIn("active_groups", " ".join(self.gate.compare_contracts([actual], [changed])))


if __name__ == "__main__":
    unittest.main()
