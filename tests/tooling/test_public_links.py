"""The public source export must not ship documents whose links point outside it."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
LINK = re.compile(r"\]\(([^)#\s]+)(?:#[^)]*)?\)")
PUBLIC_DOCS = ["README.md", "CONTRIBUTING.md", "INSTALL.md", "UPGRADE.md", "docs/ARCHI.md", "docs/dev/TESTING.md",
               "docs/reference/doe-calibration.md", "docs/reference/duet-control.md",
               "docs/user-guide/print-console.md",
               "docs/user-guide/legend.md", "docs/user-guide/settings-files.md",
               "docs/user-guide/interlayer-time.md"]


def public_paths():
    lines = (ROOT / "tools" / "public-paths.txt").read_text(encoding="utf-8-sig").splitlines()
    return [line.strip() for line in lines if line.strip() and not line.strip().startswith("#")]


def is_public(rel, allow):
    rel = rel.replace("\\", "/")
    for entry in allow:
        entry = entry.rstrip("/")
        if rel == entry or rel.startswith(entry + "/"):
            return True
    return False


class PublicLinkTests(unittest.TestCase):
    def test_public_docs_link_only_to_exported_files(self):
        allow = public_paths()
        self.assertIn("README.md", allow)
        missing, private = [], []
        for doc in PUBLIC_DOCS:
            path = ROOT / doc
            self.assertTrue(path.exists(), doc)
            self.assertTrue(is_public(doc, allow), doc + " is linked from the public set but not exported")
            for target in LINK.findall(path.read_text(encoding="utf-8-sig")):
                if target.startswith(("http://", "https://", "mailto:")):
                    continue
                resolved = (path.parent / target).resolve()
                try:
                    rel = resolved.relative_to(ROOT).as_posix()
                except ValueError:
                    private.append(f"{doc}: {target} leaves the repository")
                    continue
                if not resolved.exists():
                    missing.append(f"{doc}: {target}")
                elif not is_public(rel, allow):
                    private.append(f"{doc}: {target} -> {rel} is not in the public export")
        self.assertEqual([], missing, "broken links:\n" + "\n".join(missing))
        self.assertEqual([], private, "links outside the public export:\n" + "\n".join(private))

    def test_release_stages_the_upstream_license_payload_and_records_provenance(self):
        text = (ROOT / "tools" / "release.ps1").read_text(encoding="utf-8-sig")
        for needle in ("SkiaSharp.NativeAssets.Win32", "OpenTK.GLControl", "PROVENANCE.txt", "BUILD-INFO.txt",
                       "HASHES-$version.txt", "nupkg.sha512"):
            self.assertIn(needle, text, needle)
        self.assertIn("'.txt'", text, "license/provenance text files must pass the stage extension gate")


if __name__ == "__main__":
    unittest.main()
