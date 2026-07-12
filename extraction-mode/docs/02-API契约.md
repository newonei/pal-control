# 02：API 契约

本文定义玩法模块的目标契约。路径 `/extraction/v1` 属于新增玩家门户，不等同于现有 `/api/v1` 运营控制 API。

## 当前开发服已落地接口

当前实现位于只监听本机的 Control API，供 `http://127.0.0.1:5174/` 运营控制台使用。它尚无玩家公网认证，因此不得直接暴露到外网：

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/api/v1/extraction/overview?userId=...` | 当前周档、双货币余额与撤离统计 |
| GET | `/api/v1/extraction/catalog?userId=...` | 每日确定性轮换价、周限购与商品发放摘要 |
| POST | `/api/v1/extraction/orders` | 创建幂等订单；Body 为 `userId/productId/quantity` |
| GET | `/api/v1/extraction/orders?userId=...` | 订单与 PalDefender 发货状态 |
| GET | `/api/v1/extraction/ledger?userId=...` | 钱包账本 |
| POST | `/api/v1/extraction/runs/quote` | 连续位置采样、背包白名单扫描和 30 秒报价 |
| POST | `/api/v1/extraction/runs/{runId}/settle` | 白名单 RCON 扣物、REST 证明和唯一入账 |
| GET | `/api/v1/extraction/runs?userId=...` | 撤离记录与结算 capability |
| POST | `/api/v1/extraction/admin/wallet-adjustments` | 本机管理员幂等调账 |
| POST | `/api/v1/extraction/admin/orders/{orderId}/reconcile` | 维护状态下把 uncertain 发货确认为 `delivered` 或幂等 `refund` |
| POST | `/api/v1/extraction/admin/runs/{runId}/reconcile` | 维护状态下把 uncertain 撤离确认为 `failed` 或唯一入账 `settled` |
| GET | `/api/v1/extraction/admin/rcon/status` | 仅本机 RCON 协议/认证探针 |
| POST | `/api/v1/extraction/admin/rollover/maintenance` | 关闭或开放商城与撤离写入 |
| GET | `/api/v1/extraction/admin/rollover/preflight` | 停服前核对赛季到期时间、当前世界与目标周窗口 |
| GET | `/api/v1/extraction/admin/rollover/readiness` | 检查未完成/不确定订单和撤离 |
| POST | `/api/v1/extraction/admin/rollover/commit` | 核对当前物理世界后，首次绑定当前周档或关闭过期周档并创建新赛季；同 worldId 幂等 |

`orders`、`runs/{id}/settle`、管理员调账都要求 `Idempotency-Key`。购买和撤离还会验证经济赛季 worldId 与 `GameUserSettings.ini`、活动存档目录、官方 REST world GUID 三者一致；周窗口到期不会自动开空 worldId 赛季，而是保持写入关闭直到受控换档提交。当前返回形状以 [前端 DTO](../../apps/console-web/src/features/extraction/api.ts) 与 [端点实现](../../services/control-api/Infrastructure/ExtractionModeEndpoints.cs) 为准；下文 `/extraction/v1` 是完成 Steam 登录、HTTPS、CSRF 和账户隔离后的正式玩家门户目标。

## 1. 公共规则

- 玩家接口只接受 HTTPS，并使用服务端 Session Cookie；不在 URL、localStorage 或返回体中暴露上游 token。
- 所有 POST 要求 `Idempotency-Key`，长度 8–128 个无控制字符。
- 同一账户、同一 key、相同规范请求返回原资源；同 key 不同请求返回 `409 IDEMPOTENCY_KEY_REUSED`。
- 所有响应含 `requestId`；时间是 RFC 3339 UTC；金额和数量是 JSON 整数。
- 客户端不得提交价格、余额、PlayerUID、UserId、ItemID 或撤离总额作为权威值。
- 写请求要求 CSRF header；购买和撤离按账户与 IP 限流。

统一错误：

```json
{
  "code": "PLAYER_NOT_IN_EXTRACTION_ZONE",
  "message": "当前玩家未通过撤离区连续位置校验。",
  "requestId": "01J...",
  "retryable": false,
  "details": null
}
```

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
    "displayName": "新医"
  },
  "gates": {
    "purchases": true,
    "extractions": true,
    "reason": null
  },
  "requestId": "01J..."
}
```

