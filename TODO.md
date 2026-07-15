# 幻兽商域玩法 TODO

> 审计基线：2026-07-15，`main` 分支。本文只描述当前仓库能够证明的能力，以及从“本机开发服原型”走到“可长期运营的周世界资源经济服”仍需完成的工作。设计文档、单次手工演示或页面截图不等于功能完成。

## 当前结论

当前玩法更准确的定位是：

> **Palworld 常驻周世界 + 网页战备商城 + 固定资源兑换点。**

项目已通过 [ADR-0001](docs/architecture/decisions/0001-weekly-world-resource-economy.md) 正式采用 **方案 A：周世界资源经济服**。它已经具备一条本机开发服可运行的经济闭环，以下行为是确定的产品规则，不再作为“搜打撤缺失功能”追踪：

- 不建立“开始行动、行动中、死亡、放弃、成功撤离”的逐局生命周期；
- 当前 `run` 只是一笔资源兑换 settlement，不代表一局行动；
- 当前周世界、允许容器中的任意白名单资源均可出售，不追踪其采集时间、交易、制作或掉落来源；
- 死亡和断线不是资源兑换成功/失败状态，也不生成“行动成功率”。

平台身份/本周角色绑定、管理员 RBAC、SQLite 发货 outbox、Native 原子扣物代码、服务级故障注入和经济监控已经完成本地自动化闭环。当前硬阻断项是 Native 真实保存/停服/重启/重登持久化证据、世界与经济独立恢复、连续 3 次真实周换档、正式域名 Steam/TLS/代理验收及 5–10 人完整 7 天试运行。

因此，现阶段应继续标记为 **仅限可信本机/好友开发服的原型**，不能直接作为公网经济服开放。

## 当前实际可玩流程

1. 公网 Steam 服先经 Steam OpenID 建立短时待绑定身份；可信好友服可显式使用平台 UserId fallback。两种模式都由本机受限 RCON `/send` 向在线角色私发 8 位验证码。
2. 验证码成功且实时确认当前 `worldId + PlayerUID` 后建立玩家门户会话。首次访问创建跨周账户；1000 商域币/300 战备券只属于 `legacy-v1` 开发兼容配置，正式奖励必须通过版本化活动显式领取。
3. 玩家浏览 5 个硬编码战备商品；商品基础价格每天按确定性种子调整到 90%–110%。
4. 购买时先在 SQLite 提交扣款/订单，再以确定性 key 接入同库 PalDefender outbox 发货；两者是可恢复的两个事务而非一次原子提交。确定失败退款，部分或无法证明的结果进入人工对账，派发后绝不盲重发。
5. 玩家回到常驻 Palworld 周世界自行采集、战斗、生产、交易和积累资源。
6. 玩家门户地图每 5 秒更新位置；默认配置只有 1 个开发服资源兑换点。
7. 进入资源兑换区后，服务端间隔 2 秒进行两次位置采样。
8. 系统读取 `Items`、`Food`、`DropSlot`，对约 21 种白名单资源的**全部数量**生成 30 秒报价；当前不能选择只出售一部分。
9. 玩家确认后，Control API 以持久 run key 向 Native `inventory.consume` 发送一次完整快照消费命令；Production 不允许 RCON `/delitems` fallback。
10. 只有 Native 逐行差值、完整 after 快照和聚合回读全部证明实际扣物与报价一致时，系统才在 SQLite 单事务内唯一增加战备券；明确失败不入账，`uncertain` 不重试并进入人工对账。当前能力仍为 experimental，真实持久化验收前 Production 闸门保持关闭。
11. 玩家使用战备券继续购买补给，形成“采集/战斗 -> 资源出售 -> 战备采购”的循环。
12. 周档结束后，管理员先运行默认 plan-only 的换档脚本；显式 `-Execute` 才会按持久步骤执行游戏备份、经济快照/复核、停服、新世界探针和赛季提交，脚本不会移动或删除旧世界。

26 个统一 contract/integration 脚本已经覆盖身份、商城、发货、兑换、并发、故障边界、恢复、Windows 配置保留和换档客户端；真实游戏证据仍主要是早期一次商城发货与一次 5 个皮革 RCON 历史联调，不能替代当前 Native 持久化、完整周档和生产回调验收。

## 当前已经具备的基础

这些能力应复用和加固，不建议推倒重写：

