# 玩家通知投影、浏览器提醒与异常核对手册

本文说明方案 A 玩家通知的权威来源、去重方式、隐私边界和故障处置。通知系统是经济状态的**只读投影**：它不会购买商品、扣除资源、入账、退款或重试任何经济动作。

## 已实现范围

通知契约版本为 `schemaVersion=1`，有四类来源：

| `sourceType` | 权威来源 | 成功条件 | 非成功终态 |
| --- | --- | --- | --- |
| `order-delivery` | 商城订单、结构化发货回执 | 只有订单 `Delivered` 才显示“已送达” | `failed`、`refunded` 独立显示；不冒充送达 |
| `resource-settlement` | 资源兑换 run 与唯一账本 credit | 只有 run `Settled` 才显示“已结算” | `failed`、`cancelled`、`expired` 独立显示 |
| `season-end` | 冻结周榜、season job、权威钱包 ledger | 冻结、永久币完成、周券过期分别推进同一条通知 | 未完成时只显示当前已证明阶段 |
| `reconciliation` | `partial`/`uncertain` 或个人周结算证据异常 | 不存在推测成功 | 明确提示“不要重复购买或结算，等待人工核对” |

订单和兑换异常使用独立事件键保留历史；之后人工核对为成功时会新增被权威终态证明的成功通知，不会覆盖掉此前的异常证据。一个周档的冻结、奖励完成与周券过期则共享同一个 `sourceEventKey` 和通知 ID，只更新版本，不产生三条重复的站内消息。

## 持久性与重放

`PlayerNotificationStore` 在 `extraction-commerce.db` 中维护：

- `player_notifications`：当前玩家通知、阅读时间和游戏内投递状态；
- `player_notification_events`：来源更新、阅读和游戏内状态变化的追加审计；
- `economy_schema_migrations` 中的 `player-notifications/1` 迁移记录。

通知 ID 由 `sourceEventKey` 确定性生成。相同来源版本重放 20 次仍只有一条记录；新版本更新原记录并重新变为未读。游戏内投递明确启用时，写入通知后、调用游戏内队列前崩溃会留下 `pending`，重启后使用相同创建/派发幂等键恢复；不会触碰经济事务。

## 游戏内投递的升级安全默认

站内 feed 与游戏内投递是两个独立边界。所有仓库配置和 Windows 部署样例都显式保持：

```json
{
  "PlayerNotifications": {
    "GameDeliveryEnabled": false
  }
}
```

关闭时 worker 仍回放订单、兑换、周档和对账来源，新增或更新站内通知，但把该来源版本的游戏内状态持久化为 `not-requested`，且绝不调用游戏内 dispatcher。之后改为 `true` 并重启 Control API，也不会把同一个历史 `sourceVersion` 从 `not-requested` 改成 `pending`；只有启用后权威来源产生的新版本才请求投递。这避免首次升级扫描已有订单和周档时批量发送旧消息。

安全启用顺序：

1. 升级后先保持 `false`，启动服务并让通知投影完成历史 feed 回填；抽查四类来源及异常提示。
2. 验证固定游戏/MOD 组合的 Native probe 确实支持 `players` audience、`player-message` preset 和预期参数；同时检查命令审计、版本门禁和目标玩家隔离。
3. 在外置配置中把该值改为 `true` 并重启 Control API。不要通过直接改 SQLite 的 `game_state` 来“补发”历史消息。
4. 用启用后新产生的一条测试来源版本验证 `pending → queued/sent`；旧的 `not-requested` 行应保持不变。

若再次设为 `false`，投影仍会更新站内 feed，但运行进程必须重启后才读取新的选项。关闭期间产生的新来源版本都会保持 `not-requested`，之后重新开启也不会回扫补发这些相同版本。

