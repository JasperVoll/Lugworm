"""The shipped DoE example must fit cleanly; a Cook's D warning on it is a data bug."""
import importlib.util
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GENERATOR = ROOT / "tools" / "gen_doe_example.py"


def _load():
    spec = importlib.util.spec_from_file_location("gen_doe_example", GENERATOR)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class DoeExampleMeasurements(unittest.TestCase):
    def setUp(self):
        self.gen = _load()
        self.values = self.gen.measurements()
        self.diag = self.gen.diagnostics(self.values)

    def test_no_run_steers_the_fitted_surface(self):
        # The component warns above 1.0 and hints above 4/n; the example has to stay
        # clear of both so that only a deliberately corrupted cell makes it fire.
        hint = 4.0 / len(self.gen.CODED_RUNS)
        for name, f in self.diag.items():
            with self.subTest(response=name):
                worst = max(f["cook"])
                run = f["cook"].index(worst) + 1
                self.assertLess(worst, hint,
                                f"{name}: run {run} has Cook's D {worst:.3f}")

    def test_centre_points_carry_real_pure_error(self):
        # Identical centre replicates make the lack-of-fit F a division by zero and
        # collapse RMSE onto the rounding step - the defect this file was rebuilt for.
        centres = [i for i, xc in enumerate(self.gen.CODED_RUNS) if all(v == 0 for v in xc)]
        self.assertGreaterEqual(len(centres), 3)
        for name, series in self.values.items():
            with self.subTest(response=name):
                replicates = {series[i] for i in centres}
                self.assertGreater(len(replicates), 1,
                                   f"{name}: every centre point reads {replicates}")
        for name, f in self.diag.items():
            with self.subTest(response=name):
                self.assertTrue(f["lof_f"] < float("inf"),
                                f"{name}: lack-of-fit F is infinite (no pure error)")
                self.assertGreater(f["lof_p"], self.gen.MIN_LOF_P)

    def test_noise_dominates_the_reported_precision(self):
        # Residuals must be measurement noise, not rounding artefacts.
        for name, spec in self.gen.TRUTH.items():
            with self.subTest(response=name):
                step = 10.0 ** -spec["decimals"]
                self.assertGreaterEqual(spec["sigma"], 3 * step,
                                        f"{name}: sigma {spec['sigma']} too close to "
                                        f"the rounding step {step}")

    def test_fit_stays_the_clean_reference(self):
        self.assertEqual([], self.gen.verify(self.diag))

    def test_shipped_workbook_matches_the_generator(self):
        self.assertTrue(self.gen.XLSX_OUT.exists(), f"{self.gen.XLSX_OUT} is missing")
        expected = self.gen.workbook_parts()
        current = self.gen.read_parts(self.gen.XLSX_OUT)
        self.assertEqual(sorted(expected), sorted(current))
        for name, content in expected.items():
            with self.subTest(part=name):
                self.assertEqual(content, current[name],
                                 "run tools/gen_doe_example.py")

    def test_augmented_fixture_matches_the_generator(self):
        self.assertTrue(self.gen.AUGMENTED_OUT.exists(), f"{self.gen.AUGMENTED_OUT} is missing")
        self.assertEqual(self.gen.augmented_csv(),
                         self.gen.AUGMENTED_OUT.read_text(encoding="utf-8"),
                         "run tools/gen_doe_example.py")

    def test_augmentation_makes_the_quadratic_estimable(self):
        # The plain 2^3 block cannot carry a quadratic (10 terms on 8 runs, every
        # pure quadratic aliased with the intercept); the star block fixes that.
        runs = self.gen.AUGMENTED_RUNS
        values = self.gen.measurements(runs, self.gen.AUGMENTED_SEED)
        corners = [self.gen.design_row(tuple(float(v) for v in xc)) for xc in runs[:8]]
        with self.assertRaises(ValueError):
            self.gen.fit(corners, [float(v) for v in values["layer_width"][:8]])
        diag = self.gen.diagnostics(values, runs)
        self.assertEqual([], self.gen.verify(diag))
        for name, f in diag.items():
            with self.subTest(response=name):
                self.assertTrue(f["lof_f"] < float("inf"), f"{name}: no pure error")


if __name__ == "__main__":
    unittest.main()