| 模块 | 当前能力 | 判断 |
| --- | --- | --- |
| 玩家入口 | 可选 Steam OpenID + 游戏内 8 位验证码、本周 PlayerUID 绑定、HttpOnly/SameSite Cookie、CSRF、Origin 白名单、用户/IP/并发限流 | 已实现；OpenID state、待绑定身份、会话和限流仍是进程内状态，正式域名回调待验收 |
| 账户与钱包 | SQLite 事件库、双货币、不可负余额、幂等账本、退款与人工调账 | 单机基础可用 |
| 商城 | 5 个硬编码商品、个人周限购、先扣款、PalDefender 逐物品结构化 receipt、确定失败退款、partial/`uncertain` 不重发 | 单机闭环已具备；真实 PalDefender 回执语义仍需按固定版本验收 |
| 资源兑换 | 1 个固定兑换点、位置双采样、30 秒完整 Native 报价快照、约 21 种白名单资源、原子扣物与唯一入账 | 代码/假桥闭环完成；真实保存/重启/重登前保持 fail-closed |
| 内容刷新 | 每个业务日按确定性种子对基础价格做 90%–110% 调整 | 只有价格变化，不是完整日轮换 |
| 周换档 | 持久化阶段状态机、确定性 step key、默认 plan-only 受控客户端、经济快照/RPO/未决交易阻断、expiry/reward 幂等框架 | 自动化故障恢复与运维客户端已具备；三次真实演练仍未完成 |
| Native | dev36 `inventory.consume.experimental` 已支持完整槽元数据、同 Tick 全单校验/回滚和安全清空普通静态槽位，Control API 已接入 | 未完成真实保存/重启/重登验证，不能提升为 stable |
| 存档 | 游戏稳定快照、经济一致性快照、manifest/SHA-256、staging 复核与恢复后默认关门 | 缺少世界/经济各一次真实独立恢复和生产切换演练 |

## 已冻结的玩法基线：方案 A

[ADR-0001](docs/architecture/decisions/0001-weekly-world-resource-economy.md) 已确定：

- 本周世界中，位于 `Items`、`Food`、`DropSlot` 的任意白名单资源都可出售，不做来源归因；
- 玩家必须位于开放的资源兑换点，服务端完成位置校验后才能报价；
- 报价包含扫描时全部合格资源的全部数量；玩家可以取消整单，首发不支持选择性出售；
- 确认后先完成可归因、幂等、全成或全败的扣物，再唯一增加周战备券；
- 玩家死亡、断线和离开游戏不是经济 settlement 状态；
- 排行榜只使用可从账本复算的兑换价值、资源数量、任务积分和适用的订单指标，不制造行动次数或成功率。

资源兑换使用独立的 settlement saga：

```text
quoted -> expired | cancelled | consuming
consuming -> removed | definitively_failed | uncertain
uncertain -> removed | definitively_failed | manual_review
manual_review -> removed | definitively_failed
removed -> credited -> settled
```

只有 `settled` 代表资源已扣除且货币已唯一入账。技术 `failed/uncertain` 不得记为玩家死亡或玩法失败。部分扣物不得进入普通失败路径；consume 一旦 dispatched，必须先解析最终证据，不能因为玩家断线或死亡覆盖经济结果。存在任何未终结 settlement 时禁止换档或切换世界。

## 优先级定义

- **P0**：公开测试或对抗性多人测试前的硬门槛；任一未完成都必须保持经济玩法关闭或仅限可信开发服。
- **P1**：形成有内容深度、能连续运行完整周档的 MVP 所需工作。
- **P2**：在核心闭环连续稳定两个周档后再做的扩展。

只有本路线图中标为 P0 且未注明“不适用”的项目才是当前发布门槛。方案 B 的行动模型和战利品来源归因已移到“远期重新立项”，不计入方案 A 的完成率。

## P0：正确性、安全与资源兑换闭环

### P0-00 冻结方案 A 的产品规则 `[已完成][DESIGN]`

- [x] 用 [ADR-0001](docs/architecture/decisions/0001-weekly-world-resource-economy.md) 正式选择方案 A，并记录目标循环、可出售范围、兑换规则、异常处置、术语和赛季统计。
- [x] 首发采用“报价并出售允许容器中的全部合格资源”；玩家可以取消整单，选择性出售留作远期体验项。
- [x] 明确方案 B 当前不适用：不实施行动状态机、携入物/死亡结算或战利品来源归因，也不把相关 spike 作为发布门槛。
- [x] 玩家端将兼容状态 `extracted`/`settled` 显示为“已兑换”，不再把已入账记录显示成“待结算”。

验收标准：

1. ADR 记录选择、未采用方案、风险、术语、排行榜、重新评审条件和兼容性影响。
2. 后续需求和页面只把 `run` 称为兑换单/结算记录，不再称为一局行动。
3. 方案 B 专属工作不出现在当前 P0/P1 发布门槛中。

### P0-02 持久化平台身份与本周角色绑定 `[BE][SECURITY]`

- [x] 增加 `(platformSubject, seasonId, worldId, playerUid, accountId)` 绑定模型和双向唯一约束。
- [x] 新周档必须重新绑定当前世界的完整 `PlayerUID`；旧世界的绑定立即失效。
- [x] 购买、生成报价和确认兑换前都校验实时 PlayerUID 与绑定一致。
- [x] 昵称变化不能创建新账户或改变授权主体。
- [x] 通过 [ADR-0002](docs/architecture/decisions/0002-steam-openid-and-world-binding.md) 决定：公网 Steam 服先用 Steam OpenID 建立平台身份，再用游戏内验证码绑定本周 `PlayerUID`；可信好友服/其他平台可显式使用验证码 fallback。
- [x] 已实现可选 Steam OpenID 开始/回调、服务端有界 `check_authentication`、一次性 state/nonce 重放防护、固定 HTTPS realm/return_to 和与游戏验证码的双层绑定；公网/Production 配置会强制 `OpenIdThenGameCode`，可信好友服才能显式使用 `TrustedGameCode` fallback。
- [x] 增加绑定历史、会话撤销、封禁联动和异常登录审计。

