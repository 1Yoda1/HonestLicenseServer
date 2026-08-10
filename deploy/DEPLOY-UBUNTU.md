# HonestLicenseServer: Ubuntu deployment

The production database is `/opt/honestserver/HonestLicenseFull.db`. Never copy
an empty or local database over it.

## Build on Windows

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Publish-Linux.ps1
```

The uploadable executable is `artifacts/linux-x64/HonestLicenseServer`.

## Secrets

Copy `deploy/honestserver.env.example` to
`/etc/honestserver/honestserver.env`, preserve the current admin/signing values,
enter the Yandex application password and restrict the file:

```bash
sudo install -d -m 0750 -o root -g pavel-shadrov /etc/honestserver
sudo chmod 600 /etc/honestserver/honestserver.env
```

Do not put the production environment file in `/opt/honestserver` or Git.

## Safe release sequence

1. Verify the new binary locally and upload it under a temporary name.
2. Validate the Caddy configuration with `caddy validate`.
3. Back up SQLite with its own backup command before replacing the binary:

```bash
sudo sqlite3 /opt/honestserver/HonestLicenseFull.db ".backup '/opt/honestserver/backups/HonestLicenseFull-before-deploy.db'"
```

4. Keep the previous executable as a rollback copy.
5. Install the new executable, restart the service and inspect its journal.
6. Test `/swagger`, login, current license, and `POST /api/connection-requests`.

The first successful start creates the `ConnectionRequests` table in the
existing database without replacing any existing table or data.

## Same-origin website

Prefer routing the site's `/api/*` path to `127.0.0.1:5498` through Caddy.
The browser can then keep using `fetch('/api/connection-requests')` without a
CORS policy. Do not expose port 5498 publicly.
