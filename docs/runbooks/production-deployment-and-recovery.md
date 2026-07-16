# 单实例生产部署、升级与恢复手册

## 1. 适用范围与未完成边界

本手册只支持当前已经实现的单机拓扑：Windows x64、一个 Control API Windows Service、同进程 hosted workers、一个 Caddy Windows Service 和一个 SQLite 权威库。它不宣称以下能力已经完成：

- Docker/Compose 或容器镜像；
- PostgreSQL、transactional outbox 跨库迁移、多 Control API/worker 实例、lease 接管或 HA；
- 真实公网域名、Steam OpenID/TLS 黑盒验收；
- 空白生产机器的独立人员安装、真实经济库切换恢复、24 小时 soak。

当前 Palworld、PalDefender、RCON、Native Named Pipe、存档与 Windows ACL 都是同机边界；为“看起来现代”而新增未经验证的容器网络会改变安全模型。容量或可用性指标真正要求多实例时，必须另立迁移设计并保持领域接口不变。

## 2. 固定目录与权限

推荐目录不可放在彼此内部，也不能是 junction/symlink：

| 路径 | 内容 | 账户 |
|---|---|---|
| `C:\Program Files\PalControl\releases\<releaseId>` | 不可变发布物、管理台和玩家站点 | 管理员/SYSTEM 完全控制；两个服务只读 |
| `C:\ProgramData\PalControl\config` | 外置生产 JSON | Control API 只读 |
| `C:\ProgramData\PalControl\secrets` | RCON/PalDefender 等文件密钥 | Control API 只读；普通用户无权读取 |
| `C:\ProgramData\PalControl\resources` | 管理员自行提供且授权的资源目录 | Control API 只读；不进入公开包 |
| `C:\ProgramData\PalControl\data` | SQLite、outbox 与兼容 side state | Control API 修改 |
| `backups` / `staging` / `logs` | 保存、经济 staging 与日志 | Control API 修改 |
| `caddy-data` / `caddy-config` / `caddy-logs` | TLS 私钥、OCSP、自动配置与访问日志 | Caddy 修改 |
| `deployment` | 发布状态、冷快照、失败恢复 quarantine | 仅管理员/SYSTEM |

入口使用不同的虚拟服务账户：`NT SERVICE\PalControl.ControlApi` 与 `NT SERVICE\PalControl.Caddy`。workers 当前是 Control API 的 hosted services，没有可以单独扩容的 worker 可执行文件。SCM 配置延迟自动启动，并在失败后等待 5、15、60 秒重启；不能再用桌面“启动”脚本冒充生产守护。

