#!/usr/bin/env python3
"""Zip a dotnet publish directory for the portable (non-MSI) distribution.

Usage: make-zip.py <publish-dir> <output-zip>
"""
import sys
import zipfile
from pathlib import Path

from publish_skip import is_runtime_state


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit("usage: make-zip.py <publish-dir> <output-zip>")
    publish_dir = Path(sys.argv[1]).resolve()
    output_zip = Path(sys.argv[2]).resolve()
    output_zip.parent.mkdir(parents=True, exist_ok=True)

    count = 0
    with zipfile.ZipFile(output_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in sorted(publish_dir.rglob("*")):
            if not path.is_file():
                continue
            relative = path.relative_to(publish_dir)
            if is_runtime_state(relative):
                continue
            archive.write(path, str(relative))
            count += 1

    size_mb = output_zip.stat().st_size / (1024 * 1024)
    print(f"{output_zip.name}: {count} files, {size_mb:.1f} MB")


if __name__ == "__main__":
    main()
