# 经济备份、恢复与持久化周换档手册

适用玩法：方案 A——本周世界中的白名单资源均可出售，定位为“周世界资源经济服”。本手册只覆盖经济连续性和周世界切换，不替代 Palworld 存档备份手册。

## 安全边界

- 经济权威库是 `ExtractionMode:Persistence:DataDirectory/extraction-commerce.db`。SQLite 使用 WAL；在线备份 API 会把已提交 WAL frame 合并进备份数据库，清单同时记录 checkpoint frame 数，不复制一个可能不一致的裸 `-wal` 文件。
- 账户、钱包、账本、商城订单、资源结算 run、delivery evidence/receipt、PalDefender command outbox、身份/管理审计、经济 gate、周换档状态和赛季 job 位于同一个 SQLite 文件。快照会从 `paldefender_commands` 读取 `accepted/dispatched/uncertain/dead_lettered` 阻断项；公告/通知/存档等非经济 JSONL 仍作为兼容 side state 冻结复制和校验，并生成独立的通道归档清单。
- 恢复永远先进入 staging。工具不会自动覆盖生产数据，也不会自动删除旧世界。
- staging 中的经济写入默认关闭；只有 worldId、账本、待决交易和所有依赖重新验证后，值班人员才能人工开放。
- 受控脚本只支持 `PreviousWorldPolicy=Keep`，并明确拒绝旧的 `Archive`、`Delete`、`AllowDeletePreviousWorld` 和 `ArchiveRoot` 参数；代码中没有移动或删除旧世界的执行路径。任何后续人工清理都必须有单独业务 ADR 和人工审批。

## 恢复目标与保留策略

默认配置：

| 项目 | 默认值 | 含义 |
|---|---:|---|
| RPO | 15 分钟 | 事故后最多允许人工核对最近 15 分钟交易；周换档仍必须另做即时一致性快照 |
| 目标 RTO | 60 分钟 | 从决定恢复到 staging 验证完成并作出切换/不切换决定 |
| 保留期 | 60 天 | 经济快照低于此年龄不进入清理候选 |
| 最少快照 | 8 份 | 即使超过保留期，也保留最新 8 份 |
| 容量安全系数 | 150% | 按当前/历史单份大小估算至少 8 份所需空间 |
| 自动删除 | 禁止 | API 只生成候选清单，不执行删除；旧世界也不自动删除 |

如果业务要求“无条件至少 8 周”，必须另立 ADR，明确每周快照频率、磁盘预算和恢复成本；不能把“8 份”偷换成“8 周”。

### 非经济 JSONL 的统一归档边界

活动目录中的以下通道在迁移到其他权威存储前都执行同一策略：

| 文件 | 通道 | 活动文件保留 |
|---|---|---|
| `announcement-events.jsonl` | 公告草稿与状态 | 追加式权威；不截断、不轮转、不自动删除 |
| `command-audit.jsonl` | 公告逐渠道派发 | 追加式权威；不截断、不轮转、不自动删除 |
| `in-game-notification-events.jsonl` | 游戏内通知状态 | 追加式权威；不截断、不轮转、不自动删除 |
| `in-game-notification-command-audit.jsonl` | 游戏内通知派发 | 追加式权威；不截断、不轮转、不自动删除 |
| `save-command-audit.jsonl` | 保存/受管备份命令 | 追加式权威；不截断、不轮转、不自动删除 |
| `paldefender-command-audit.jsonl` | 旧 PalDefender 导入源 | 只作迁移证据；当前权威 outbox 已在 SQLite |

一致性快照会在 `command-state/command-side-state-archive.json`（命令目录与经济目录完全相同时位于 `economy-state/`）记录完整通道注册表、存在/缺失状态、用途、权威级别、字节数、SHA-256、拍摄时间和当时继承的保留期/最少份数。清单自身也由外层经济 manifest 记录 SHA-256。出现未登记 `.jsonl`、嵌套 JSONL、重解析点、空行、损坏 JSON 或不完整末行时，快照失败并清理 partial 目录；新增通道必须先更新注册表、恢复语义、测试和本手册，不能静默归档。

JSONL 归档不单独轮转或删除，而是与 SQLite、门禁和调度状态组成不可拆分的经济快照。`PlanRetention` 只在超过保留期且高于最少份数时列出整个 bundle 候选，不执行删除；不得只删除 `command-state` 节省空间。活动文件增长计入容量计划。需要缩减活动日志时，必须先把该通道迁移到具备等价幂等/重放约束的存储，完成一次真实恢复并另行提交迁移与回滚方案，不能原地压缩现有权威日志。

## 管理请求头

写操作需要 `SeasonAdmin`（或更高角色）、TOTP 和审计原因。不要把 API Key、TOTP 写进脚本或仓库。

