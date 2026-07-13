# Contract tests

Run the current contract suite from the repository root:

```powershell
npm run test:contract
```

`resource-economy-contract.ps1` protects the Scheme A resource-economy
boundary without contacting a game server. It verifies that the settlement
whitelist is non-empty and case-insensitively unique, all prices are positive
and bounded, and every item exactly matches the committed Scheme A item-id
fixture. When the ignored, locally authorized full Palworld resource catalog is
present, the test additionally checks every ID against it; a clean clone does
not require or redistribute that external dataset. The contract also verifies
that quotes remain whitelist-only, inventory aggregation covers `Items`,
`Food`, and `DropSlot`, and both overview endpoints and the OpenAPI schema stay
pinned to `weekly-resource-economy`, with explicit settled, failed, uncertain,
and exchanged-value metrics.

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
