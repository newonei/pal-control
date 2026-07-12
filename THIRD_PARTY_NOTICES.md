# Third-Party Notices

This file records third-party code, data and assets used by or referenced from Pal Control. It is informational and does not replace the original license texts. Pal Control source code is distributed under the root MIT License.

## Bundled map background

The following files are copied from [`RNZ01/palworld-server-dashboard`](https://github.com/RNZ01/palworld-server-dashboard/tree/9db699751df9206f734feba30b928bc309e819ff) at commit `9db699751df9206f734feba30b928bc309e819ff`:

- `apps/console-web/public/palworld-map/full-map-z4.png`
- `apps/player-web/public/palworld-map/full-map-z4.png`

The source repository uses the MIT License at that commit. Attribution and a copy of that license are kept beside each bundled image in `README.md` and `LICENSE`.

The image depicts Palworld game content. The upstream MIT license and this notice do not grant rights to Pocketpair trademarks or other game material beyond rights the respective owners may permit.

## Generated Palworld resource catalog

`services/control-api/Resources/palworld-resource-catalog.json` is generated from multiple sources:

- [Paldeck](https://api.paldeck.cc/items) for item, Pal and technology reference data;
- [PalCalc](https://github.com/tylercamp/palcalc) for localized Pal names; PalCalc declares the MIT License, while its database is generated from Palworld game files;
- [Palworld BWIKI](https://wiki.biligame.com/palworld) for simplified-Chinese item names.

The repository currently has no recorded Paldeck redistribution license, and BWIKI/community and underlying game-data terms have not been cleared for bundling in a public source release. Treat this generated file as **publication-blocked pending permission or replacement with a clearly licensed source**. Public accessibility of an endpoint does not by itself grant redistribution rights.

## Build and runtime integrations not bundled

- [UE4SSCPPTemplate](https://github.com/UE4SS-RE/UE4SSCPPTemplate) and [RE-UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) are MIT-licensed upstream projects. Local clones, build trees and runtime binaries are excluded from this repository.
- The Palworld-specific runtime currently targets the [`Okaetsu/RE-UE4SS`](https://github.com/Okaetsu/RE-UE4SS) fork at commit `c2ac246447a8bcd92541070cb474044e7a2bbbe6`. It is downloaded separately and is not relicensed by Pal Control.
- PalDefender is an optional separately installed integration. Its source/runtime is not bundled in this repository.
- Palworld Dedicated Server and SteamCMD are external runtimes and must never be copied into Pal Control releases.

## Package dependencies

JavaScript dependency names and exact versions are recorded in `package-lock.json`. Their licenses include MIT, Apache-2.0, MPL-2.0, BSD-3-Clause, ISC and 0BSD packages. In particular, `lightningcss` and its platform packages are MPL-2.0.

The Control API directly references:

- `Microsoft.Data.Sqlite` — MIT License;
- `SQLitePCLRaw.bundle_e_sqlite3` — Apache License 2.0.

When distributing compiled frontend, API or Native MOD artifacts, collect and ship the license/notices required by the exact dependency versions in that build. Do not assume the future Pal Control project license replaces dependency obligations.

## Trademarks and game content

Pal Control is an unofficial community project and is not affiliated with, authorized by or endorsed by Pocketpair. Palworld names, trademarks, game data and game assets belong to their respective owners.
