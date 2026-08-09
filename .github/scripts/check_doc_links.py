#!/usr/bin/env python3
"""Validate internal links across the documentation site.

The W3C Nu checker validates markup and Stylelint validates CSS, but neither notices that
``href="configuration.html#interactive-session"`` points at a section that no longer exists. That is how a
dead anchor reached the published site: ``search-index.json`` referenced ``installation.html#service-recovery``,
an id that never existed on that page, so the in-page search silently landed users on the wrong place.

Checks, over ``docs/``:
  * every internal ``href`` in every ``.html`` file resolves to a real file, and to a real ``id`` when it
    carries a fragment;
  * every ``url`` in ``search-index.json`` does the same;
  * no two elements on a page share an ``id`` (a duplicate makes fragment links ambiguous).

External links (``http(s)://``, ``mailto:``) are deliberately not fetched: the network is not this check's
business and a flaky third-party host must never fail the docs build.

Exit code 0 when clean, 1 with a report of every problem otherwise.
"""

from __future__ import annotations

import json
import re
import sys
from collections import Counter
from pathlib import Path

HREF = re.compile(r'href="([^"]+)"')
ID = re.compile(r'id="([^"]+)"')
EXTERNAL = ("http://", "https://", "mailto:", "//")
# Referenced by the pages but not part of the checked set (assets, not documents).
NON_DOCUMENTS = (".css", ".js", ".json", ".png", ".svg", ".ico")


def collect_ids(pages: dict[str, str]) -> tuple[dict[str, set[str]], list[str]]:
    """Map each page to its element ids, reporting any duplicates found along the way."""
    ids: dict[str, set[str]] = {}
    problems: list[str] = []
    for name, text in pages.items():
        found = ID.findall(text)
        ids[name] = set(found)
        for dupe, count in Counter(found).items():
            if count > 1:
                problems.append(f"{name}: id '{dupe}' is defined {count} times - fragment links are ambiguous")
    return ids, problems


def check_target(source: str, target: str, ids: dict[str, set[str]]) -> str | None:
    """Return a problem description for one link, or None when it resolves."""
    if target.startswith(EXTERNAL) or target.startswith("#") and not target[1:]:
        return None

    page, _, anchor = target.partition("#")
    page = page or source

    if target.startswith("#"):
        page = source

    if page.endswith(NON_DOCUMENTS):
        return None if (Path("docs") / page).exists() else f"{source}: '{target}' - file not found"

    if page not in ids:
        return f"{source}: '{target}' - no such page in docs/"
    if anchor and anchor not in ids[page]:
        return f"{source}: '{target}' - page exists but has no id '{anchor}'"
    return None


def main() -> int:
    docs = Path("docs")
    if not docs.is_dir():
        print("docs/ not found - run from the repository root", file=sys.stderr)
        return 1

    pages = {p.name: p.read_text(encoding="utf-8") for p in sorted(docs.glob("*.html"))}
    if not pages:
        print("no HTML pages found under docs/", file=sys.stderr)
        return 1

    ids, problems = collect_ids(pages)

    links = 0
    for name, text in pages.items():
        for target in HREF.findall(text):
            links += 1
            if problem := check_target(name, target, ids):
                problems.append(problem)

    index_path = docs / "search-index.json"
    entries = 0
    if index_path.exists():
        try:
            index = json.loads(index_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            problems.append(f"search-index.json: invalid JSON - {exc}")
            index = []
        for topic in index:
            for item in topic.get("items", []):
                entries += 1
                if problem := check_target("search-index.json", item.get("url", ""), ids):
                    problems.append(problem)

    if problems:
        print(f"Found {len(problems)} problem(s):\n", file=sys.stderr)
        for problem in problems:
            print(f"  {problem}", file=sys.stderr)
        return 1

    # ASCII only: this also runs on a Windows console, where a non-ASCII glyph can raise UnicodeEncodeError.
    print(f"OK - {len(pages)} pages, {links} internal links, {entries} search-index entries, all resolve.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