验收标准：

1. Steam A 无法读取或操作 Steam B 的钱包、订单、资源报价或兑换记录。
2. 同一个 PlayerUID 不能绑定两个账户，伪造昵称/UserId/PlayerUID 均失败。
3. 换世界后旧 PlayerUID 的会话不能继续发起经济写操作。

### P0-04 稳定并接入 Native 原子 `inventory.consume` `[NATIVE][BE]`

- [x] 完成可靠清空最后一个槽位，移除 `inventory.consume.partial-stack-only`；只允许完整元数据可验证的普通静态槽位，动态/腐化/不可验证槽位在写前拒绝。
- [ ] 完成“保存世界 → 停服 → 重启 → 重登”的持久化验证，只有通过后才把能力从 experimental 提升为 stable。
- [x] 报价时保存 `inventory.probe` 返回的三个完整容器，以及每项 `containerId`、`slotIndex`、`ItemID`、数量、动态元数据、腐化值 bit pattern 和规范化快照 hash；`beforeRevision/observedRevision` 仅作为 consume 结果证据。
- [x] 结算时在同一个游戏 Tick 内比较全部预期槽位并全成或全败。
- [x] 逐行验证 `actualConsumed == requestedQuantity`、前后数量差、完整快照和聚合回读，任一不一致不得入账。
- [x] 相同幂等键返回持久化原结果；同键不同 payload 或跨 run 重用拒绝；跨 Control API 重启仍保持最终幂等。
- [x] 正式模式禁用 RCON `delitems` fallback；旧路径只允许宿主为 Development、`Security:DevelopmentMode=true`、`PlayerPortal:PublicSteam=false`、显式设置 `AllowDevelopmentSettlement=true`、启用 RCON 且未强制 Native 的隔离诊断环境。

验收标准：

1. 清空槽位、部分栈、多 ItemID、跨三个容器都能正确处理。
2. 使用固定随机种子和场景矩阵验证同 Tick 原子性、同键/异键请求、响应丢失、保存/停服/重启、断线、物品移动及并发 100 组；每组都断言请求量、实际扣除量和剩余量守恒。
3. 不出现部分扣物、重复扣物、无依据 credit 或自动重试 `uncertain`。

### P0-05 修复资源兑换 saga 的并发与终态倒退 `[BE]`

本轮已堵住旧快照覆盖和“未知 RCON dispatch 仅凭背包下降自动入账”两条高风险路径，并完成同库原子 credit、心跳、消费队列、背压及服务级故障注入；真实 Palworld 持久化仍由 P0-04 单独阻断。

- [x] 为 run 增加向后兼容的 `revision`、`leaseId`、`leaseOwner`、`leaseExpiresAt` 和状态变更时间。
- [x] 增加 attempt 计数和长操作最后心跳；当前关键区使用 90 秒内部超时且 lease 为 2 分钟。
- [x] 原请求持有有效 lease 时，恢复任务不能接管；只允许回收过期 lease。
- [x] 所有状态转换都携带 expected state、revision 和适用的 lease fence，使用 compare-and-swap。
- [x] `Settled`、`Failed`、`Cancelled` 等终态不可降级回 `Removed`、`Uncertain` 或其他中间态。
- [x] `Consuming` 恢复不再依据聚合背包下降自动入账；只有 HTTP 持有者确认成功并持久化为 `Removed` 后，恢复任务才可继续 credit。
- [x] 钱包 credit、唯一账本记录和 `Removed -> Credited` 在同一数据库事务提交；`Credited -> Settled` 可安全恢复。
- [x] 若账本已经存在该 run 的 credit，自动或人工路径不会再标记 failed、退款或重复入账；稳定 reference/幂等键支持 credit 后崩溃恢复。
- [x] 增加每玩家串行锁、全服有界消费队列和 backpressure。

验收标准：

1. 固定种子场景矩阵覆盖同 run 同键/异键、lease 过期接管、每个故障边界、多账户隔离和并发 1000 次；不出现重复命令、跨账户阻塞或终态倒退。
2. 不存在“账本已入账但 run 为 failed/uncertain”的组合。
3. 服务重启只能接管过期 lease，不能重新发送已经 dispatched 的游戏命令。

### P0-06 让商城发货具备可归因的结构化 receipt `[BE][PALDEFENDER/NATIVE]`

旧版按背包总量判断发货会受自然拾取或立即消耗干扰；当前已改为绑定 delivery key 的逐物品结构化 receipt 和 SQLite outbox，旧总量差只作为历史问题保留。

