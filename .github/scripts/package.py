#!/usr/bin/env python3
"""Package the built plugin the way Jellyfin expects, and update the repo manifest.

Produces artifacts/<slug>_<version>.zip containing meta.json plus the plugin DLL,
then prepends a version entry to manifest.json so a Jellyfin server pointed at the
raw manifest URL sees the new release.
"""

import argparse
import hashlib
import json
import pathlib
import zipfile
from datetime import datetime, timezone

import yaml


def four_part(version: str) -> str:
    parts = [p for p in version.strip().lstrip("v").split(".") if p != ""]
    while len(parts) < 4:
        parts.append("0")
    return ".".join(parts[:4])


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True, help="release version, with or without a leading v")
    parser.add_argument("--tag", required=True, help="git tag the release assets hang off")
    parser.add_argument("--repo", required=True, help="owner/name of the GitHub repository")
    parser.add_argument("--publish-dir", default="publish", help="dotnet publish output")
    parser.add_argument("--out-dir", default="artifacts", help="where to write the zip")
    parser.add_argument("--build-file", default="build.yaml")
    parser.add_argument("--manifest", default="manifest.json")
    parser.add_argument("--changelog", default="", help="changelog text for this version")
    parser.add_argument(
        "--manifest-only",
        action="store_true",
        help="reuse the zip already in --out-dir and only update the manifest, so the "
        "published asset and the manifest checksum can never disagree",
    )
    args = parser.parse_args()

    root = pathlib.Path.cwd()
    version = four_part(args.version)
    spec = yaml.safe_load((root / args.build_file).read_text(encoding="utf-8"))

    publish = root / args.publish_dir
    out_dir = root / args.out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z")
    changelog = args.changelog.strip() or str(spec.get("changelog", "")).strip()

    meta = {
        "category": spec["category"],
        "changelog": changelog,
        "description": str(spec["description"]).strip(),
        "guid": spec["guid"],
        "name": spec["name"],
        "overview": spec["overview"],
        "owner": spec["owner"],
        "targetAbi": spec["targetAbi"],
        "timestamp": timestamp,
        "version": version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": list(spec.get("artifacts", [])),
    }

    slug = spec["name"].lower().replace(" ", "-")
    zip_path = out_dir / f"{slug}_{version}.zip"
    if args.manifest_only:
        if not zip_path.is_file():
            raise SystemExit(f"--manifest-only needs an existing zip: {zip_path}")
    else:
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
            archive.writestr("meta.json", json.dumps(meta, indent=4))
            for artifact in spec.get("artifacts", []):
                source = publish / artifact
                if not source.is_file():
                    raise SystemExit(f"missing build artifact: {source}")
                archive.write(source, artifact)

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()  # noqa: S324 - Jellyfin requires md5
    source_url = f"https://github.com/{args.repo}/releases/download/{args.tag}/{zip_path.name}"

    manifest_path = root / args.manifest
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.is_file() else []
    entry = next((e for e in manifest if e.get("guid") == spec["guid"]), None)
    if entry is None:
        entry = {
            "guid": spec["guid"],
            "name": spec["name"],
            "description": str(spec["description"]).strip(),
            "overview": spec["overview"],
            "owner": spec["owner"],
            "category": spec["category"],
            "imageUrl": "",
            "versions": [],
        }
        manifest.append(entry)

    entry["versions"] = [v for v in entry.get("versions", []) if v.get("version") != version]
    entry["versions"].insert(0, {
        "version": version,
        "changelog": changelog,
        "targetAbi": spec["targetAbi"],
        "sourceUrl": source_url,
        "checksum": checksum,
        "timestamp": timestamp,
    })

    manifest_path.write_text(json.dumps(manifest, indent=4) + "\n", encoding="utf-8")

    print(f"packaged {zip_path.relative_to(root)}")
    print(f"md5 {checksum}")
    print(f"url {source_url}")


if __name__ == "__main__":
    main()