```powershell
$base = "http://127.0.0.1:5180/api/v1"
$headers = @{
  "X-Pal-Admin-Key"    = $env:PAL_CONTROL_ADMIN_KEY
  "X-Pal-Admin-Totp"   = Read-Host "TOTP"
  "X-Pal-Admin-Reason" = "weekly rollover 2026-W29"
}
```

TOTP 过期后重新读取，不要重放旧码。

## 受控脚本：先计划，再显式执行

入口为 `extraction-mode/scripts/Invoke-WeeklyRollover.ps1`。不带 `-Execute`（包括只带 `-WhatIf`）时只读取服务端状态并输出计划，不创建 operation、不关维护、不写配置，也不创建本地 journal。

```powershell
$adminKey = Read-Host "Control API administrator key" -AsSecureString
$plan = .\extraction-mode\scripts\Invoke-WeeklyRollover.ps1 `
  -ControlApiUrl "http://127.0.0.1:5180" `
  -AdminApiKey $adminKey
$plan | Format-List
```

新 operation 必须把审核过的计划值原样带回；脚本不会在执行时悄悄生成另一个世界 ID：

```powershell
$operationId = [Guid](Read-Host "Server active operationId")
.\extraction-mode\scripts\Invoke-WeeklyRollover.ps1 `
  -ControlApiUrl "http://127.0.0.1:5180" `
  -AdminApiKey $adminKey `
  -Execute `
  -InstallRoot "D:\PalServerRuntime" `
  -TargetWorldId $plan.proposedWorldId `
  -RulesVersion "2026-W29-v1"
```

每个高风险请求默认重新安全读取 6 位 TOTP，以免长时间备份期间复用已经过期的验证码。自动值守只能通过 `-AdminTotpProvider` 接入返回 `SecureString` 的本机凭据代理；不得在命令行、脚本、环境变量或日志中放明文 TOTP。

管理员 API Key 有两种输入方式：

- `-AdminApiKey (Read-Host -AsSecureString)`；
- `-AdminApiKeyFile <path>`，文件只包含 key，脚本会拒绝 reparse point 以及向 Everyone、Authenticated Users 或 Users 开放读取的 ACL。

Palworld Official REST 凭据使用 `Get-Credential` 安全输入，或使用当前 Windows 用户 DPAPI 保护的 CLIXML：

```powershell
$credentialPath = "$env:ProgramData\PalControl\secrets\palworld-rest.credential.xml"
Get-Credential -UserName admin | Export-Clixml -LiteralPath $credentialPath
# 随后把该文件 ACL 收紧为仅值班账户、SYSTEM 和 Administrators 可读。
```

执行时可传 `-OfficialRestCredentialFile $credentialPath`。脚本不会输出请求头、API Key、TOTP 或 Basic Authorization；本地 journal 也只保存 operation、step key、命令 ID、备份 ID 和非敏感证据。

### 中断和网络超时后的恢复

默认 journal 位于 `%ProgramData%\PalControl\rollover-client\<serverId>.json`，并使用每服务器独占锁和原子替换。它不是权威状态；每次启动都先读取服务端 active operation，再验证 journal 的 `operationId`、冻结 world/rules 和 `requiredStepKey`。

```powershell
.\extraction-mode\scripts\Invoke-WeeklyRollover.ps1 `
  -ControlApiUrl "http://127.0.0.1:5180" `
  -AdminApiKey $adminKey `
  -Execute `
  -InstallRoot "D:\PalServerRuntime" `
  -OperationId $operationId
```

`InstallRoot` 没有开源模板默认值，生产执行必须显式传入实际 PalServer 根目录；plan-only 和使用测试 action adapter 时不读取该路径。

`ExternalActionAdapter`、`FaultAfterActionStep` 和 `FaultAfterSubmitStep` 只用于仓库内隔离测试；脚本还要求同时提供 `-EnableTestHooks` 与专用测试环境标记。生产 runbook、计划任务和人工操作不得启用这些参数。

恢复规则：

- step 响应丢失：先 GET operation；只有服务端 `completedSteps` 中存在完全相同的 step key/evidence hash 才继续，不盲重发；
- save command 已受理：journal 保存 `commandId`，重启后只轮询原命令；没有取得 commandId 时先确认 operation 仍停在原阶段，下次运行才以相同服务端 key 幂等重放；
- economy snapshot/staging 响应丢失：固定复用同一 `backupId`/step key；staging 同备份重放会重新校验已经发布的 staging，而不会再建第二份；
- commit 响应丢失：读取当前活动赛季和真实 world；只有目标赛季已经成为唯一 active 才记录成功，否则保持维护并停止，绝不自动切回旧世界；
- Reopen 在“状态已提交、维护门尚未打开”之间中断：以已持久化的 Reopen step key/evidence 重放，只完成开门尾动作。

每个阶段开始前还会重新检查未决订单、settlement/command queue、RPO、规则版本和真实 world。任一不一致立即停止并保留当前维护状态。

## 一致性经济快照

1. 先通过现有维护接口关闭经济写入并等待 `activeOperations=0`。
2. 查看容量计划：

```powershell
Invoke-RestMethod -Headers $headers `
  -Uri "$base/admin/economy-continuity/capacity"
```

