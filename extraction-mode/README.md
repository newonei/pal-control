# 幻兽商域：周世界资源经济模式

本目录是该玩法的实施设计基线。产品已通过 [ADR-0001](../docs/architecture/decisions/0001-weekly-world-resource-economy.md) 选择方案 A：当前周世界允许容器中的任意白名单资源都可出售，不建立逐局行动或战利品来源归因。这里把 Pal Control、PalDefender 和 Palworld 专用服务器能力组合成一个可实施的资源经济 MVP，并明确标出尚未实现的硬门槛。

## 已定方案

- 采用“**每周一个世界档、每日刷新内容**”，不做每日删档，也不在 MVP 中为每一局启动独立服务器实例。
- 核心循环是“周世界采集/战斗/生产/交易 -> 资源兑换点整单出售白名单资源 -> 获得战备券 -> 商城消费”。
- 资源不区分采集时间和来源；死亡、断线不属于兑换成功/失败状态，也不统计行动成功率。
- 每周一 `04:00`（`Asia/Shanghai`）换新世界；每日 `04:00` 刷新营业日内容、商城价格、日任务与热点选择。个人周限购只在新周档重置。
- 永久货币“**商域币**”跨世界保留；赛季货币“**战备券**”在周档结束时结算并归零。
- 商城只通过 Control API 调用本机 PalDefender；正式资源兑换只通过 Control API 与本机 Native Bridge 的稳定 `inventory.consume` 执行。浏览器绝不直连 PalDefender、RCON、Native Bridge 或官方 REST。
- 平台 `UserId` 是跨档身份；`PlayerUID` 只在一个世界内有效，不能用玩家名绑定账户。
- 商城与资源兑换均使用数据库账本、幂等键、Outbox 和可对账状态机；任何 `uncertain` 都禁止自动重发。
- MVP 只兑换 `Items`、`Food`、`DropSlot` 三类容器中的白名单物品；武器、护甲、关键物品和帕鲁暂不兑换。报价默认包含全部合格数量，玩家可取消整单但不能选择部分出售。
- 商品、回收白名单、兑换区、任务和轮换策略来自完整的不可变内容版本；当前授权目录完整时内置 10 个商品、51 个可售资源、1 个兑换区和 3 日/3 周可靠任务。旧 offer 不得跨 content hash 使用。
- 启用 `ExtractionMode` 后，Control API 必须在 HTTP 流量进入前从本机经授权目录原子初始化当前营业日内容；开源仓库不附带该目录，缺失或非法时启动直接失败。
- **先由 Native 在同一 Tick 验证完整报价快照并原子扣除，再用逐行/逐槽位/完整快照/聚合回读和持久化回执证明结果，最后唯一增加数据库货币**。任何证据不能证明时保持冻结并进入对账。

## 文档索引

| 文件 | 用途 |
| --- | --- |
| [00-MVP玩法与架构](docs/00-MVP玩法与架构.md) | 玩法循环、范围、现有能力和目标架构 |
| [01-数据模型](docs/01-数据模型.md) | 当前 SQLite/未来多实例存储模型、唯一约束、账本和状态字段 |
| [02-API契约](docs/02-API契约.md) | 玩家端、运营端、内部适配器 API 契约 |
| [03-交易与资源兑换状态机](docs/03-交易与撤离状态机.md) | 文件名为兼容保留；商城发货、资源兑换扣物、幂等和故障恢复 |
| [04-运行与安全换档手册](docs/04-运行与安全换档手册.md) | 日刷新、周换档、对账、回滚与应急操作 |
| [05-实施阶段与验收](docs/05-实施阶段与验收.md) | 开发顺序、测试矩阵、上线门槛和完成定义 |
| [经济内容发布与回滚运行手册](../docs/runbooks/economy-content-publishing.md) | 草稿、diff、严格校验、维护排空、发布失败重放和回滚 |
| [玩家使用教程](../docs/player-portal/05-玩家使用教程.md) | 登录、任务、购买、整单出售、轮询与异常状态处置 |
| [Invoke-WeeklyRollover.ps1](scripts/Invoke-WeeklyRollover.ps1) | 默认只生成计划；显式执行时按服务端持久化步骤完成关闸、双备份、停服、新世界探针、提交与恢复 |
| [Test-ExtractionMode.ps1](scripts/Test-ExtractionMode.ps1) | 使用只读管理凭据检查 API、PalDefender、Native 结算、可选 RCON 诊断隔离、闸门和未决交易 |

## 周换档命令

只读预检，不关闭服务、不修改存档：

```powershell
.\extraction-mode\scripts\Invoke-WeeklyRollover.ps1 -PlanOnly
```

新换档的首次执行必须先审核计划输出，再显式提供 `-Execute`、冻结的 `-TargetWorldId`、`-RulesVersion` 和 `-InstallRoot`。脚本会安全获取管理 API/TOTP 与官方 REST 凭据，并且永远保留旧世界目录。`Archive`、`Delete`、`AllowDeletePreviousWorld` 和 `ArchiveRoot` 等旧兼容参数会立即拒绝，不存在自动删除或移动旧世界路径。不要把 `25575` 加入路由器或内网穿透映射。

## 当前可用与待实现

> 当前交付是**本机开发服候选**，不是可直接反代到公网的正式经济服。游戏内验证码身份、管理员 RBAC/审计、SQLite PalDefender outbox 和 Native 完整快照原子消费代码已经落地；公开经济测试仍被真实“扣物 → 保存 → 停服 → 重启 → 重新登录”证据、世界/经济独立恢复和三次真实周换档阻断。资源兑换的 RCON 路径只用于 Development 诊断，不能作为生产降级；玩家登录验证码仍由本机受限 RCON `/send` 通道送达。

