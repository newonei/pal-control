# Bridge smoke server

This disposable process implements the Native Mod side of the length-prefixed Named Pipe protocol. It verifies Control API reconnection, hello parsing, heartbeat handling, and client-overlay command/result framing before a UE4SS binary is installed.

It advertises `bridge.hello` and `announcements.overlay.write`; inventory and Pal capabilities remain disabled. Overlay commands return a deterministic fake delivery result and never contact a live Palworld process.

```powershell
dotnet run --project tools\bridge-smoke
```

Pass a custom pipe name as the first argument when running integration tests in parallel.
