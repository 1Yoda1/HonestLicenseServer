#!/usr/bin/env python3
"""Create individually signed PersonalGrant records from a legacy snapshot.

Dry-run is the default. The ECDSA private key stays on the local computer and
only signed grant bytes are sent to the HonestLicenseServer admin API.
"""

import argparse
import base64
import getpass
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec


def compact_json(value: object) -> bytes:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def api_request(base_url: str, admin_key: str, method: str, path: str, body=None):
    data = compact_json(body) if body is not None else None
    request = urllib.request.Request(
        base_url.rstrip("/") + "/" + path.lstrip("/"),
        data=data,
        method=method,
        headers={"X-Admin-Key": admin_key, "Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        raw = error.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{method} {path}: HTTP {error.code} {raw}") from error


def pair(client_id: str, device_id: str) -> tuple[str, str]:
    return client_id, device_id


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("private_key", type=Path)
    parser.add_argument("public_key", type=Path)
    parser.add_argument("--api-url", default="https://api.honestflow.ru")
    parser.add_argument("--key-id", default="primary-2026")
    parser.add_argument("--overrides", type=Path)
    parser.add_argument("--local-check", action="store_true")
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()

    snapshot = json.loads(args.source.read_text(encoding="utf-8-sig"))
    private_key = serialization.load_pem_private_key(args.private_key.read_bytes(), password=None)
    public_key = serialization.load_pem_public_key(args.public_key.read_bytes())
    if not isinstance(private_key, ec.EllipticCurvePrivateKey) or not isinstance(public_key, ec.EllipticCurvePublicKey):
        raise ValueError("The supplied keys are not ECDSA keys.")
    if private_key.curve.name != "secp256r1" or public_key.curve.name != "secp256r1":
        raise ValueError("The supplied keys are not ECDSA P-256 keys.")
    if private_key.public_key().public_numbers() != public_key.public_numbers():
        raise ValueError("private.pem and public.pem are not a matching key pair.")

    clients = snapshot.get("Clients", [])
    client_index = {client["ClientId"]: client for client in clients}
    eligible = [
        (client, device)
        for client in clients if client.get("Enabled") is True
        for device in client.get("Devices", []) if device.get("Enabled") is True
    ]
    print(f"Source revision: {snapshot['Revision']}")
    print(f"Clients in snapshot: {len(clients)}")
    print(f"Devices in snapshot: {sum(len(x.get('Devices', [])) for x in clients)}")
    print(f"Eligible enabled client/device pairs: {len(eligible)}")
    print(f"Mode: {'APPLY' if args.apply else 'DRY RUN'}")
    if args.local_check:
        print("Local snapshot and ECDSA key-pair check completed successfully.")
        return 0

    admin_key = getpass.getpass("Admin API key: ")
    if not admin_key:
        raise ValueError("Admin API key is empty.")
    _, server_devices = api_request(args.api_url, admin_key, "GET", "/api/admin/devices")
    _, server_licenses = api_request(args.api_url, admin_key, "GET", "/api/admin/licenses")
    device_index = {pair(x["clientId"], x["deviceId"]): x for x in server_devices}
    if args.overrides:
        overrides = json.loads(args.overrides.read_text(encoding="utf-8-sig"))
        for item in overrides.get("IncludeApiDevices", []):
            key = pair(item["ClientId"], item["DeviceId"])
            server_device = device_index.get(key)
            client = client_index.get(item["ClientId"])
            if server_device is None:
                raise ValueError(f"Override device is absent from API: client={key[0]}, device={key[1]}")
            if client is None:
                raise ValueError(f"Override client is absent from snapshot: client={key[0]}")
            if server_device["status"].lower() != "active":
                raise ValueError(f"Override device is not active in API: client={key[0]}, device={key[1]}")
            if not any(pair(c["ClientId"], d["DeviceId"]) == key for c, d in eligible):
                eligible.append((client, {
                    "DeviceId": server_device["deviceId"],
                    "Name": server_device.get("name"),
                    "Address": server_device.get("address"),
                    "Comment": server_device.get("comment"),
                    "Enabled": True,
                }))
        print(f"Explicitly included API devices: {len(overrides.get('IncludeApiDevices', []))}")
    eligible_pairs = {pair(c["ClientId"], d["DeviceId"]) for c, d in eligible}
    migration_revision = snapshot["Revision"] * 1000 + 1
    errors = []
    plans = []
    missing_devices = []

    for client, device in eligible:
        key = pair(client["ClientId"], device["DeviceId"])
        server_device = device_index.get(key)
        if server_device is None:
            missing_devices.append((client, device))
        elif server_device["status"].lower() != "active":
            errors.append(f"Not active in API: client={key[0]}, device={key[1]}, status={server_device['status']}")
            continue
        already_published = any(
            x["clientId"] == key[0] and x["deviceId"] == key[1]
            and x["revision"] == migration_revision and x["signatureScope"] == "PersonalGrant"
            for x in server_licenses
        )
        if already_published:
            continue

        grant = {
            "schemaVersion": snapshot["SchemaVersion"],
            "revision": migration_revision,
            "clientId": client["ClientId"],
            "deviceId": device["DeviceId"],
            "issuedAtUtc": snapshot["IssuedAtUtc"],
            "validUntilUtc": snapshot["ValidUntilUtc"],
            "minHonestFlowVersion": client.get("MinHonestFlowVersion"),
            "offlineGraceHours": client.get("OfflineGraceHours", 0),
            "features": client.get("Features", []),
            "device": {
                "name": device.get("Name"),
                "address": device.get("Address"),
                "comment": device.get("Comment"),
            },
        }
        grant_bytes = compact_json(grant)
        signature = private_key.sign(grant_bytes, ec.ECDSA(hashes.SHA256()))
        plans.append((key, {
            "grantBase64": base64.b64encode(grant_bytes).decode("ascii"),
            "signatureBase64": base64.b64encode(signature).decode("ascii"),
            "keyId": args.key_id,
        }))

    extra_active = [
        x for x in server_devices
        if x["status"].lower() == "active" and pair(x["clientId"], x["deviceId"]) not in eligible_pairs
    ]
    print(f"Already migrated: {len(eligible) - len(errors) - len(plans)}")
    print(f"Planned publications: {len(plans)}")
    print(f"Planned missing device creations: {len(missing_devices)}")
    print(f"Active API devices not eligible in snapshot: {len(extra_active)}")
    for device in extra_active[:20]:
        print(
            "INFO: API-only/non-eligible device: "
            f"client={device['clientId']}, device={device['deviceId']}, name={device.get('name', '')}"
        )
    for client, device in missing_devices:
        print(
            "INFO: Missing API device will be created on apply: "
            f"client={client['ClientId']}, device={device['DeviceId']}, name={device.get('Name', '')}"
        )
    if errors:
        for error in errors[:20]:
            print("ERROR: " + error)
        if len(errors) > 20:
            print(f"... and {len(errors) - 20} more errors.")
        raise RuntimeError("Migration stopped because the snapshot and API do not match.")
    if not args.apply:
        print("Dry run completed. No licenses were published.")
        return 0

    print(f"About to create {len(missing_devices)} missing devices and publish {len(plans)} signed personal grants.")
    if input("Type PUBLISH to continue: ") != "PUBLISH":
        print("Cancelled.")
        return 3
    for client, device in missing_devices:
        api_request(args.api_url, admin_key, "POST", "/api/admin/devices", {
            "clientId": client["ClientId"],
            "deviceId": device["DeviceId"],
            "name": device.get("Name") or device["DeviceId"],
            "address": device.get("Address"),
            "comment": device.get("Comment"),
        })
        print(f"Created missing device: client={client['ClientId']}, device={device['DeviceId']}")

    for index, (key, request) in enumerate(plans, 1):
        try:
            status, _ = api_request(args.api_url, admin_key, "POST", "/api/admin/licenses", request)
            if status != 201:
                raise RuntimeError(f"Unexpected HTTP {status}")
        except Exception as error:
            raise RuntimeError(
                f"Publishing failed after {index - 1} grants for client={key[0]}, device={key[1]}: {error}"
            ) from error
        print(f"\rPublished {index}/{len(plans)}", end="", flush=True)
    print("\nMigration completed successfully.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:
        print(f"ERROR ({type(exception).__name__}): {exception}", file=sys.stderr)
        raise SystemExit(1)
