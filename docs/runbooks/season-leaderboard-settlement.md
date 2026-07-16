# 周资源排行榜与奖励结算手册

本手册适用于“周世界资源经济服”：排行榜只读取已经完成的资源兑换结算和可靠任务积分，不读取行动次数、撤离成功率、死亡率，也不会用客户端上报值结算奖励。

## 固定规则

当前规则版本为 `weekly-resource-ranking-v1`：

- 截止时间：`周档 EndsAt + 15 分钟`。时间等于截止点的数据计入，晚于截止点的数据只进入迟到证据列表，不参与排名。
- 资源榜最低贡献：至少 1 笔已结算兑换，且有效兑换总价值不少于 100。
- 任务榜最低贡献：可靠任务积分不少于 10。
- 资源榜顺序：总价值降序、有效资源数量降序、首次结算时间升序、账户 UUID 升序。
- 任务榜顺序：积分降序、首次积分入账时间升序、账户 UUID 升序。
- 冻结时已封禁或被人工排除的账户保留来源证据和个人汇总，但不进入任何名次。
- 冻结后、奖励准备前新增的封禁或人工排除不会改写历史名次，只把对应奖励决策记录为 `cancelled`。

标准奖励是永久 `MarketCoin`：资源榜前三名分别为 500、300、150，任务榜前三名分别为 300、200、100。同一玩家可以同时获得两个榜的奖励。

## 数据与审计证据

冻结快照和奖励记录都保存在 `extraction-commerce.db`：

- `season_leaderboard_snapshots`：不可变快照、完整来源证据、规则/source/snapshot 三类 SHA-256、奖励作业绑定和奖励决策。
- `season_leaderboard_exclusions`：赛季与账户维度的人工排除状态、原因和操作者。
- `season_leaderboard_audit`：冻结、排除/恢复、奖励准备/完成和人工补发的有序审计事件。
- `season_settlement_jobs` / `season_settlement_job_items`：确定性的奖励作业与奖励项状态。
- 钱包 ledger：最终的唯一资金凭证。排行榜表本身不直接修改余额。

管理员查询返回 `snapshot`、`evidence` 和 `audit`。`evidence` 保存截止时纳入的每笔 settlement/任务积分，以及当时已观察到的迟到记录 ID；因此 ItemID、类别、账户和全局数值都能离线复算。

## 结算前检查

1. 确认周档已经关闭，并已超过 `EndsAt + 15 分钟`。
2. 完成未决兑换、异常订单和任务奖励恢复；不得用排行榜冻结掩盖未终结交易。
3. 对确认作弊的账户先设置人工排除；需要保留原因和工单/案件编号。
4. 通过既有周换档流程进入维护模式，并确认 `activeOperations = 0`。
5. 高风险请求使用 `SeasonAdmin` 或 `Owner` 身份，并提供当前 TOTP 与 8–512 字符审计原因。API Key、TOTP 不得写入仓库、命令行历史或截图。

下面示例只从环境变量读取 API Key；TOTP 每次交互读取：

```powershell
$base = "http://127.0.0.1:5173/api/v1"
$headers = @{
  "X-Pal-Admin-Key"    = $env:PAL_CONTROL_ADMIN_KEY
  "X-Pal-Admin-Totp"   = Read-Host "Current TOTP"
  "X-Pal-Admin-Reason" = "weekly leaderboard settlement 2026-W29"
  "X-Correlation-ID"   = [guid]::NewGuid().ToString("D")
}
$seasonId = "00000000-0000-0000-0000-000000000000"
```

## 标准操作顺序

### 1. 设置或撤销排除

冻结前设置的排除会取消名次；冻结后、奖励准备前设置的排除只取消奖励。奖励作业一旦准备，排除即不可再修改。

```powershell
$accountId = "00000000-0000-0000-0000-000000000000"
$body = @{
  excluded = $true
  reason   = "anti-cheat case AC-2026-0042 confirmed"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Put `
  -Uri "$base/admin/season-leaderboards/$seasonId/exclusions/$accountId" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```

恢复账户时把 `excluded` 改为 `false`，并写明复核原因。相同状态和相同原因的请求是无副作用重放。

### 2. 冻结排行榜

```powershell
$frozen = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/admin/season-leaderboards/$seasonId/freeze" `
  -Headers $headers
```

相同赛季重复冻结会返回同一个 `snapshotId`、`sourceHash` 和 `snapshotHash`。冻结完成后新出现的迟到数据不得改变快照。核对：

- `cutoffAt` 与周档结束时间相差 15 分钟；
- `lateSettlementCountAtFreeze`、`lateTaskPointCountAtFreeze` 是否符合预期；
- 被排除账户的 `rankingExclusionCode`；
- `globalItems`、`globalCategories` 和个人 `items/categories` 汇总；
- 资源榜和任务榜的同分次序。

### 3. 准备标准奖励

准备阶段会再次读取当前封禁和人工排除状态，并把每个获奖名次固化为 `granted` 或 `cancelled` 决策。此阶段仍不修改钱包。

```powershell
$headers["X-Pal-Admin-Totp"] = Read-Host "Current TOTP"
$job = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/admin/season-leaderboards/$seasonId/rewards/prepare" `
  -Headers $headers
