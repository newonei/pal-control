# Offline world restore

This local-only tool verifies and stages a SaveManagement managed backup, then
performs a stopped-server directory switch after two distinct, short-lived
ECDSA P-256 approvals. `apply` is plan-only unless `--execute` is present. It
has no HTTP endpoint and no delete operation.

The operator guide is [SaveManagement](../../docs/runbooks/save-management.md).

## Safety boundary

- The manifest, verification anchor, source data, staging tree and every
  post-move tree are checked by relative path, length and SHA-256 inventory.
- The active world, staging, lock and journal must be siblings on one ready
  local volume. UNC and network-drive paths are rejected.
- Every configured ancestor must be free of symlinks, junctions and other
  reparse points. The plan binds the installation root, evidence directory,
  active world and deterministic lock path.
- The stop gate inspects `PalServer`, `PalServer-Win64-Shipping-Cmd` and
  `PalServer-Win64-Shipping`. A process from the planned installation blocks
  the operation. If any matching process path cannot be read, the gate fails
  closed. The gate is repeated immediately before each final `Directory.Move`
  and during recovery.
- Before `plan`, the operator must disable every Windows Service,
  scheduled task, watchdog and hosting-panel policy that can start or restart
  this PalServer installation, then stop the processes. Keep that restart
  interlock disabled through result verification or journal recovery. Plan
  creation refuses a running server and freezes the complete original file and
  empty-directory inventory into the signed plan hash. A
  process check cannot eliminate the race created by an external supervisor
  starting PalServer immediately after the check.
- Mutating commands hold the same-volume lock file with `FileShare.None` from
  initial world verification until plan/result/failure publication. `plan`
  creates this deterministic lease anchor. `status` opens that existing file
  with `FileMode.Open`, `FileAccess.Read` and `FileShare.Read`: it neither
  creates, truncates nor writes the lock, fails closed if it is missing, permits
  other read-only status leases, and is refused while apply/recover owns the
  exclusive lock.
- Approval keys are checked by the NIST P-256 curve OID
  `1.2.840.10045.3.1.7`, not merely by a 256-bit key size.
- Restore schema v3 binds one externally published trust-store SHA-256 into the
  plan and both signed approval payloads. `plan`, `approve`, and
  `apply --execute` require the same explicit lowercase pin. The tool never
  computes, adopts, or defaults this pin from the supplied trust file. The
  trust-store parser also rejects duplicate or unknown fields at every level.
- Immediately before the first world rename, the exact pinned trust store and
  two verified execute-purpose approvals are copied with create-new durable
  writes into the operation authorization directory. Journal and result files
  reference only these snapshots, so later source-file deletion or trust-store
  rotation cannot silently change or prevent crash review.

Windows path canonicalization is lexical plus a no-reparse ancestor walk. The
tool deliberately does not claim complete NT object-manager alias detection
(for example, every possible 8.3 or device-path alias). Restrict ACLs on the
installation and active-world parent, use ordinary drive-letter paths emitted
by `Resolve-Path`, and do not expose either tree through alternate aliases.

## Normal workflow

Before an incident, a security/change authority must approve the trust-store
bytes, publish their lowercase SHA-256 through an immutable change record or
ACL-protected deployment configuration, and keep that publication separate
from the restore operator. Do not let the person executing the restore generate
a replacement trust store and publish its pin. Copy the already-published value:

```powershell
$trustStorePin = '<externally-published-64-lowercase-hex-sha256>'

# First disable every supervisor and stop all PalServer process variants.

dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  plan --backup-dir C:\managed-backup\<id> `
  --active-world-dir C:\PalServer\Pal\Saved\SaveGames\0\<world-guid> `
  --server-id local --world-guid <world-guid> `
  --settings-file C:\PalServer\Pal\Saved\Config\WindowsServer\GameUserSettings.ini `
  --palserver-executable C:\PalServer\PalServer.exe `
  --evidence-dir C:\ProgramData\PalControl\restore-evidence `
  --trust-store-sha256 $trustStorePin

# Each independent approver repeats the published pin; it becomes signed data.
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  approve --plan-file <plan.json> --subject ops-a `
  --reason 'Approve incident restore after evidence review' `
  --private-key-file <ops-a-private.pem> --output-file <approval-a.json> `
  --trust-store-sha256 $trustStorePin

