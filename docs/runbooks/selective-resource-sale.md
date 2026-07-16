# 选择性资源出售运行手册

本手册适用于方案 A“周世界资源经济服”的资源选择、恢复和对账。它不改变 Production 只接受经真实持久化验收的 Native `inventory.consume` 规则。

## 正常流程

1. `POST /api/v1/player/me/runs/quote` 扫描允许容器，创建包含全部合格白名单资源、完整背包快照、内容版本和动态事件证据的短时源报价。
2. 页面默认全选；玩家可取消勾选或把数量调低到 `1..quotedQuantity`，实时核对种类、件数和价值。
3. `POST /api/v1/player/me/runs/{sourceRunId}/select` 携带 CSRF、`Idempotency-Key` 和仅含 `sourceRevision/items` 的 JSON。玩家身份、账户与赛季只取 HttpOnly 会话。
4. 服务端在同一 RunStore 临界区验证所有者、赛季、状态、到期时间、revision、current content pointer、ItemID/数量与安全上限，再用一次持久快照同时取消源报价并创建所选子报价。
5. 结算只针对子报价。Native 保留源报价的完整 `quoteSnapshotHash/nativeInventorySnapshot` 作为乐观锁，但 `inventory.consume.items` 只允许所选行；未选物品不会进入消费授权。Development RCON 诊断同样只生成所选行命令。

## 幂等与恢复

- 选择键绑定规范化选择、账户、赛季、源报价与源 revision。ItemID 大小写或顺序不改变同一逻辑请求；完全相同的请求跨响应丢失、刷新和进程重启返回同一个子 `runId`。
- 同键换选择、账户、赛季、源报价或 revision 返回 `409 IDEMPOTENCY_CONFLICT`，不改变源或子报价。不要用新键“碰碰运气”。
- 浏览器在 `sessionStorage` 中只保存当前登录用户的未完成选择、选择键和结算键；过期、损坏或其他用户的记录会被丢弃。首次选择响应丢失时刷新页面，复核后复用原键。
- 源报价一旦派生子报价就处于 `Cancelled`，不能再结算。子报价继承原到期时间；响应恢复不会延长报价。

## 拒绝条件

以下情况均应在扣物、入账和 run 变更前拒绝：空选择、重复或非法 ItemID、未知行、零/负数、单行/总量/行数超过安全上限、超过报价数量、错误所有者/赛季、过期、非 `Quoted`、旧 revision、current content pointer 已变化。玩家接口还必须拒绝 Body/Query 中的 `userId/accountId/seasonId` 覆盖。

## 并发和故障判断

- 选择与源报价结算共享同一持久 CAS 临界区。竞争结束只能是“源 `Cancelled` + 唯一子报价”或“源 `Consuming` + 无子报价”；两者同时成功属于 P0 故障。
- 选择持久化失败必须保持源 `Quoted` 且无子报价。不能依赖先写内存、后补磁盘。
- Native `expectedContainers` 缺少源完整快照，或消费 `items` 含未选行，立即关闭资源兑换并保留脱敏请求/回执证据。
- `Consuming/Uncertain` 仍按主结算 saga 处置；不得因为“只选了一部分”而按比例自动入账、自动补扣或重发。

## 值班核对

1. 用原选择键重放，确认返回原子 `runId`；核对源 `SelectedChildRunId`、子 `SourceQuoteRunId/SourceQuoteRevision` 和冻结内容/事件证据。
2. 核对 Native 完整 before/after 快照，同时确认 `actualConsumed` 精确等于子报价行，未选 ItemID 没有进入授权列表或发生额外差值。
3. 核对只有子 `runId` 对应一笔唯一 `extraction_credit`，源报价没有账本分录。
4. 无法证明全部所选行的最终结果时保持 `Uncertain` 并关闭该账户经济写入；证据不足不得猜测成功或失败。

自动化覆盖不替代真实 Palworld 验收。开放 Production 前仍必须在固定游戏/UE4SS/MOD 版本上完成“所选扣物 → 保存 → 停服 → 重启 → 重登”，并证明未选资源保留、相同 Native/Control API 键不重复扣物或入账。
