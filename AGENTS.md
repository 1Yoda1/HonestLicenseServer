# AGENTS.md

## Purpose

This repository is the HonestFlow licensing/configuration backend:
ASP.NET Core on .NET 10 with EF Core and SQLite. Keep changes focused,
preserve licensing/security invariants, and avoid spending agent time on
mechanical operations the user can perform locally.

## Architecture map

Start here before searching broadly: - `Program.cs` --- application
composition and ASP.NET Core setup. - `Controllers/` --- HTTP API
surface. - `Authentication/` --- authentication/session/token
behavior. - `Contracts/` --- request/response contracts. - `Data/` ---
EF Core/SQLite persistence. - `Infrastructure/` --- supporting services
and integrations. - `Models/` --- persisted/domain models. -
`docs/API-RU.md` --- API documentation. -
`docs/architecture-decisions.md` --- authoritative system boundaries and
licensing decisions.

Read `docs/architecture-decisions.md` before changing authentication,
device registration, licensing, assets, configuration, signing, or
deployment behavior.

## Non-negotiable invariants

Do not change these unless the user explicitly requests an architecture
change: 1. Production SQLite is external deployment state.
`/opt/honestserver/HonestLicenseFull.db` must never be bundled into
publish output or overwritten during deployment. 2. The API never
contains or receives the private ECDSA signing key. The private key
exists only in HonestDesk. 3. The server may hold trusted ECDSA public
keys and verifies license signatures before publishing licenses. 4.
HonestFlow receives signed personal grants and verifies them locally. 5.
Preserve exact signed grant bytes. Never parse and reserialize a signed
grant before verification/storage/return. 6. `deviceId` is an HonestFlow
installation GUID, not a hardware fingerprint. 7. `Devices.Address` is a
physical shop/workplace address, never an IP address. 8. Clients/devices
are disabled logically; do not replace this with destructive deletion
unless explicitly requested. 9. Executables/installers/archives remain
outside this API. Return metadata or temporary download redirects; do
not turn the API into binary storage. 10. Effective component version
remains `client override ?? global version` unless explicitly
redesigned. 11. Never commit admin keys, tokens, passwords, signing
secrets, production secrets, or production database files.

## API and contract changes

Preserve existing authorization checks. Before changing a public
request/response contract, inspect affected controllers/contracts and
likely HonestFlow/HonestDesk consumers. Preserve backward compatibility
unless the task explicitly requires a breaking change. Update
`docs/API-RU.md` when public behavior changes. Do not casually rename
JSON fields, enum values, routes, status meanings, or authentication
headers.

## Database changes

Follow existing EF Core/SQLite patterns. Preserve imported data. Never
recreate/delete the production database as part of startup or
deployment. Use the repository's migration strategy for schema changes
and make transformations idempotent where practical. Do not edit
production database contents as part of a code task.

## Security-sensitive areas

Treat login/refresh/logout, token hashing/rotation/revocation, device
approval/disable flows, admin authorization, license publication/ECDSA
verification, asset access, rate limiting, and audit events as high
risk. Trace the complete request flow before editing. Mention changed
trust boundaries in the final summary.

## Agent efficiency rules

1.  Inspect only files needed for the task; use the architecture map
    before repository-wide search.
2.  Implement the smallest coherent change.
3.  Do not run `dotnet restore`, `dotnet publish`, deployment commands,
    SSH/SCP, service restarts, Git commit/push, or release creation
    unless explicitly requested.
4.  Do not repeatedly build after every edit.
5.  Small/local change: do not build unless explicitly requested.
6.  Large cross-cutting/security-sensitive change: one targeted
    build/test pass is reasonable when it materially validates the work.
7.  On validation failure, inspect the concrete error and fix only
    related issues. No unrelated cleanup.
8.  Never deploy automatically.

The user normally handles publish, Git operations and server binary
replacement separately.

## Testing

Integration tests must use a temporary SQLite database, never production
data. Primary validation when warranted:

``` powershell
dotnet test HonestLicenseServer.slnx -c Release
```

Prefer targeted tests. Never create tests depending on production
credentials/databases/private live services.

## Code style

Follow surrounding C# style. Prefer explicit, boring backend code over
unnecessary abstractions. Avoid new libraries for problems already
solved by the stack. Keep controllers thin where existing services own
logic. Avoid unrelated formatting/refactoring.

## Cross-repository awareness

Consumers are `1Yoda1/HonestFlow` and `1Yoda1/HonestDesk`. When changing
contracts, state which client is affected. Do not edit another
repository unless explicitly asked.

## Completion format

Report: what changed; files changed; whether validation ran;
contract/database/security impact; exact next manual validation command
if needed. Do not commit, push, publish, deploy, restart services, or
modify production data unless explicitly requested.
