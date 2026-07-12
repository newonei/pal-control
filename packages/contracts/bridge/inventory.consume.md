# `inventory.consume` 原子消费契约

`inventory.consume` 是只减不增的游戏线程命令，用于撤离结算。它不能创建物品、不能写入任意目标数量，也不能操作装备、护甲或关键物品容器。

当前能力标记：

- `inventory.consume.experimental`：命令已实现并通过编译，但尚未完成真实服务器保存/重启持久化验证，因此不宣告稳定能力。
- `inventory.consume.partial-stack-only`：当前构建没有经过验证的原生清槽函数；任何会使槽位数量变成 `0` 的请求都会在写入前整体拒绝。

## 请求

命令 envelope 的 `operation` 为 `inventory.consume`。`payload` 只允许以下三个字段，未知字段会被 Native Mod 拒绝：

```json
{
  "ownerPlayerId": "0a22244b-0000-0000-0000-000000000000",
  "items": [
    { "itemId": "Leather", "quantity": 5 }
  ],
  "expectedContainers": [
    {
      "containerKind": "common",
      "containerId": "11111111-1111-1111-1111-111111111111",
      "slots": [
        { "slotIndex": 0, "itemId": "Leather", "quantity": 20 },
        { "slotIndex": 1, "itemId": "None", "quantity": 0 }
      ]
    },
    {
      "containerKind": "dropSlot",
      "containerId": "22222222-2222-2222-2222-222222222222",
      "slots": []
    },
    {
      "containerKind": "food",
      "containerId": "33333333-3333-3333-3333-333333333333",
      "slots": []
    }
  ]
}
```

约束：

- `ownerPlayerId` 必须是完整的本档 PlayerUID，不接受 8 位前缀。
- `items` 为本次唯一允许扣除的物品白名单；物品 ID 必须唯一，数量必须为正数。
- `expectedContainers` 必须恰好包含 `common`、`dropSlot`、`food`，容器 ID 必须属于该玩家。
- 每个容器必须传入 `inventory.probe` 所见的完整槽位数组，而不是只传目标物品槽位。调用方把探针的 `staticItemId`/`stackCount` 映射为 `itemId`/`quantity`。
- Native Mod 在同一次 `UEngine::Tick` 中重新枚举对象并精确比较容器 ID、槽位数、槽位索引、物品 ID 和数量；任一处变化都会在写入前拒绝。
- 单次计划最多修改 128 个槽位；超过上限会在写入前拒绝，避免阻塞游戏 tick。
- 容器必须由 `PalItemContainerManager.TryGetContainer` 从玩家自身的 `PalContainerId` 唯一解析；不会沿用 `inventory.probe` 的“同 GUID 副本按内容分数选择”启发式。
- 新命令会在进入消费前验证 RFC 3339 deadline，并在进程内缓存最近 256 个 `serverId + operation + idempotencyKey` 结果；同键不同 hash/payload 会冲突，同键重放返回原结果。payload 上限为 256 KiB。跨服务器重启的最终幂等性仍必须由 Control API 持久化层负责。
- 当前版本只会把已占用槽位从 `N` 减到大于等于 `1`。如果完成请求必须清空某个槽位，返回 `INVENTORY_SLOT_CLEAR_UNSUPPORTED`，且不改动任何槽位。

## 成功证据

成功结果的 `data` 包含：

- `beforeRevision` / `observedRevision`；
- 每个物品的 `requestedQuantity`、`beforeQuantity`、`afterQuantity`、`actualConsumed`；
- 每个容器的前后 revision 和逐槽变化；
- `snapshotMatched=true`、`aggregateVerified=true`；
- 实际调用的 `PalItemContainer.OnUpdateSlotContent` 和 `PalPlayerInventoryData.OnUpdateInventoryContainer`。

Native Mod 同时使用反射槽位和 `PalItemContainer.GetItemStackCount` 回读。只有两种读法都证明实际差值等于请求值时才返回 `succeeded`。

这里的 `succeeded` 只证明当前进程内的实时容器差值、更新回调和完整 after 快照。当前结果明确返回 `persistenceVerified=false`：尚未通过“保存 → 停服 → 重启 → 重登”测试证明该变化会持久化，因此 experimental 能力不能直接作为正式经济入账终点。

写后验证失败时，Native Mod 会在同一 tick 恢复所有已改数量并再次触发更新回调：

- 回滚数量、原生聚合和回调均得到证明：`failed` + `INVENTORY_CONSUME_VERIFY_FAILED`；
- 任一回滚证据不能证明：`uncertain`。调用方不得自动重试，也不得发放货币。

## Control API 接入点

撤离链路应在 `POST /api/v1/extraction/runs/quote` 保存 Native `inventory.probe` 返回的三个完整容器快照，并在 `POST /api/v1/extraction/runs/{runId}/settle` 使用该持久化快照发送 `inventory.consume`：

1. 用平台 UserId 找到当前世界已加载玩家，再取完整 PlayerUID 和 Native 背包快照。
2. 报价只选择服务端价值表允许的物品；客户端不能提交或扩大消费清单。
3. 结算进入 `Consuming` 后，以持久化的 run idempotency key 发送一次 bridge 命令。
4. 开发验证阶段仅记录 `succeeded` 证据，不入账；完成保存/重启持久化测试并把 hello 能力提升为稳定 `inventory.consume` 后，才允许在每行 `actualConsumed == requestedQuantity` 时进入货币入账。
5. `failed` 不入账；快照冲突需要重新报价。
6. `uncertain` 不入账、不重试，进入人工对账。
7. 切换完成后禁用该结算路径的 RCON `delitems` fallback，否则 RCON 响应伪造/差值竞态仍然存在。

当前 Native 实现已经可编译，但 Control API 尚未切换到这个命令；在后端接入并完成真实服务器烟雾测试前，不能宣称 RCON P0 已闭环。
