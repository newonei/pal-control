# External acceptance evidence verifier

This tool turns the external gates in `TODO.md` into versioned, machine-checkable evidence bundles. It deliberately does **not** execute a Palworld drill or certify that a human statement is true. It verifies that a live drill produced a complete, integrity-bound package that meets the repository gate policy; factual provenance remains accountable to the recorded executor and independent reviewer.

The verifier exits `0` only for a passing, independently pinned and dual-signed live manifest, or for the explicitly conditional PostgreSQL gate when its independent `not-applicable` policy is satisfied. Templates, synthetic environments, self-asserted identities, unsigned/tampered envelopes, pending/failed conclusions, empty artifacts, hash or size mismatches, unsafe paths, mock markers, missing samples, insufficient duration, scan findings and subject-separation failures all exit `2`.

Canonical files:

- `acceptance-evidence.schema.v1.json`: strict JSON Schema 2020-12 manifest contract.
- `identity-trust-store.schema.v1.json`: strict external executor/reviewer identity and ECDSA P-256 public-key contract.
- `gate-catalog.schema.v1.json`: strict catalog contract.
- `gate-catalog.v1.json`: P0/P1/P2 external gate policy and current fixed Palworld/Steam target.
- `examples/manifest.template.json`: non-verifying example with no real identity or secret.
- `examples/identity-trust-store.template.json`: format-only example with deliberately invalid public keys; it cannot verify evidence.

## Commands

Run from the repository root:

```powershell
$project = "tools/acceptance-evidence/PalControl.AcceptanceEvidence.csproj"

dotnet run --project $project --configuration Release -- list-gates
dotnet run --project $project --configuration Release -- create-campaign `
  --output C:\acceptance\campaign-2026w30
dotnet run --project $project --configuration Release -- inspect-campaign `
  --root C:\acceptance\campaign-2026w30
dotnet run --project $project --configuration Release -- create-template `
  --gate p0-04-native-persistence `
  --output C:\acceptance\campaign-2026w30\p0-04-native-persistence\manifest.json

dotnet run --project $project --configuration Release -- hash `
  --file C:\acceptance\campaign-2026w30\p0-04-native-persistence\evidence\inventory-before.json

dotnet run --project $project --configuration Release -- combination-id `
  --manifest C:\acceptance\campaign-2026w30\p0-04-native-persistence\manifest.json

# After all fields, hashes, scan, review and conclusion are final, generate the
# exact non-self-referential bytes for both organizational signing services.
dotnet run --project $project --configuration Release -- signature-payload `
  --manifest C:\acceptance\campaign-2026w30\p0-04-native-persistence\manifest.json `
  --trust-store-sha256 sha256:<independently-pinned-digest> `
  --executor-key executor-2026q3 `
  --reviewer-key reviewer-2026q3 `
  --output C:\acceptance\signing\payload.json

dotnet run --project $project --configuration Release -- verify `
  --manifest C:\acceptance\campaign-2026w30\p0-04-native-persistence\manifest.json `
  --trust-store C:\acceptance\policy\identity-trust-store.json `
  --trust-store-sha256 sha256:<independently-pinned-digest>

dotnet run --project $project --configuration Release -- verify-campaign `
  --root C:\acceptance\campaign-2026w30 `
  --trust-store C:\acceptance\policy\identity-trust-store.json `
  --trust-store-sha256 sha256:<independently-pinned-digest>
```

`create-template` uses create-new semantics and never overwrites a manifest. It intentionally emits `evidenceMode: template`, `isSynthetic: true`, zero hashes, pending results and absent artifact files. Verification must fail until operators replace every template field with observed live values.

`create-campaign` atomically creates a new directory outside the Git repository and includes every catalog gate: 13 P0, 6 P1 and 3 P2. It creates `campaign-index.json`, one manifest/evidence directory per gate, and the final P0 review at `p0-release-review.json` with exact in-root paths to all twelve prerequisite P0 manifests. It refuses existing destinations and repository-local outputs. `inspect-campaign` is deliberately non-authoritative: every result has `verified: false` and it can only distinguish missing, template, pending or ready-for-verification files. Only `verify-campaign` with an externally pinned trust store attempts all 22 cryptographic verifications and exits `0` when every gate—including an independently approved PostgreSQL N/A decision when applicable—passes.

The trust-store SHA-256 is an external policy pin. Obtain it from the protected release record or another authenticated channel; never copy it from the manifest being verified. Every subject in the manifest must exactly match an active trust-store mapping. Key ids, subjects and imported canonical P-256 public-key fingerprints are globally unique: two identities cannot reuse the same EC key under different base64 or metadata. The executor and reviewer must use different keys and different canonical public keys; both signatures use `ecdsa-p256-sha256-p1363`. The payload binds the payload schema, trust-store digest, both key ids and every manifest field except the two signatures themselves. Private keys remain in the organization's HSM, signing service or OS key store; this repository deliberately has no production private-key signer.

Evidence paths use portable ASCII segments only. Absolute paths, `..`, empty segments, ADS syntax, Windows device names, trailing dots/spaces, symlinks and reparse points anywhere from the manifest/artifact through the filesystem root fail closed. A manifest is a complete, bounded document: at most 1 MiB, strict UTF-8 without BOM, no duplicate property at any nesting level, and either no trailing byte or one LF after the root object. Formal commands use the embedded schema/catalog and do not accept a clock override; custom policy loading exists only in the test harness.

One verification leases at most 1,024 files. It opens the root/related manifests and every artifact with a read-only deny-write/delete share on Windows and reads their parsers and hashes from those held handles. External trust/schema/catalog inputs are first parsed as bounded snapshots, then immediately leased only when the held bytes have the same SHA-256 as that parsed snapshot. After constructing the summary, the verifier rehashes both every held handle and every current path before releasing any handle. On Unix-like systems the held descriptor plus final handle/path comparison is best effort because another process may ignore advisory sharing or replace a directory entry. A successful verifier exit is therefore not a retention mechanism: after return, evidence still belongs in WORM/object-lock storage or under ACLs that deny mutation and deletion.

The external scanner covers all pre-review evidence artifacts. After review, the verifier validates both signatures and performs a second credential-pattern scan over the canonical manifest envelope, scan report and textual review records. A successful JSON summary reports both `identitySignatures: verified` and `envelopeSensitiveScan: pass`.

Version-combination ids hash a domain-separated canonical JSON structure, not delimiter-separated `key=value` text. Control characters are rejected before hashing, and recursive release review compares the complete canonical combination bytes in addition to validating each id.

For `p1-07-24-hour-soak`, `soak-metric-series` must be the exact canonical `report.json` emitted by `tools/soak`, and `soak-report-hash` must be its exact lowercase SHA-256 sidecar. Verification rejects CI/custom profiles, recomputes the complete analysis from samples and frozen production thresholds, validates contiguous sequence, the exact `load` → `recovery` phase shape, cadence/gaps and UTC coverage, and checks `attempted = succeeded + failed`, per-window request bounds, zero recovery load, and non-null P95 for attempted work. Manifest duration/sample/finding metrics must exactly equal the recomputed report.

For the final P0 release review, place the review manifest at the campaign root and each related gate manifest below that root. The `p0-11-independent-release-review` policy requires exact SHA-256 references to every other P0 external manifest. Verification recursively validates each referenced manifest; a missing, changed, failed or unexpected gate fails the release review.

See [`docs/runbooks/acceptance-evidence.md`](../../docs/runbooks/acceptance-evidence.md) for collection order, identity pseudonymization, artifact scanning and retention rules.