响应不向普通前端暴露完整 `PlayerUID`。未绑定时 `player.bound=false`，商城和撤离写入关闭。

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

### `GET /extraction/v1/shop`

返回当前已发布 rotation 和 offer 快照：

```json
{
  "rotation": {
    "businessDate": "2026-07-12",
    "expiresAt": "2026-07-12T20:00:00Z",
    "version": "sha256:..."
  },
  "offers": [
    {
      "id": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19",
      "name": "基础弹药箱",
      "description": "本周战备",
      "currency": "supply_ticket",
      "unitPrice": 80,
      "grantQuantity": 50,
      "remainingLimit": 3
    }
  ],
  "requestId": "01J..."
}
```

不返回内部 Palworld ItemID，避免客户端伪造发货内容。

### `POST /extraction/v1/orders`

Header：`Idempotency-Key: order-<uuid>`、CSRF token。

```json
{
  "lines": [
    { "offerId": "99dc09ee-fbea-49e5-b69b-9d3888fa6e19", "quantity": 1 }
  ]
}
```

服务端重新读取 offer、限购、绑定、在线状态和钱包；在一个事务中创建订单、扣款分录和发货 outbox。返回 `202 Accepted`：

```json
{
  "orderId": "2a3b9452-9e95-44cf-b94a-910cf9ca0cca",
  "state": "funds_debited",
  "total": { "currency": "supply_ticket", "amount": 80 },
  "statusUrl": "/extraction/v1/orders/2a3b9452-9e95-44cf-b94a-910cf9ca0cca",
  "requestId": "01J..."
}
```

MVP 一张订单只能使用一种货币，最多 10 行，每行数量 1–99，最终限制以商品为准。

### `GET /extraction/v1/orders/{orderId}`

只有订单所有者可读。状态：

- `funds_debited`：扣款与 outbox 已提交；
- `delivery_queued` / `delivery_dispatched`：等待游戏通道；
- `fulfilled`：PalDefender 明确确认发放；
- `failed_refunded`：派发前或明确失败，已产生反向分录；
- `delivery_uncertain`：可能已发放，冻结人工对账，不能重复购买同一订单；
- `manual_review`：自动证据不足。

```json
{
  "orderId": "2a3b9452-9e95-44cf-b94a-910cf9ca0cca",
  "state": "fulfilled",
  "lines": [
    { "name": "基础弹药箱", "quantity": 1, "grantedQuantity": 50 }
  ],
  "total": { "currency": "supply_ticket", "amount": 80 },
  "createdAt": "2026-07-12T03:00:00Z",
  "completedAt": "2026-07-12T03:00:01Z",
  "requestId": "01J..."
}
```

客户端轮询间隔不得低于 1 秒，并在终态停止；后续可换成 SSE。

## 4. 撤离

### `GET /extraction/v1/extraction-zones`

返回当前开放区域的名称、开放时间和用于展示的粗略位置。精确半径仍由服务端判断。

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

### `POST /extraction/v1/extractions/{extractionId}/commit`

Header：新 `Idempotency-Key: extract-<uuid>`。

```json
{ "snapshotHash": "sha256:..." }
```

服务端必须重新检查：所有权、报价未过期、赛季开放、玩家在线且仍在区域、背包快照完全一致。通过后创建消费 outbox，返回 `202`：

