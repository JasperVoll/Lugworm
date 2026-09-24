"""The plugin and Core assemblies ship together: their csproj <Version> values must match."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def version_of(rel):
    text = (ROOT / rel).read_text(encoding="utf-8-sig")
    match = re.search(r"<Version>([^<]+)</Version>", text)
    return match.group(1) if match else None


class VersionSyncTests(unittest.TestCase):
    def test_core_version_matches_plugin_version(self):
        plugin = version_of("Lugworm/Lugworm.csproj")
        core = version_of("Lugworm.Core/Lugworm.Core.csproj")
        self.assertIsNotNone(plugin, "Lugworm.csproj has no <Version>")
        self.assertIsNotNone(core, "Lugworm.Core.csproj has no <Version>")
        self.assertEqual(plugin, core)

    def test_release_script_refuses_a_core_version_mismatch(self):
        script = (ROOT / "tools" / "release.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("Lugworm.Core\\Lugworm.Core.csproj", script)
        self.assertIn("$coreVersion -cne $version", script)


if __name__ == "__main__":
    unittest.main()
