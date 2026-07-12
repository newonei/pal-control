# Integration tests

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
