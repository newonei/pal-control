# 幻兽商域：周世界资源经济模式

本目录是该玩法的实施设计基线。产品已通过 [ADR-0001](../docs/architecture/decisions/0001-weekly-world-resource-economy.md) 选择方案 A：当前周世界允许容器中的任意白名单资源都可出售，不建立逐局行动或战利品来源归因。这里把 Pal Control、PalDefender 和 Palworld 专用服务器能力组合成一个可实施的资源经济 MVP，并明确标出尚未实现的硬门槛。

## 已定方案

- 采用“**每周一个世界档、每日刷新内容**”，不做每日删档，也不在 MVP 中为每一局启动独立服务器实例。
- 核心循环是“周世界采集/战斗/生产/交易 -> 资源兑换点整单出售白名单资源 -> 获得战备券 -> 商城消费”。
- 资源不区分采集时间和来源；死亡、断线不属于兑换成功/失败状态，也不统计行动成功率。
- 每周一 `04:00`（`Asia/Shanghai`）换新世界；每日 `04:00` 刷新商城、限购、任务与价格修正。
- 永久货币“**商域币**”跨世界保留；赛季货币“**战备券**”在周档结束时结算并归零。
- 商城只通过 Control API 调用本机 PalDefender；资源兑换首版通过 Control API 的本机白名单 RCON 适配器执行。浏览器绝不直连 PalDefender、RCON、Native Bridge 或官方 REST。
- 平台 `UserId` 是跨档身份；`PlayerUID` 只在一个世界内有效，不能用玩家名绑定账户。
- 商城与资源兑换均使用数据库账本、幂等键、Outbox 和可对账状态机；任何 `uncertain` 都禁止自动重发。
- MVP 只兑换 `Items`、`Food`、`DropSlot` 三类容器中的白名单物品；武器、护甲、关键物品和帕鲁暂不兑换。报价默认包含全部合格数量，玩家可取消整单但不能选择部分出售。
- **先扣除且通过 REST 背包回读证明物品已经消失，后增加数据库货币**。RCON 文本响应本身不是成功证据；结果不能证明时，资源兑换功能保持冻结并进入对账。

## 文档索引

| 文件 | 用途 |
| --- | --- |
| [00-MVP玩法与架构](docs/00-MVP玩法与架构.md) | 玩法循环、范围、现有能力和目标架构 |
| [01-数据模型](docs/01-数据模型.md) | PostgreSQL 表、唯一约束、账本和状态字段 |
| [02-API契约](docs/02-API契约.md) | 玩家端、运营端、内部适配器 API 契约 |
| [03-交易与资源兑换状态机](docs/03-交易与撤离状态机.md) | 文件名为兼容保留；商城发货、资源兑换扣物、幂等和故障恢复 |
| [04-运行与安全换档手册](docs/04-运行与安全换档手册.md) | 日刷新、周换档、对账、回滚与应急操作 |
| [05-实施阶段与验收](docs/05-实施阶段与验收.md) | 开发顺序、测试矩阵、上线门槛和完成定义 |
| [Invoke-WeeklyRollover.ps1](scripts/Invoke-WeeklyRollover.ps1) | 交易关闸、优雅停服、切换 DedicatedServerName、新世界启动与提交 |
| [Test-ExtractionMode.ps1](scripts/Test-ExtractionMode.ps1) | 只读检查 API、PalDefender、RCON、防火墙、闸门和未决交易 |

## 周换档命令

只读预检，不关闭服务、不修改存档：

```powershell
.\extraction-mode\scripts\Invoke-WeeklyRollover.ps1 -PlanOnly
```

正式执行时脚本会安全提示输入官方 REST 管理凭据。默认 `PreviousWorldPolicy=Keep`，旧世界目录原地保留。当前开发服若明确不保留旧档，可使用 `-PreviousWorldPolicy Delete -AllowDeletePreviousWorld`；该组合仍会触发 PowerShell 高风险确认。不要把 `25575` 加入路由器或内网穿透映射。

## 当前可用与待实现

