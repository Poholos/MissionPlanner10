#!/usr/bin/env python3
"""Create a deterministic ZIP for the updater or a portable release archive."""

import argparse
import os
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("publish_dir")
    parser.add_argument("output_zip")
    parser.add_argument(
        "--root-name",
        help="Optional top-level directory name; updater bundles intentionally omit it.",
    )
    args = parser.parse_args()

    publish_dir = os.path.abspath(args.publish_dir)
    output_zip = os.path.abspath(args.output_zip)
    os.makedirs(os.path.dirname(output_zip), exist_ok=True)

    files = []
    for root, _, names in os.walk(publish_dir):
        for name in names:
            path = os.path.join(root, name)
            files.append((os.path.relpath(path, publish_dir).replace(os.sep, "/"), path))

    with zipfile.ZipFile(
        output_zip, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6
    ) as archive:
        for relative, path in sorted(files):
            archive_name = (
                f"{args.root_name.rstrip('/')}/{relative}"
                if args.root_name
                else relative
            )
            archive.write(path, archive_name)

    print(f"update bundle: {len(files)} files -> {output_zip}")


if __name__ == "__main__":
    main()
