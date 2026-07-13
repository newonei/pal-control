# Contract tests

Run the current contract suite from the repository root:

```powershell
npm run test:contract
```

`resource-economy-contract.ps1` protects the Scheme A resource-economy
boundary without contacting a game server. It verifies that the settlement
whitelist is non-empty and case-insensitively unique, all prices are positive
and bounded, every item exists in the versioned Palworld resource catalog,
quotes remain whitelist-only, and inventory aggregation continues to cover
`Items`, `Food`, and `DropSlot`. It also pins both overview endpoints and the
OpenAPI schema to `weekly-resource-economy`, with explicit settled, failed,
uncertain, and exchanged-value metrics.

`extraction-mode-options-contract.ps1` runs a small .NET harness against the
real `ExtractionModeOptions.IsValid` implementation. It freezes the enabled
`legacy-v1` bootstrap grant at 1000/300, rejects positive grants for newer
policies, and preserves the disabled production zero-grant configuration.

Additional planned checks:

- validate `control-api.yaml` as OpenAPI 3.1;
- validate bridge hello/command/result fixtures with `message.schema.json`;
- ensure generated TypeScript and C# clients stay compatible;
- verify every mutating endpoint requires Idempotency-Key, reason and revision;
- prevent newly added Pal fields from exposing owner/instance/raw save mutation.