> 当前交付是**仅本机开发服可玩的闭环原型**，不是可直接反代到公网的正式经济服。公开测试前仍有两项硬门槛：Steam/平台身份认证与管理员 RBAC；以及由 Native MOD 在同一游戏 Tick 内完成的原子背包消费。RCON 方案用于联调玩法、价格和换档，不能抵御恶意玩家制造背包竞态。

| 能力 | 当前证据 | MVP 决策 |
| --- | --- | --- |
| 读取在线玩家、`UserId`、`PlayerUID`、位置 | PalDefender 玩家接口已接入 | 复用，用于身份映射与资源兑换区判断 |
| 读取六类背包 | PalDefender `GET items/{playerIdentifier}` 已接入 | 复用，生成商城审计快照与资源报价 |
| 发放物品 | PalDefender `POST give/items/{playerIdentifier}` 已接入持久幂等队列 | 复用，作为商城发货通道 |
| 发放经验和点数 | PalDefender `POST give/progression/{playerIdentifier}` 已接入 | 可用于任务奖励，不属于首发商城必需项 |
| 命令审计与 `uncertain` 语义 | Pal Control 已有 JSONL 持久队列 | 已复用并增加发货前背包基线、重启恢复和回读证明 |
| 定向回收物品 | PalDefender RCON 提供 `/delitems <UserId> ItemId:Amount...` | MVP 使用仅本机白名单适配器；执行后必须 REST 回读 |
| RCON 运行状态 | 开发服已启用 `25575/TCP`；本机协议探针成功 | Windows 防火墙阻断非 loopback 来源，Control API 只暴露白名单方法 |
| 清空背包 | PalDefender RCON 提供 `/clearinv` | 破坏范围过大，不用于自动资源兑换，只允许开发服/人工应急 |
| 精确原子消费槽位 | Native Bridge 当前不足，目标 `inventory.consume` 尚未实现 | 本机开发原型可用 RCON；任何公开经济测试前必须完成 |
| 永久账户、钱包、订单、赛季 | 已实现本机 SQLite 事务事件数据库与不可负账本；旧 JSONL 首次启动自动迁移并只读保留，权威标记存在时数据库缺失会拒绝启动 | 开发服可用；正式多实例部署前迁移 PostgreSQL，领域接口不变 |
| 在线商城与可靠发货 | 已实现页面、幂等订单、PalDefender 发货、REST 增量回读和明确失败退款 | 已用真实在线玩家完成一次购买/重放验收 |
| 资源报价与兑换 | 已实现位置双采样、30 秒整单报价、白名单扣物、REST 后读和唯一入账 | 已用 5 皮革完成真实扣物与战备券入账；同键重放未重复入账 |
| 自动创建新世界与切换世界 | 已提供维护闸门、readiness/commit API 与受控 rollover 脚本 | 默认保留旧世界；开发服可显式选择 Delete 且需二次授权参数 |
| 公网玩家登录 | 当前 Control API 无独立登录认证且只能留在本机 | 新增独立玩家门户与 Steam 登录/绑定 |

## MVP 完成定义

只有以下闭环全部通过验收，才能称为“周世界资源经济 MVP 已实现”：

1. 玩家能以平台账户登录并正确绑定当前周档的 `PlayerUID`。
2. 并发购买不会透支，重复请求只产生一个订单和一次发货。
3. 发货成功、明确失败、结果不确定三种路径均不会产生免费物品或重复扣款。
4. 资源兑换必须通过服务端位置检查、精确背包快照、白名单 `/delitems` 和 REST 后快照；扣物未被证明时绝不入账。
5. 服务在“扣物成功但入账前”崩溃后，能够只补账一次。
6. 每日刷新可重复执行且不会生成重复轮换。
7. 每周换档具备关闭交易、备份校验、新世界身份校验、开放闸门和有边界回滚。
8. 当前世界、PalDefender/RCON 版本或本机隔离不符合已验收组合时，所有经济写操作自动关闭；启用 Native 增强后同样校验其版本。
