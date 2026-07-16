# Integration tests

Run all isolated integration tests sequentially from the repository root:

```powershell
npm run test:integration
```

Use `npm test` to run both console-web and player-web Node tests, the player
portal Playwright E2E suite, and the contract and integration suites. The
smoke tests require Windows PowerShell, .NET 10, Python 3, and restored NuGet
dependencies. They use only local fakes, synthetic resource catalogs and
disposable temporary directories; no live Palworld server or ignored local
game-data catalog is contacted.

Install the pinned Playwright Chromium once, then run the player portal suite
independently when working on responsive or accessibility behavior:

```powershell
npx playwright install chromium
npm run test:player:e2e
```

It uses mocked same-origin player APIs in a real browser and covers keyboard
navigation, dialog focus trapping/restoration, focused error guidance, a
375x812 mobile viewport, and serious/critical WCAG A/AA Axe findings on login,
portal, and modal states. CI installs Chromium before the unified test command.

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

The selective-resource-sale harness uses the real SQLite run store and Native
payload builder. It covers invalid and unsafe selections with zero side effects,
atomic source cancellation/child creation, frozen content and dynamic evidence,
20 exact replays, cross-account/source key conflicts, process restart response
loss, a 100-call selection-versus-settlement race, injected persistence rollback,
SQLite trigger evidence that derivation performs exactly one source-row update
and one child-row insert without deleting historical runs, stale row-CAS rollback
after an earlier insert in the same transaction, the full Native optimistic-lock
snapshot with a selected-only consume list, and Development hash semantics:

```powershell
.\tests\integration\selective-resource-sale-smoke.ps1
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

The economy-invariants smoke verifies wallet conservation, price and zone
evidence, 100-way personal/global stock concurrency, refund replenishment,
replay, migration and uniqueness constraints:

```powershell
.\tests\integration\economy-invariants-smoke.ps1
```

The versioned-content tests cover strict validation, canonical hashing,
semantic diff, 20-day publication/restart persistence, fail-closed stale
offers, and atomic product projection + current-pointer CAS across injected
publish and rollback failures:

```powershell
.\tests\integration\content-definitions-smoke.ps1
.\tests\integration\content-projection-atomicity-smoke.ps1
```

The economy-balance guard runs a deterministic 100-player x 7-day simulation
and rejects attested bundle/transformation/cross-currency arbitrage. The
permanent-currency contract proves the built-in 480 weekly MarketCoin inflow
and 1200 outflow caps, rejects missing/unbounded sources or sinks, and exercises
real task-reward/purchase idempotency. The reliable-task smoke proves six
version-pinned daily/weekly tasks, authoritative event gating, 20-way replay,
unique wallet/ranking rewards and restart recovery:

```powershell
.\tests\integration\economy-balance-guard-smoke.ps1
.\tests\integration\permanent-currency-contract-smoke.ps1
.\tests\integration\reliable-tasks-smoke.ps1
```

The season-leaderboard smoke uses the real SQLite settlement, reliable-task,
identity-ban, snapshot, reward-job and wallet-ledger stores. It verifies the
15-minute cutoff, minimum contribution, deterministic tie rules, per-item and
per-category aggregates, pre-freeze exclusions, post-freeze reward
cancellation, immutable audit hashes, restart persistence, and 20 replays of
freeze, standard payout and manual supplement without duplicate ledgers. It
also verifies self-only latest/by-season player results, 200/not-frozen and
stable 404 contracts, identity-override rejection, current-week rollover with
the previous frozen result still visible, and voucher/reward job-to-ledger
reconciliation across a process restart:

```powershell
.\tests\integration\season-leaderboard-smoke.ps1
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
initial world binding with restart persistence, published catalog evidence,
fail-closed stale-offer rejection, evidence-bound catalog purchase,
receipt-backed delivery, resource exchange, wallet credit, idempotent replay,
session revocation, the adapter-neutral settlement status endpoint and its
deprecated alias. The resource path also rejects empty/duplicate/unknown/
over-quantity selections, identity overrides and IDOR, then proves a selected
`Bone:2` child quote, 20 exact replays, source cancellation, selected-only RCON
deletion, unchanged unselected inventory and exactly-once wallet credit:

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
redaction, registered non-economic JSONL byte-for-byte archives, nested manifest
hashes, unknown/partial/tampered JSONL rejection, bundle-level plan-only
retention, and fail-closed pending/RPO/world/version blockers:

```powershell
.\tests\integration\continuity-rollover-smoke.ps1
```

The new-player activity smoke covers immutable versions, current-world
identity isolation, atomic dual-wallet claims, replay conflicts, uniqueness
and restart persistence:

