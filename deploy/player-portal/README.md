# Player Portal Caddy Template

This directory is the deployable boundary for the public player portal. It does not contain a domain, password, API token, certificate, or game administrator credential.

## Required variables

Set these variables in the Caddy service account environment before validating or starting Caddy:

| Variable | Meaning |
| --- | --- |
| `PLAYER_PORTAL_DOMAIN` | Public DNS name owned by the operator, without a scheme or path |
| `PLAYER_PORTAL_ROOT` | Absolute path to the built `apps/player-web/dist` directory |
| `PLAYER_PORTAL_ACCESS_LOG` | Absolute writable path for the JSON access log |

`player-portal.env.example` documents the names only. Caddy does not load it automatically. Do not add secrets to that file.

## Validate before reload

From an elevated PowerShell session, set the three variables for that process and run:

```powershell
caddy fmt --diff --config .\deploy\player-portal\Caddyfile
caddy validate --config .\deploy\player-portal\Caddyfile --adapter caddyfile
caddy adapt --config .\deploy\player-portal\Caddyfile --adapter caddyfile --pretty
```

Inspect the adapted output and confirm that the only `reverse_proxy` matcher is `/api/v1/player/*`, its only upstream is `127.0.0.1:5180`, player request bodies are capped at 16384 bytes, and dotfile paths are rejected before static serving. After copying the validated file into the service configuration location:

```powershell
caddy reload --config C:\ProgramData\Caddy\Caddyfile --adapter caddyfile
```

If Caddy is not already installed as a service, follow the official Windows `sc.exe` or WinSW procedure rather than starting it from a logged-in desktop session. Give the service identity read-only access to the static directory, write access to its log and persistent Caddy data directories, and no access to Palworld passwords.

Detailed deployment and security instructions are in [the player portal documentation](../../docs/player-portal/README.md).