## 玩家自助接口

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| `GET` | `/api/v1/player/me/notifications?limit=50` | 本人 feed、未读数和是否存在待投递提醒 |
| `POST` | `/api/v1/player/me/notifications/{notificationId}/read` | 本人单条标记已读，幂等 |
| `POST` | `/api/v1/player/me/notifications/read-all` | 本人全部标记已读 |

账户只从 HttpOnly 玩家会话解析。`accountId/userId/steamId/playerUid` 及蛇形别名查询覆盖会被拒绝；其他账户的通知 ID 在标记已读时与不存在完全相同。写接口要求允许的 `Origin` 和会话 CSRF token。响应不包含 account ID、平台 subject、PlayerUID、游戏目标 ID、原始 source ID 或 source event key。

## 浏览器系统提醒

站内消息无需浏览器权限，始终可用。系统级 Notification API 遵循以下规则：

1. 页面加载和轮询绝不自动请求权限；玩家必须在“消息中心”点击“启用浏览器提醒”。
2. `denied` 或浏览器不支持时，页面显示安全降级说明，不循环弹权限，也不影响站内 feed。
3. 浏览器本地只保存 `enabled/disabled` 偏好和最多 100 个不透明通知 UUID；不保存标题、正文、账户、SteamID 或 PlayerUID。
4. 每次最多展示 3 条新未读消息，已展示 UUID 在本次会话和本地有界集合中去重。
5. 网络轮询只在页面可见，并且满足“当前就在消息页、存在未读、游戏内投递活跃或经济动作处理中”之一时运行；无活动后停止。

## 游戏内提醒的诚实状态

只有 `PlayerNotifications:GameDeliveryEnabled=true` 时，投影适配器才使用已有 `InGameNotificationStore` 与 `InGameNotificationCommandQueue`。目标 audience 固定为 `players`，只传平台玩家标识，绝不传 PlayerUID。它要求 Native 能力探针同时声明：

- `players` audience；
- 专用 `player-message` preset；
- `title` 与 `message` 参数契约。

当前游戏构建若只提供全服原生 UI preset，系统会把站内记录标为 `blocked`，不会借用含义错误的“Boss 奖励/困难模式”等 UI 冒充玩家消息，也不会宣称游戏内已送达。`queued` 只表示队列已接受；`sent` 只表示 Native 已提交客户端 RPC，现有原生通道没有逐客户端 ACK；`uncertain` 后不会自动重发。

## 故障处置

- `pending` 长时间不变：检查 Control API hosted worker 和游戏通知队列是否启动；恢复服务后相同幂等键可继续。
- `blocked`：检查 Native probe 是否确实支持 `players + player-message`。在能力不存在时无需处理经济数据，玩家仍能使用站内消息。
- `failed`：查看脱敏 command audit 的固定错误码。不要因此重跑购买、发货、兑换、退款或周结算。
- `uncertain`：保持不重发；先核对原生命令审计和玩家实际界面。无逐客户端 ACK 时不能手工改成 delivered。
- `reconciliation`：只处理该通知所属账户和来源。先查订单回执/run/ledger，再走已有管理员人工对账流程；禁止让玩家重复提交来“验证”。

## 自动化证据

```powershell
./tests/integration/player-notifications-smoke.ps1
npm run test:player
$env:PLAYWRIGHT_USE_SYSTEM_CHROME='1'; npm run test:player:e2e
```

后端 harness 覆盖四类来源、20 次重放、SQLite 重启、默认关闭零 dispatcher 调用、关闭期间的新建/更新均为 `not-requested`、开启不回放同版本旧消息、开启后的新版本投递、通知写入后的崩溃窗口、同周更新、成功终态门禁、A/B feed 与标记已读隔离、身份覆盖拒绝及响应无目标标识。玩家单测覆盖显式 opt-in、拒绝/不支持降级、本地不保存正文和可见性/活动轮询门禁；真实系统 Chrome 覆盖通知页、阅读、授权、375×812 布局以及 Axe WCAG A/AA 严重/致命规则。
