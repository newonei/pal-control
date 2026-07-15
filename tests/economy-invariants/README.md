# Economy invariants harness

Run from the repository root:

```powershell
dotnet run --project .\tests\economy-invariants\PalControl.EconomyInvariantsHarness.csproj -c Release
```

The harness executes production pricing, zone, whitelist-hash and SQLite repository code against disposable local directories. It covers deterministic daily pricing, circular-zone boundaries, canonical snapshot hashing, wallet/ledger conservation, 100 concurrent debits, 100 concurrent limited purchases, exact replay and conflicting reuse of idempotency keys, account-scoped keys, legacy JSONL migration, domain uniqueness and the SQLite unique event constraint.

This is not evidence of a live Palworld, PalDefender, RCON or Native `inventory.consume` integration. Real-server settlement, delivery and rollover acceptance remain separate release gates.
