# `inventory.consume` 原子消费契约

`inventory.consume` 是只减不增的游戏线程命令，用于白名单资源兑换结算。它不能创建物品、不能写入任意目标数量，也不能操作装备、护甲或关键物品容器。

能力标记与当前隔离状态：

- 当前 `0.3.0-dev.39-ro` / 协议 1.1 源码/制品候选不宣传、也不执行任何 consume/write capability；本文件描述的命令源码由编译期开关隔离。报价库存还必须携带由权威在线玩家控制器证明的 `ownerOnline=true`，字段缺失或 false 均拒绝。该精确候选尚未实服加载或执行固定探针套件。
- `inventory.consume.experimental`：旧 dev36 和未来显式写候选可使用的非稳定标记；命令虽已实现，但尚未完成当前真实服务器保存/重启持久化验证，因此不能宣告稳定能力。

写能力源码支持清空普通静态资源槽位，因此后续候选不再声明 `inventory.consume.partial-stack-only`。清槽前会确认动态物品 GUID 为空、动态数据引用为空、腐坏进度为零；动态物品、正在腐坏或元数据不可证明的槽位会在任何写入前整体拒绝。dev39-ro 会在解析这些写操作之前返回 `NATIVE_WRITE_CAPABILITIES_QUARANTINED`。

## 请求

命令 envelope 的 `operation` 为 `inventory.consume`。`payload` 只允许以下三个字段，未知字段会被 Native Mod 拒绝：

