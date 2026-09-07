#!/usr/bin/env python3
"""Generate the Dalamud plugin repository manifest (repo.json) for a release.

The plugin manifest that DalamudPackager writes next to latest.zip already carries the assembly
version and the API level the plugin was built against, so this only has to bolt the download links
onto it. Run it after a Release build:

    python3 scripts/make-repo-json.py --tag v1.0.0.0
"""

from __future__ import annotations

import argparse
import json
import pathlib
import time

REPO = "Liquidize/Wyu2"
DEFAULT_MANIFEST = pathlib.Path("src/Wyu2/bin/Release/Wyu2/Wyu2.json")
DEFAULT_OUTPUT = pathlib.Path("repo.json")


def build_entry(manifest: dict, tag: str, asset: str) -> dict:
    download = f"https://github.com/{REPO}/releases/download/{tag}/{asset}"

    entry = dict(manifest)
    entry.update(
        {
            "IsHide": False,
            "IsTestingExclusive": False,
            "DownloadCount": 0,
            "LastUpdate": int(time.time()),
            "DownloadLinkInstall": download,
            "DownloadLinkUpdate": download,
            "DownloadLinkTesting": download,
        }
    )

    # A testing channel that points at the stable build is just noise; only advertise one when the
    # manifest actually declares a testing version.
    entry.setdefault("TestingAssemblyVersion", entry["AssemblyVersion"])
    return entry


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tag", required=True, help="release tag, e.g. v1.0.0.0")
    parser.add_argument("--asset", default="Wyu2.zip", help="release asset file name")
    parser.add_argument("--manifest", type=pathlib.Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    entry = build_entry(manifest, args.tag, args.asset)

    args.output.write_text(json.dumps([entry], indent=2) + "\n", encoding="utf-8")
    print(f"wrote {args.output} for {entry['Name']} {entry['AssemblyVersion']} ({args.tag})")


if __name__ == "__main__":
    main()
