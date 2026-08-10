#!/usr/bin/env python3
"""Import identification codes and CHZ tokens from the legacy XOR file.

The script is dry-run by default. It never prints passwords or tokens and
verifies that every imported password already matches an active credential.
"""

import argparse
import base64
import datetime
import hashlib
import hmac
import json
import sqlite3
from pathlib import Path


def decode_source(path: Path) -> list[dict]:
    encrypted = base64.b64decode(path.read_text(encoding="utf-8").strip(), validate=True)
    decoded = bytes(value ^ 0xAA for value in encrypted).decode("utf-8")
    value = json.loads(decoded)
    if not isinstance(value, list):
        raise ValueError("Legacy source root must be an array")
    return value


def password_matches(password: str, stored: str) -> bool:
    parts = stored.split("$")
    if len(parts) != 4 or parts[0] != "PBKDF2-SHA256":
        return False
    iterations = int(parts[1])
    salt = base64.b64decode(parts[2], validate=True)
    expected = base64.b64decode(parts[3], validate=True)
    actual = hashlib.pbkdf2_hmac("sha256", password.encode(), salt, iterations, len(expected))
    return hmac.compare_digest(actual, expected)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("database", type=Path)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()

    rows = decode_source(args.source)
    connection = sqlite3.connect(args.database)
    connection.execute("PRAGMA foreign_keys = ON")
    try:
        imported: list[tuple[int, str, str]] = []
        seen: set[str] = set()
        for row in rows:
            client_id = str(row.get("ClientId", "")).strip()
            password = str(row.get("Password", "")).strip()
            token = str(row.get("Token", "")).strip()
            if not client_id or not password or not token:
                raise ValueError(f"Client record has empty required fields: {client_id or '<missing id>'}")
            if client_id in seen:
                raise ValueError(f"Duplicate ClientId: {client_id}")
            seen.add(client_id)

            client = connection.execute(
                "SELECT Id FROM Clients WHERE ExternalClientId = ?", (client_id,)
            ).fetchone()
            if client is None:
                raise ValueError(f"Client is absent from database: {client_id}")
            hashes = connection.execute(
                "SELECT PasswordHash FROM Credentials WHERE ClientId = ? AND IsActive = 1",
                (client[0],),
            ).fetchall()
            if not hashes or not any(password_matches(password, item[0]) for item in hashes):
                raise ValueError(f"Identification code does not match active credential: {client_id}")
            imported.append((client[0], password, token))

        database_client_count = connection.execute("SELECT COUNT(1) FROM Clients").fetchone()[0]
        print(f"Validated {len(imported)} source records against {database_client_count} database clients.")
        if not args.apply:
            print("Dry run completed. Re-run with --apply to write values.")
            return

        now = datetime.datetime.now(datetime.timezone.utc).isoformat()
        with connection:
            for internal_id, password, token in imported:
                connection.execute(
                    """
                    INSERT INTO ClientSettings (
                        ClientId, IdentificationCode, ChzToken,
                        RuDesktopEnabled, RuDesktopAutoOfferPasswordSetup,
                        RuDesktopPasswordHash, EngineerAlgorithm, EngineerIterations,
                        EngineerSaltBase64, EngineerPasswordHashBase64
                    ) VALUES (?, ?, ?, 0, 0, NULL, NULL, NULL, NULL, NULL)
                    ON CONFLICT(ClientId) DO UPDATE SET
                        IdentificationCode = excluded.IdentificationCode,
                        ChzToken = excluded.ChzToken
                    """,
                    (internal_id, password, token),
                )
                connection.execute(
                    "UPDATE Clients SET UpdatedAtUtc = ? WHERE Id = ?", (now, internal_id)
                )
        print(f"Imported {len(imported)} client integration settings.")
    finally:
        connection.close()


if __name__ == "__main__":
    main()