- [x] 选择 PalDefender 逐物品 grant adapter，返回绑定 `deliveryId` 和 `idempotencyKey` 的不可变结构化 receipt。
- [x] receipt 包含目标 PlayerUID、请求数量、实际 Granted 数量、命令版本、逐行 `CompletedAt` 和稳定结果 ID。
- [x] 重放相同 delivery key 返回同一持久化 receipt，同键不同请求拒绝，跨服务重启保持一致。
- [x] 部分成功进入人工对账，不能按“明确失败”自动全额退款。
- [x] 发货 ACK 时间从持久化命令的 `CompletedAt` 计算，不从基线采集时开始。
- [x] 给发货队列增加容量、每玩家串行、dead-letter、最老任务年龄和熔断指标。

验收标准：

1. 自然拾取、移动、丢弃或立即消耗相同 SKU 都不影响发货归因。
2. 正常、明确失败、部分成功、响应丢失和超时路径都不会产生免费物品或重复扣款。
3. 连续 200 次测试发货无重复、无余额差异；每个故障点至少重复 20 次。

### P0-07 管理认证、RBAC 与真实审计身份 `[SECURITY][BE][WEB]`

- [x] 为全部管理 API、PalDefender 写接口、调账、对账和换档操作增加 ASP.NET Core Authentication/Authorization。
- [x] 至少定义 `Viewer`、`Operator`、`EconomyAdmin`、`SeasonAdmin`、`Owner`。
- [x] 玩家门户 Cookie 与管理身份完全隔离，玩家会话不能访问任何管理路由。
- [x] 移除 `local-console` 等硬编码 actor，记录真实 subject、角色、来源 IP、reason、request hash、before/after、版本和结果。
- [x] 钱包正向调账、人工确认入账、开放新赛季等高风险操作使用 MFA、再次确认或双人审批。

验收标准：

1. 匿名、玩家 Cookie 和低权限管理员访问越权接口稳定返回 401/403。
2. 至少两个管理员账号完成权限隔离、撤权和审计测试。
3. 任何资金或赛季变更都能追溯到真实操作者，不能由客户端伪造 actor。

### P0-08 统一 Economy Safety Gate 与生产 fail-closed 配置 `[BE][OPS]`

统一门禁现已覆盖 SQLite 回滚写探针与磁盘阈值、活动赛季/物理 world、完整玩家绑定、PalDefender token capability 与版本、Native 生产能力、受限 RCON 验证码/开发能力、维护和有界队列；合同测试与 HTTP smoke 验证了结构性启动拒绝、稳定 blocker、写前拒绝、独立熔断热恢复、排空和状态持久化。正式服新手奖励已改为冻结版本、显式领取和同事务双钱包入账。

- [x] 玩法代码默认关闭，生产配置样例显式关闭且初始商域币/战备券为 0。
- [x] 将正式服新手奖励实现为按周世界冻结、版本化、可审计且显式领取的活动；双钱包/账本/领取记录同事务提交，当前 `legacy-v1` 的 1000/300 只保留给开发配置兼容。
- [x] 新订单扣款前验证数据库可写、世界 ID、赛季、角色绑定、游戏/PalDefender/Native 版本与能力、维护状态和队列容量。
- [x] 同一个门禁也约束后台发货 worker，避免版本漂移后“先扣款再失败”。
- [x] 购买与资源兑换提供独立熔断开关，不需要重启服务。
- [x] 密钥、监听、Cookie、安全目录和 ACL 等结构性安全错误使用 `ValidateOnStart` 并使启动失败；只对已启用的 RCON/Native adapter 做条件校验。
- [x] 世界、版本、能力、磁盘余量和运行时依赖漂移不杀死只读服务，而是关闭对应经济写闸门并暴露明确原因；维护期已有账户只读、经济存储 readiness 分离和购买/资源兑换逐功能门禁均已接入。
- [x] 修复初始货币配置变更导致旧幂等键 hash 冲突：冻结 `legacy-v1` 额度，非 legacy 策略禁止隐式发币，历史冲突只告警且要求显式 adjustment/migration。

验收标准：

1. 任一版本、能力、世界或存储检查失败时，不产生 debit、游戏写入或 credit。
2. 热改下一周初始额度不会阻塞现有玩家登录，也不会重新发钱。
3. 结构性安全配置错误时进程拒绝启动；运行时依赖不满足时门户可只读，但经济写入绝不降级开放。

### P0-09 统一经济持久化、备份与恢复 `[BE][OPS]`

