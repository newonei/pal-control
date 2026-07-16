# Pal Control 开源发布方案

本文是把当前工作区整理为 GitHub 公共仓库的执行清单。仓库根目录必须是 `pal-control/`，不要在上一级“幻兽商域”目录初始化 Git。

## 结论

推荐采用“**源码仓库**”与“**本地游戏运行环境**”彻底分离的方式：

```text
幻兽商域/
├─ pal-control/   <- 唯一 Git 仓库，只放本项目源码和可公开文档
├─ PalServer/     <- 游戏服务端与真实存档，不上传
├─ steamcmd/      <- SteamCMD，不上传
├─ PalDefender/   <- 独立第三方项目，不并入本仓库
├─ .tools/        <- 本机下载工具，不上传
└─ dumps/         <- 崩溃转储，不上传
```

`pal-control/` 已作为独立 Git 仓库初始化，默认分支为 `main`，远端是已公开的 [`newonei/pal-control`](https://github.com/newonei/pal-control)。因此本轮不再执行 `git init` 或新建远端；任何修改都必须先在本地完成候选提交复核，再推送到公开分支。

## 应上传

| 路径 | 内容 | 说明 |
| --- | --- | --- |
| `README.md` | 项目介绍与使用教程 | 必须上传 |
| `.gitignore`、`.gitattributes`、`.gitleaks.toml` | 忽略、换行与密钥扫描规则 | 必须上传；扫描例外必须逐条可审计，不创建空白 ignore 文件 |
| `.github/` | CI、Issue 与 PR 模板 | 必须上传；Actions 权限保持最小化 |
| `LICENSE`、`SECURITY.md`、`CONTRIBUTING.md`、`THIRD_PARTY_NOTICES.md` | 许可、安全和协作治理 | 必须上传 |
| `package.json`、`package-lock.json` | npm workspace 与依赖锁 | 必须上传，保证前端依赖可复现 |
| `apps/*/src/`、`apps/*/public/` | 两个前端的源码与静态资源 | 上传；保留地图目录中的来源与许可证文件 |
| `services/control-api/` | C# 源码、`.csproj`、安全默认配置 | 上传源码和 `appsettings.json`，不上传本地覆盖与 `data/` |
| `mods/pal-control-native/` | Native MOD 源码与版本锁 | 上传源码、配置示例、模板和依赖锁，不上传 DLL |
| `packages/contracts/` | OpenAPI 与 Bridge 契约 | 上传，但先统一许可证声明 |
| `deploy/` | Windows/Caddy 脚本与示例配置 | 只上传不含域名、密码和 Token 的模板 |
| `docs/`、`extraction-mode/README.md`、`extraction-mode/docs/`、`extraction-mode/scripts/` | 架构、玩法、换档脚本、运维手册和脱敏界面预览 | 上传；`docs/images/` 只保留 README 引用且人工检查过的产品截图，移除机器路径、真实玩家、凭据和运行证据 |
| `tests/`、`tools/` | 隔离测试与开发工具源码 | 上传源码，不上传测试输出 |

## 不应上传

### 不得提交到 Git 源码树

- `PalServer/`、`steamcmd/`、`Saved/`、`SaveGames/`；
- `.sav` 世界和玩家存档、原生轮转备份、托管备份；
- `PalWorldSettings.ini`、`PalModSettings.ini`、真实 `GameUserSettings.ini`、封禁列表；
- `services/control-api/appsettings.Local.json`、`appsettings.Production.json`、`.env*`；
- 密码、Token、证书、私钥、Cookie 密钥和真实域名配置；
- `services/control-api/data/` 下的 SQLite、JSONL、锁文件和审计日志；
- `node_modules/`、`dist/`、`bin/`、`obj/`、`artifacts/`、`output/`、`.agent-build/`；
- 本机 `third_party/`、CMake/xmake 中间目录和已生成 Solution；
- `.dll`、`.exe`、`.pdb`、`.obj`、压缩包、日志、临时测试截图、真实服务器截图和崩溃转储；README 明确引用、已脱敏且位于 `docs/images/` 的产品界面预览除外。自行干净构建、完成扫描并附 SHA-256/来源清单的安装包或 ZIP 可以作为 GitHub Release asset，但仍不得提交进源码树；
- SteamCMD 配置、缓存、日志、匿名用户数据和 depot manifest；
- PalDefender 或 UE4SS 的第三方二进制副本。

本次检查确认本机存在非空的游戏 REST/RCON 凭据、本地 Token 文件路径和 SQLite 运行数据库；它们目前都位于 `.gitignore` 覆盖范围内。不要为了“方便部署”取消这些忽略规则。

### 需要人工确认后再上传

1. `services/control-api/Resources/palworld-resource-catalog.json`
   - 当前快照组合了 Paldeck、PalCalc 与 BWIKI 中文物品名。
   - PalCalc 仓库声明 MIT，但其中的数据库由 Palworld 游戏文件生成；项目许可不等于可以重新许可原始游戏数据。
   - Paldeck 数据再分发条款尚未在仓库内记录。
   - BWIKI 社区规则存在转载和商业使用限制，当前不能仅凭“公开 API 可访问”推定允许打包再分发。
   - 当前 `.gitignore` 已阻止该快照进入首次公开提交。不要使用 `git add -f` 绕过；取得明确授权并加入第三方声明后，或替换为完全自有/明确许可的数据源后，才考虑提交。

2. `apps/*/public/palworld-map/full-map-z4.png`
   - 两份文件内容相同，单文件约 7.06 MB，不触及 GitHub 100 MB 单文件限制。
   - 当前目录已记录来源为 `RNZ01/palworld-server-dashboard` commit `9db6997`，上游该提交使用 MIT License。
   - 必须同时保留同目录的 `README.md` 和 `LICENSE`，并保留 Palworld 游戏素材归属免责声明。

3. `packages/contracts/openapi/control-api.yaml`
   - 已与根许可证同步为 SPDX `MIT`。
   - 后续若更换项目许可证，必须同时修改根 `LICENSE`、README、package metadata 与此 OpenAPI 声明。

4. Native MOD
   - `third_party/` 约 4.7 GB，包含下载的源码、子模块和构建产物，不应整体提交。
   - 已提供锁定 `dependencies.lock.json` 的 `scripts/Build-PalControlNative.ps1`：它校验 UE4SS/Unreal commit、工具链和独立构建目录，不自动下载、部署或重启服务器。
   - 首次公开只发布 `mods/pal-control-native/` 源码、模板、锁文件和构建脚本；正式发布 DLL 前仍必须保存干净环境构建证据、产物 SHA-256 和真实服务器持久化验收记录。

## 发布前阻断项

以下任一项未完成时，不得推送当前发布候选提交或创建 GitHub Release：

- [x] 选择 MIT 根许可证并加入 `LICENSE`；
- [x] 把 OpenAPI 的许可证声明改为同一 SPDX 标识；
- [x] 资源目录生成文件已从公开提交移除并由 `.gitignore` 阻止误传；
- [x] 核对并保留地图、PalCalc、UE4SS 等第三方来源与许可证；
- [x] 检查当前提交中的所有示例配置为空值/占位符，不含真实密码、Token、路径或域名；2026-07-16 已完成暂存树审计与 Gitleaks 复核；
- [x] 修正当前提交中明显过时的运行文档和机器专用路径；本轮候选的 71 个 Markdown 文件相对内链和 29 个 JSON 文件已复核；
- [x] 从代码候选 `cce69ce` 克隆全新副本并使用 npm 官方 registry 安装，重新运行两个前端构建、OpenAPI、管理台 21/21、玩家端 32/32、Chromium 15/15、统一 contract/integration 52/52 和 Control API Release；全部通过，OpenAPI 仅保留 63 条既有非阻断警告，Control API 为 0 警告、0 错误；
- [x] 使用 Gitleaks CLI `8.30.1` 对本轮完整暂存树和候选创建前全部 23 个可达历史提交重新扫描，均为 0 泄漏；固定错误曲线测试不再把 PEM 私钥块写入源码；
- [x] 人工审阅本轮 114 个暂存路径：无运行数据、存档、凭据、构建产物或第三方二进制，无机器专用用户路径，且本轮最大暂存文件约 382 KiB；仓库既有两份 7.06 MiB 地图仍按已记录的 MIT 来源保留。

本项目已采用 MIT。若未来更换许可证，必须同步评估既有贡献者授权，并统一根许可证、README、包元数据与 OpenAPI 声明。

## 验证命令

在 `pal-control` 根目录执行版本与构建检查：

```powershell
node --version
npm --version
dotnet --version

npm ci
npm run build
npm test
dotnet build .\services\control-api\PalControl.ControlApi.csproj -c Release
```

隔离 smoke tests：

```powershell
dotnet restore .\tools\bridge-smoke\PalControl.BridgeSmoke.csproj
.\tests\integration\announcement-publish-smoke.ps1
.\tests\integration\in-game-notification-smoke.ps1
.\tests\integration\live-map-smoke.ps1
.\tests\integration\save-backup-smoke.ps1
```

这些命令会生成被忽略的本地产物。不要为了上传测试证据而提交 `dist/`、`bin/`、`obj/`、`.agent-build/` 或日志。

## 形成并推送发布候选提交

完成除“候选提交干净克隆验证”外的阻断项后，在 `pal-control` 目录先确认现有仓库边界和远端：

```powershell
git rev-parse --show-toplevel
git branch --show-current
git remote -v
git add --dry-run .
git status --ignored --short
```

重点确认以下路径显示为 ignored，而不是 staged：

```powershell
git check-ignore -v `
  .\services\control-api\appsettings.Local.json `
  .\services\control-api\data\extraction\extraction-commerce.db `
  .\services\control-api\Resources\palworld-resource-catalog.json `
  .\node_modules `
  .\third_party `
  .\artifacts `
  .\backups
```

确认文件清单后暂存，同时扫描既有 Git 历史和本次暂存内容：

```powershell
git add .
git status --short
git diff --cached --name-status
gitleaks git --redact --no-banner --config .gitleaks.toml
gitleaks git --staged --redact --no-banner --config .gitleaks.toml
git diff --cached --check
```

以上命令要求预先安装并可直接调用 Gitleaks。当前候选已用 CLI `8.30.1` 验证；CI 的 action 固定在不可变提交，但 action 版本不等于 CLI 版本。以后发布可使用当时受支持的 CLI，必须先记录 `gitleaks version`，且不得跳过扫描；安装来源见 [Gitleaks 官方发布页](https://github.com/gitleaks/gitleaks/releases)。

两次 Gitleaks、完整暂存清单和工作区构建验证全部通过后，先只在本地创建候选提交，不要推送：

```powershell
git commit -m "feat: harden weekly resource economy MVP"
```

随后从这个本地提交创建独立干净 clone，并在 clone 内重新安装和验证。以下命令会打印临时目录；验证结束后确认路径无误，再手工删除该目录：

```powershell
$verifyRoot = Join-Path $env:TEMP ("pal-control-verify-" + [Guid]::NewGuid().ToString("N"))
git clone --no-local . $verifyRoot
Push-Location $verifyRoot
npm ci
npm run build
npm test
dotnet build .\services\control-api\PalControl.ControlApi.csproj -c Release
Pop-Location
$verifyRoot
```

干净 clone 全部通过并完成最终暂存清单复核后，才允许推送：

```powershell
git push -u origin main
```

不要在命令行中粘贴 GitHub Token。优先使用 Git Credential Manager、GitHub CLI 登录或 SSH key。

## GitHub 推荐设置

当前仓库已是 **Public**，本地候选提交在通过复核前不得推送。远端建议配置：

1. 启用 Secret scanning、Push protection 和 Dependabot alerts；
2. 启用私密 Security Advisories；
3. 为 `main` 配置合并前检查和禁止强推；
4. Actions 工作流加入仓库前逐行审查其权限，不执行来自不受信 PR 的秘密；
5. 创建 Release 时只上传自行构建且有来源清单的产物，不打包游戏服务端或第三方闭源文件；
6. 在仓库 About 中使用 `palworld`、`dedicated-server`、`react`、`dotnet`、`ue4ss` 等主题，并保留“unofficial”说明。

## 仓库协作文件

当前仓库已包含：

- `SECURITY.md`：漏洞私下报告方式和受支持版本；
- `CONTRIBUTING.md`：开发、测试、契约和安全规则；
- `THIRD_PARTY_NOTICES.md`：外部代码、数据、图片和许可证清单；
- `.gitattributes`：文本换行和二进制文件识别；
- `.github/workflows/ci.yml`：前端、`.NET`、契约和隔离测试；
- Issue/PR 模板：要求测试证据、风险、迁移和回滚说明。

## 最终发布判定

达到以下状态才可以称为“已准备公开”：

- Git 仓库根是 `pal-control/`；
- staged 清单中没有运行时、存档、凭据、日志、数据库、构建产物或第三方二进制；
- 项目许可证与 OpenAPI 完全一致；
- 第三方地图和目录数据的来源、许可和归属可以被验证；
- README 中的命令能在干净 clone 上运行；
- 管理 API 仍只监听 loopback，文档没有暗示可直接公网开放；
- Native 构建限制、目标游戏版本和未完成的生产门槛都被清楚披露。