# Read-only revalidation; still does not switch the world.
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  apply --plan-file <plan.json>

# Mutation requires the explicit flag and exactly two current approvals.
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  apply --plan-file <plan.json> --execute `
  --trust-store <trusted-approvers.json> `
  --trust-store-sha256 $trustStorePin `
  --approval-file <approval-a.json> --approval-file <approval-b.json>
```

The result and failure reports include operation-scoped authorization snapshot
paths, exact approval-file hashes, public-key fingerprints, the externally
pinned trust-store hash, rollback/retired/candidate inventory summaries,
durable phase and every successful process-gate observation.

## Crash journal and recovery

Before the first rename, the tool publishes canonical JSON with `Flush(true)`
to a deterministic same-volume journal. Each transition is durably replaced:

| State | Filesystem intent |
| --- | --- |
| `prepared` | Cold rollback and staged candidate are fully verified. |
| `old-retired` | The original world is in the retained sibling path. |
| `candidate-active` | The candidate is active but result publication is not committed. |
| `committed` | Outcome is `restored` or fail-safe `recovered`. |

Never hand-edit or remove a journal or any sibling tree. With PalServer stopped:

```powershell
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  status --plan-file <plan.json>

# Each independent approver signs the exact current journal bytes and state.
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  approve-recovery --plan-file <plan.json> --subject ops-a `
  --reason 'Approve exact crash recovery after evidence review' `
  --private-key-file <ops-a-private.pem> `
  --output-file <recovery-a.json> --trust-store-sha256 $trustStorePin

# Repeat approve-recovery independently for ops-b, then recover:
dotnet run --project tools/world-restore/PalControl.WorldRestore.csproj -- `
  recover --plan-file <plan.json> --trust-store-sha256 $trustStorePin `
  --approval-file <recovery-a.json> --approval-file <recovery-b.json>
```

`status` is persistently read-only and verifies the complete recorded
inventories while holding a shared read lease on the existing plan-created lock
file. It does not update lock-owner bytes or modification time and will not
recreate a missing lock. `recover` holds the exclusive lock and stop gates,
verifies two new current recovery-purpose
approvals, durably snapshots them, preserves an active failed candidate, moves
the verified retired original back, verifies it again, and commits a
`recovered` outcome plus failure evidence. Recovery approvals bind the plan
hash, exact pre-authorization journal SHA-256/state/outcome, external trust pin,
and both original/candidate inventory summaries. Execute approvals are not
valid recovery approvals. `apply` never moves a pending journal left by a dead
process; only an exception caught inside the still-running, already-authorized
apply process may auto-return the old world. If a complete result was already
published just before a crash, newly approved recovery validates it and commits
`restored` instead of rolling it back.

There is no separately provisioned machine-signing key in this tool. The two
fresh, distinct recovery approvals are therefore the authorization root for
manual crash recovery; operational policy must keep those private keys under
different people or systems.

The managed backup, cold rollback, retired original, staged/failed candidate,
journal and evidence are retained. Cleanup is a separate retention-controlled
operation and is intentionally absent here.

## Synthetic verification

This smoke test uses only operating-system temporary fixtures. It covers the
three process names, cross-process lock contention, read-only status lock-byte,
mtime and complete-file-set invariance, status rejection during execute,
missing-lock fail-closed behavior, secp256k1 rejection,
external-pin omission/mismatch, rogue replacement authorities, changed trust
bytes, legacy/unexpired/wrong-purpose/same-person recovery approvals, nested
duplicate JSON properties, forged original inventory, deletion/rotation of
temporary authorization sources, canonical evidence, an in-process switch
exception, and real child-process force termination in both rename gaps
followed by freshly approved recovery:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/integration/world-restore-smoke.ps1
```

It never discovers, starts, stops or modifies a real PalServer or real save.