- [x] 单机阶段使用同一个 `extraction-commerce.db` 承载账户、钱包、订单、run、delivery evidence/receipt、PalDefender 发货 outbox、管理审计和赛季/换档调度，并为各组件登记版本化 migration。购买事务与 PalDefender command 接收仍是同库的两个事务，由持久 delivery/receipt 和确定性 key 恢复衔接，不能宣称跨步骤原子提交。
- [x] 经济 run/evidence 已停止整文件 JSON 权威写入；PalDefender 发货命令已从 JSONL/内存权威迁移为 SQLite 命令投影和不可变事件。旧 JSONL 只在一次性事务导入时读取，成功后改名保留；冲突、篡改或导入失败会使启动失败。
- [ ] 公告、游戏内通知和 save 等非经济命令审计仍使用追加式 JSONL；为其制定保留/归档策略，或在需要统一查询与多实例 worker 前迁移到相应持久存储。
- [x] 玩家登录与会话撤销写审计；P0 可在重启后强制全部玩家重新登录。若未来持久化会话，只保存 token hash，不保存原始凭据。
- [x] 提供一致性经济快照，覆盖 SQLite/WAL、命令状态、run、delivery、闸门和调度状态。
- [x] 提供恢复到 staging、hash 校验、worldId/账本/未决交易复核后再切换的工具。
- [x] 恢复后经济默认关闭，直到全部门禁重新通过。

自动化证据：身份安全契约覆盖 token hash、进程重启失效、登录/撤销脱敏审计；delivery receipt/outbox harness 覆盖 100 路并发、容量、server+key 冲突、租约崩溃恢复、`dispatched` 不重发、死信、不可变事件、终态不可逆、JSONL 幂等迁移和迁移失败无部分写入；continuity harness 覆盖 SQLite 在线备份、命令与 side-state 一致性、manifest/hash/worldId/账本/未决状态复核、staging 恢复及恢复后双熔断和维护闸门默认关闭，并覆盖损坏、N-1 manifest/schema 兼容和多个故障注入点。

仍未完成：世界备份与经济备份的真实独立恢复演练、从已发布 N-1 包执行迁移失败后的真实回退，以及 staging 人工复核后的生产切换尚未验收。因此下列验收标准仍按整体未完成处理。

验收标准：

1. 世界备份和经济备份各完成一次独立恢复演练。
2. 明确 RPO/RTO，并能列出备份时间点之后需要人工核对的交易。
3. fresh install、N-1 升级、迁移失败回退、损坏检测和恢复测试全部通过。

### P0-10 持久化周换档状态机并完成真实演练 `[BE][OPS]`

- [x] 将 `preflight -> drain -> game backup -> economy backup -> stop -> new world -> probe -> commit -> reopen` 持久化。
- [x] 每一步使用确定性 step key，重启后可以继续，不能跳步或重复发放。
- [x] 战备券过期写入唯一 ledger；提供版本化、幂等的赛季结算 job 框架，若当前内容版本配置了周奖励，则每项奖励最多执行一次。
- [x] 存在任何未终结资源兑换 settlement、未决订单、`uncertain`、过期备份或版本漂移时禁止换档。
- [x] 生产配置禁止自动删除旧世界；按 RPO、审计/回滚窗口、磁盘容量和恢复成本制定可配置保留期并做容量预估，固定“至少 8 周”时须另有业务 ADR。

自动化证据：持久化状态机、确定性 operation/step key、SQLite 事务与 evidence envelope hash 已由 continuity harness 覆盖；每个阶段都在“证据写入后”和“步骤推进后”注入崩溃并验证回滚/重放，expiry/reward 重放、规则版本漂移、备份 RPO、未决订单/settlement/命令队列和安全门禁均由代码路径拒绝。受控 PowerShell 客户端默认 plan-only，强制 managed game backup 与 economy snapshot/verify/stage，逐阶段覆盖 action 后崩溃、服务端提交后响应丢失、同键恢复、凭据脱敏和错误阻断；脚本结构性拒绝旧世界 Archive/Delete。经济备份保留期、最少份数、容量安全系数均可配置，清理 API 只输出候选而不执行删除。

仍未完成：3 次真实 Palworld 周换档演练，以及新赛季产生真实交易后“拒绝自动回滚到旧世界”的真实环境验收尚未完成；自动化和受控客户端不能替代这些外部证据。

验收标准：

1. 连续完成 3 次“旧世界 -> 验证备份 -> 新世界 -> 新赛季”的完整演练。
2. 在每个阶段注入崩溃，恢复后不误删世界、不重复 expiry；若内容配置了周奖励，也不重复奖励或错误回滚。
3. 新赛季产生交易后，系统拒绝自动回滚到旧世界。

### P0-11 自动化测试、CI、指标与发布闸门 `[QA][OPS]`

- [x] 新建统一 `test` 命令和 GitHub Actions Windows workflow，运行两个前端 build、.NET Release build、玩家端单元测试、契约测试、结算 harness 和现有 smoke tests。
- [x] 增加价格、区域、哈希、状态机、钱包与账本守恒单元测试。
- [x] 增加 100 并发扣款、限购、同键/异键重放、迁移和唯一约束测试。
- [x] 增加登录、CSRF、IDOR、角色绑定、商城、发货、资源兑换、Native consume 与换档 contract/E2E。
- [x] 在每个“持久化、派发、ACK、扣物、回读、入账”边界执行故障注入。
- [x] 区分进程存活与经济存储只读 readiness；单个游戏 adapter 故障不再让可读门户返回 503，并有 Release 黑盒测试覆盖。
- [x] 购买/资源兑换能力通过兼容路径 `/extraction/capabilities` 与逐功能写闸门单独暴露。
- [x] 输出订单/资源兑换状态与延迟、发货/资源队列和最老 outbox、`uncertain` 数、账本守恒、身份冲突、版本、世界一致性、备份年龄及独立熔断指标；同时提供 Viewer 保护的 JSON 与 Prometheus 接口。
- [x] 为队列饱和/停滞、`uncertain`、账本/身份不变量、版本/世界漂移、备份过期和采集失败配置稳定告警码与逐功能自动熔断；指标与本模块日志不带 Cookie、验证码、Token、密码或玩家原始标识，自动动作带 correlation ID。
- [ ] 为全部既有后台 worker/adapter 日志统一 correlation scope 并完成全服务脱敏审计；不能用本指标模块的黑盒扫描代替旧日志逐项复核。