```json
{
  "extractionId": "2b559ccb-8ae3-46a1-95f9-07fb3cd29445",
  "state": "consume_queued",
  "statusUrl": "/extraction/v1/extractions/2b559ccb-8ae3-46a1-95f9-07fb3cd29445",
  "requestId": "01J..."
}
```

### `GET /extraction/v1/extractions/{extractionId}`

状态：`quoted/consume_queued/consuming/consume_uncertain/consumed/crediting/completed/rejected/manual_review`。

只有 `completed` 表示扣物和入账均已证明。`consume_uncertain` 时 UI 必须显示“不要再次撤离或移动相关物品，等待对账”，不能提供“重试扣除”按钮。

## 5. 运营 API

运营接口放在现有内网控制面或独立 `/api/v1/servers/{serverId}/extraction-admin` 路由，要求明确 RBAC；不与玩家 Cookie 共享认证。

建议契约：

| 方法和路径 | 权限 | 作用 |
| --- | --- | --- |
| `GET /status` | `extraction.read` | 当前赛季、世界身份、闸门、队列积压 |
| `POST /gates` | `extraction.admin` | 关闭/开放购买与撤离，必须填 reason |
| `POST /daily-refreshes/{businessDate}/prepare` | `shop.manage` | 生成草稿和 diff |
| `POST /daily-refreshes/{businessDate}/publish` | `shop.publish` | 发布一次，幂等 |
| `GET /reconciliation?state=uncertain` | `extraction.reconcile` | 查询不确定订单和撤离 |
| `POST /orders/{id}/reconcile` | `extraction.reconcile` | 基于证据终结，不直接再次发货 |
| `POST /extractions/{id}/reconcile` | `extraction.reconcile` | 标记已扣/未扣/人工处置并附证据 |
| `POST /wallet-adjustments` | `wallet.adjust` | 追加调整分录；建议双人审批 |
| `POST /rollovers/prepare` | `season.manage` | 换档预检，不改游戏 |
| `POST /rollovers/{id}/execute` | `season.manage` | 执行受控换档 |
| `GET /rollovers/{id}` | `season.read` | 查看阶段和证据 |
| `POST /rollovers/{id}/abort` | `season.manage` | 仅在开放新赛季前回滚 |

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

### 6.2 MVP：本机白名单 RCON 消费适配器

建议 Control API 路径：

```http
POST /api/v1/servers/local/players/{userId}/inventory/consumptions
Idempotency-Key: extraction:<extractionId>:consume:1
```

```json
{
  "expectedSnapshotHash": "sha256:...",
  "reason": "撤离 2b559ccb-8ae3-46a1-95f9-07fb3cd29445 回收战利品",
  "dryRun": false,
  "items": [
    {
      "itemId": "Ingot",
      "amount": 20
    }
  ]
}
```

Control API 不接受原始命令文本。适配器从经过目录白名单的 DTO 构造唯一一条：

```text
/delitems <UserId> ItemId:Amount ItemId:Amount ...
```

`userId` 必须是规范平台 UserId，不允许昵称。ItemID 只能由服务端报价行产生并通过严格目录匹配，amount 必须是正整数；限制条目数、单项数量和总数量。`/clearinv` 不在自动接口白名单中。

适配器执行顺序：

1. 验证 RCON 已启用且安全预检通过：只由本机连接、没有公网端口映射、防火墙阻断远程入站、密码来自 Secret Store。
2. 通过 PalDefender REST 重新读取六类背包；规范 hash 必须等于 `expectedSnapshotHash`，并验证每个 ItemID 数量充足且只属于允许结算范围。
3. 在任何 socket 写入前持久化消费记录；紧接写入前标记 `dispatched`。只有明确未连接且写入 0 字节时才允许安全失败。
4. 发送一条构造后的 `/delitems`；RCON 文本返回仅作传输证据，不能直接把操作判为成功。
5. 通过 PalDefender REST 做有界后读，比较目标 ItemID 在完整六类背包中的总量差值。
6. 每个目标数量都精确减少 `amount`，且没有从允许容器转移到排除容器时，才返回 `succeeded`。
7. 部分减少、超额减少、后读失败、连接在写入后中断或证据冲突，一律返回 `COMMAND_OUTCOME_UNCERTAIN`。

