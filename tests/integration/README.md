# Integration tests

Run all isolated integration tests sequentially from the repository root:

```powershell
npm run test:integration
```

Use `npm test` to run the player-web Node tests plus the contract and
integration suites. The smoke tests require Windows PowerShell, .NET 10,
Python 3, and restored NuGet dependencies. They use only local fakes and
disposable temporary directories; no live Palworld server is contacted.

The Control API boundary smoke test starts the Release executable on a random
loopback port. It verifies read readiness while disabled PalDefender/RCON keep
the two economy write paths fail-closed with independent stable blockers,
rejects a forwarded public client
from operator APIs with the stable loopback error, and confirms the same
forwarded client can reach the player namespace without that operator error:

```powershell
.\tests\integration\control-api-boundary-smoke.ps1
```

The administrator-authentication smoke test also exercises the production
EconomyAdmin safety-gate endpoint. It closes only purchase, observes
`PURCHASE_CIRCUIT_OPEN` without closing resource exchange, then reopens
purchase in the same process while preserving TOTP/reason audit attribution:

```powershell
.\tests\integration\admin-auth-smoke.ps1
```

The settlement saga smoke test runs the real SQLite settlement and wallet
repository in disposable directories, including one-time migration from the
legacy JSON run store. It verifies revision/CAS enforcement, persisted attempt
counts and lease heartbeats, terminal-state monotonicity, rejection of
recovery-only `Consuming -> Removed` promotion, and competing manual
resolutions. It also injects a SQLite failure between wallet/event preparation
and the `Removed -> Credited` transition to prove full transaction rollback,
runs 1,000 concurrent credit attempts to prove exactly-once ledger behavior,
restarts from `Credited` to prove crash-safe finalization, and validates
unpaged season statistics with more than 1,000 settlement rows. The same
harness exercises per-player serialization, same-request coalescing, bounded
server-wide queue backpressure and parallel workers for independent players:

```powershell
.\tests\integration\settlement-saga-smoke.ps1
```

The Native settlement smoke runs the production snapshot, request-hash,
receipt-validation and run-store code against a deterministic Native transport.
It covers exact slot metadata, stable-capability enforcement, atomic mismatch
rejection, transport uncertainty, pre-dispatch persistence failure, lost receipt
commit, restart persistence and no blind redispatch:

```powershell
.\tests\integration\native-settlement-smoke.ps1
```

The delivery-receipt smoke runs the production outbox and receipt store with a
deterministic PalDefender client. It verifies persistence before dispatch,
exact structured acknowledgements, malformed and missing readback handling,
accepted/dispatched/terminal-ACK/receipt persistence fault injection, terminal
immutability, 100-way queue pressure, SQLite leases/dead-letter, restart-safe
no-resend behavior, and fail-closed one-time JSONL migration:

```powershell
.\tests\integration\delivery-receipts-smoke.ps1
```

The identity-binding smoke test exercises the production SQLite repository and
its physical uniqueness constraints. It verifies Steam account isolation,
server-observed complete PlayerUID binding, forged identity rejection, weekly
world rebinding, display-name independence, history, active-binding lookup and
restart persistence:

```powershell
.\tests\integration\identity-binding-smoke.ps1
```

The player economy security smoke starts the Release Control API plus local
official-REST, PalDefender and Source-RCON protocol fakes. It exercises login
codes, Origin and CSRF enforcement, identity/IDOR isolation, strict one-time
initial world binding with restart persistence, catalog purchase, receipt-backed
delivery, resource exchange, wallet credit, idempotent replay, session revocation,
the adapter-neutral settlement status endpoint and its deprecated alias:

```powershell
.\tests\integration\player-economy-security-smoke.ps1
```

The announcement smoke test runs against a dependency-free fake official REST server and a local fake Native Bridge; it never contacts the live Palworld instance. It covers chat-only, client-overlay-only and combined delivery, per-channel results, draft and publish idempotency, different-payload conflicts, delayed dispatch, `uncertain` handling, audit transitions, and restart-safe no-resend behavior.

```powershell
.\tests\integration\announcement-publish-smoke.ps1
```

The isolated server-native in-game notification smoke test validates the live-probe-driven preset contract, strict rejection of presentation overrides and unadvertised parameters, create/dispatch idempotency, scheduling, append-only audit states, and restart recovery from `dispatched` to `uncertain` without a blind resend:

```powershell
.\tests\integration\in-game-notification-smoke.ps1
```

The live-map smoke test validates the shared official REST sampler, complete
snapshot and SSE endpoints, coordinate updates, ETag revalidation, privacy
filtering, and stale/unavailable retention without contacting the game server:

```powershell
.\tests\integration\live-map-smoke.ps1
```

The save/backup smoke test builds a disposable PalServer save tree and validates
status discovery, native-backup listing, official REST save dispatch, stable
managed snapshot creation, SHA-256 manifests, verification and tamper detection,
idempotency conflicts, uncertain outcomes, missing-snapshot failure, audit
transitions, path privacy, and restart-safe no-resend behavior:

```powershell
.\tests\integration\save-backup-smoke.ps1
```

The continuity/rollover smoke test runs the SQLite snapshot/state-machine
harness and a loopback fake-Control-API test for the production PowerShell
client. It covers default plan-only behavior, explicit rejection of old-world
deletion, deterministic step keys, managed game backup plus economy
snapshot/verify/stage, every-phase crash and lost-response recovery, credential
redaction, and fail-closed pending/RPO/world/version blockers:

```powershell
.\tests\integration\continuity-rollover-smoke.ps1
```

The isolated integration infrastructure uses deterministic local fakes:

- fake official Palworld REST API for info/players/announce/save;
- fake PalDefender REST inventory/give API and Source RCON protocol for login and
  compatibility settlement enabled only by the isolated Development host,
  `Security:DevelopmentMode=true`, `PlayerPortal:PublicSteam=false`, and the
  explicit `ExtractionMode:Rcon:AllowDevelopmentSettlement=true` diagnostic switch;
- fake Named Pipe bridge for handshake, timeouts, backpressure and uncertain outcomes.

Real-game smoke tests remain separate because they require a matching Palworld build, UE4SS and disposable save data.

The economy-observability smoke test starts a Release Control API on a random
loopback port with disposable SQLite and queue directories. It proves that both
exports require a Viewer identity, refresh remains read-only, every P0-11
metric family is present, and neither response exposes player identifiers,
cookies, tokens or passwords:

```powershell
.\tests\integration\economy-observability-smoke.ps1
```