Caddy 官方文档支持在 Windows 用 `sc.exe` 或 WinSW 运行服务；本入口使用系统自带 `sc.exe`，不下载第三方 wrapper。[Caddy Windows service](https://caddyserver.com/docs/running) 说明了这一模式。Caddy 的证书和私钥目录不是缓存；入口显式设置 `XDG_DATA_HOME`/`XDG_CONFIG_HOME` 到持久状态树，符合 [Caddy 文件位置约定](https://caddyserver.com/docs/conventions)。

## 3. 生成和批准固定发布物

只从已经审查、无未提交变更的 commit 构建：

```powershell
git status --short
git rev-parse HEAD
.\deploy\windows\build-release.ps1 -Version 0.2.0 -SkipInstaller
Get-Content .\artifacts\release\SHA256SUMS.txt
```

`build-release.ps1` 会生成：

- self-contained `win-x64` ZIP；
- `版本信息.json`：产品版本、source revision、dirty 状态；
- `release-manifest.json`：releaseId、数据契约、入口程序和除清单自身外每个文件的字节数/SHA-256；
- 外层 `SHA256SUMS.txt`。

部署入口要求操作人员把批准的 ZIP SHA-256 显式传入，并拒绝 dirty commit、篡改/额外文件、路径逃逸、reparse point 及同 releaseId 不同内容。不得把“某个文件名是最新版”当作版本固定证据，也不得从 GitHub 页面直接运行未核对 hash 的二进制。

Caddy 必须单独取得固定版本的官方二进制和批准 SHA-256；入口不会联网下载或执行 `caddy upgrade`。变更版本/模块后先保存来源、版本、模块清单和 hash，再运行 `caddy validate`。Caddy 官方说明 `validate` 会实际加载/provision 模块，强于只转换配置；参见 [Caddy command line](https://caddyserver.com/docs/command-line#caddy-validate)。

## 4. 配置、secret 与持久状态

1. 将 `deploy/windows/appsettings.Production.example.json` 复制到 `C:\ProgramData\PalControl\config\appsettings.Production.json` 并逐项替换占位符。
2. 所有状态、备份、staging、日志和 `Palworld:ResourceCatalogPath` 必须是 `C:\ProgramData\PalControl` 下的绝对路径；Control API 仍只监听 `http://127.0.0.1:5180`。
3. 授权资源目录放到 `resources\palworld-resource-catalog.json`。公开发布物不包含它；缺少目录时保持 `ExtractionMode:Enabled=false`。
4. PalDefender token 与 RCON 密码优先使用 `TokenFile`/`PasswordFile`，放在 `secrets`。管理 API 只保存随机 key 的 SHA-256；TOTP seed 和 Official REST 密码仍在 ACL 保护的外置配置中。不要把明文放进命令行、Git、截图或服务注册表环境。
5. 首次安装和升级时保持 `PlayerNotifications:GameDeliveryEnabled=false`，先完成历史站内 feed 投影；只有核对定向 Native 通知能力与命令审计后才显式改为 `true` 并重启。启用不会补发已经持久为 `not-requested` 的相同历史版本，禁止直接改 SQLite 制造补发。
6. 复制 `deploy/player-portal/Caddyfile` 和 env 样例到 `C:\ProgramData\PalControl\caddy`；域名必须替换 `.invalid`，访问日志必须位于 `caddy-logs`。部署脚本只改 `PLAYER_PORTAL_ROOT` 到目标版本，不改域名或日志路径。

服务只收到两个非 secret 环境值：`DOTNET_ENVIRONMENT=Production` 和 `PAL_CONTROL_CONFIG_PATH=<绝对路径>`。程序在默认 JSON 后加载该外置文件，再由标准环境变量/命令行覆盖；不要把生产机遗留环境变量当作配置管理。每次升级前审计服务注册表的 `Environment`，删除不在批准清单中的覆盖。

## 5. 首次安装

在提升权限的 Windows PowerShell 中先只暂存和校验发布物：

```powershell
$zip = "C:\deploy\幻兽商域-Portable-0.2.0-win-x64.zip"
$zipSha = "<SHA256SUMS.txt 中批准的 64 位值>"
$caddy = "C:\Program Files\Caddy\caddy.exe"
$caddySha = "<批准的 Caddy SHA-256>"

.\deploy\windows\production\Invoke-PalControlDeployment.ps1 `
  -Action Stage `
  -ReleaseArchive $zip `
  -ExpectedSha256 $zipSha
```

确认外置配置和 Caddy 文件已经准备好，再安装并启动：

```powershell
.\deploy\windows\production\Invoke-PalControlDeployment.ps1 `
  -Action Install `
  -ReleaseArchive $zip `
  -ExpectedSha256 $zipSha `
  -CaddyExecutablePath $caddy `
  -CaddyExpectedSha256 $caddySha
```

入口会配置/刷新两个 Service、收紧 ACL、启动 Control API、等待 `/health/ready` 的 `readReady=true`，最后才启动 Caddy。再次传入相同 releaseId/hash 会校验并返回幂等结果，不新建另一份版本或重复迁移。

首次安装后仍必须人工核对：

```powershell
Get-CimInstance Win32_Service -Filter "Name='PalControl.ControlApi' OR Name='PalControl.Caddy'" |
  Select-Object Name, StartName, StartMode, State, PathName
sc.exe qfailure PalControl.ControlApi
sc.exe qfailure PalControl.Caddy
Invoke-RestMethod http://127.0.0.1:5180/health/live
Invoke-RestMethod http://127.0.0.1:5180/health/ready
```

再按玩家门户验收清单检查真实 TLS、Cookie、外部路由和 Steam 回调；本地 readiness 不能替代公网验收。

## 6. 可安全重复的升级与启动迁移

### 6.1 升级前硬条件

先使用现有受保护管理操作进入 maintenance，等待 active operations、blocking orders/runs、delivery/settlement/outbox queue 和全部 uncertain 为 0，并取得配置要求的新鲜经济备份。存在未完成周换档时禁止升级。

部署入口会用 Viewer/Owner API key 读取 `/admin/rollover/readiness` 和全局运营 overview 再检查一次。key 使用交互式 `SecureString` 或 ACL 已收紧的文件，不写命令行明文：

```powershell
$adminKey = Read-Host "Control API key" -AsSecureString
```

### 6.2 执行

```powershell
.\deploy\windows\production\Invoke-PalControlDeployment.ps1 `
  -Action Upgrade `
  -ReleaseArchive "C:\deploy\幻兽商域-Portable-0.2.1-win-x64.zip" `
  -ExpectedSha256 "<批准 ZIP SHA-256>" `
  -AdminApiKey $adminKey `
  -CaddyExecutablePath $caddy `
  -CaddyExpectedSha256 $caddySha
```

唯一顺序：

1. 校验并把完整发布物移动到同卷不可变版本目录；
2. 验证 maintenance/排空/uncertain/备份；
3. 使用目标静态根运行 `caddy validate`；
4. 先停 Caddy，再通过 SCM 停 Control API；
5. 对已停止的 `StateRoot\data` 创建逐文件 SHA-256 冷快照；
6. 把 Service 切到目标 exe，启动时执行组件自身的幂等 SQLite migration；
7. `/health/ready` 成功后启动 Caddy 并原子写入部署状态。

若 6/7 任一步失败，入口保持公网边界关闭，停止目标进程、校验并恢复冷快照、切回旧 exe/静态根，旧版本 read-ready 后才重新启动 Caddy。冷快照和失败数据 quarantine 不自动删除，留给事故复核。

该冷快照只用于“公网未重新开放”的同次升级失败回切，不代替经济一致性快照或 Palworld 世界备份。迁移成功并恢复流量后，不能用冷快照倒回已经发生的新交易。

## 7. 回滚规则

普通二进制回滚默认选择 `previousReleaseId`，也可显式指定：

```powershell
.\deploy\windows\production\Invoke-PalControlDeployment.ps1 `
  -Action Rollback `
  -TargetReleaseId "0.2.0-0123456789ab" `
  -AdminApiKey $adminKey `
  -CaddyExecutablePath $caddy `
  -CaddyExpectedSha256 $caddySha
```

它仍要求 maintenance、完全排空和备份，且只允许相同 SQLite `dataContract`。回滚保留当前数据库，不回放旧快照，因此不会抹掉升级后已提交的账本。目标旧二进制若不 read-ready，会恢复本次停服快照和原二进制。

跨数据契约回滚会 fail closed。此时必须按[经济备份、恢复与持久化周换档手册](economy-continuity-and-weekly-rollover.md)把批准快照恢复到 staging，核对快照之后的交易与未决副作用，由两人批准生产切换；不能强改 manifest 或直接执行 SQL downgrade。

## 8. 密钥轮换

### 管理 API key/TOTP

1. maintenance 并排空；备份配置与经济库。
2. 生成新的随机 key/hash 与 TOTP seed，把“新 principal + 旧 principal”一起写入临时文件并原子替换外置配置。
3. 重启 Control API；用新 key/TOTP 验证 Viewer 和一项受控高风险 dry-run/预检，同时确认旧身份仍可应急。
4. 删除旧 principal，再次原子替换和重启；验证旧 key 返回 401、新 key 正常，保留脱敏审计。

不要原地覆盖唯一凭据后才测试。`new-admin-credential.ps1` 的输出只能进入密码管理器和 ACL 私有配置，不进入日志。

### PalDefender/RCON/Official REST

- 先在上游创建新凭据，原子替换 `secrets` 中的 token/password file，重启并完成 capability probe，再撤销旧值。
- Official REST 仍使用 ACL 私有外置配置；采用相同“双值窗口 -> 新值验证 -> 撤旧”流程。
- 任何版本或权限漂移必须让 Economy Safety Gate 关闭对应写路径；禁止为了恢复绿色而放宽批准版本或伪造 capability。

## 9. 故障处置

### 磁盘接近满或已满

1. 立即 maintenance，停止新购买/兑换；不要删除 `.db-wal`、`.db-shm`、JSONL、manifest 或未决 outbox。
2. 记录 `Get-Volume`、目录大小、最新已验证备份与队列状态。日志可按已批准保留策略转移到另一受控卷；经济备份只使用 continuity API 的 plan-only 清理候选，不手工挑文件删。
3. 扩容或转移完整备份 bundle 后重新校验 hash/free space/readiness；门禁恢复前先调查写入失败和 `uncertain`。

### SQLite 损坏或启动 migration 失败

1. 保持 Caddy 和 Control API 停止，复制数据库、WAL/SHM、日志与部署状态到只读事故目录；禁止在原件上执行 repair/vacuum。
2. 对最近批准经济快照执行 staging integrity、foreign key、schema、ledger projection、worldId 和 post-snapshot reconciliation；逐账户/逐行基线命令见 [SQLite 经济迁移前后核对手册](economy-migration-reconciliation.md)。
3. 只有两人复核后才能切换；原数据保留。恢复后默认 maintenance，先核对未决订单/run/outbox，再决定开门。

### 疑似重复发货

1. 只关闭 purchase circuit/maintenance，不要生成新幂等键或重发。
2. 按 deliveryId/request hash/resultId 核对订单、PalDefender command、structured receipt、inventory evidence、ledger 与审计。
3. `dispatched/uncertain/partial` 进入人工对账；只有确定失败且没有副作用证据时才允许受控退款/重试。

### 版本漂移

保持相应写闸门关闭，核对发布 manifest、游戏/PalDefender/Native 版本、Caddy hash 和服务 `PathName`。重新部署批准版本或发布新批准组合；不能直接修改数据库中的证据字段。

## 10. 可重复检查与仍需外部证据

仓库内自动检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\tests\integration\windows-production-deployment-smoke.ps1
```

它覆盖错误 archive hash、清单根目录外夹带文件、重复 stage/install 与 Caddy 静态根漂移修复、虚拟服务账户与外置配置注入、Caddy 持久目录、升级 health 失败后的数据/服务/静态根恢复、损坏冷快照拒绝且旧服务恢复、同契约回滚不倒退交易、跨契约回滚拒绝，并真实连续启动两次 Control API 验证 SQLite integrity/foreign keys 与迁移指纹不重复。

以下证据仍必须保持未完成，取得后才能勾选 P1-07 整体：

- 在全新 Windows VM 由非实现者按本手册完成两次 install/upgrade/rollback；
- 用脱敏真实经济备份和世界备份完成独立 staging/生产切换演练；
- 在真实域名完成 Caddy TLS/Steam/防火墙外部黑盒；
- 24 小时 soak 证明内存、句柄、日志、SQLite WAL、队列和会话没有持续增长；
- 若未来启用多实例，完成 PostgreSQL 数据核对、transactional outbox、lease 接管和节点断连并发测试。

仓库已提供 [`tools/soak`](../../tools/soak/README.md) 采样和 fail-closed
分析器，用于生成 canonical JSON 与 SHA-256 证据。短时 CI 只证明工具自身；本项仍须
在真实生产候选环境按该手册连续运行 24 小时并独立审阅报告后才能勾选。