发布验收：

1. 先由 5–10 名玩家运行完整 7 天，不能跳过真实周换档。
2. 最近 7 天无未解释余额差、重复发货、无依据入账或部分扣物。
3. 值班人员可独立完成订单/资源兑换 `uncertain` 对账和备份恢复。
4. 所有 P0 证据由不同于实现者的人复核。

## P1：补足内容、成长与运营

### P1-01 数据驱动的内容配置 `[DESIGN][BE][WEB]`

- [ ] 将商品、价格、限购、可选的全服库存、回收目录、资源兑换点和任务从代码/单机配置迁到版本化内容定义；未配置全服库存时只执行个人限购。
- [ ] 首发达到 10–20 个商品、50–100 个可售资源和至少 2 个实测资源兑换区。
- [ ] 管理员可创建草稿、查看 diff、校验、原子发布和回滚到上一完整版本。
- [ ] 分类、标签和推荐位使用显式字段，不再通过 SKU 字符串推断。
- [ ] 区分“服务器库存”和“个人剩余限购”，修正玩家端文案。

验收标准：

1. 同一业务日、同一内容重复发布 20 次只生成一个生效版本；失败时 current pointer 只指向完整旧版或完整新版。
2. 草稿校验能拒绝重复 SKU、非法物品、负价格、失效区域和缺少版本的依赖，回滚后玩家提交旧 offer 得到稳定错误。
3. 配置了全服库存时并发购买不超卖；未配置时页面不显示虚假的“服务器库存”。

### P1-02 真正的日轮换、任务与热点 `[DESIGN][BE][WEB][NATIVE]`

- [ ] rotation 包含 `rulesVersion`、业务日唯一键、随机种子、content hash 和 current pointer。
- [ ] 原子发布整套 offer；失败时继续展示上一完整版本，不出现半套新价格。
- [ ] 初版任务只使用当前能可靠证明的事件：成功资源兑换、结算指定 ItemID 或价值、完成商城订单、消耗指定货币。
- [ ] 击杀、采集、进入热点、死亡和 PvP 任务必须先接入权威 Native/PalDefender 事件，定义事件幂等键、来源和重放语义后再开放。
- [ ] 增加至少 3 个日任务池、3 个周任务池；同一事件重复投递不能重复推进，同一奖励只能入账一次。
- [ ] 每天轮换资源热点和开放资源兑换点；热点必须真实改变服务端收益、掉落或价格倍率，地图显示开放时间、路线和收益/风险倍率。
- [ ] 任意资源兑换开放时段至少有一个可用兑换点；若所有兑换点关闭，服务端必须拒绝新报价并显示下一次开放时间。
- [ ] 规则明确区域在报价期间关闭时允许原报价完成还是立即失效，并对关闭边界和活动报价做自动化测试。
- [ ] 旧 offer 提交稳定返回 `OFFER_NOT_AVAILABLE`，重复刷新不会重复任务或奖励。

验收标准：

1. 同一业务日重复发布/重放 20 次仍只有一个 rotation、一个任务实例和一笔任务奖励。
2. 开放、关闭、grace、报价跨边界和“最后一个兑换点”全部自动测试；无可用兑换点时不能生成报价。
3. 任务进度、热点倍率和地图状态均能由同一服务端版本证据复算，不以客户端上报为准。

### P1-03 经济平衡与反套利 `[DESIGN][DATA]`

- [ ] 为每个可售物品按货币拆分组合商品的影子成本，建立“购买 → 拆包/制作/转换 → 回收”的完整有向图；发布前检测所有可达套利环，而不是只比较单品回收价与最低商品价。
- [ ] 目录外物品默认不可售；按物品类别配置回收折价目标和风险缓冲，不用统一 50% 规则掩盖双货币或组合商品成本。
- [ ] 对至少 100 个模拟玩家运行完整一周经济仿真。
- [ ] 定义货币产出/消耗、余额中位数、头部余额、商品购买率和资源兑换收益目标区间。
- [ ] 正式配置取消开发服默认 300 战备券，改为教学任务或小额新手补贴。
- [ ] 连续两个周档输出通胀、产销、热门商品和异常账户报告。

验收标准：

