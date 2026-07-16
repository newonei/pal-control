# 经济指标、告警与自动熔断运行手册

本手册适用于方案 A“周世界资源经济服”。Control API 以聚合方式暴露商城订单、发货、资源兑换、账本、身份绑定、版本、世界和备份状态；任何指标都不得包含 SteamID、平台 UserId、PlayerUID、昵称、会话、验证码、Cookie、Token 或密码。

## 接口与权限

- `GET /api/v1/economy/observability`：JSON 快照，供控制台、值班脚本和故障排查使用。
- `GET /api/v1/economy/observability?refresh=true`：同步重采集一次；该读请求本身不会改变熔断器。
- `GET /api/v1/economy/metrics`：Prometheus 0.0.4 文本格式，使用最近一次快照。

两者均受管理 API 的 loopback 边界和 `Viewer` RBAC 保护。不要把管理 API 通过公网反向代理。采集器使用专用 Viewer 密钥时，只在进程环境或受 ACL 保护的秘密文件中提供明文；仓库、命令行历史和监控标签不得保存明文密钥。

```powershell
$Api = "http://127.0.0.1:5180/api/v1"
$Headers = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_VIEWER_KEY }
$Snapshot = Invoke-RestMethod "$Api/economy/observability?refresh=true" -Headers $Headers
$Metrics = Invoke-RestMethod "$Api/economy/metrics" -Headers $Headers
```

## JSON 快照

`schemaVersion` 当前固定为 `1`。`status` 取 `healthy`、`warning` 或 `critical`，由活动告警的最高等级决定。

| 字段 | 含义 |
| --- | --- |
| `orders` | 每个商城订单状态的当前数量，以及从创建到当前/终态的平均、最大和 P95 延迟。 |
| `resourceSettlements` | 每个资源兑换状态的数量和延迟。这里的 run 是 settlement，不是一局行动。 |
| `deliveries` | 发货状态数量和延迟。 |
| `deliveryQueue` | 会阻塞购买安全恢复的未终结发货数量（待处理、处理中、失败或不确定）、容量、占用率和最老任务年龄。 |
| `resourceSettlementQueue` | 当前接纳的资源兑换数量、容量和可接纳状态。 |
| `outbox` | SQLite PalDefender 持久命令各状态数量、容量和最老未终结命令年龄；`states` 额外输出 `leased` 与 `deadLettered` 计数。 |
| `uncertain` | 订单、发货、结构化 receipt、部分 receipt、资源兑换和 outbox 的不确定数量。 |
| `ledger` | 钱包—账本流数量、余额投影差异、资源兑换 credit 差异和总体守恒结论。 |
| `identity` | 结构性绑定冲突、累计拒绝数和时间窗内拒绝数；只输出计数。数据库中的冲突证据也只保存 SHA-256 指纹。 |
| `dependencyConsistency` | 购买与资源兑换各自的存储、磁盘和运行时探针结果；只输出稳定 blocker code，不输出异常正文或依赖地址。 |
| `versionConsistency` | 游戏、PalDefender 与 Native 的批准版本/能力一致性；生产资源结算只允许 Native 稳定能力，RCON 仅保留给显式启用的 Development 诊断兼容路径；购买和资源兑换 blocker 分开。 |
| `worldConsistency` | 活动经济赛季与当前 Palworld 存档世界、周窗口的一致性。 |
| `gameBackup` / `economyBackup` | 最近备份时间、年龄、允许年龄、是否为写入硬门槛和新鲜度。 |
| `circuits` | 购买与资源兑换两个独立持久熔断器的状态。 |
| `alerts` | 稳定告警码、等级、是否活动、影响功能、当前值和阈值。 |

延迟是状态记录的 wall-clock 年龄，单位秒；它用于发现积压和卡死，不承诺等同于上游网络请求耗时。备份不存在时 `ageSeconds` 为 `null`，Prometheus 对应值为 `-1`，同时以 `backup_available=0` 区分。

## 自动熔断条件

生产样例启用 `ExtractionMode:Observability:AutoCircuitBreakEnabled`。后台采集默认每 30 秒执行一次。仅活动且 `autoCircuit=true` 的 `critical` 告警会打开对应熔断器：

| 稳定告警码 | 默认条件 | 自动关闭的写路径 |
| --- | --- | --- |
| `DELIVERY_QUEUE_SATURATED` | 发货队列不可用或占用率达到 100% | 购买 |
| `DELIVERY_QUEUE_STALLED` | 最老待发货超过 300 秒 | 购买 |
| `OUTBOX_SATURATED` | PalDefender outbox 不可用或达到容量 | 购买 |
| `OUTBOX_STALLED` | 最老未终结命令超过 180 秒 | 购买 |
| `OUTBOX_DEAD_LETTER_PRESENT` | 任一命令在进入 `dispatched` 前连续失败并进入 dead-letter | 购买 |
| `SETTLEMENT_QUEUE_SATURATED` | 资源兑换队列不可用或达到容量 | 资源兑换 |
| `PURCHASE_UNCERTAIN_PRESENT` | 订单、发货、receipt、部分 receipt 或 outbox 的不确定总数超过允许值（生产默认为 0） | 购买 |
| `RESOURCE_SETTLEMENT_UNCERTAIN_PRESENT` | 不确定资源兑换超过允许值（生产默认为 0） | 资源兑换 |
| `LEDGER_INVARIANT_VIOLATION` | 任一余额—账本或 settlement credit 不守恒 | 两者 |
| `IDENTITY_BINDING_INVARIANT_VIOLATION` | 权威绑定表存在重复或孤立历史 | 两者 |
| `IDENTITY_BINDING_CONFLICT_SPIKE` | 默认 15 分钟内拒绝的绑定冲突达到 5 次 | 两者 |
| `PURCHASE_DEPENDENCY_UNAVAILABLE` | 购买所需存储、磁盘或运行时探针返回 blocker | 购买 |
| `RESOURCE_DEPENDENCY_UNAVAILABLE` | 资源兑换所需存储、磁盘或运行时探针返回 blocker | 资源兑换 |
| `PURCHASE_VERSION_INCONSISTENT` | 购买 adapter 的版本或能力不满足批准组合 | 购买 |
| `RESOURCE_VERSION_INCONSISTENT` | Native 资源扣物的版本或能力不满足批准组合 | 资源兑换 |
| `WORLD_INCONSISTENT` | 活动周、存档世界或 world 验证失败 | 两者 |
| `GAME_BACKUP_STALE` | 生产要求近期游戏备份，但最近备份不存在或超过 60 分钟 | 两者 |
| `ECONOMY_BACKUP_STALE` | 生产要求近期经济快照，但最近快照不存在或超过 15 分钟 | 两者 |
| `ECONOMY_METRICS_COLLECTION_FAILED` | 无法完成权威指标采集 | 两者 |