```powershell
.\tests\integration\new-player-activity-smoke.ps1
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

The logging-correlation harness reflects the compiled service assembly and
requires an exact inventory of hosted workers, logger owners and external
adapters. It also parses IL to reject raw `Exception` logger overloads, then
injects Cookie/code/token/password/PlayerUID-bearing failures to prove that
only correlation metadata, exception type and one-way fingerprints reach the
logging provider:

```powershell
.\tests\integration\logging-correlation-smoke.ps1
```

The economy-analytics smoke runs the real SQLite repository with 120 synthetic
accounts. It proves server-observed portal/catalog facts are unique across
refreshes, successful shop and resource outcomes exclude failed/refunded and
`uncertain` rows, product/exchange rates retain complete denominators, dual
currencies and zone heat are recomputed, pagination and restart preserve the
source hash, cohorts below five are suppressed, and corrupted source JSON fails
closed with a stable code:

```powershell
.\tests\integration\economy-analytics-smoke.ps1
```

The weekly-economy-report smoke creates two adjacent SQLite seasons and uses
the production leaderboard freezer before archiving canonical aggregate and
restricted pseudonymous artifacts. It proves dual-currency/product/resource
metrics, a common-basket week-over-week inflation comparison, replay-stable
manifests, two distinct review subjects, privacy, and fail-closed source and
archive tampering. Synthetic weeks are not evidence for the real two-week
production acceptance gate:

```powershell
.\tests\integration\weekly-economy-report-smoke.ps1
```

The Windows production-deployment smoke validates pinned archive hashes,
immutable release manifests, isolated service identities, external state,
Caddy persistent paths, repeated install and static-root drift repair,
stopped-state backup, corrupt-snapshot rejection, target-health failure
recovery and same-contract rollback without discarding a newer transaction. It
rejects cross-contract binary rollback, then starts the real Release Control
API twice with an external production-shaped configuration and uses the real
SQLite provider to check integrity, foreign keys and an unchanged startup-
migration fingerprint:

```powershell
.\tests\integration\windows-production-deployment-smoke.ps1
```

The SQLite economy-reconciliation smoke creates a real account, permanent and
weekly wallet streams, delivered order/delivery, PalDefender per-item command,
receipt/evidence, resource settlement run, unique credit and idempotency records.
The read-only auditor recomputes every account from
sequence-ordered ledger entries and emits privacy-safe canonical hashes for
logical rows and every application table. The harness proves JSON formatting
does not change hashes, then injects balance, idempotency, run-credit,
delivery/receipt/per-item-command references and immutable SQL-column faults;
each must produce a stable issue code plus strict baseline row differences:

```powershell
.\tests\integration\economy-reconciliation-smoke.ps1
```

The player-notification smoke uses the durable SQLite projection with four
versioned source classes. It proves 20x replay, restart persistence, the crash
window after durable projection but before game-channel status persistence,
same-row season milestones, delivered/settled-only success semantics, identity
override rejection, A/B feed/read isolation, and no game target identifier in
the player response:

```powershell
.\tests\integration\player-notifications-smoke.ps1
```

The soak-runner smoke first drives synthetic stable/leaking time series through
the production analyzer, then samples a real short-lived .NET child process in
bounded `--ci-mode`. It verifies process/GC, SQLite DB/WAL/SHM, log, active-
session and three-queue samples, fixed read-only load, canonical JSON and the
matching SHA-256 sidecar. API-key, response-body, log-body, URL and local-path
canaries must all remain absent from the report. This short test validates the
tool only; it is not evidence that the external 24-hour gate passed:

```powershell
.\tests\integration\soak-runner-smoke.ps1
```

The zone-calibration smoke uses a dedicated harness to create ephemeral,
independent P-256 capture/reviewer keys and 31 signed artifacts. It proves the
formal CLI, external trust-store SHA pin, controlled expected bindings, coordinate
route transitions, inside-success/outside-fail-closed quote evidence, independent
review signature and strict canonical report verification. Thirty-one negative
mutations cover pin/binding/expiry/radius, campaign subject separation, artifact/
capture/review signatures, nonce replay, key reuse, route/quote/risk failures,
unsafe paths, IPv6 leakage, full artifact-metadata review binding, controlled
evidence/trust/artifact/report/sidecar races and report tampering. The harness deletes its keys and
coordinates and cannot satisfy P1-01's second-zone live Palworld acceptance:

```powershell
.\tests\integration\zone-calibration-smoke.ps1
```

The offline world-restore smoke uses only OS-temporary synthetic save trees and
renamed short-lived host processes. It proves exact managed-backup validation,
same-volume cross-process exclusion, all three PalServer executable names,
P-256 curve-OID and dual-approval gates, schema-v3 external trust-store pin,
frozen original/candidate inventories, rogue/swapped/legacy trust rejection,
nested duplicate-property JSON rejection, and canonical evidence. Real child
processes are force-terminated after each directory-move journal state and after
result publication. Temporary execute approvals are deleted and their source
trust is rotated; status takes a shared read-only lease on the existing lock
without changing its bytes/mtime or any fixture file, refuses a missing lock
and a concurrent execute, while recover requires two new
current journal-bound approvals and durable operation snapshots. Wrong/missing
pin, execute-purpose reuse, same-subject recovery, signed-but-expired recovery
and forged OriginalInventory all fail without deleting or moving a tree.
It never discovers or changes a real PalServer installation:

```powershell
.\tests\integration\world-restore-smoke.ps1
```
