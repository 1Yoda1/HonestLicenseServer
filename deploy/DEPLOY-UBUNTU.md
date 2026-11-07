# HonestLicenseServer: Ubuntu deployment

The legacy production database is `/opt/honestserver/HonestLicenseFull.db`.
The service-account layout uses `/var/lib/honestserver/HonestLicenseFull.db`.
Never copy an empty or local database over either path.

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
sudo install -d -m 0750 -o root -g root /etc/honestserver
sudo chmod 600 /etc/honestserver/honestserver.env
```

Do not put the production environment file in `/opt/honestserver` or Git.

### Cloud.ru diagnostics credentials

After the server code/schema deployment, configure the private diagnostic
archive bucket from Windows. The script prompts for both values without echoing
them, creates or updates the dedicated mode-600 file
`/etc/honestserver/honestserver-diagnostics.env`, and adds a systemd drop-in at
`/etc/systemd/system/honestserver.service.d/diagnostic-storage.conf`. It does
not edit the service's existing environment file. The script also creates the
otherwise-missing `/etc/honestserver` directory safely, then restarts
`honestserver` and checks local Swagger:

```powershell
.\deploy\configure-diagnostic-storage.ps1
```

The script asks for the Cloud.ru tenant ID, then Key ID and Key Secret. Enter
the tenant ID from **Object Storage → API settings**, not the Cloud.ru project
name. Cloud.ru requires the S3 access identifier in the form
`tenant_id:key_id`; the script combines those two values itself.

Use `-SshTarget user@host` when the production SSH target differs from the
default in `deploy-production.ps1`. Real credentials are never written to the
repository; temporary local and remote files are removed in `finally`/`trap`.
If restart or the health check fails, the previous dedicated diagnostic file
and drop-in are restored automatically.

## Safe release sequence

1. Verify the new binary locally and upload it under a temporary name.
2. Validate the Caddy configuration with `caddy validate`.
3. Back up SQLite with its own backup command before replacing the binary.
   Use the legacy path before the service-account migration and the state path
   afterwards:

```bash
sudo sqlite3 /var/lib/honestserver/HonestLicenseFull.db ".backup '/var/lib/honestserver/backups/HonestLicenseFull-before-deploy.db'"
```

4. Keep the previous executable as a rollback copy.
5. Install the new executable, restart the service and inspect its journal.
6. Test `/swagger`, login, current license, and `POST /api/connection-requests`.

The first successful start creates the `ConnectionRequests` table in the
existing database without replacing any existing table or data.

## Separate service account and SQLite migration

`deploy/honestserver.service` runs the API as `honestflow-svc`; the SSH/deploy
account remains `pavel-shadrov`. The executable is installed root-owned and is
read-only to the service. SQLite is the only persistent service write location.

### Production preflight

Do these checks on the host in the approved maintenance window; they do not
change data or restart the service. They must be run by an administrator, not
through an unattended deployment command.

```bash
sudo -l -U pavel-shadrov
sudo -u honestflow-svc test -x /opt/honestserver/HonestLicenseServer
sudo -u honestflow-svc test -r /var/lib/honestserver -a -w /var/lib/honestserver -a -x /var/lib/honestserver
sudo test -r /etc/honestserver/honestserver.env
test ! -e /opt/honestserver/appsettings.json || sudo -u honestflow-svc test -r /opt/honestserver/appsettings.json
test ! -e /opt/honestserver/appsettings.Local.json || sudo -u honestflow-svc test -r /opt/honestserver/appsettings.Local.json
```

`systemd` reads `/etc/honestserver/honestserver.env` as root and supplies its
variables to the service, so that file may remain root-owned mode `0600`. If an
`appsettings.json` or optional `/opt/honestserver/appsettings.Local.json` is
present, the service process itself must read it; use root-owned `0640` files in
group `honestflow-svc` (and keep their parent directories searchable). The
deployment preflight rejects unreadable configuration without printing it. The
current workspace has no `appsettings.Local.json`; production secrets therefore
remain expected in the protected environment file.

The actual sudo policy is not in this repository. It must allow
`pavel-shadrov`, after `sudo -v`, to run the commands used by
`deploy-production.ps1`: `systemctl is-active|stop|start honestserver`, Python
SQLite backup/integrity commands, `install`, `mv`, `chown`, `chmod`, and the
limited database-sidecar removal under `/var/lib/honestserver`, plus
`sudo -u honestflow-svc test`. Confirm those exact command paths and arguments
from `sudo -l -U pavel-shadrov` before approving the deployment.

The code may be deployed before this migration: the new executable remains
compatible with the existing absolute `ConnectionStrings__DefaultConnection`
path. Do not install the new unit or change the environment file until the
database copy has completed.

On the host, first ensure the account and state directory exist (the directory
in the current migration was already provisioned this way):

```bash
sudo useradd --system --user-group --home-dir /nonexistent --shell /usr/sbin/nologin honestflow-svc
sudo install -d -m 0700 -o honestflow-svc -g honestflow-svc /var/lib/honestserver
```

Copy the migration script to a root-controlled location and run it explicitly.
It stops the current service, checks source integrity, runs a WAL checkpoint,
creates an integrity-checked SQLite backup under `/var/lib/honestserver/backups`,
then creates the new database atomically as `honestflow-svc` mode `0600`. The
legacy database is never renamed or deleted. By default it restarts the old
service with its unchanged configuration, so the old installation remains the
rollback path.

```bash
sudo install -m 0700 deploy/migrate-sqlite-to-service-user.sh /usr/local/sbin/honestserver-migrate-sqlite
sudo /usr/local/sbin/honestserver-migrate-sqlite --apply
```

After a successful copy, make the cutover as one planned maintenance action.
Keep a transitional connection string until the new binary has completed its
first deployment: the currently running legacy binary does not read
`HONESTSERVER_SQLITE_PATH`.

1. Back up the current `/etc/systemd/system/honestserver.service` and
   `/etc/honestserver/honestserver.env` outside the application tree.
2. Install `deploy/honestserver.service` as
   `/etc/systemd/system/honestserver.service` (root-owned, mode `0644`).
3. In the existing protected environment file, retain a compatible connection
   string pointed to the migrated file **and** add the new path variable:

   ```text
   ConnectionStrings__DefaultConnection="Data Source=/var/lib/honestserver/HonestLicenseFull.db;Mode=ReadWrite;Cache=Shared"
   HONESTSERVER_SQLITE_PATH=/var/lib/honestserver/HonestLicenseFull.db
   ```

   Keep all existing admin, signing and storage variables unchanged. The old
   binary uses the connection string; the new binary prefers the dedicated path
   and rejects database creation in Production.
4. Run `sudo systemctl daemon-reload`, then explicitly restart `honestserver`
   and verify its journal and local Swagger endpoint. Next, run the approved
   `deploy-production.ps1 -Mode Code` release. Only after its health checks
   pass may the compatibility connection string be removed.

For rollback after the cutover, stop the service, restore the saved unit and
environment file pointing to `/opt/honestserver/HonestLicenseFull.db`, run
`systemctl daemon-reload`, and start the service. The original database remains
in place; do not copy the new database back over it. The migration script also
supports `--leave-service-stopped` when the unit/environment cutover is being
performed immediately under an approved maintenance window.

### Recommended migration order

1. Keep the old version and old unit running; capture the preflight evidence.
2. Run `honestserver-migrate-sqlite --apply`: it stops the old service only for
   WAL checkpoint and SQLite backup, writes the new path atomically, and
   restarts the unchanged old service.
3. Install the new unit and the transitional environment variables above; start
   it and verify local Swagger. At this point an old binary remains compatible
   because it still reads `ConnectionStrings__DefaultConnection`.
4. Run the approved code deployment. Its health checks verify the new service,
   API and target database; remove the compatibility connection string only
   after it succeeds.
5. If either the unit cutover or code deployment fails, first use the deploy
   script's binary/database rollback when it reports failure. To roll back the
   entire account migration, restore the saved old unit and environment file
   and start the service against the untouched legacy database.

## Same-origin website

Prefer routing the site's `/api/*` path to `127.0.0.1:5498` through Caddy.
The browser can then keep using `fetch('/api/connection-requests')` without a
CORS policy. Do not expose port 5498 publicly.
