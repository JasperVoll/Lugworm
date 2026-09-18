"""Check page metadata, navigation, and links in the generated Jekyll site."""
import json
import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit

ROOT = Path(__file__).resolve().parents[1]
SITE = ROOT / "_site"
BASE = "/iamlab-slicer-documentation"
HOST = "jaspervoll.github.io"
errors = []
expected = json.loads((ROOT / "tools" / "pages.json").read_text(encoding="utf-8"))

class Document(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if attrs.get("id"):
            self.ids.add(attrs["id"])
        if tag == "a" and attrs.get("name"):
            self.ids.add(attrs["name"])
        for attr in ("href", "src", "poster"):
            if attrs.get(attr):
                self.links.append(attrs[attr])

def meta(path):
    text = path.read_text(encoding="utf-8")
    match = re.match(r"---\n(.*?)\n---\n", text, re.S)
    if not match:
        errors.append(f"Missing front matter: {path.relative_to(ROOT)}")
        return {}, text
    values = {}
    for line in match[1].splitlines():
        if ": " in line:
            key, value = line.split(": ", 1)
            values[key] = value.strip('"')
    for required in ("title", "nav_order", "description"):
        if not values.get(required):
            errors.append(f"Missing {required}: {path.relative_to(ROOT)}")
    if not values.get("nav_order", "").isdigit():
        errors.append(f"Invalid nav_order: {path.relative_to(ROOT)}")
    if "[[" in text:
        errors.append(f"Unconverted wiki link: {path.relative_to(ROOT)}")
    return values, text

page_files = [ROOT / "index.md", *sorted((ROOT / "docs").rglob("*.md"))]
metadata = {p.relative_to(ROOT).as_posix(): meta(p)[0] for p in page_files}
for original, item in expected.items():
    actual = metadata.get(item["path"], {})
    for field in ("title", "parent"):
        if actual.get(field) != item.get(field):
            errors.append(f"{item['path']}: wrong {field}")
    if actual.get("permalink") != item["url"]:
        errors.append(f"{item['path']}: wrong permalink")
if len(page_files) != 37 or len(expected) != 34:
    errors.append("Expected 34 imported pages and 3 navigation landing pages")
component_count = sum(m.get("parent") == "Components" for m in metadata.values())
if component_count != 25:
    errors.append("Expected 25 component children")
titles = {m.get("title") for m in metadata.values()}
for name, item in metadata.items():
    if item.get("parent") and item["parent"] not in titles:
        errors.append(f"{name}: unknown parent")
if not SITE.exists():
    sys.exit("FAIL: _site is missing; run bundle exec jekyll build first")

html = {p: Document(p.read_text(encoding="utf-8")) for p in SITE.rglob("*.html")}
checked = 0

def check_link(origin, link):
    global checked
    if link.startswith(("mailto:", "tel:", "data:", "javascript:")):
        return
    current = BASE + "/" + origin.relative_to(SITE).as_posix()
    if current.endswith("index.html"):
        current = current[:-len("index.html")]
    parsed = urlsplit(urljoin("https://" + HOST + current, link))
    if parsed.netloc != HOST:
        return
    checked += 1
    path = unquote(parsed.path)
    if path != BASE and not path.startswith(BASE + "/"):
        errors.append(f"{origin.relative_to(SITE)}: baseurl missing: {link}")
        return
    relative = path[len(BASE):].lstrip("/")
    target = SITE / relative
    if target.is_dir() or path.endswith("/"):
        target = target / "index.html"
    if not target.exists():
        errors.append(f"{origin.relative_to(SITE)}: missing target: {link}")
    elif parsed.fragment and target.suffix == ".html":
        if unquote(parsed.fragment) not in html[target].ids:
            errors.append(f"{origin.relative_to(SITE)}: missing anchor: {link}")

for path, doc in html.items():
    for link in doc.links:
        check_link(path, link)
for css in SITE.rglob("*.css"):
    for link in re.findall(r"url\(['\"]?([^'\"\)]+)", css.read_text(encoding="utf-8")):
        check_link(css, link)

search_file = SITE / "assets" / "js" / "search-data.json"
if not search_file.exists():
    errors.append("Search index missing")
else:
    search = json.loads(search_file.read_text(encoding="utf-8"))
    for entry in search.values():
        check_link(search_file, entry["url"])
    if len(search) != 37:
        errors.append(f"Search contains {len(search)} pages; expected 37")

for name, item in metadata.items():
    target = SITE / item["permalink"].lstrip("/") / "index.html"
    if not target.exists():
        errors.append(f"Page not rendered: {name}")
        continue
    text = target.read_text(encoding="utf-8")
    edit = "https://github.com/JasperVoll/iamlab-slicer-documentation/edit/main/" + name
    if edit not in text:
        errors.append(f"Edit link missing or incorrect: {name}")
    if "IDAC" not in text or "Hamburg University of Technology" not in text:
        errors.append(f"Footer credit missing: {name}")

# Scan maintained files and publishable output, excluding installed dependencies.
forbidden = re.compile("|".join(["git" + "lab", "IAMLab" + "Slicer.Core", r"\.s" + "ln\b", "build from " + "source"]), re.I)
scan_files = [ROOT / "_config.yml", ROOT / "index.md", *list((ROOT / "docs").rglob("*.md")), *SITE.rglob("*.html")]
for path in scan_files:
    if forbidden.search(path.read_text(encoding="utf-8")):
        errors.append(f"Private-reference scan failed: {path.relative_to(ROOT)}")

print(f"Imported pages: {len(expected)}/34; component children: {component_count}/25")
print(f"Navigation landing pages: {len(page_files) - len(expected)}; front matter and hierarchy checked")
print(f"Rendered HTML pages: {len(html)}; internal URLs and anchors checked: {checked}")
print("Search entries: " + str(len(search) if search_file.exists() else 0))
print(f"Broken internal links/assets/anchors: {sum('missing target:' in e or 'missing anchor:' in e or 'baseurl missing:' in e for e in errors)}")
print(f"Total errors: {len(errors)}")
if errors:
    print("\n".join(sorted(set(errors))))
    sys.exit(1)
print("PASS: pages, navigation, project baseurl, assets, anchors, search, edit links, footer, and public-content scan")
