# Native bridge protocol

撤离原子消费的严格 payload、证据与 Control API 接入规则见 [`inventory.consume.md`](./inventory.consume.md)。

当前 dev37-ro 使用双向 Windows Named Pipe：`\\.\pipe\pal-control.local.v1`。名称与允许连接的低权限 `NT SERVICE\PalControl.ControlApi` service SID 固定在 `dependencies.lock.json` 和受控构建参数中；只改 INI 或 Control API 配置不会重命名 Native 端。ACL 还允许 SYSTEM、Administrators 和对象 owner，远程客户端始终被拒绝。每帧为：

```text
uint32 little-endian payloadLength
payloadLength bytes UTF-8 JSON
```

连接建立后，模组必须把 `hello` 作为首帧且每条连接只能发送一次。hello 强制携带 game/Steam build、mod/协议、宿主 PalServer EXE、实际 PalControlNative DLL、实际 UE4SS DLL 的大小/SHA-256、runtime 验证位、write mode 和探针。Control API 从 pipe 句柄独立取得服务端 PID，以 `PROCESS_QUERY_LIMITED_INFORMATION` 解析并哈希该进程主 EXE；模块摘要由已绑定宿主的 Native 从实际加载路径读取，并与锁和批准配置比较。任一值不一致、断线后未重新 hello、畸形/半帧、关键类/字段探针失败或协议不兼容时均断开并关闭写能力。

当前已实现 `players.probe` 与 `players.schema`：Control API 发送 `command`，Native Mod 将命令放入有界队列，在 `UEngine::Tick` 游戏线程枚举 `PalPlayerState` 或读取反射元数据，并返回 `result`。身份映射只读取已验证类型的 `PlayerUId(Guid)`、`AccountName`、`PlayerId` 和 `PlayerNamePrivate`；返回值不包含指针、内存地址或属性偏移。

背包侧已实现 `inventory.schema`、`inventory.probe`、`inventory.read` 和 `inventory.mutate`。适配器按 `OwnerPlayerUId` 关联玩家，解析 common、dropSlot、essential、weaponLoadout、armor、food 六类容器，并返回 `SlotIndex`、`StaticItemId` 和 `StackCount`。同一容器 GUID 存在多个运行期副本时，优先选择已占用槽位最多的副本。数量写入当前仅开放 common、dropSlot 和 food 的已占用槽位：写入前核对物品 ID 与旧数量，随后依次调用 `PalItemContainer.OnUpdateSlotContent` 和 `PalPlayerInventoryData.OnUpdateInventoryContainer`，聚合数量校验失败时自动恢复旧值。装备、护甲、关键物品、新增物品和数量清零继续保持关闭。

帕鲁侧已实现 `pals.schema`、`pals.probe`、`pals.read`、`pals.skills.catalog` 和 `pals.mutate`。适配器只枚举已加载的 `PalIndividualCharacterParameter`，使用 `PalInstanceID` 作为稳定实例 ID，并按 `OwnerPlayerUId` 关联玩家。当前可写白名单包含 `NickName`、`IsFavoritePal`、被动技能替换和已掌握主动技能的装备切换；写入要求加载状态、匹配的 owner、精确 `expectedRevision`、修改原因和显式关闭 dry-run。所有写入最终由游戏原生 `OnRep_SaveParameter` 完成差异通知和 `SaveParameterMirror` 结算；Mirror 未同步或回读不一致时会自动回滚并拒绝成功。等级、Rank、个体值和未掌握主动技能仍保持只读。

技能侧通过 `pals.skills.catalog` 枚举当前版本的 `EPalWazaID`、主被动技能数据表，以及 `DT_SkillNameText` / `DT_SkillDescText` 中的游戏原生 `zh-Hans` 文本。目录条目返回稳定 ID、中文名称、描述、Rank、分类、正负面、内部标记和效果数组；网页默认隐藏没有正式中文或标记为不可展示的内部条目。在帕鲁快照中返回 `activeSkills.equipped`、`activeSkills.mastered` 与 `passiveSkills`。被动替换依次调用 `RemovePassiveSkill`、`AddPassiveSkill` 和 `OnRep_SaveParameter`，只接受目录中的唯一技能 ID；主动装备切换调用 `ClearEquipWaza`、`AddEquipWaza` 和 `OnRep_SaveParameter`，只允许当前已装备或 `MasteredWaza` 中的技能，最多 3 个。技能数组已纳入 revision，写后必须通过 SaveParameter、Mirror 和回读校验，否则执行原生回滚。

客户端浮层使用 `announcements.overlay.send`，且只有模组 hello 明确声明 `announcements.overlay.write` 时 Control API 才会开放。适配器在游戏线程筛选唯一、存活且具有 AuthorityGameMode 的 `PalGameStateInGame`，严格验证 `BroadcastServerNotice(FString)` 仍为 Palworld 预期的 `Reliable NetMulticast` 函数后才调用；类、对象、参数或函数标志不匹配时 fail closed。浏览器不能直接调用该命令，所有浮层仍先经过 Control API 的持久化幂等队列和逐渠道审计。

## 命令规则

- 同一个 `idempotencyKey + requestHash` 返回原命令结果。
- 同一幂等键对应不同 hash 时返回冲突。
- 命令超过 `deadline` 后不得进入游戏线程。
- IPC 线程只能反序列化并压入有界 MPSC 队列。
- UObject/AActor 查询、校验和修改只能发生在游戏线程。
- ACK 丢失时命令状态为 `uncertain`，先对账，禁止直接重放发放物品。
- result/event 中不得返回原生指针、内存地址或未经筛选的存档字段。
