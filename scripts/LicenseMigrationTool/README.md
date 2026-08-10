# LicenseMigrationTool

One-time transition tool that converts the enabled client/device entries from
the signed legacy snapshot into individually signed `PersonalGrant` records.

The private key is read locally and is never uploaded or included in an HTTP
request. Dry-run is the default. The tool fetches the server device and license
lists, verifies every eligible pair, creates snapshot devices missing from the
API, and only changes the server with `--apply` plus an explicit `PUBLISH`
confirmation. Active API devices which are disabled or absent in the snapshot
are reported and do not receive a grant.

The script uses Python's `cryptography` package. In Codex Desktop it is already
available in the bundled Python runtime.

```powershell
& "C:\Users\admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe" `
  .\scripts\LicenseMigrationTool\migrate_personal_grants.py `
  "F:\Загрузки\licenses (2).json" `
  "C:\Users\admin\AppData\Local\HonestFlowConfigEditor\keys\private.pem" `
  "C:\Users\admin\AppData\Local\HonestFlowConfigEditor\keys\public.pem" `
  --overrides .\scripts\LicenseMigrationTool\migration-overrides.2026-08-10.json
```

After reviewing the dry-run counts, add `--apply` to publish the grants.
The admin API key is requested with masked console input and is not persisted.