```

检查 `job.items` 数量与金额，并重新查询快照确认 `rewardDecisions`。如果响应丢失，重复调用同一路径，不要另建通用奖励批次。

### 4. 执行标准奖励

```powershell
$headers["X-Pal-Admin-Totp"] = Read-Host "Current TOTP"
$completed = Invoke-RestMethod `
  -Method Post `
  -Uri "$base/admin/season-leaderboards/$seasonId/rewards/run" `
  -Headers $headers
```

只有 `state = Completed` 且快照 `rewardState = completed` 才算完成。作业可在进程重启或响应丢失后重复执行；每个账户、每个奖励项由确定性 reference 和 idempotency key 保证最多一笔 ledger。

### 5. 查询完整证据

```powershell
$readHeaders = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_ADMIN_KEY }
$record = Invoke-RestMethod `
  -Method Get `
  -Uri "$base/admin/season-leaderboards/$seasonId" `
  -Headers $readHeaders
```

只读查询需要 `SeasonAdmin`（或更高角色）。重点保存 `snapshotHash`、标准 `rewardJobId`、被取消的奖励决策和 audit sequence，外部工单不要复制原始凭据。

## 人工补发

人工补发只用于已经出现在冻结快照中的账户。`manualKey` 必须是稳定案件键；同一键重放 20 次仍只会生成一个作业和一笔 ledger。不得为重试更换键。

```powershell
$headers["X-Pal-Admin-Totp"] = Read-Host "Current TOTP"
$body = @{
  accountId = $accountId
  amount     = 77
  manualKey = "appeal-case-2026-07-16"
  reason    = "appeal reviewed with external evidence"
} | ConvertTo-Json

Invoke-RestMethod `
  -Method Post `
  -Uri "$base/admin/season-leaderboards/$seasonId/rewards/manual" `
  -Headers $headers `
  -ContentType "application/json" `
  -Body $body
```

相同 `manualKey` 如果改了账户或金额会被当作幂等冲突拒绝。人工补发不会改写标准名次或标准奖励决策，只新增独立的作业、ledger 和 `reward.manual-supplement` 审计事件。

## 玩家查询

登录后的玩家默认查询当前服务器最近一份已冻结周榜，不需要知道赛季 UUID：

```text
GET /api/v1/player/me/season-leaderboards/latest
```

即使当前活动周已经切换，该端点仍按冻结截止时间返回最近结果；从未冻结过周榜时返回 HTTP 200、`status=not-frozen` 和 `settlement=null`。需要核对指定旧周时保留按 `seasonId` 查询：

```text
GET /api/v1/player/me/season-leaderboards/{seasonId}
```

两个接口都只从 HttpOnly Cookie 确定账户，显式拒绝 `accountId/userId/steamId/playerUid` 查询覆盖。返回内容只含本人的冻结资源/任务成绩与名次、参与/排除原因、规则/截止/冻结时间、周券过期 job/ledger 状态，以及标准奖励、取消决定和人工补发的实际 ledger 完成状态；不会返回其他玩家身份、账户 UUID 或完整排行榜。指定的冻结周不存在时返回稳定的 HTTP 404 `SEASON_LEADERBOARD_NOT_FOUND`。

## 故障恢复

- 冻结响应丢失：重新调用 `/freeze`，比较三个 ID/hash，不要删除数据库行重做。
- 准备响应丢失：重新调用 `/rewards/prepare`；确定性 job 会返回原载荷。
- 执行中断：重新调用 `/rewards/run`；已落 ledger、尚未更新 job item 的崩溃窗口会通过幂等 reference 收敛。
- 快照 hash 校验失败：停止奖励，保留数据库副本和相关 `X-Correlation-ID`，从可信备份恢复；不得手工编辑 snapshot/evidence JSON。
- 奖励准备后发现作弊：标准奖励决策已冻结，不能修改 exclusion。若尚未执行，保持维护并升级人工事件处理；若已经执行，使用独立、经审计的钱包纠正流程，不得删除 ledger。

本地验证命令：

```powershell
.\tests\integration\season-leaderboard-smoke.ps1
```

该测试使用真实 SQLite 存储验证冻结、标准奖励、人工补发各 20 次重放、截止后数据排除、封禁/人工排除、同分规则、唯一 ledger、审计 hash 和重启恢复；同时验证 latest 在无快照时的 200/not-frozen、指定周 404、身份覆盖拒绝、当前周切换后仍读取最近冻结周、A/B 玩家投影隔离，以及周券/永久奖励在重启后的 job + ledger 对账结果。