| 能力 | 当前证据 | MVP 决策 |
| --- | --- | --- |
| 读取在线玩家、`UserId`、`PlayerUID`、位置 | PalDefender 玩家接口已接入 | 复用，用于身份映射与资源兑换区判断 |
| 读取资源兑换背包 | Native `inventory.probe` 已读取 `Items`、`Food`、`DropSlot` 完整槽位和动态元数据 | 作为资源报价权威快照；PalDefender 读取只用于商城/运营辅助 |
| 发放物品 | PalDefender `POST give/items/{playerIdentifier}` 已接入逐物品命令与不可变 receipt | 作为商城发货通道；partial/uncertain 进入人工对账 |
| 发放经验和点数 | PalDefender `POST give/progression/{playerIdentifier}` 已接入 | 可用于任务奖励，不属于首发商城必需项 |
| 命令审计与 `uncertain` 语义 | PalDefender command、租约、死信和不可变事件已进入 `extraction-commerce.db`；旧 JSONL 一次性迁移后不再是权威 | `dispatched` 重启后进入 `uncertain`，不能盲目重发；死信触发购买熔断并人工复核 |
| 定向回收物品 | Native dev36 已实现完整快照 `inventory.consume`、回滚和安全清空普通静态槽位 | Production Native-only；当前 experimental 在真实持久化验收前 fail-closed |
| RCON 运行状态 | 可选开发诊断通道 | Windows 防火墙阻断远程来源；不参与正式资源兑换 |
| 清空背包 | PalDefender RCON 提供 `/clearinv` | 破坏范围过大，不用于自动资源兑换，只允许开发服/人工应急 |
| 精确原子消费槽位 | Native 与 Control API 已接入完整快照、稳定 request hash、持久回执和跨重启幂等 | 仍需真实保存/重启/重登后才能从 experimental 提升 stable |
| 永久账户、钱包、订单、赛季 | 已实现本机 SQLite 事务数据库、版本化 migration、不可负账本、PalDefender outbox 与一致性快照 | 单机候选可用；多实例不在当前 MVP，购买事务与命令接收靠持久 delivery/receipt 和确定性 key 跨事务恢复 |
| 在线商城与可靠发货 | 已实现页面、幂等订单、逐物品 PalDefender receipt、明确失败退款和 partial/uncertain 对账 | 固定 PalDefender 版本的真实回执语义仍是上线门禁 |
| 版本化内容与库存 | 草稿/diff/严格校验、不可变版本、current pointer 与完整商品投影同事务激活、发布/回滚、显式分类/标签/推荐位、个人/全服库存和旧 offer 拒绝已有自动化 | 当前仍只有 1 个默认兑换区；真实内容发布/恢复演练尚未完成 |
| 可靠任务 | 3 日/3 周任务实例冻结内容证据；只接收成功兑换、指定 ItemID/价值、成功订单和货币消费，同事件/奖励跨重启唯一 | 未开放击杀、采集、热点进入、死亡或 PvP；真实周档尚未验收 |
| 玩家实时体验 | 5 步引导、3 秒有界终态轮询、报价倒计时、退款/取消/partial/uncertain 与运维错误指引已有代码和单测 | 手机、键盘、焦点与屏幕阅读器真实 E2E 未完成 |
| 资源报价与兑换 | 已实现位置双采样、开放窗口/路线/收益倍率、30 秒完整 Native 报价、51 项目录过滤白名单候选、持久回执和唯一入账 | fake bridge/故障恢复已通过；第二真实兑换区与 Native 持久化验收尚未通过 |
| 自动创建新世界与切换世界 | 已实现持久化阶段状态机、确定性 step key、强制游戏/经济备份与 staging、RPO/未决交易阻断和可恢复客户端 | 默认只计划且结构性禁止删除旧世界；三次真实演练未完成 |
| 玩家登录 | `TrustedGameCode` 与 `OpenIdThenGameCode` 显式分层；Steam 回调服务端校验、state/nonce 防重放、待绑定身份、游戏验证码、当前周 worldId + PlayerUID、会话轮换、CSRF/限流/脱敏审计均已实现 | 公网/Production 强制官方 Steam OpenID 双层绑定；可信好友服才能使用验证码 fallback；仍需正式域名 TLS 黑盒验收 |

## MVP 完成定义

只有以下闭环全部通过验收，才能称为“周世界资源经济 MVP 已实现”：

1. 玩家能以平台账户登录并正确绑定当前周档的 `PlayerUID`。
2. 并发购买不会透支，重复请求只产生一个订单和一次发货。
3. 发货成功、明确失败、结果不确定三种路径均不会产生免费物品或重复扣款。
4. 资源兑换必须通过服务端位置检查、完整 Native 报价快照、同 Tick 全单扣物和逐行/完整快照/聚合/持久化回执；扣物未被证明时绝不入账。
5. 服务在“扣物成功但入账前”崩溃后，能够只补账一次。
6. 每日刷新可重复执行且不会生成重复轮换。
7. 每周换档具备关闭交易、备份校验、新世界身份校验、开放闸门和有边界回滚。
8. 当前世界、PalDefender/Native 精确版本、稳定能力、本机隔离或备份新鲜度不符合已验收组合时，对应经济写操作自动关闭且只允许人工复核后恢复。
