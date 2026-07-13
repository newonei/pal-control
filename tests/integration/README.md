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
loopback port. It verifies read readiness, rejects a forwarded public client
from operator APIs with the stable loopback error, and confirms the same
forwarded client can reach the player namespace without that operator error:

```powershell
.\tests\integration\control-api-boundary-smoke.ps1
```

The settlement saga smoke test runs the real JSON run store and SQLite wallet
repository in disposable directories. It verifies legacy JSON loading,
revision/CAS enforcement, persisted HTTP and recovery leases, terminal-state
monotonicity, rejection of recovery-only `Consuming -> Removed` promotion,
competing manual resolutions, restart-safe wallet idempotency, and unpaged
season statistics with more than 1,000 settlement rows. It does not emulate a
live RCON transport; full dispatch fault injection remains a release TODO:

```powershell
.\tests\integration\settlement-saga-smoke.ps1
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

The remaining planned integration coverage uses two fakes:

- fake official Palworld REST API for info/players/announce/save;
- fake Named Pipe bridge for handshake, timeouts, backpressure and uncertain outcomes.

Real-game smoke tests remain separate because they require a matching Palworld build, UE4SS and disposable save data.
