#!/usr/bin/env python3
"""Add a public license verification key to an existing local JSON config."""

import argparse
import base64
import json
import os
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("config", type=Path)
    parser.add_argument("public_key_base64", type=Path)
    parser.add_argument("key_id")
    args = parser.parse_args()

    public_key = "".join(args.public_key_base64.read_text(encoding="utf-8").split())
    decoded = base64.b64decode(public_key, validate=True)
    if len(decoded) < 64:
        raise ValueError("Public key SubjectPublicKeyInfo is unexpectedly short")

    mode = args.config.stat().st_mode
    value = json.loads(args.config.read_text(encoding="utf-8"))
    keys = value.setdefault("LicenseSigningKeys", {})
    keys[args.key_id] = {"PublicKeyBase64": public_key}

    temporary = args.config.with_suffix(args.config.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.chmod(temporary, mode)
    os.replace(temporary, args.config)
    print(f"Configured license public key: {args.key_id}")


if __name__ == "__main__":
    main()