```json
{
  "snapshotVersion": 1,
  "ownerPlayerId": "0a22244b-0000-0000-0000-000000000000",
  "items": [
    { "itemId": "Leather", "quantity": 5 }
  ],
  "expectedContainers": [
    {
      "containerKind": "common",
      "containerId": "11111111-1111-1111-1111-111111111111",
      "slots": [
        {
          "slotIndex": 0,
          "itemId": "Leather",
          "quantity": 20,
          "dynamicCreatedWorldId": "00000000-0000-0000-0000-000000000000",
          "dynamicLocalIdInCreatedWorld": "00000000-0000-0000-0000-000000000000",
          "hasDynamicItemData": false,
          "corruptionProgress": 0,
          "corruptionProgressBits": 0
        },
        {
          "slotIndex": 1,
          "itemId": "None",
          "quantity": 0,
          "dynamicCreatedWorldId": "00000000-0000-0000-0000-000000000000",
          "dynamicLocalIdInCreatedWorld": "00000000-0000-0000-0000-000000000000",
          "hasDynamicItemData": false,
          "corruptionProgress": 0,
          "corruptionProgressBits": 0
        }
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
- `snapshotVersion` 当前必须精确为 `1`。
- 每个容器必须传入 `inventory.probe` 所见的完整槽位数组，而不是只传目标物品槽位。调用方把探针的 `staticItemId`/`stackCount` 映射为 `itemId`/`quantity`，并原样保存两个动态 GUID、动态数据存在标记、腐坏进度及其 IEEE-754 bit pattern。
- Native Mod 在同一次 `UEngine::Tick` 中重新枚举对象并精确比较容器 ID、槽位数、槽位索引、物品 ID、数量和上述槽元数据；任一处变化都会在写入前拒绝。`corruptionProgressBits` 是精确比较依据，十进制 `corruptionProgress` 用于审计可读性且必须与 bit pattern 一致。
- 单次计划最多修改 128 个槽位；超过上限会在写入前拒绝，避免阻塞游戏 tick。
- 容器必须由 `PalItemContainerManager.TryGetContainer` 从玩家自身的 `PalContainerId` 唯一解析；不会沿用 `inventory.probe` 的“同 GUID 副本按内容分数选择”启发式。
- 新命令会在进入消费前验证 RFC 3339 deadline，并在进程内缓存最近 256 个 `serverId + operation + idempotencyKey` 结果；同键不同 hash/payload 会冲突，同键重放返回原结果。payload 上限为 256 KiB。跨服务器重启的最终幂等性仍必须由 Control API 持久化层负责。
- 显式写候选可把符合上述静态资源前置条件的槽位从 `N` 原子清空为 `None/0`，并通过 `PalItemSlot.IsEmpty`、完整槽快照和容器聚合量三重回读验证。若请求必须清空不符合条件的槽位，返回 `INVENTORY_SLOT_CLEAR_UNSUPPORTED`，且不改动任何槽位。

## 成功证据

成功结果的 `data` 包含：

- `beforeRevision` / `observedRevision`；
- 每个物品的 `requestedQuantity`、`beforeQuantity`、`afterQuantity`、`actualConsumed`；
- 每个容器的前后 revision 和逐槽变化；
- `snapshotMatched=true`、`aggregateVerified=true`；
- 实际调用的 `PalItemContainer.OnUpdateSlotContent` 和 `PalPlayerInventoryData.OnUpdateInventoryContainer`。
- `slotClearSupported=true`（仅表示当前进程内的受限静态槽清空实现可用，不代表跨重启持久性已验收）。

Native Mod 同时使用反射槽位和 `PalItemContainer.GetItemStackCount` 回读。只有两种读法都证明实际差值等于请求值时才返回 `succeeded`。

这里的 `succeeded` 只证明当前进程内的实时容器差值、更新回调和完整 after 快照。当前结果明确返回 `persistenceVerified=false`：尚未通过“保存 → 停服 → 重启 → 重登”测试证明该变化会持久化，因此 experimental 能力不能直接作为正式经济入账终点。

写后验证失败时，Native Mod 会在同一 tick 恢复所有已改数量并再次触发更新回调：

- 回滚数量、原生聚合和回调均得到证明：`failed` + `INVENTORY_CONSUME_VERIFY_FAILED`；
- 任一回滚证据不能证明：`uncertain`。调用方不得自动重试，也不得发放货币。

## Control API 接入点

资源兑换链路在 `POST /api/v1/extraction/runs/quote` 保存 Native `inventory.probe` 返回的三个完整容器快照，并在 `POST /api/v1/extraction/runs/{runId}/settle` 使用该持久化快照发送 `inventory.consume`：

1. 用平台 UserId 找到当前世界已加载玩家，再取完整 PlayerUID 和 Native 背包快照。
2. 报价只选择服务端价值表允许的物品；客户端不能提交或扩大消费清单。
3. 结算进入 `Consuming` 后，以持久化的 run idempotency key 发送一次 bridge 命令。
4. 开发验证阶段仅记录 `succeeded` 证据，不入账；完成保存/重启持久化测试并把 hello 能力提升为稳定 `inventory.consume` 后，才允许在每行 `actualConsumed == requestedQuantity` 时进入货币入账。
5. `failed` 不入账；快照冲突需要重新报价。
6. `uncertain` 不入账、不重试，进入人工对账。
7. Production 从配置、启动校验和运行时策略三层禁止 RCON `delitems` fallback，避免 RCON 响应伪造和差值竞态重新进入入账边界。

Control API 已在方案 A 结算路径接入该命令，并持久化 run 幂等键、完整报价快照和最终结果；Production 不会降级到 RCON `/delitems`。当前 dev39-ro 源码 hello 不发布任何 consume/write capability，Control API 还要求 `ownerOnline=true`、runtime EXE 身份与 `writeEnabled=true`，所以生产资源兑换门禁必然关闭。dev39-ro 只有 893,440 字节、SHA-256 `c2dab9f9bfd3c47ac1a244139fb96ce1de6f598c4bce438ebddde96185063b34` 的双独立可复现制品证据，尚未实服加载或运行固定套件。9 项非玩家只读成功、3 项无人在线拒绝、0 项意外失败属于现已 `superseded/quarantined` 的 dev38-ro 历史证据；官方保存和优雅关服也只证明历史维护流程。旧 dev36 仅为 experimental，dev37-ro 因离线库存边界缺陷也已隔离。只有 dev39-ro 固定套件、在线玩家三项与 PalDefender 组合经独立复核、再产生显式写候选，并由真实玩家完成“扣物 → 保存 → 停服 → 重启 → 重登”验收后，才可提升为稳定 `inventory.consume`。
