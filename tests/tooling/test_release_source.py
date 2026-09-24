"""Keep the clean source export buildable and its allowlist shared with the public import."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
PRIVATE_ONLY = [".gitignore", "AGENTS.md", "CLAUDE.md", "docs/github", "docs/dev/FOOD4RHINO.md",
                "docs/dev/analysis", "docs/4-unit-tests/TESTING.md", "tests/parity"]


def read_script(name):
    return (ROOT / "tools" / name).read_text(encoding="utf-8-sig")


def public_paths():
    lines = (ROOT / "tools" / "public-paths.txt").read_text(encoding="utf-8-sig").splitlines()
    return [line.strip() for line in lines if line.strip() and not line.strip().startswith("#")]


class SourceExportTests(unittest.TestCase):
    def test_release_clean_uses_one_msbuild_worker(self):
        script = read_script("release.ps1")
        self.assertIn("@('clean', 'Lugworm.sln', '-c', 'Release', '-v', 'q', '-m:1')", script)
        self.assertIn("@('build', 'Lugworm.sln', '-c', 'Release', '-m:1')", script)
        self.assertIn("@('test', 'Lugworm.sln', '-c', 'Release', '--no-build', '--no-restore', '-m:1')", script)

    def test_release_runs_the_solution_not_only_the_net10_suite(self):
        script = read_script("release.ps1")
        self.assertIsNotNone(re.search(r"dotnet test Lugworm\.sln -c Release", script),
                             "Release must run the focused shipping-runtime project too")

    def test_every_solution_project_is_in_the_public_allowlist(self):
        paths = public_paths()
        self.assertTrue(paths, "Missing explicit source allowlist")
        solution = (ROOT / "Lugworm.sln").read_text(encoding="utf-8-sig")
        projects = re.findall(r'"([^"\r\n]+\.csproj)"', solution)
        self.assertTrue(projects, "No solution projects inspected")
        for project in projects:
            project = project.replace("\\", "/")
            self.assertTrue(any(project == p or project.startswith(p + "/") for p in paths),
                            "Source export omits solution project: " + project)

    def test_allowlist_names_committed_paths_and_nothing_private(self):
        paths = public_paths()
        for p in paths:
            self.assertTrue((ROOT / p).exists(), "allowlisted path is missing: " + p)
            self.assertNotIn("\\", p, "allowlist uses forward slashes: " + p)
            self.assertFalse(p.startswith("/") or p.endswith("/"),
                             "allowlist paths are relative without a trailing slash: " + p)
        for private in PRIVATE_ONLY:
            self.assertFalse(any(private == p or private.startswith(p + "/") for p in paths),
                             "private path is exported: " + private)
        self.assertEqual(len(paths), len(set(paths)), "duplicate allowlist entries")

    def test_release_and_import_scripts_share_the_allowlist(self):
        for name in ("release.ps1", "publish-source.ps1"):
            self.assertIn("public-paths.txt", read_script(name), name + " must read tools/public-paths.txt")
        release = read_script("release.ps1").replace("\r\n", "\n")
        self.assertNotIn("$publicPaths = @(\n", release,
                         "release.ps1 must not carry a second, literal allowlist")

    def test_source_export_is_recorded_but_not_a_release_asset(self):
        release = read_script("release.ps1")
        self.assertIn("SOURCE-EXPORT-$version.txt", release)
        self.assertIn("foreach ($artifact in @($zip, $yakDist)) {", release,
                      "HASHES must list the send zip and the .yak only")
        importer = read_script("publish-source.ps1")
        self.assertIn("SOURCE-EXPORT-$version.txt", importer)
        self.assertIn("source: import Lugworm $version", importer)
        self.assertNotIn("'push'", importer, "the import script must not push")
        self.assertNotIn("git push", importer, "the import script must not push")


if __name__ == "__main__":
    unittest.main()
