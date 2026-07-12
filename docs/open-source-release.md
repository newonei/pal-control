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

`pal-control/` 已作为独立 Git 仓库初始化，默认分支为 `main`；首次公开提交只包含本项目源码、文档和经审核的静态资源，不继承上级运行环境的任何历史。

## 应上传

| 路径 | 内容 | 说明 |
| --- | --- | --- |
| `README.md` | 项目介绍与使用教程 | 必须上传 |
| `.gitignore` | 敏感文件与产物排除规则 | 必须上传 |
| `package.json`、`package-lock.json` | npm workspace 与依赖锁 | 必须上传，保证前端依赖可复现 |
| `apps/*/src/`、`apps/*/public/` | 两个前端的源码与静态资源 | 上传；保留地图目录中的来源与许可证文件 |
| `services/control-api/` | C# 源码、`.csproj`、安全默认配置 | 上传源码和 `appsettings.json`，不上传本地覆盖与 `data/` |
| `mods/pal-control-native/` | Native MOD 源码与版本锁 | 上传源码、配置示例、模板和依赖锁，不上传 DLL |
| `packages/contracts/` | OpenAPI 与 Bridge 契约 | 上传，但先统一许可证声明 |
| `deploy/` | Windows/Caddy 脚本与示例配置 | 只上传不含域名、密码和 Token 的模板 |
| `docs/`、`extraction-mode/docs/` | 架构、威胁模型和运维手册 | 上传；先移除任何机器专用断言和真实路径 |
| `tests/`、`tools/` | 隔离测试与开发工具源码 | 上传源码，不上传测试输出 |

## 不应上传

### 任何情况下都不上传

- `PalServer/`、`steamcmd/`、`Saved/`、`SaveGames/`；
- `.sav` 世界和玩家存档、原生轮转备份、托管备份；
- `PalWorldSettings.ini`、`PalModSettings.ini`、真实 `GameUserSettings.ini`、封禁列表；
- `services/control-api/appsettings.Local.json`、`appsettings.Production.json`、`.env*`；
- 密码、Token、证书、私钥、Cookie 密钥和真实域名配置；
- `services/control-api/data/` 下的 SQLite、JSONL、锁文件和审计日志；
- `node_modules/`、`dist/`、`bin/`、`obj/`、`artifacts/`、`output/`、`.agent-build/`；
- 本机 `third_party/`、CMake/xmake 中间目录和已生成 Solution；
- `.dll`、`.exe`、`.pdb`、`.obj`、压缩包、日志、截图、崩溃转储；
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
   - 当前本机模板有未提交修改，干净 clone 尚无一键复现构建流程。
   - 首次公开可先发布 `mods/pal-control-native/` 源码并明确“构建流程待补”；正式发布 DLL 前必须补依赖获取脚本、锁定 commit、哈希校验、许可证汇总和干净环境构建证据。

## 发布前阻断项

以下任一项未完成时，不建议把仓库设为 Public：

- [x] 选择 MIT 根许可证并加入 `LICENSE`；
- [x] 把 OpenAPI 的许可证声明改为同一 SPDX 标识；
- [x] 资源目录生成文件已从公开提交移除并由 `.gitignore` 阻止误传；
- [x] 核对并保留地图、PalCalc、UE4SS 等第三方来源与许可证；
- [x] 检查所有示例配置为空值/占位符，不含真实密码、Token、路径或域名；
- [x] 修正明显过时的运行文档和机器专用路径；
- [x] 从首次提交克隆干净副本，并完成两个前端与 `.NET` Release 构建；
- [x] 运行高置信度秘密扫描；
- [x] 人工审阅首次提交的完整暂存文件清单。

本项目已采用 MIT。若未来更换许可证，必须同步评估既有贡献者授权，并统一根许可证、README、包元数据与 OpenAPI 声明。

## 验证命令

在 `pal-control` 根目录执行版本与构建检查：

```powershell
node --version
npm --version
dotnet --version

npm ci
npm run build:web
npm run build:player
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

## 创建首次 Git 提交

完成所有阻断项后，在 `pal-control` 目录执行：

```powershell
git init -b main
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

确认文件清单和秘密扫描无问题后再提交：

```powershell
git add .
git status --short
git commit -m "chore: prepare initial open-source release"
git remote add origin https://github.com/<owner>/<repository>.git
git push -u origin main
```

不要在命令行中粘贴 GitHub Token。优先使用 Git Credential Manager、GitHub CLI 登录或 SSH key。

## GitHub 推荐设置

首次建议创建 **Private** 仓库完成远端复核，再切换 Public：

1. 启用 Secret scanning、Push protection 和 Dependabot alerts；
2. 启用私密 Security Advisories；
3. 为 `main` 配置合并前检查和禁止强推；
4. Actions 工作流加入仓库前逐行审查其权限，不执行来自不受信 PR 的秘密；
5. 创建 Release 时只上传自行构建且有来源清单的产物，不打包游戏服务端或第三方闭源文件；
6. 在仓库 About 中使用 `palworld`、`dedicated-server`、`react`、`dotnet`、`ue4ss` 等主题，并保留“unofficial”说明。

## 建议的后续仓库文件

首次发布后按优先级补充：

- `SECURITY.md`：漏洞私下报告方式和受支持版本；
- `CONTRIBUTING.md`：开发、测试、契约和安全规则；
- `THIRD_PARTY_NOTICES.md`：已创建；首次公开前继续补齐所有外部代码、数据、图片和许可证；
- `.gitattributes`：统一文本换行和二进制文件识别；
- `.github/workflows/ci.yml`：前端构建、`.NET` 构建、隔离 smoke tests；
- Issue/PR 模板：要求测试证据、风险和回滚说明。

## 最终发布判定

达到以下状态才可以称为“已准备公开”：

- Git 仓库根是 `pal-control/`；
- staged 清单中没有运行时、存档、凭据、日志、数据库、构建产物或第三方二进制；
- 项目许可证与 OpenAPI 完全一致；
- 第三方地图和目录数据的来源、许可和归属可以被验证；
- README 中的命令能在干净 clone 上运行；
- 管理 API 仍只监听 loopback，文档没有暗示可直接公网开放；
- Native 构建限制、目标游戏版本和未完成的生产门槛都被清楚披露。
