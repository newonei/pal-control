# 周世界团队经济运行手册

本手册对应方案 A 的同服、同一周世界小队协作。它不是 PalDefender 公会同步，也不实现跨服组队、跨服转币、玩家交易、拍卖或客户端上报的采集/击杀统计。团队系统只重放已有的服务端权威经济事实，本身不调用扣物、钱包、账本、商城发货或任务奖励写入。

## 1. 能力与安全边界

每个账户在一个 `serverId + seasonId` 中最多有一段有效小队成员关系。创建、加入、离队、转让队长和解散都从 HttpOnly 玩家会话派生账户、服务器与周世界；请求体、查询参数或请求头不能覆盖 `accountId`、UserId、PlayerUID、SteamID、`serverId`、`seasonId` 或 `teamId`。

团队投影仅接受以下权威终态：

- `extraction_settlement_runs` 中结构、哈希、逐行和总额均验证通过的 `Settled` 资源结算；
- `extraction_events` 中同一订单的 `ShopOrder + ShopDelivery` 均为 `Delivered` 后的实际消费与成功送达；退款、失败、部分、处理中和 `uncertain` 不计入；
- `reliable_task_ranking_rewards` 中已经持久化的正任务积分；
- `player_identity_bans` 中当前有效的真实身份封禁，以及 `season_leaderboard_exclusions` 中的赛季人工排除，会合并为同一资格集合，并从成员门槛、当前贡献与排行榜中剔除。

采集、击杀、死亡、PvP、移动和客户端埋点不属于团队事实。加入之前、离队之后以及更换队伍后的事实不会倒灌或转移。事实发生时间只会匹配当时有效的一段成员关系。

## 2. 默认关闭与生产配置

仓库默认值和生产示例都保持：

```json
{
  "TeamEconomy": {
    "Enabled": false,
    "InvitePepper": ""
  }
}
```

先完成 Player Portal、ExtractionMode、SQLite 备份/恢复和周世界绑定验收，再启用团队系统。邀请 pepper 必须是独立的 32–512 字符高熵秘密，不能与管理员 API Key、Cookie 密钥、RCON 或 PalDefender Token 复用，也不能提交到 Git、写进截图或打印到日志。

PowerShell 可生成一个候选值；只把输出写入受保护的服务环境或外部密钥配置：

```powershell
$bytes = New-Object byte[] 48
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$pepper = [Convert]::ToBase64String($bytes)
# 示例仅设置当前进程；生产服务应使用受 ACL 保护的外部环境配置。
$env:TeamEconomy__InvitePepper = $pepper
$env:TeamEconomy__Enabled = "true"
```

启用时，空值、包含 `replace` 的占位值、控制字符或长度不合规会令 Control API 启动失败。不要把真实 pepper 回填到 `appsettings*.json`。

其他配置：

| 键 | 默认值 | 含义 |
| --- | ---: | --- |
| `ProjectionIntervalSeconds` | 15 | 权威事实重放周期；合法范围 5–3600 秒 |
| `InviteLifetimeMinutes` | 1440 | 邀请有效期；合法范围 5–43200 分钟 |
| `InviteMaximumUses` | 10 | 默认邀请使用上限；合法范围 1–100 |
| `MinimumLeaderboardMembers` | 1 | 排行榜最低未排除有效成员数 |
| `MinimumResourceValue` | 1 | 资源价值榜最低贡献 |
| `MinimumTaskPoints` | 1 | 任务积分榜最低贡献 |
| `MinimumDeliveredOrders` | 1 | 成功送达榜最低贡献 |
| `ResourceItemsGoal` | 500 | 冻结到新队伍的资源件数目标 |
| `ResourceValueGoal` | 2500 | 冻结到新队伍的资源价值目标 |
| `ReliableTaskPointsGoal` | 100 | 冻结到新队伍的可靠任务积分目标 |
| `DeliveredOrdersGoal` | 10 | 冻结到新队伍的成功送达目标 |
| `GoalTemplateVersion` | `team-goals-v1` | 目标模板版本；改值只应用于之后创建的队伍 |

目标和门槛必须是 JavaScript 安全整数范围内的正整数。不要在周中修改既有队伍的目标快照；运营改版应升级 `GoalTemplateVersion`，并在下一周世界生效。

## 3. 邀请、成员与队长操作

所有写接口都要求允许的 `Origin`、当前会话 CSRF Token 和 8–128 字符 `Idempotency-Key`。同一账户、操作与键的精确重试返回原结果；同键不同请求返回 `TEAM_IDEMPOTENCY_CONFLICT`。

队长轮换邀请时，旧邀请立即撤销。明文 token 只在首次成功响应显示，SQLite 只保存带独立 pepper 的 HMAC-SHA-256 摘要；同一幂等键在响应丢失后重放时返回 `token: null`，不得从数据库、WAL、日志或客服后台找回。一个 token 可按配置的次数上限供同服、本周玩家加入，但每次加入都只能成功占用一次有效成员关系。怀疑泄露时直接轮换，不要记录旧 token。

成员列表不会返回账号、平台身份、PlayerUID 或显示名。队长转让只使用本队临时 HMAC 成员句柄和“成员 XXXX”标签。队长不能直接离队，必须先转让或输入完整队名确认解散。解散队伍从公开排行榜隐藏，历史成员与投影证据仍保留审计。

## 4. 目标、贡献与排行榜

