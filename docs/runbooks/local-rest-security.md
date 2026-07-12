# Local-only Palworld REST

Palworld `v1.0.0.100427` binds its REST listener to `0.0.0.0:8212`; there is no documented bind-address setting. Pal Control therefore applies defense in depth:

1. Palworld REST uses Basic Auth backed by `AdminPassword`.
2. Windows Firewall rule `PalControl-Palworld-REST-Block-Remote-TCP-8212` blocks remote inbound TCP 8212 for the exact Shipping executable.
3. Control API uses `http://127.0.0.1:8212/v1/api/`.
4. Control API itself listens only on `127.0.0.1:5180` until a separate authenticated HTTPS reverse proxy is installed.
5. REST and RCON ports must never be forwarded by the router or tunnel provider.

The local REST password lives in `services/control-api/appsettings.Local.json`, which is ignored by source control. The checked-in `appsettings.json` keeps an empty password.

## Verification

```powershell
# Must be 401
Invoke-WebRequest http://127.0.0.1:8212/v1/api/info

# Control API should report the official adapter as connected
Invoke-RestMethod http://127.0.0.1:5180/api/v1/servers/local/capabilities
```

Do not paste the Basic Auth value into browser code, logs, screenshots or audit payloads.
