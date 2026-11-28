#!/usr/bin/env python3
"""Download reference photographs from Wikimedia Commons for local art review.

The photos are reference-only: they are never committed to the repository
(the destination folder is git-ignored) and never shipped in the game. The
manifest written next to them records file, source URL, author and licence so
docs/art/aircraft-references.md can cite them accurately.

Usage:

    python3 tools/fetch_reference_photos.py \
        --query "Supermarine Spitfire Mk IX" --aircraft spitfire-mk9 --limit 24

Licences accepted: CC0, CC BY, CC BY-SA, public domain/mark, and IWM/NARA-style
"no known restrictions" are treated as reference-only with provenance kept.
Anything unclear is skipped.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
import urllib.parse
import urllib.request

API = "https://commons.wikimedia.org/w/api.php"
USER_AGENT = "IronSkies-reference-fetch/1.0 (open-source flight sim; contact via repo)"

ALLOWED_LICENSE_MARKERS = (
    "cc0",
    "cc by",
    "cc-by",
    "cc-by-sa",
    "public domain",
    "publicdomain",
    "pd-",
    "no known restrictions",
    "fal",  # Free Art License
)


def api_get(params: dict) -> dict:
    url = API + "?" + urllib.parse.urlencode(params)
    for attempt in range(6):
        req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                return json.load(resp)
        except urllib.error.HTTPError as exc:
            if exc.code == 429 and attempt < 5:
                time.sleep(5 * (attempt + 1))
                continue
            raise
    raise RuntimeError("unreachable")


def search_files(query: str, limit: int) -> list[str]:
    data = api_get({
        "action": "query",
        "list": "search",
        "srsearch": f"filetype:bitmap {query}",
        "srnamespace": "6",
        "srlimit": str(min(limit * 3, 100)),
        "format": "json",
    })
    return [hit["title"] for hit in data.get("query", {}).get("search", [])]


def image_info(titles: list[str]) -> list[dict]:
    out = []
    for i in range(0, len(titles), 20):
        chunk = titles[i:i + 20]
        data = api_get({
            "action": "query",
            "titles": "|".join(chunk),
            "prop": "imageinfo",
            "iiprop": "url|extmetadata|size|mime",
            "iiurlwidth": "1920",
            "format": "json",
        })
        for page in data.get("query", {}).get("pages", {}).values():
            info = (page.get("imageinfo") or [None])[0]
            if not info:
                continue
            meta = info.get("extmetadata", {})
            license_short = (meta.get("LicenseShortName", {}) or {}).get("value", "")
            out.append({
                "title": page.get("title", ""),
                "url": info.get("thumburl") or info.get("url"),
                "width": info.get("thumbwidth") or info.get("width", 0),
                "height": info.get("height", 0),
                "mime": info.get("mime", ""),
                "license": license_short,
                "author": re.sub(r"<[^>]+>", "", (meta.get("Artist", {}) or {}).get("value", ""))[:200],
                "description": re.sub(r"<[^>]+>", "", (meta.get("ImageDescription", {}) or {}).get("value", ""))[:400],
                "descriptionurl": info.get("descriptionurl", ""),
            })
    return out


def license_ok(name: str) -> bool:
    probe = name.lower()
    return any(marker in probe for marker in ALLOWED_LICENSE_MARKERS)


def safe_name(title: str) -> str:
    name = title.replace("File:", "").strip()
    name = re.sub(r"[^A-Za-z0-9._-]+", "_", name)
    return name[:180]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--query", required=True)
    parser.add_argument("--aircraft", required=True)
    parser.add_argument("--limit", type=int, default=20)
    parser.add_argument("--min-width", type=int, default=900)
    parser.add_argument("--dest", default="reference-photos")
    args = parser.parse_args()

    dest_dir = os.path.join(args.dest, args.aircraft)
    os.makedirs(dest_dir, exist_ok=True)
    manifest_path = os.path.join(dest_dir, "manifest.json")
    manifest = []
    if os.path.exists(manifest_path):
        with open(manifest_path) as f:
            manifest = json.load(f)
    have = {entry["title"] for entry in manifest}

    titles = search_files(args.query, args.limit * 2)
    infos = image_info(titles)
    picked = []
    for info in infos:
        if len(picked) >= args.limit:
            break
        if info["title"] in have:
            continue
        if info["mime"] not in ("image/jpeg", "image/png", "image/tiff"):
            continue
        if info["width"] < args.min_width:
            continue
        if not license_ok(info["license"]):
            continue
        picked.append(info)

    downloaded = 0
    for info in picked:
        filename = safe_name(info["title"])
        path = os.path.join(dest_dir, filename)
        try:
            req = urllib.request.Request(info["url"], headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=120) as resp, open(path, "wb") as out:
                while True:
                    chunk = resp.read(1 << 16)
                    if not chunk:
                        break
                    out.write(chunk)
        except Exception as exc:  # noqa: BLE001 - keep going, report at the end
            print(f"FAIL {info['title']}: {exc}", file=sys.stderr)
            continue
        info["file"] = filename
        manifest.append(info)
        downloaded += 1
        print(f"ok   [{info['license']}] {info['title']} -> {path}")
        time.sleep(0.4)

    with open(manifest_path, "w") as f:
        json.dump(manifest, f, indent=2)
    print(f"downloaded {downloaded} files; manifest at {manifest_path} ({len(manifest)} total)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
