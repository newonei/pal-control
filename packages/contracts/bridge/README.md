# Native bridge protocol

撤离原子消费的严格 payload、证据与 Control API 接入规则见 [`inventory.consume.md`](./inventory.consume.md)。

当前 dev39-ro 源码候选使用双向 Windows Named Pipe：`\\.\pipe\pal-control.local.v1`。名称与允许连接的低权限 `NT SERVICE\PalControl.ControlApi` service SID 固定在 `dependencies.lock.json` 和受控构建参数中；只改 INI 或 Control API 配置不会重命名 Native 端。ACL 还允许 SYSTEM、Administrators 和对象 owner，远程客户端始终被拒绝。该精确候选尚未受控加载，因此这里描述的是锁定协议，不是已观察到的 dev39-ro Bridge 会话。每帧为：

```text
uint32 little-endian payloadLength
payloadLength bytes UTF-8 JSON
```

连接建立后，模组必须把 `hello` 作为首帧且每条连接只能发送一次。hello 强制携带 game/Steam build、mod/协议、宿主 PalServer EXE、实际 PalControlNative DLL、实际 UE4SS DLL 的大小/SHA-256、runtime 验证位、write mode 和探针。Control API 从 pipe 句柄独立取得服务端 PID，以 `PROCESS_QUERY_LIMITED_INFORMATION` 解析并哈希该进程主 EXE；模块摘要由已绑定宿主的 Native 从实际加载路径读取，并与锁和批准配置比较。任一值不一致、断线后未重新 hello、畸形/半帧、关键类/字段探针失败或协议不兼容时均断开并关闭写能力。

dev39-ro 源码候选只声明 schema/probe/read/catalog 及 UI 诊断能力，`writeEnabled=false`，不发布 `inventory.consume`、任何 `*.mutate` 或 `*.send`。两个独立锁定构建均得到 893,440 字节、SHA-256 `c2dab9f9bfd3c47ac1a244139fb96ce1de6f598c4bce438ebddde96185063b34` 的字节一致 DLL；该精确制品尚未受控加载或运行固定探针套件，Bridge 可用性仍为 unknown。888,320 字节、SHA-256 `012f84929448321196734b0bdc1f1b6a899a6f7a0aa87564d99b6e4c40b868aa` 的 dev38-ro 历史制品曾取得 9 项非玩家成功、3 项无人在线拒绝、0 项意外失败，但现已 `superseded/quarantined`，不可作为 dev39-ro 证据。旧 dev36 是 experimental 历史，dev37-ro 因离线库存边界缺陷也已隔离。

当前已实现 `players.probe` 与 `players.schema`：Control API 发送 `command`，Native Mod 将命令放入有界队列，在 `UEngine::Tick` 游戏线程枚举 `PalPlayerState` 或读取反射元数据，并返回 `result`。身份映射只读取已验证类型的 `PlayerUId(Guid)`、`AccountName`、`PlayerId` 和 `PlayerNamePrivate`；返回值不包含指针、内存地址或属性偏移。

背包侧的 dev39-ro 源码候选声明 `inventory.schema`、`inventory.probe` 和 `inventory.read`。适配器按 `OwnerPlayerUId` 关联玩家，解析 common、dropSlot、essential、weaponLoadout、armor、food 六类容器，并返回 `SlotIndex`、`StaticItemId`、`StackCount` 与 `ownerOnline`。只有当前权威 live game world 中有效 `PalPlayerController` 引用的 `PalPlayerState` 才使对应库存标记 `ownerOnline=true`；缺失或 false 时 Control API 报价和结算都 fail-closed。probe 同时返回 `onlinePlayerCount` 与 `onlineInventoryCount`，无人在线时不得用持久离线库存伪造成功。上述 dev39-ro 行为仍待受控运行验证；`inventory.mutate` 仅是历史源码接口，在 dev39-ro 的构建、hello 与游戏线程三层关闭。

帕鲁侧的 dev39-ro 源码候选声明 `pals.schema`、`pals.probe`、`pals.read` 和 `pals.skills.catalog`。适配器只枚举已加载的 `PalIndividualCharacterParameter`，使用 `PalInstanceID` 作为稳定实例 ID，并按 `OwnerPlayerUId` 关联玩家。`pals.mutate` 及昵称、收藏、技能替换等写路径仅保留为历史源码接口，在当前候选完全关闭；不得从源码存在推断为已观察到的 hello capability。

技能侧通过 `pals.skills.catalog` 枚举当前版本的 `EPalWazaID`、主被动技能数据表，以及 `DT_SkillNameText` / `DT_SkillDescText` 中的游戏原生 `zh-Hans` 文本。目录条目返回稳定 ID、中文名称、描述、Rank、分类、正负面、内部标记和效果数组；网页默认隐藏没有正式中文或标记为不可展示的内部条目。在帕鲁快照中返回 `activeSkills.equipped`、`activeSkills.mastered` 与 `passiveSkills`。历史写源码曾以 `RemovePassiveSkill`、`AddPassiveSkill`、`ClearEquipWaza`、`AddEquipWaza` 和 `OnRep_SaveParameter` 实现技能替换、装备、Mirror 与回读校验；dev39-ro 不发布也不执行这些写路径。

客户端浮层在 dev39-ro 源码候选只声明 `announcements.overlay.probe` 等只读诊断；它可以校验唯一 authoritative GameState 与函数签名，但不会调用 `ProcessEvent`。精确 dev39-ro 运行结果仍待固定套件验证。历史 `announcements.overlay.send` 只有未来独立写候选的 hello 明确声明对应 write capability 且通过评审时才可能开放。浏览器不能直接调用 Bridge 命令。

## 命令规则

- 同一个 `idempotencyKey + requestHash` 返回原命令结果。
- 同一幂等键对应不同 hash 时返回冲突。
- 命令超过 `deadline` 后不得进入游戏线程。
- IPC 线程只能反序列化并压入有界 MPSC 队列。
- UObject/AActor 查询、校验和修改只能发生在游戏线程。
- ACK 丢失时命令状态为 `uncertain`，先对账，禁止直接重放发放物品。
- result/event 中不得返回原生指针、内存地址或未经筛选的存档字段。
