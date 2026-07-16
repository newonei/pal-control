# Exchange-zone calibration verifier

This tool verifies a signed, live Palworld exchange-zone calibration bundle. It
does not connect to a server, collect evidence, manufacture reviewer approval or
turn repository fixtures into production acceptance.

The production CLI has no test/bypass policy. Every verification requires:

- the exact schemas embedded in the built assembly;
- an external trust-store file plus its independently supplied SHA-256 pin;
- controlled expected server, game build, Steam build, content version/number/hash
  and zone id values;
- a maximum evidence age and an unexpired `expiresAt`;
- ECDSA P-256 signatures on every raw capture;
- a different trusted reviewer key signing the complete canonical evidence hash
  and all-artifact manifest hash.

Before `prepare-review` returns, before/after report publication, and before
`verify-report` returns, the verifier reopens evidence, trust store and every
artifact and compares canonical path, byte length and SHA-256 with the validated
snapshots. Report and sidecar receive the same final recheck. Concurrent mutation
therefore fails closed instead of returning a result for stale bytes.

The tool, content validator, configured-zone validator, player map projection and
settlement gate use the same `ZoneGeometryLimits` and `ExtractionZoneGeometry`.
The radius is in `(0, 10000]`; eligibility is recomputed as
`(x-centerX)^2 + (y-centerY)^2 <= radius^2`.

## Embedded contracts

`zone-calibration-evidence.schema.v1.json` and
`zone-calibration-report.schema.v1.json` are `EmbeddedResource` inputs. Runtime
verification never accepts a caller-selected schema file. Print the official
assembly hashes with:

```powershell
dotnet run --project tools/zone-calibration/PalControl.ZoneCalibration.csproj `
  -c Release -- schema-info
