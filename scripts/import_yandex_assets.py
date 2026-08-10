#!/usr/bin/env python3
"""Import HonestFlow distribution metadata from a public Yandex Disk folder."""

import argparse
import getpass
import json
import re
import sys
import urllib.error
import urllib.parse
import urllib.request


YANDEX_RESOURCE_API = "https://cloud-api.yandex.net/v1/disk/public/resources"


def get_folder(public_key: str, path: str = "/") -> dict:
    query = urllib.parse.urlencode({"public_key": public_key, "path": path, "limit": 100})
    with urllib.request.urlopen(f"{YANDEX_RESOURCE_API}?{query}", timeout=30) as response:
        return json.load(response)


def put_asset(api_url: str, admin_key: str, asset: dict) -> None:
    component = urllib.parse.quote(asset["component"], safe="")
    version = urllib.parse.quote(asset["version"], safe="")
    body = {
        "fileName": asset["fileName"],
        "architecture": asset["architecture"],
        "downloadUrl": None,
        "yandexPublicKey": asset["yandexPublicKey"],
        "yandexPath": asset["yandexPath"],
        "sha256": asset.get("sha256"),
        "sizeBytes": asset.get("sizeBytes"),
    }
    request = urllib.request.Request(
        f"{api_url.rstrip('/')}/api/admin/assets/{component}/{version}",
        data=json.dumps(body, separators=(",", ":")).encode("utf-8"),
        method="PUT",
        headers={"X-Admin-Key": admin_key, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            if response.status != 204:
                raise RuntimeError(f"Unexpected HTTP {response.status}")
    except urllib.error.HTTPError as error:
        details = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"HTTP {error.code}: {details}") from error


def asset(component: str, version: str, architecture: str,
          item: dict, public_key: str) -> dict:
    return {
        "component": component,
        "version": version,
        "architecture": architecture,
        "fileName": item["name"],
        "yandexPublicKey": public_key,
        "yandexPath": item["path"],
        "sha256": item.get("sha256"),
        "sizeBytes": item.get("size"),
    }


def discover(public_key: str) -> list[dict]:
    root = get_folder(public_key)
    items = root.get("_embedded", {}).get("items", [])
    result: list[dict] = []

    for folder in items:
        if folder.get("type") != "dir" or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", folder["name"]):
            continue
        for item in get_folder(public_key, folder["path"]).get("_embedded", {}).get("items", []):
            if item.get("type") == "file" and item.get("name") == "HonestFlow.exe":
                result.append(asset("HonestFlow", folder["name"], "any", item, public_key))

    patterns = [
        (re.compile(r"KKT10-(\d+\.\d+\.\d+\.\d+)-windows(32|64)-setup(?:-signed)?\.exe", re.I),
         lambda match: ("AtolDriver", match.group(1), "x86" if match.group(2) == "32" else "x64")),
        (re.compile(r"esm-lm-controller_(\d+\.\d+\.\d+\.\d+)-windows-setup\.exe", re.I),
         lambda match: ("Controller", match.group(1), "any")),
        (re.compile(r"esm_(\d+\.\d+\.\d+\.\d+)-windows(?:-signed)?-setup\.exe", re.I),
         lambda match: ("ESM", match.group(1), "any")),
        (re.compile(r"regime-(\d+\.\d+\.\d+-\d+)\.msi", re.I),
         lambda match: ("LmModule", match.group(1), "any")),
    ]
    for item in items:
        if item.get("type") != "file":
            continue
        for pattern, mapping in patterns:
            match = pattern.fullmatch(item["name"])
            if match:
                component, version, architecture = mapping(match)
                result.append(asset(component, version, architecture, item, public_key))
                break

    return sorted(result, key=lambda x: (x["component"], x["version"], x["architecture"]))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("public_key")
    parser.add_argument("--api-url", default="https://api.honestflow.ru")
    parser.add_argument("--admin-key-config", help=argparse.SUPPRESS)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()

    assets = discover(args.public_key)
    print(f"Discovered distributive assets: {len(assets)}")
    for item in assets:
        print(f"{item['component']} | {item['version']} | {item['architecture']} | "
              f"{item['fileName']} | {item['sizeBytes']}")
    if not args.apply:
        print("Dry run completed. No database records were changed.")
        return 0

    if args.admin_key_config:
        with open(args.admin_key_config, encoding="utf-8") as config_file:
            admin_key = json.load(config_file).get("AdminApi", {}).get("Key", "")
    else:
        admin_key = getpass.getpass("Admin API key: ")
    if not admin_key:
        raise ValueError("Admin API key is empty")
    if input(f"Type IMPORT to publish {len(assets)} assets: ") != "IMPORT":
        print("Cancelled.")
        return 3
    for index, item in enumerate(assets, 1):
        put_asset(args.api_url, admin_key, item)
        print(f"\rImported {index}/{len(assets)}", end="", flush=True)
    print("\nAsset import completed successfully.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"ERROR ({type(exception).__name__}): {exception}", file=sys.stderr)
        raise SystemExit(1)