1. 内容发布器能给出具体套利路径并阻止发布，双货币、礼包拆分和至少一层制作转换均有测试。
2. 仿真输入、随机种子、假设和输出可复现；连续两个周档的真实指标位于 ADR 设定区间或有经审批的调价记录。

### P1-04 永久成长、排名与赛季结算 `[DESIGN][BE][WEB]`

- [ ] 为商域币增加稳定但受限的玩法来源，以及至少两个有意义的长期消费场景。
- [ ] 实现周资源兑换总价值、按 ItemID/类别汇总的有效兑换数量与价值、任务积分排行榜；所有数值均可从冻结快照与账本复算。
- [ ] 固定同分规则、迟到 settlement 截止时间、最低有效贡献和封禁/冻结账户排除规则；不发布行动次数、撤离成功率或死亡率。
- [ ] 周奖励基于冻结快照幂等发放，支持作弊冻结、取消与人工补发审计。
- [ ] 周档结束页展示个人成绩、排名、过期战备券和永久奖励。

验收标准：

1. 冻结快照后迟到事件不静默改榜；同分、最低次数、封禁排除和补发都有确定性测试。
2. 同一赛季奖励 job 重放 20 次，每个账户、奖励项最多一笔 ledger，页面数值可从快照与账本复算。

### P1-05 玩家门户实时状态与异常体验 `[WEB][BE]`

- [ ] 使用 SSE/WebSocket 或 2–5 秒有界轮询更新订单和资源兑换终态，终态后停止。
- [ ] 报价显示实时倒计时，到期后自动禁用结算按钮。
- [ ] 区分退款、取消、部分成功和人工核对；`uncertain` 明确提示不要重复提交。
- [ ] 对维护、离线、版本不匹配、队列拥堵和会话过期给出明确下一步。
- [ ] 增加首次登录、购买、前往资源兑换点、确认整单出售和账本查看的新手引导。
- [ ] 完成手机端、键盘、焦点和屏幕阅读器 E2E。

验收标准：

1. 每种终态出现后客户端停止对应轮询/订阅，过期报价按钮不可提交且服务端同样拒绝。
2. 断网、重连、服务重启、维护和 `uncertain` 场景不会自动重复购买或结算，玩家看到的下一步与服务端处置一致。

### P1-06 完整运营工作台 `[WEB][BE][OPS]`

- [ ] 展示全局订单、资源兑换、`uncertain`、outbox、闸门、世界、版本和备份状态。
- [ ] 支持证据查看、人工对账、调账、换档阶段、审批和审计检索。
- [ ] 高风险操作展示 before/after、确认短语和授权要求。
- [ ] 页面刷新或服务重启后可以恢复未完成操作。

验收标准：

1. 刷新、重复点击、响应丢失和服务重启不会重复执行高风险操作。
2. 每次操作都能从审计记录还原操作者、授权、输入、before/after、结果和关联账本/命令。

### P1-07 可复现部署与可选多实例 `[BE][OPS]`

- [ ] 单实例先完成可复现部署；只有容量或可用性目标要求多个 Control API/worker 实例时，才迁移 PostgreSQL + transactional outbox + lease，保持领域接口不变。
- [ ] 从现有 SQLite 迁移后逐账户重算余额，并核对 ledger、订单、run、delivery、幂等键和行级规范 hash；若需要事件 hash 链，先定义 canonical serialization 与版本。
- [ ] Control API、Caddy 与 worker 以最小权限 Windows Service 运行，支持自动启动、失败重启、版本化发布和原子回滚。
- [ ] 补齐升级、密钥轮换、版本漂移、磁盘满、数据库损坏和疑似重复发货手册，并由非开发人员实操。
- [ ] 完成 24 小时 soak test，确认内存、句柄、日志、队列和会话没有持续增长。

验收标准：

1. 同一提交可在空机器重复部署并通过 capabilities、备份恢复和最小权限检查；回滚不丢未决交易。
2. 若启用多实例，lease 接管、节点断连和并发 worker 测试保持余额及副作用唯一；未启用时不把 PostgreSQL/HA 宣称为已完成。

## P2：核心稳定后再扩展

- [ ] 选择性资源出售：玩家可从整单报价中保留部分合格资源，未选择物品绝不扣除；报价、扣物和入账仍保持全成或全败。
- [ ] 动态资源兑换区、随机世界事件、区域收益/风险等级和限时热点。
- [ ] 商品/物品图标、稀有度、用途说明和更丰富的地图图层。
- [ ] 浏览器通知、游戏内送达通知、赛季结束通知和异常对账通知。
- [ ] 公会/小队资源目标、合作任务和团队经济排行榜。
- [ ] 运营分析面板：玩法漏斗、商品购买率、资源兑换转化率、区域热度和经济健康度。
- [ ] 多 Palworld 服务器、跨服账户和兼容矩阵自动化；单服 Control API/worker 多实例由 P1-07 负责。

以下内容继续留在远期，不应抢占当前 MVP：拍卖行、装备/帕鲁兑换、跨服交易和复杂付费系统。

## 远期重新立项：方案 B（不计入当前发布门槛）

