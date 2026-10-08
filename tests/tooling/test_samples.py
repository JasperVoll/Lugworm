"""Keep developer samples distinct from the product and in the public source export."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
SAMPLES = ROOT / "samples" / "Lugworm.Samples"
GUID = r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}"


class SamplesTests(unittest.TestCase):
    def test_guids_are_new_and_unique(self):
        files = list(SAMPLES.rglob("*.cs"))
        files = [p for p in files if "obj" not in p.parts and "bin" not in p.parts]
        declarations = []
        for path in files:
            source = path.read_text(encoding="utf-8-sig")
            for member in ("ComponentGuid", "Id"):
                declarations.extend(re.findall(
                    rf'override\s+Guid\s+{member}\s*=>\s*new\s+Guid\("({GUID})"\)',
                    source))
        self.assertEqual(7, len(declarations), "Expected six component GUIDs and one assembly GUID")
        guids = [guid.lower() for guid in declarations]
        self.assertEqual(len(guids), len(set(guids)), "Sample GUIDs must be unique")
        existing = set()
        for path in (ROOT / "Lugworm").rglob("*"):
            if path.is_file():
                existing.update(match.decode("ascii").lower()
                                for match in re.findall(GUID.encode("ascii"), path.read_bytes()))
        existing.update(re.findall(GUID, (ROOT / "tests/grasshopper/component-contracts-v3.json")
                                   .read_text(encoding="utf-8-sig").lower()))
        self.assertFalse(set(guids) & existing, "Sample GUIDs collide with the product or contract")

    def test_samples_have_their_own_category(self):
        files = [p for p in SAMPLES.glob("[0-9][0-9]-*/*.cs")]
        self.assertEqual(6, len(files))
        for path in files:
            source = path.read_text(encoding="utf-8-sig")
            self.assertNotRegex(source, r'"Lugworm"', str(path))
            self.assertIn('"Lugworm Samples"', source, str(path))

    def test_each_feature_has_code_and_readme(self):
        folders = sorted(p for p in SAMPLES.glob("[0-9][0-9]-*") if p.is_dir())
        self.assertEqual(["01", "02", "03", "04", "05", "06"], [p.name[:2] for p in folders])
        for folder in folders:
            self.assertTrue((folder / "README.md").is_file(), str(folder))
            self.assertTrue(list(folder.glob("*.cs")), str(folder))

    def test_samples_are_not_staged_as_payload(self):
        release = (ROOT / "tools/release.ps1").read_text(encoding="utf-8-sig")
        payload = re.search(r"\$payload\s*=\s*@\((.*?)\)", release, re.DOTALL)
        self.assertIsNotNone(payload, "Release must declare its payload")
        self.assertNotIn("Lugworm.Samples", payload.group(1))
        self.assertNotIn("Lugworm.Samples", release)

    def test_samples_are_exported(self):
        entries = (ROOT / "tools/public-paths.txt").read_text(encoding="utf-8-sig").splitlines()
        entries = [line.strip() for line in entries if line.strip() and not line.startswith("#")]
        self.assertIn("samples", entries)
        self.assertFalse(any(e == "!samples" or e.startswith("!samples/") for e in entries))


if __name__ == "__main__":
    unittest.main()