```

Evidence must bind the printed `evidenceSchema` hash. A canonical report binds
both embedded schema hashes and is accepted only when its JSON bytes are the
exact minified serializer output plus one LF byte.

## Trust store

The externally managed trust store is strict JSON:

```json
{
  "schema": "https://schemas.pal-control.dev/zone-calibration-trust-store.v1.json",
  "schemaVersion": "1.0.0",
  "captureKeys": [
    {
      "keyId": "capture-key-2026a",
      "subjectId": "subj:hmac-sha256:<64-lower-hex>",
      "pseudonymDomain": "zone-calibration:<calibrationId>",
      "algorithm": "ecdsa-p256-sha256",
      "publicKeySpkiBase64": "<P-256-SPKI-DER-as-base64>",
      "validFrom": "2026-07-01T00:00:00Z",
      "expiresAt": "2026-08-01T00:00:00Z",
      "revoked": false
    }
  ],
  "reviewerKeys": [
    {
      "keyId": "review-key-2026a",
      "subjectId": "subj:hmac-sha256:<different-64-lower-hex>",
      "pseudonymDomain": "zone-calibration:<calibrationId>",
      "algorithm": "ecdsa-p256-sha256",
      "publicKeySpkiBase64": "<different-P-256-SPKI-DER-as-base64>",
      "validFrom": "2026-07-01T00:00:00Z",
      "expiresAt": "2026-08-01T00:00:00Z",
      "revoked": false
    }
  ]
}
```

Key ids are globally unique. Capture and reviewer public keys cannot be reused.
Reuse detection imports each key, verifies the NIST P-256 curve, then fingerprints
the canonical `ExportSubjectPublicKeyInfo()` DER rather than caller-supplied SPKI
bytes, so alternate accepted encodings of one EC point cannot create two roles.
The capture key subject must equal the executor; the review key subject must
equal the reviewer. Both pseudonyms are isolated to
`zone-calibration:<calibrationId>`. Revoked, expired, missing, wrong-role and
same-key/same-subject evidence fails closed.

Store the SHA pin in a separately controlled change/approval record. Supply it
with `--trust-store-sha256` or `PAL_CONTROL_ZONE_TRUST_STORE_SHA256`; deriving the
pin from the trust-store file in the same unattended command defeats the control.

## Signed raw capture envelope

Every artifact is strict JSON with this outer shape:

```json
{
  "body": { "recordType": "pal-control-live-position-v1" },
  "attestation": {
    "algorithm": "ecdsa-p256-sha256",
    "keyId": "capture-key-2026a",
    "signedAt": "2026-07-17T01:02:03Z",
    "nonce": "<at-least-128-bit-lower-hex>",
    "bodySha256": "sha256:<exact-body-json-hash>",
    "signatureBase64": "<64-byte-IEEE-P1363-signature-as-base64>"
  }
}
```

The fixed signature statement binds schema id/version/hash, calibration campaign,
server, game/Steam build, content version/number/hash, zone, artifact id/role,
capture time, nonce and exact body hash. Nonces must be unique across the bundle.
Use a controlled exporter/HSM signer; never place a private key in the bundle.
The independent review payload additionally binds every complete `ArtifactRecord`
in stable id order: path, producer, media/capture mode, capture time, length and
envelope SHA-256. Moving an identical capture or rewriting provenance after review
invalidates the reviewer signature.

## Minimum live bundle

Eight boundary directions require 31 distinct signed artifacts:

- server-build and current-content/zone bindings;
- one center position and 16 paired boundary positions;
- signed ingress and egress coordinate traces (three or more points each);
- for each route, an inside quote request/response and an outside quote
  request/response;
- accessibility and risk captures.

Ingress must recompute outside to inside; egress must recompute inside to outside.
Inside quote responses must succeed. Outside responses must be HTTP 409 with
`PLAYER_OUTSIDE_EXTRACTION_ZONE`. Risk disposition must be `approved` or
`acceptable`, with terrain, hostile exposure and respawn return checks complete.

Artifacts are at most 1 MiB and referenced once. The verifier rejects traversal,
colon/ADS paths, Windows device names, trailing dots/spaces, symlinks/junctions,
duplicate JSON properties, unmapped fields, unstable reads, mock markers and
identity/network/credential material including embedded IPv4 and IPv6 addresses.

## Review and verification workflow

Create the evidence with the intended review time/result/key id and placeholder
review hash/signature fields. Validate all capture signatures and print the exact
review challenge:

```powershell
$common = @(
  "--evidence", "$bundle\evidence.json",
  "--trust-store", "C:\controlled\zone-trust-store.json",
  "--trust-store-sha256", $approvedTrustStoreSha,
  "--expected-server-id", $serverId,
  "--expected-game-build", $gameBuild,
  "--expected-steam-build", $steamBuild,
  "--expected-content-version-id", $contentVersionId,
  "--expected-content-version-number", $contentVersionNumber,
  "--expected-content-hash", $contentHash,
  "--expected-zone-id", $zoneId,
  "--max-evidence-age-seconds", "86400"
)

dotnet run --project tools/zone-calibration/PalControl.ZoneCalibration.csproj `
  -c Release -- prepare-review @common
```

The independent reviewer signs decoded `statementBase64` with the trusted review
key using ECDSA P-256/SHA-256 and IEEE-P1363 format, then records the two returned
hashes and signature in `evidence.review`. Final verification is create-new only:

```powershell
dotnet run --project tools/zone-calibration/PalControl.ZoneCalibration.csproj `
  -c Release -- verify @common `
  --report "$bundle\zone-calibration.canonical.json" `
  --report-hash "$bundle\zone-calibration.canonical.sha256"

dotnet run --project tools/zone-calibration/PalControl.ZoneCalibration.csproj `
  -c Release -- verify-report `
  @common `
  --report "$bundle\zone-calibration.canonical.json" `
  --report-hash "$bundle\zone-calibration.canonical.sha256"
```

In automation, build the common argument list explicitly. `verify-report`
revalidates the signed source bundle and requires byte-for-byte equality with a
regenerated report. A real gate requires exit code `0`,
`valid:true`, a matching sidecar and a separately archived external acceptance
manifest.

`tests/zone-calibration` is the only ephemeral-key harness. It exercises the
production policy without adding a custom policy to the CLI, deletes its bundle,
and cannot be used as live evidence. Controlled mutation hooks cover evidence,
trust-store, artifact, report and sidecar TOCTOU checks.

See [the live runbook](../../docs/runbooks/zone-calibration.md).