3. 创建快照。周换档中必须把当前 `economy_backup` 的 `requiredStepKey` 作为 `idempotencyKey`；重启重放会返回同一备份，不会再创建一份。

```powershell
$snapshot = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Uri "$base/admin/economy-continuity/snapshots" `
  -Body (@{
    serverId = "local"
    worldId = $currentWorldId
    idempotencyKey = $rollover.requiredStepKey
  } | ConvertTo-Json)
```

4. 立即校验：

```powershell
Invoke-RestMethod -Headers $headers `
  -Uri "$base/admin/economy-continuity/snapshots/local/$($snapshot.backupId)/verify"
```

清单固定包含文件大小、SHA-256、SQLite `user_version`、最后事件序号、WAL frame 证据、RPO/RTO、非经济 JSONL 通道归档清单和拍摄时的待决交易清单。

## staging 恢复与是否切换

```powershell
$restored = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Uri "$base/admin/economy-continuity/snapshots/local/$($snapshot.backupId)/stage" `
  -Body (@{ expectedWorldId = $currentWorldId } | ConvertTo-Json)
```

必须同时满足：

- `hashesValid=true`
- `sqliteIntegrityValid=true`
- `economyReplayValid=true`
- `worldIdValid=true` 且 `activeSeasonWorldValid=true`
- `ledgerProjectionValid=true`
- `economyForcedClosed=true`
- `blockingOrderCount=0`，或每笔都有已批准的人工处置单

列出快照之后发生、恢复时需要人工核对的事件和当前待决交易：

```powershell
$reconcile = Invoke-RestMethod -Headers $headers `
  -Uri "$base/admin/economy-continuity/snapshots/local/$($snapshot.backupId)/post-snapshot"
$reconcile.items | Format-Table kind,id,state,updatedAt
```

人工复核至少包含：商城 `pending/dispatching/failed/uncertain` 订单、未终结资源结算、未完成周换档、未完成赛季 job，以及 `lastEconomySequence` 之后的每个经济事件。任何一项无法解释时，不得切换生产目录，也不得开放经济。

当前服务只完成 staging，不提供“自动替换生产经济库”接口。生产切换必须在停服窗口内由两人复核，保留原目录只读副本，切换后再次执行相同验证；失败时回到原目录，仍保持经济关闭。

## 持久化周换档状态机

唯一顺序为：

```text
preflight -> drain -> game_backup -> economy_backup -> stop
          -> new_world -> probe -> commit -> reopen -> completed
```

创建操作：

```powershell
$rollover = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Uri "$base/admin/weekly-rollover/operations" `
  -Body (@{
    serverId = "local"
    fromSeasonId = $currentSeasonId
    fromWorldId = $currentWorldId
    targetWorldId = $targetWorldId
    rulesVersion = "2026-W29-v1"
  } | ConvertTo-Json)
```

进程重启后不要创建新目标世界，先恢复未完成操作：

```powershell
$rollover = Invoke-RestMethod -Headers $headers `
  -Uri "$base/admin/weekly-rollover/operations/active?serverId=local"
```

每一步的 `requiredStepKey` 是该外部动作的幂等键。创建受管游戏备份时作为 `Idempotency-Key`；创建经济快照时作为 `idempotencyKey`；其余动作只允许设置同一个目标 worldId，且启动脚本必须保持单进程锁。动作成功并取得 SHA-256 证据后才提交步骤：

```powershell
$body = @{
  stepKey = $rollover.requiredStepKey
  evidence = @{
    verified = $true
    evidenceType = "managed-backup"
    evidenceReference = $backupId
    evidenceHash = $manifestSha256
    observedWorldId = $null
    blockingTransactions = 0
    allGatesPassed = $false
    blockerCodes = @()
  }
} | ConvertTo-Json -Depth 6

$rollover = (Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Uri "$base/admin/weekly-rollover/operations/$($rollover.operation.operationId)/steps/GameBackup" `
  -Body $body).operation