RCON 没有服务端幂等键。Control API 的 `Idempotency-Key` 只能阻止本地重复创建命令；一旦状态到达 `dispatched`，相同或新 key 都不得自动再次发送，只能 REST 回读和人工对账。

响应示例：

```json
{
  "state": "succeeded",
  "adapter": "paldefender-rcon-delitems",
  "beforeSnapshotHash": "sha256:...",
  "afterSnapshotHash": "sha256:...",
  "consumed": [
    { "itemId": "Ingot", "amount": 20, "beforeTotal": 35, "afterTotal": 15 }
  ]
}
```

### 6.3 长期增强：Native `inventory.consume`

长期可以在不改变玩家 API、账本或状态机的前提下，把 `IInventoryConsumePort` 后端替换为 Native。目标能力是在一个游戏 Tick 中先验证全部容器/槽位/ItemID/数量和 revision，再批量扣减、允许归零、调用原生结算并逐槽位回读。

Native 后端能减少 RCON 的文本响应、非事务批量和容器定位限制，但仍须保留 after snapshot 和 `uncertain` 语义；不能把任意反射函数名或任意内存写入暴露给玩法模块。

无论后端是哪一种，只要派发后无法证明全部结算，就返回 `COMMAND_OUTCOME_UNCERTAIN`，不可自动重发。

## 7. 错误码

| 错误码 | HTTP | 语义 |
| --- | --- | --- |
| `SEASON_NOT_OPEN` | 409 | 当前无开放周档 |
| `ECONOMY_GATE_CLOSED` | 503 | 运营或健康熔断关闭写入 |
| `PLAYER_NOT_BOUND` | 409 | 当前周档未绑定 |
| `PLAYER_OFFLINE` | 409 | 游戏写入只支持在线玩家 |
| `PLAYER_SESSION_CHANGED` | 409 | 报价后角色会话变化 |
| `PLAYER_NOT_IN_EXTRACTION_ZONE` | 409 | 未通过连续位置校验 |
| `OFFER_NOT_AVAILABLE` | 409 | 商品下架、过期或未发布 |
| `PURCHASE_LIMIT_EXCEEDED` | 409 | 超过日/周限购 |
| `INSUFFICIENT_FUNDS` | 409 | 钱包不足 |
| `IDEMPOTENCY_KEY_REUSED` | 409 | 同键不同请求 |
| `QUOTE_EXPIRED` | 409 | 撤离报价过期 |
| `INVENTORY_SNAPSHOT_CHANGED` | 412 | 背包与报价不一致 |
| `NOTHING_TO_EXTRACT` | 409 | 无可售战利品 |
| `INVENTORY_CONSUME_UNAVAILABLE` | 503 | 当前扣物适配器不可用或安全预检未通过 |
| `RCON_DISABLED` | 503 | RCON 未启用；默认保持关闭 |
| `RCON_SECURITY_CHECK_FAILED` | 503 | 回环连接、防火墙、端口映射或 Secret 配置未通过 |
| `RCON_RESPONSE_UNPROVEN` | 202 | 文本响应或 REST 后读不足以证明精确扣物 |
| `DELIVERY_OUTCOME_UNCERTAIN` | 202 | 发货结果待对账；订单资源仍可查询 |
| `CONSUME_OUTCOME_UNCERTAIN` | 202 | 扣物结果待对账，不入账 |
| `VERSION_COMBINATION_UNAPPROVED` | 503 | 游戏/PalDefender/RCON 适配器（及启用的 MOD）版本未验收 |
| `WORLD_IDENTITY_MISMATCH` | 503 | 运行世界与开放赛季不一致 |