每支新队伍冻结四项目标：出售白名单资源件数、累计资源价值、可靠任务积分、成功送达订单。达标只写单调里程碑和首次达到时间，**不会自动发放货币、物品或任务奖励**。若账户在贡献后被真实封禁或人工排除，它会立即从当前团队累计、合格成员数和三榜剔除，但已经达到的无奖励目标里程碑不会回退；审计解封/取消排除后，历史权威事实会确定恢复，重启前后结果一致。

玩家门户只显示团队汇总、当前登录者本人的贡献、四项目标和三张团队榜：资源价值、任务积分、成功送达。公开榜项只包含队名、未排除成员数、聚合值、名次、达到时间和“是否我的队伍”，不包含成员身份。排序固定为 `value desc, reachedAt asc, teamId asc`；分页 cursor 是稳定偏移量，单页最多 100 项。

投影首次尚未成功时页面显示“等待权威投影”，不会伪造 0。最近一次快照超过三个投影周期，或新一轮重放失败时，页面明确标记为 stale 并继续展示最后一份通过哈希验证的安全快照。若快照、来源行、聚合或哈希损坏，读取 fail-closed，不返回未经验证的数据。

## 5. 数据、备份与恢复

团队表位于 ExtractionMode 的同一 `extraction-commerce.db`，使用独立 `team_economy_*` 命名空间，包括 schema、teams、memberships、invites、goal snapshots、idempotency、projection events/exclusions/state/failures 和单调 goal progress。邀请表没有明文 token 列。

使用现有经济冷/热备份流程整体备份 SQLite 主库、`-wal` 和 `-shm`，不要只复制某几张团队表。恢复后先执行 SQLite integrity/foreign-key 检查，再以相同 pepper 启动；更换或丢失 pepper 会使旧邀请全部不可验证，但不会改变成员关系或贡献。主动轮换 pepper 前应通知队长重新生成邀请。

团队投影是可重建读模型，但恢复时不要手工删除 projection 表。正常 worker 会从权威事实重新计算并以来源哈希、事件唯一键和快照哈希验证结果；20 次重放、并发触发和进程重启不应增加贡献或触发任何经济副作用。

投影还会把账户事件中的 `accountId -> ExternalUserId` 只转换为不可逆 subject fingerprint，再与同库真实 ban 表核对；原始 UserId 不写入团队表、来源 hash 或日志。缺少账户映射、ban schema、行哈希或非排除来源发生回退时会保留上次安全快照并 fail closed。封禁后保存的旧成员 handle 不能用于转让队长；队长被封禁/排除时旧邀请也会拒绝加入。当前恢复路径是由管理员完成审计解封或移除排除，再由原队长转让/解散；不得直接改团队表。

## 6. 故障处理

1. 页面显示“等待权威投影”：确认 `TeamEconomy:Enabled=true`、当前周世界已绑定、存在至少一支队伍，并检查 `team_economy_projection_failures.error_code`；不要手工填 0。
2. 页面显示“使用上次安全快照”：保留数据库和日志证据，按错误码检查权威来源表。修复来源前不要删除最后快照。
3. `TEAM_PROJECTION_SOURCE_UNAVAILABLE`：检查 SQLite 文件、磁盘、锁和表可用性。
4. `TEAM_PROJECTION_OVERFLOW` 或 `TEAM_SOURCE_*_INVALID`：停止团队公开展示，调查异常来源行；不得截断或钳制后继续排名。
5. `TEAM_PROJECTION_CORRUPT`/哈希不一致：隔离数据库副本，按经济恢复手册从已验证备份恢复，并对比权威源；不要重算后覆盖篡改证据。
6. 邀请响应丢失：同键重放不会再显示 token；队长应使用新幂等键轮换一枚新邀请。
7. 玩家声称贡献少：核对事实发生时间是否位于成员有效期、事实是否达到权威终态、账户是否在冻结排除集合；不要用客户端截图人工补入投影。

worker 日志只能记录稳定错误码、服务器和周世界范围，不应记录账户、UserId、PlayerUID、成员句柄或邀请 token。

## 7. 自动化与外部验收

仓库内证据：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\contract\team-economy-contract.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\integration\team-economy-smoke.ps1
npm --workspace @pal-control/player-web test
$env:PLAYWRIGHT_USE_SYSTEM_CHROME = "1"
npm --workspace @pal-control/player-web run test:e2e -- e2e/team-economy.spec.ts
```

store harness 覆盖 100 账户/10 队、100 路邀请竞争、并发转让/离队、加入前/离队后/换队隔离、20 次和跨重启重放、1001 队稳定分页、贡献后真实 ban/unban、进程内 fail-closed ban fence、旧 handle 转让、封禁队长旧邀请、非排除来源回退保护、排除成员门槛、Web 安全整数溢出、投影篡改和 DB/WAL 隐私扫描。玩家经济黑盒还使用生产管理员策略证明团队 GET/POST 只凭玩家 Cookie/CSRF 成功、无 session 为 401，且请求不携带管理员 API Key。浏览器覆盖桌面/375×812、键盘焦点、Axe、CSRF/幂等、邀请不落本地存储和请求体无身份覆盖。

以上自动化不能替代真实多人周世界验收。上线前仍需至少两支真实小队跨完整一周验证：不同时间加入/离队/换队、资源结算、任务积分、成功发货、封禁排除、周切换、服务重启、数据库备份恢复、邀请泄露轮换，以及玩家对目标/榜单的实际理解；保存脱敏证据后才能宣称团队玩法在生产完成。
