# 02：API 契约

当前权威玩家路由是 `/api/v1/player/*`，版本化内容运营路由是 `/api/v1/servers/{serverId}/economy-content/*`，并分别使用玩家 Cookie/CSRF 与管理员 API Key/RBAC。本文后半保留的 `/extraction/v1` 片段是早期目标模型说明，不是可调用路由；实现、OpenAPI 与客户端不应从这些历史示例反推当前路径。

## 当前开发服已落地接口

当前实现位于只监听本机的 Control API，供运营控制台使用。管理路由使用独立 API Key、五级 RBAC 和持久审计；调账、人工对账、维护与换档提交还要求 TOTP 和审计原因。玩家门户使用完全隔离的 Cookie/CSRF 会话。即便已经有应用层认证，Control API 仍不得直接暴露到外网：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/extraction/capabilities` | 独立返回购买与资源兑换写闸门；依赖故障时保留只读能力并给出稳定 blocker code |
| PUT | `/api/v1/extraction/admin/safety-gate/{purchase\|resource-exchange}` | 无重启关闭或恢复单一经济写路径；要求 EconomyAdmin、TOTP 与审计原因 |
| GET | `/api/v1/extraction/overview?userId=...` | 当前周档、双货币余额与资源兑换统计 |
| GET | `/api/v1/extraction/catalog?userId=...` | 每日确定性轮换价、周限购与商品发放摘要 |
| POST | `/api/v1/extraction/orders` | 创建幂等订单；Body 为 `userId/productId/quantity` |
| GET | `/api/v1/extraction/orders?userId=...` | 订单与 PalDefender 发货状态 |
| GET | `/api/v1/extraction/ledger?userId=...` | 钱包账本 |
| POST | `/api/v1/extraction/runs/quote` | 连续位置采样、背包白名单扫描和 30 秒报价 |
| POST | `/api/v1/extraction/runs/{runId}/select` | 管理兼容路径；携带 `userId/sourceRevision/items` 与幂等键原子派生所选子报价 |
| POST | `/api/v1/extraction/runs/{runId}/settle` | Native 完整快照原子扣物、持久化回执和唯一入账；正式环境不使用 RCON fallback |
| GET | `/api/v1/extraction/runs?userId=...` | 资源兑换记录与结算 capability |
| POST | `/api/v1/extraction/admin/wallet-adjustments` | 本机管理员幂等调账 |
| POST | `/api/v1/extraction/admin/orders/{orderId}/reconcile` | 维护状态下把 uncertain 发货确认为 `delivered` 或幂等 `refund` |
| POST | `/api/v1/extraction/admin/runs/{runId}/reconcile` | 维护状态下把 uncertain 资源兑换确认为 `failed` 或唯一入账 `settled` |
| GET | `/api/v1/extraction/admin/settlement/status` | 探测当前资源结算适配器；Production 返回 `adapter=native`，Development 显式兼容模式返回 `adapter=development-rcon` |
| GET | `/api/v1/extraction/admin/rcon/status` | 已废弃的兼容别名；返回与 `/admin/settlement/status` 相同的通用结构，新客户端不得继续依赖此路径 |
| POST | `/api/v1/extraction/admin/rollover/maintenance` | 关闭或开放商城与资源兑换写入 |
| GET | `/api/v1/extraction/admin/rollover/preflight` | 停服前核对赛季到期时间、当前世界与目标周窗口 |
| GET | `/api/v1/extraction/admin/rollover/readiness` | 检查未完成/不确定订单和资源兑换 |
| POST | `/api/v1/extraction/admin/rollover/commit` | 核对当前物理世界后，首次绑定当前周档或关闭过期周档并创建新赛季；同 worldId 幂等 |
| GET | `/api/v1/player/me/overview` | 当前登录玩家、本周绑定、双钱包与兑换统计 |
| GET | `/api/v1/player/me/catalog` | 当前营业日完整内容证据、显式商品字段、个人剩余限购与可选全服库存 |
| POST | `/api/v1/player/me/orders` | 使用 `productId/contentVersionId/contentHash/sku/quantity` 创建订单；旧 offer 返回 `OFFER_NOT_AVAILABLE` |
| GET | `/api/v1/player/me/orders` | 本人订单；区分退款、取消、partial 与 uncertain |
| GET | `/api/v1/player/me/ledger` | 本人资金流水 |
| GET | `/api/v1/player/me/extraction-zones` | 本人位置、内容定义兑换区、开放状态、路线、下一开放时间与收益倍率 |
| POST | `/api/v1/player/me/runs/quote` | 本人位置双采样、当前内容白名单与 30 秒源报价；页面默认全选 |
| POST | `/api/v1/player/me/runs/{runId}/select` | 本人选择；Body 只含 `sourceRevision/items`，身份、账户与赛季只取会话，要求 CSRF 与幂等键 |
| POST | `/api/v1/player/me/runs/{runId}/settle` | 本人报价结算；Cookie 身份、CSRF 与幂等键均必需 |
| GET | `/api/v1/player/me/runs` | 本人资源兑换记录 |
| GET | `/api/v1/player/me/new-player-activities` | 当前周世界已发布活动与本人领取状态 |
| POST | `/api/v1/player/me/new-player-activities/{activityKey}/versions/{version}/claim` | 显式领取指定不可变活动版本 |
| GET | `/api/v1/player/me/tasks` | 版本固定的日/周可靠任务、进度、奖励和积分 |

`orders`、`runs/{id}/select`、`runs/{id}/settle`、活动领取和管理员调账都要求 `Idempotency-Key`。购买和资源兑换还会验证数据库真实可写、经济赛季 worldId 与 `GameUserSettings.ini`/活动存档目录/官方 REST world GUID 一致、当前周完整玩家 UID 绑定、对应 adapter 版本与能力、维护状态和队列容量；周窗口到期不会自动开空 worldId 赛季，而是保持写入关闭直到受控换档提交。控制台应分别读取 `capabilities.writes.purchase` 与 `capabilities.writes.resourceExchange`，不能用其中一个功能的状态推断另一个；`enabled=false` 时展示 `blockers[].code/message` 和 `circuit`，不应自行绕过服务端门禁。完整操作见 [Economy Safety Gate 运行手册](../../docs/runbooks/economy-safety-gate.md)。当前返回形状以玩家/控制台 DTO、端点实现和 OpenAPI 为准。

版本化内容管理的当前路由：

| 方法 | 路径后缀 | 权限与语义 |
| --- | --- | --- |
| GET | `/current`、`/versions`、`/drafts`、`/drafts/{draftId}`、`/drafts/{draftId}/diff` | `Viewer`；只读当前指针、不可变历史、草稿与语义 diff |
| POST | `/drafts` | `EconomyAdmin`；从指定完整版本复制草稿 |
| PUT | `/drafts/{draftId}` | `EconomyAdmin`；`If-Match` 必须等于当前 revision |
| POST | `/drafts/{draftId}/validate` | `EconomyAdmin`；使用当前授权目录和批准依赖严格校验 |
| POST | `/drafts/{draftId}/publish` | 高风险 `EconomyAdmin`；要求维护/排空、`If-Match`、幂等键、TOTP、原因和 `PUBLISH ECONOMY CONTENT` |
| POST | `/rollback` | 高风险 `EconomyAdmin`；要求维护/排空、预期 current version、幂等键、TOTP、原因和 `ROLLBACK ECONOMY CONTENT` |

完整操作和激活失败边界见 [经济内容发布与回滚运行手册](../../docs/runbooks/economy-content-publishing.md)。

发布和回滚先准备不可变版本，再把 current pointer、整套商品投影与激活记录放在同一 SQLite 事务中提交；第 N 个商品写入故障时事务整体回滚，读取与购买继续使用完整旧版。资源、兑换区和任务直接从 pointer 指向的完整定义解析，不存在逐项切换接口。

## 1. 公共规则

- 玩家接口只接受 HTTPS，并使用服务端 Session Cookie；不在 URL、localStorage 或返回体中暴露上游 token。
- 订单、选择、结算、活动领取和管理员经济写入等有经济副作用的 POST 要求 `Idempotency-Key`，长度 8–128 个无控制字符；无副作用的资源扫描报价不接收该键。
- 选择键全局绑定规范请求、账户和源报价；完全相同的请求跨进程重启返回同一个子报价，同键换选择、账户或源报价返回 `409 IDEMPOTENCY_CONFLICT` 且不改变任何 run。订单与结算沿用各自幂等规则。
- 错误响应使用 `ApiError` 的 `code`、`message`、`traceId`；成功 DTO 不承诺统一 `requestId`。时间是 RFC 3339 UTC；金额和数量是 JSON 整数。
- 客户端不得提交价格、余额、PlayerUID、UserId、ItemID 或资源兑换总额作为权威值。
- 写请求要求 CSRF header；购买和资源兑换按账户与 IP 限流。

统一错误：

```json
{
  "code": "PLAYER_OUTSIDE_EXTRACTION_ZONE",
  "message": "当前玩家未通过资源兑换区连续位置校验。",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00"
}
```

### 1.1 从源报价派生所选子报价

玩家请求只允许以下字段，禁止提交 `userId`、`accountId`、`seasonId`、价格或总额：

```json
{
  "sourceRevision": 1,
  "items": [
    { "itemId": "Bone", "quantity": 2 }
  ]
}
```

服务端在同一个 RunStore 临界区中验证所有者、赛季、状态、有效期、revision、current content pointer、ItemID 唯一性和数量上限，并通过一次持久快照同时把源报价改为 `Cancelled`、创建所选 `Quoted` 子报价。响应继续使用 `ExtractionQuote`，并包含 `revision`、`sourceQuoteRunId` 与 `selectionDerived=true`。Native 子报价保留完整 `quoteSnapshotHash/nativeInventorySnapshot` 作为乐观锁证据，但 `inventory.consume.items` 只含所选行；Development RCON 诊断也只删除所选行。完整错误、恢复和并发边界见 [选择性资源出售运行手册](../../docs/runbooks/selective-resource-sale.md)。

## 2. 身份与当前状态

### `GET /extraction/v1/me`

返回登录账户、当前赛季绑定和写入闸门：

```json
{
  "account": {
    "id": "8a7db2aa-151e-46a9-a01b-210fbcf26a5c",
    "displayName": "玩家",
    "platform": "steam",
    "userId": "steam_7656119..."
  },
  "season": {
    "id": "59d366a7-97e0-4a53-bc3f-8fc3050fbba5",
    "code": "2026-W29",
    "state": "open",
    "endsAt": "2026-07-20T19:30:00Z"
  },
  "player": {
    "bound": true,
    "online": true,
    "displayName": "演示玩家"
  },
  "gates": {
    "purchases": true,
    "extractions": true,
    "reason": null
  },
  "requestId": "01J..."
}
```

响应不向普通前端暴露完整 `PlayerUID`。未绑定时 `player.bound=false`，商城和资源兑换写入关闭。

### `POST /extraction/v1/bindings/current/verify`

首次或换档后建立绑定。服务端从认证会话取得平台 UserId，再从 PalDefender 在线玩家列表查找完全匹配记录；请求体为空。成功返回 `seasonPlayerId` 和显示名。找不到在线匹配时返回 `409 PLAYER_BINDING_NOT_OBSERVED`。

不得提供“提交昵称或 PlayerUID 绑定”的公共接口。

### `GET /extraction/v1/wallets`

```json
{
  "wallets": [
    { "currency": "market_credit", "balance": 1200, "version": 8 },
    { "currency": "supply_ticket", "balance": 450, "version": 19 }
  ],
  "requestId": "01J..."
}
```

### `GET /extraction/v1/wallets/ledger?cursor=...&limit=50`

只返回当前账户分录，使用不透明 cursor；`limit` 最大 100。

## 3. 商城

### 当前实现：`GET /api/v1/player/me/catalog`

返回当前已发布 rotation 和 offer 快照：

```json
{
  "revision": "7f1e...",
  "businessDate": "2026-07-15",
  "rulesVersion": "weekly-economy-v1",
  "contentVersionId": "d0a32ce6-2263-4d3c-aa01-65955221dcba",
  "contentHash": "a6b7...",
  "rotation": { "algorithmVersion": 1, "seed": "19cb..." },
  "items": [
    {
      "productId": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19",
      "sku": "COARSE-AMMO",
      "name": "基础弹药箱",
      "description": "本周战备",
      "category": "弹药",
      "tags": ["消耗品"],
      "price": { "currency": "weeklyTicket", "amount": 80 },
      "deliverySummary": "粗制弹药 ×100",
      "personalLimitRemaining": 3,
      "serverStockRemaining": null,
      "contentVersionId": "d0a32ce6-2263-4d3c-aa01-65955221dcba",
      "contentHash": "a6b7..."
    }
  ]
}
```

不返回内部 Palworld ItemID，避免客户端伪造发货内容。

### 当前实现：`POST /api/v1/player/me/orders`

Header：`Idempotency-Key: order-<uuid>`、CSRF token。

```json
{
  "productId": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19",
  "contentVersionId": "d0a32ce6-2263-4d3c-aa01-65955221dcba",
  "contentHash": "a6b7...",
  "sku": "COARSE-AMMO",
  "quantity": 1
}
```

服务端重新读取当前内容 offer、个人/全服限购、绑定、在线状态和钱包；SQLite 先原子创建订单与扣款分录，随后由可恢复 worker 以确定性 key 注册 PalDefender command/receipt。两步不是同一事务。当前端点返回 `200` 的订单投影：

```json
{
  "orderId": "2a3b9452-9e95-44cf-b94a-910cf9ca0cca",
  "productId": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19",
  "productName": "基础弹药箱",
  "quantity": 1,
  "currency": "weeklyTicket",
  "totalAmount": 80,
  "state": "pending"
}
```

当前玩家端一次请求只购买一个商品，数量 1–99，最终还受个人周限购与可选全服库存约束。

### 当前实现：`GET /api/v1/player/me/orders`

只按玩家会话返回本人订单。对外状态：

- `accepted/pending/delivering`：仍可能自动进入终态，玩家端有界轮询；
- `succeeded`：完整发货回执已经证明；
- `failed`：明确失败仍在完成确定性处置；
- `refunded`：退款分录已经完成；
- `cancelled`：订单取消，不会继续发货；
- `partial`：只有部分物品被证明送达，必须人工对账；
- `uncertain`：可能已发放，不能重复购买同一补给，必须人工对账。

```json
{
  "orderId": "2a3b9452-9e95-44cf-b94a-910cf9ca0cca",
  "productId": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19",
  "productName": "基础弹药箱",
  "quantity": 1,
  "currency": "weeklyTicket",
  "totalAmount": 80,
  "state": "succeeded",
  "statusMessage": null,
  "createdAt": "2026-07-15T03:00:00Z",
  "updatedAt": "2026-07-15T03:00:01Z"
}
```

客户端轮询间隔不得低于 1 秒，并在终态停止；后续可换成 SSE。

## 4. 资源兑换

### `GET /extraction/v1/extraction-zones`

当前玩家兼容路由 `GET /api/v1/player/me/extraction-zones` 返回所有活动兑换区、本人位置、当前开放/热点状态、服务端实际收益倍率、路线、风险提示、逐区下一开放时间，以及“全部关闭”时的顶层最早 `nextOpensAt`。精确半径与是否在区内仍由服务端判断；客户端不得自行决定开放或倍率。

### `POST /extraction/v1/extractions/quote`

Header：`Idempotency-Key: quote-<uuid>`。请求体：

```json
{ "zoneId": "d88f4718-a625-4140-81a0-51be70008d5e" }
```

服务器完成两次位置采样后，返回报价；该调用最长可耗时约 3 秒：

```json
{
  "extractionId": "2b559ccb-8ae3-46a1-95f9-07fb3cd29445",
  "state": "quoted",
  "expiresAt": "2026-07-12T03:10:30Z",
  "snapshotHash": "sha256:...",
  "currency": "supply_ticket",
  "totalAmount": 138,
  "lines": [
    {
      "displayName": "金属铸块",
      "quantity": 20,
      "unitPrice": 5,
      "lineAmount": 100
    }
  ],
  "excluded": [
    { "displayName": "关键物品", "reason": "CONTAINER_NOT_ELIGIBLE" }
  ],
  "requestId": "01J..."
}
```

不向客户端返回容器 ID、槽位或内部 ItemID。`totalAmount=0` 时可返回报价，但 commit 返回 `409 NOTHING_TO_EXTRACT`。

### 当前实现：`POST /api/v1/player/me/runs/{runId}/settle`

Header：玩家 Session Cookie、允许的 `Origin`、`X-CSRF-Token` 和新 `Idempotency-Key`；没有请求体。管理员兼容路由另从 body 读取明确的 `userId`，不能由玩家调用。

服务端重新检查所有权、报价未过期、current 内容版本/hash 未改变、赛季开放、玩家在线且仍在区域、背包快照完全一致。通过后持久化 `Consuming`、lease、request hash 和 Native 请求，返回 `200` 当前 run 投影：

```json
{
  "extractionId": "2b559ccb-8ae3-46a1-95f9-07fb3cd29445",
  "state": "consuming",
  "requestId": "01J..."
}
```

### 当前实现：`GET /api/v1/player/me/runs`

状态就是当前 `ExtractionSettlementState`：`Quoted/Consuming/Removed/Credited/Settled/Failed/Uncertain/Expired/Cancelled`。

只有 `Settled` 表示扣物和入账均已证明。`Uncertain` 时 UI 必须显示“不要再次资源兑换或移动相关物品，等待对账”，不能提供“重试扣除”按钮。

## 5. 运营 API

运营接口位于现有本机 `/api/v1/extraction/admin/*` 与 `/api/v1/servers/{serverId}/economy-content/*` 路由，要求明确 RBAC；不与玩家 Cookie 共享认证。

早期领域动作到当前路由的对应关系：

| 方法和路径 | 权限 | 作用 |
| --- | --- | --- |
| `GET /api/v1/extraction/capabilities` | `Viewer` | 当前赛季、世界身份、写闸门与稳定 blocker |
| `PUT /api/v1/extraction/admin/safety-gate/{feature}` | `EconomyHighRisk` | 关闭/开放购买或资源兑换，要求 TOTP/reason |
| `POST .../economy-content/drafts`、`GET .../diff` | `EconomyAdmin`/`Viewer` | 从完整版本生成草稿并查看 diff |
| `POST .../economy-content/drafts/{id}/publish` | `EconomyHighRisk` | 当前营业日发布一次，维护排空且幂等 |
| `POST /api/v1/extraction/admin/orders/{id}/reconcile` | `EconomyHighRisk` | 基于证据终结，不直接再次发货 |
| `POST /api/v1/extraction/admin/runs/{id}/reconcile` | `EconomyHighRisk` | 基于 Native 证据失败或唯一入账 |
| `POST /api/v1/extraction/admin/wallet-adjustments` | `EconomyHighRisk` | 追加调整分录；正式服仍建议双人审批 |
| `/api/v1/extraction/admin/rollover/*` | `SeasonAdmin/SeasonHighRisk` | 预检、维护、持久步骤、提交和状态查询 |

所有运营 POST 同样使用幂等键和 reason；钱包调整、开放闸门、执行换档记录操作者与 IP。

## 6. 游戏通道契约

### 6.1 现有 PalDefender 发货

Economy worker 通过现有 Pal Control 调用，而非直接调用 PalDefender：

```http
POST /api/v1/servers/local/paldefender/give/items/steam_7656119...
Idempotency-Key: shop:2a3b9452-9e95-44cf-b94a-910cf9ca0cca:delivery:1
Content-Type: application/json
```

```json
{
  "reason": "商城订单 2a3b9452-9e95-44cf-b94a-910cf9ca0cca 发货",
  "payload": {
    "Items": [
      { "ItemID": "Arrow", "Count": 50 }
    ]
  }
}
```

`playerIdentifier` 使用规范平台 `UserId`，不使用中文昵称。worker 保存返回 `commandId`，随后查询现有 PalDefender command API 直至终态。

成功证据必须是命令 `state=succeeded`、上游 HTTP 2xx，且结构化 `Granted.Items` 与请求总数量相符。背包后读是附加审计证据，不应因玩家立即消耗物品而把明确成功误判为失败。

### 6.2 MVP：Native `inventory.consume`

玩家仍只调用公开结算端点，浏览器不能直接访问 Named Pipe：

```http
POST /api/v1/player/me/runs/{runId}/settle
Cookie: pal_player_session=...
X-CSRF-Token: ...
Idempotency-Key: extraction:<runId>:consume:1
```

报价阶段由服务端调用 `inventory.probe`，冻结完整 PlayerUID、`common`/`dropSlot`/`food` 三个必需容器及每个槽位的 `containerId`、`slotIndex`、静态 ItemID、数量、动态 GUID、动态数据标记、腐化值和精确 bit pattern，并持久化 `snapshotVersion: 1` 与规范 SHA-256。客户端不能提交或删减快照。

结算阶段执行顺序：

1. 重新验证玩家会话、当前周 PlayerUID/世界绑定、兑换点、报价有效期、Safety Gate 和结算队列；
2. 使用持久化报价生成稳定 request hash 和 Native 幂等键；同键同请求返回原结果，同键异 payload 或跨 run 重用拒绝；
3. 在派发前把 `Consuming`、lease 与请求 hash 持久化；Native 在同一游戏 Tick 比较全部预期容器和槽位，任一差异都在写前全单拒绝；
4. 只减少普通静态物品堆叠；最后一件只有在动态 GUID/引用为空、腐化值可验证且清空后 `PalItemSlot.IsEmpty` 时才允许归零，动态、腐化或不可验证槽位直接拒绝；
5. Native 逐槽位回读、完整快照回读和按 ItemID 聚合回读，返回逐行 requested/actualConsumed、before/after 数量、稳定结果 ID 和 `persistenceVerified`；
6. Control API 先持久化 Native 回执，再验证每行 `actualConsumed == requestedQuantity`、前后差值、完整快照和聚合结果；只有全部成立才进入 `Removed`；
7. 钱包 credit、唯一账本和 `Removed -> Credited` 在同一 SQLite 事务提交，随后可幂等进入 `Settled`。

旧 Native dev36 曾声明 `inventory.consume.experimental` 且 `persistenceVerified=false`；dev37-ro 因离线库存边界缺陷已 `superseded/quarantined`。dev38-ro 的历史受控实服套件为 9 项非玩家成功、3 项 live-player probe 因无人在线拒绝、0 项意外失败，但该版本也已 superseded。当前 dev39-ro 只读源码/制品候选不声明任何 consume/write capability，因此生产门禁同样会拒绝结算；它只有双独立可复现构建证据，尚未实服运行。之后只有 dev39-ro 固定套件、在线玩家三项、PalDefender 组合和独立复核通过，再另行评审写候选，并由真实玩家完成“扣物 → 保存 → 停服 → 重启 → 重新登录”验收后，才能声明稳定 `inventory.consume`。完整 Bridge 请求/响应见 [`inventory.consume.md`](../../packages/contracts/bridge/inventory.consume.md)。

派发中断、回执未持久化、回滚未证明、逐行/快照/聚合证据不完整或持久化未证明都进入 `Uncertain`，不能自动重发、自动退款或入账；恢复 worker 只有读取到已持久化的成功 Native 回执时才继续唯一 credit，不会再次扣物。

### 6.3 旧 RCON 诊断路径

`delitems` 只保留给隔离的显式诊断配置：宿主必须为 `Development`，同时满足 `Security:DevelopmentMode=true`、`PlayerPortal:PublicSteam=false`、RCON 已启用、`ExtractionMode:Rcon:AllowDevelopmentSettlement=true` 且 `RequireNativeForResourceExchange=false`。任一条件不满足都选择 Native 并 fail-closed；Production 无论配置如何都不会在 Native 断开或能力不足时 fallback 到 RCON。`/clearinv` 和任意 RCON 文本从未进入公开接口。

管理员应通过 `GET /api/v1/extraction/admin/settlement/status` 检查当前结算适配器。响应固定包含 `adapter`、`enabled`、`connected`、`outcome` 和 `error`；`outcome` 只可能是 `success`、`failed` 或 `uncertain`，错误使用通用结算错误码与描述，不向客户端泄露 RCON 密码、Named Pipe 名称或原始命令。旧 `/api/v1/extraction/admin/rcon/status` 只作为已废弃别名保留。

## 7. 错误码

| 错误码 | HTTP | 语义 |
| --- | --- | --- |
| `SEASON_WORLD_UNBOUND` / `SEASON_WORLD_MISMATCH` | 423 | 当前周档未绑定真实世界，或当前世界不一致 |
| `PURCHASE_CIRCUIT_OPEN` / `RESOURCE_EXCHANGE_CIRCUIT_OPEN` | 423 | 对应经济写闸门由运营熔断关闭 |
| `PLAYER_BINDING_REQUIRED` | 409 | 当前周档未绑定完整 PlayerUID |
| `PLAYER_NOT_ONLINE` | 409 | 游戏写入只支持在线玩家 |
| `EXTRACTION_ZONE_CLOSED` | 409 | 当前报价对应兑换区已关闭且不在 grace 内；存在后续窗口时 `ApiError.nextOpensAt` 给出最早安全重试时间 |
| `PLAYER_OUTSIDE_EXTRACTION_ZONE` / `EXTRACTION_ZONE_NOT_STABLE` | 409 | 不在开放兑换区，或两次位置采样不稳定 |
| `OFFER_NOT_AVAILABLE` | 409 | 商品下架、过期或未发布 |
| `PURCHASE_LIMIT_EXCEEDED` | 409 | 超过个人周限购 |
| `INSUFFICIENT_FUNDS` | 409 | 钱包不足 |
| `IDEMPOTENCY_CONFLICT` | 409 | 同键不同请求，或 run 已绑定另一个结算键 |
| `EXTRACTION_QUOTE_NOT_SETTLEABLE` | 409 | 报价已过期或不处于 `Quoted`；run 过期时持久化 `errorCode=QUOTE_EXPIRED` |
| `QUOTE_CONTENT_CHANGED` | 409 | 报价后的 current 内容版本或 hash 已切换，必须重新扫描 |
| `EXTRACTION_INVENTORY_CHANGED` | 409 | 背包与报价不一致，需重新扫描 |
| `NO_SELLABLE_EXTRACTION_LOOT` | 422 | 当前允许容器中没有可出售的白名单资源 |
| `NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED` | 503 | Native Bridge 未连接 |
| `NATIVE_INVENTORY_CONSUME_CAPABILITY_MISSING` | 503 | 未声明稳定 `inventory.consume`；experimental 不满足 |

`NATIVE_INVENTORY_CONSUME_EVIDENCE_INVALID`、传输不确定和发货不确定不是上述端点的 HTTP 202 错误契约：请求可能返回 HTTP 200 的 run/order 投影，但状态保持 `Uncertain` 或对应发货待对账状态，并在投影中携带稳定 `errorCode`。客户端必须读取状态，禁止因 HTTP 成功就重发写入。