以下能力不属于方案 A 的 P0/P1/P2。只有产品重新转向逐局搜打撤，并通过新的 ADR 后，才能建立独立路线图：

- 独立 `RaidRun`/`ActionRun`，以及开始、行动中、死亡、放弃、断线恢复、超时和成功撤离生命周期；
- 服务端托管战利品容器、Native 来源事件账本或不可伪造物品标签；
- 携入物、保险、安全仓、死亡赔付和本局战利品归属；
- 行动次数、撤离价值、成功率和死亡率排行榜；
- 独立匹配实例、装备/帕鲁撤离与逐局队伍玩法。

完整背包前后数量差不能证明物品来源。若未来重新立项，必须先在固定版本上证明来源能力，并逐项测试基地箱取回、预放物、交易、制作、丢弃再拾取、商城发货和同 ItemID 消耗后重获；不得复用当前资源兑换 settlement 冒充行动记录。

## 推荐执行顺序

1. **继续冻结公开经济写入**；当前只允许本机/可信开发环境，不能手工伪造 Native stable capability。
2. 由受控玩家完成 **P0-04** 的“Native 扣物 → 保存 → 停服 → 重启 → 重登”验收，并保存脱敏证据。
3. 分别完成世界备份与经济备份的真实恢复，再连续执行 3 次完整周换档；新赛季产生交易后验证系统拒绝自动回滚旧世界。
4. 在正式 HTTPS 域名完成 Steam OpenID、Caddy 回调日志跳过、Cookie、代理头和重放黑盒验收；RCON `/send` 仍只在本机受限通道送达验证码。
5. 由 5–10 名玩家运行完整 7 天，并由不同于实现者的人复核余额、重复发货、`uncertain` 对账和恢复证据。
6. 上述 P0 外部证据通过后，再进入 P1 内容扩充、经济仿真和运营工作台。

## 全局完成定义

任何 TODO 只有同时满足以下条件才能勾选：

- 代码、配置、数据迁移与回滚路径均已提交；
- 单元、契约、集成/E2E 和相关故障注入自动通过；
- 文档、OpenAPI、运行手册和安全边界已同步；
- 对应指标、告警、审计和人工处置流程可用；
- 在固定 Palworld/PalDefender/UE4SS 版本组合上保存了脱敏验收证据；
- 不是只凭 mock、页面可见、一次手工成功或设计文档宣称完成。

## 关键审计证据

- 当前原型定位与公开服硬门槛：`extraction-mode/README.md:41-71`
- 旧版单次真实购买/RCON 皮革兑换仅作为历史开发证据，不能替代当前 Native 持久化验收：`extraction-mode/docs/05-实施阶段与验收.md`
- 当前初始货币与唯一资源兑换点：`services/control-api/appsettings.json` 中的 `InitialMarketCoin`、`InitialSeasonVoucher` 与 `ExtractionZones`
- 5 个硬编码商品与懒触发调价：`services/control-api/Infrastructure/ExtractionModeCoordinator.cs` 中的 `SeedProducts` 与 `EnsureDailyRotationAsync`
- `PlayerUID` 本周绑定、历史与双向唯一约束：`services/control-api/Extraction/JsonlExtractionRepository.cs`、`services/control-api/Infrastructure/ExtractionModeCoordinator.cs`
- 当前 `run` 的报价/扣物/结算状态、revision、lease 与 CAS：`services/control-api/Infrastructure/ExtractionRunStore.cs`
- 技术状态到玩家资源兑换文案的映射：`apps/player-web/src/runState.ts` 中的 `resourceExchangeStateLabel`
- Native 完整快照扣物、持久化回执、内部关键区超时、唯一 credit 与保守恢复：`services/control-api/Infrastructure/ExtractionSettlementService.cs`、`services/control-api/Infrastructure/ExtractionNativeInventoryAdapter.cs`
- Native consume 当前仍为 experimental：`packages/contracts/bridge/inventory.consume.md`
- 商城发货逐物品结构化 receipt、跨重启幂等与 partial/uncertain 对账：`services/control-api/Infrastructure/ExtractionDeliveryWorker.cs`、`services/control-api/Infrastructure/ExtractionDeliveryReceipts.cs`
- 玩家门户会话只保存进程内 token hash，重启全部失效；封禁与异常登录审计持久化：`services/control-api/Infrastructure/PlayerPortalSessionRegistry.cs`、`services/control-api/Infrastructure/PlayerIdentitySecurity.cs`
- Control API 管理 API 的 API Key/RBAC/TOTP 与持久审计：`services/control-api/Infrastructure/AdminSecurity.cs`、`services/control-api/Infrastructure/AdminAudit.cs`
- 玩家端状态与刷新逻辑：`apps/player-web/src/Portal.tsx`、`apps/player-web/src/runState.ts` 与 `apps/player-web/src/api.ts`
- 当前自动测试与 Windows GitHub Actions：`.github/workflows/ci.yml`、`tests/run-tests.ps1`、`tests/settlement-saga/`、`tests/contract/`、`tests/integration/`