`DELIVERY_QUEUE_HIGH`、`OUTBOX_HIGH` 和 `SETTLEMENT_QUEUE_HIGH` 默认为 75% warning，只告警，不自动熔断。

自动逻辑只会把仍为开放状态的熔断器关闭，不会覆盖已经由值班人员关闭的原因，也不会自动重新开放。异常消失后仍需人工完成对账、备份或版本核验，再按 [Economy Safety Gate 手册](economy-safety-gate.md) 使用高风险授权恢复写入。这样可避免短暂探针恢复掩盖免费物品、重复发货或无依据入账。

## 告警处置顺序

1. 先确认 `collectedAt` 新鲜且 `collectionErrorCode` 为空；采集失败时保持两条经济写路径关闭。
2. 查看 `circuits` 和活动 `alerts`，按 `affects` 分开处理购买与资源兑换，禁止为恢复其中一个功能而绕过另一个功能的 blocker。
3. `uncertain` 或部分发货先核对结构化 receipt、游戏内实际结果和审计，不得自动重放或自动全额退款。
4. `OUTBOX_DEAD_LETTER_PRESENT` 只表示命令在 durable `dispatched` 前达到内部失败上限；核对 `deadLettered` 事件、修复依赖并走人工审计处置，禁止直接重开终态或复用原 key。
5. 账本不守恒时冻结两条路径，保留数据库、WAL、经济快照和日志，禁止直接修表；从最近验证快照重放并定位第一条差异。
6. 身份冲突突增时检查登录入口和来源指纹计数，必要时封禁/撤销会话；监控接口不会提供原始身份，只有具备 Owner 权限的既有安全审计流程可处理个案。
7. 依赖不可用时先按 `dependencyConsistency` 的稳定 blocker code 检查经济库可写性、磁盘余量和对应运行时；版本或世界漂移必须恢复批准的 Palworld、PalDefender、UE4SS/Native 组合并重新探测，不能调高阈值掩盖。
8. 备份过期时先创建并验证游戏备份与经济快照；两者属于同一恢复点证据，不能只补其中一个。
9. 告警清零后执行一笔受控低价值购买或资源兑换，核对 receipt、实际变更和账本，再人工重开对应熔断器。

## Prometheus 最小规则示例

监控系统应对 `pal_control_economy_alert_active == 1` 直接按 `code` 和 `feature` 路由，同时至少设置：

- `pal_control_economy_snapshot_success == 0`：立即告警；
- `time() - pal_control_economy_snapshot_timestamp_seconds > 90`：采集器停滞；
- `pal_control_economy_ledger_invariant_mismatch_total > 0`：最高等级；
- `pal_control_economy_uncertain_total > 0`：需要人工对账；
- `pal_control_economy_queue_state_total{queue="outbox",state="deadLettered"} > 0`：保持购买熔断并人工处理死信；
- `pal_control_economy_dependency_consistent == 0`：按 `feature` 立即通知并保持对应写路径熔断；
- `pal_control_economy_circuit_open == 1`：通知值班并展示熔断功能；
- `pal_control_economy_backup_fresh == 0`：在生产写入窗口前阻断发布。

Prometheus 标签只有稳定状态、队列、功能、告警码和版本信息。不要将日志正文、请求头、URL 查询、管理员 reason 或任何玩家值转成动态标签。

## 日志与隐私核验

可观测性后台错误和自动熔断日志都会生成 correlation ID，自动熔断 actor 固定为 `system:economy-observability`。日志只写稳定告警码和功能，不写指标采集到的身份证据。HTTP 管理请求仍由管理审计中间件产生 `X-Correlation-ID`。

发布前运行：

```powershell
.\tests\contract\economy-observability-contract.ps1
.\tests\integration\economy-observability-smoke.ps1
npx --yes @redocly/cli lint packages/contracts/openapi/control-api.yaml
```

黑盒测试会同时扫描 JSON 和 Prometheus 响应，拒绝 `playerIdentifier`、`playerUid`、`externalUserId`、SteamID、Cookie、Token、密码以及测试管理密钥。全服务后台日志另由编译后 worker/adapter 清单、IL 调用审计和恶意异常行为测试独立验证；参见 [日志关联与脱敏审计](logging-correlation-and-redaction.md)。指标响应黑盒扫描不能替代该审计，两类门禁都必须通过。
