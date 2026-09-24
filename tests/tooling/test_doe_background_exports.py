"""Structural guards for the solver/I/O boundary; host responsiveness is manual."""
from pathlib import Path
import re
import unittest


class DoeBackgroundExports(unittest.TestCase):
    def test_exports_use_captured_background_jobs_and_detach(self):
        root = Path(__file__).resolve().parents[2]
        for name in ("DoeDesignComponent", "DesirabilityComponent"):
            source = (root / "Lugworm" / "Components" / (name + ".cs")).read_text(encoding="utf-8-sig")
            with self.subTest(component=name):
                self.assertIn("BackgroundFileWork<string>", source,
                              "DoE export still blocks the solver on filesystem I/O")
                self.assertIn("DoeExportSnapshot.Capture", source)
                self.assertNotRegex(source, r"DoeExport\.(PlanToXlsx|PlanToCsv|DesirabilityToXlsx)\(")
                self.assertNotIn("SavePath.Resolve(", source, "Path resolution probes/creates directories and must run in worker")
                self.assertIn("ForgetError()", source)
                self.assertIn("RemovedFromDocument", source)
                self.assertIn("GH_DocumentContext.Close", source)
                self.assertIn("CancelAndDetach()", source)


if __name__ == "__main__":
    unittest.main()