```

服务器会拒绝跳步、错误 step key、冲突证据、错误目标 worldId 和重复但内容不同的提交。相同证据重放返回 `idempotentReplay=true`。

生产操作应通过上述受控脚本完成；下面的逐 API 示例仅用于故障诊断和人工复核，不能绕过脚本的备份、staging、journal 与状态恢复规则。

### 各阶段硬门禁

| 阶段 | 完成前必须验证 |
|---|---|
| preflight | 活动赛季/当前 world 一致；无未终结 run/订单；购买与资源兑换依赖、版本、磁盘容量通过 |
| drain | 维护模式已开启且 `activeOperations=0` |
| game_backup | 受管备份 SHA-256 已验证；worldId 正确；创建时间不早于本次换档 |
| economy_backup | 使用 step key 的一致性快照；worldId/hash 正确；除本换档自身外没有待决交易 |
| stop | Palworld 优雅保存并停服；记录可复核的停止证据 |
| new_world | 只写入本操作冻结的 `targetWorldId`；生产禁止删除旧世界 |
| probe | 8211、PalDefender、RCON 与实际存档 worldId 全部指向目标世界 |
| commit | 旧赛季战备券到期 job 已按相同 `rulesVersion` 完成；再提交新赛季 |
| reopen | 维护门禁关闭，商城和资源兑换两个 circuit 均开启，所有探针重新通过 |

一旦 `commit` 成功，状态机会持久化 `newSeasonCommitted=true`。此后工具没有自动回滚到旧世界的路径；新赛季产生任何交易后尤其禁止自动回滚。

## 战备券到期和周奖励

维护模式且无活动操作时冻结到期 job：

```powershell
$expiry = Invoke-RestMethod -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Uri "$base/admin/season-settlement-jobs/voucher-expiry" `
  -Body (@{ seasonId = $currentSeasonId; rulesVersion = "2026-W29-v1" } | ConvertTo-Json)

Invoke-RestMethod -Method Post -Headers $headers `
  -Uri "$base/admin/season-settlement-jobs/$($expiry.jobId)/run"
```

job 在准备时冻结每个账户的金额。每个账户使用固定 idempotency key 写唯一 ledger；即使进程在“ledger 已写、item 未标记”之间崩溃，重启后也只重放并确认原账，不会再次扣除。同一赛季只能冻结一个战备券到期 job；规则版本漂移会阻断换档。

周奖励使用 `/admin/season-settlement-jobs/rewards`，请求包含 `sourceSeasonId`、`rulesVersion`、`rewardBatchKey` 和 grants。相同 job key 内容不同会冲突；相同账户/奖励 key 的 ledger 最多一笔。当前玩法没有配置周奖励时，不应创建空想奖励 job。

## 自动化证据与真实演练

本地自动化：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File tests/integration/continuity-rollover-smoke.ps1
```

该命令同时运行 .NET continuity harness 和 PowerShell fake API 客户端隔离测试。覆盖：每个换档阶段前后重启、逐阶段 action 后崩溃恢复、逐阶段服务端已提交但 HTTP 响应丢失、确定性 game backup/economy snapshot/staging 重放、跳步/冲突证据、错误 world、RPO、未决交易、规则版本漂移、expiry/reward 20 次重放、ledger 崩溃窗口、在线快照、注册 JSONL 原字节归档、未知/半行/篡改 fail-closed、bundle 级 plan-only 保留、staging 恢复、凭据不进入 journal，以及恢复后经济关闭。

它不能替代以下外部验收，未取得真实证据前 TODO 必须保持未完成：

1. 独立恢复一次真实 Palworld 世界备份，并验证玩家/公会/容器数据。
2. 在固定 Palworld/PalDefender/UE4SS 版本组合上连续完成 3 次“旧世界 → 验证备份 → 新世界 → 新赛季”演练。
3. 每次分别注入进程崩溃/机器重启，保存 operationId、step key、游戏备份 ID、经济 backupId、哈希、RPO/RTO、探针和人工复核签名。
4. 新赛季产生真实交易后验证系统拒绝自动切回旧世界。

## 故障处置

- `operations/active` 有记录：从 `currentStep` 恢复，禁止新建目标 worldId。
- 游戏或经济备份哈希不符：保持维护，隔离该备份，不纳入自动保留清理候选。
- `uncertain` 订单或未终结资源结算：先按订单/资源结算手册人工对账，禁止越过 preflight/backup/commit。
- `rulesVersion` 漂移：停止换档，由内容发布者确认旧冻结版本或创建新的完整业务决策；不得修改既有 job 内容。
- commit 请求结果未知：读取活动赛季和实际 worldId；任何证据表明新赛季已提交，就禁止自动恢复旧世界。
- staging 验证失败：不触碰生产目录；记录失败备份 ID 和哈希，改用更早的已验证恢复点并扩大人工核对窗口。
