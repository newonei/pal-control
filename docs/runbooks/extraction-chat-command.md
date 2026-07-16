# 游戏内撤离点查询命令

基线版本：Palworld `v1.0.0.100427`、PalDefender `1.8.1`、PalControlNative
`0.3.0-dev.36`。

本机部署状态（2026-07-15）：运行 DLL、源码与模板均为
`0.3.0-dev.36`，DLL SHA-256 为
`6EF1DCD71DF9FFC3458A20560E10264F1F795753C2ADC8C2E109889A552DE44A`；Bridge
已连接，`UE4SS.log` 已确认公开聊天别名 Hook 注册成功。仍需由普通玩家在游戏内发送命令，完成
“只回复发起者”的人工可见性验收。

## 能力边界

Palworld 会在普通聊天广播之前把 `/` 前缀路由给管理员命令解析器，非管理员输入
`/撤离` 会收到 `You are not an Admin`，MOD 的广播 Hook 无法拦截这一条命令。PalDefender
1.8.1 也没有面向普通玩家的自定义命令注册或 alias 配置；`isChineseCmd` 只是旧版控制台
中文编码开关。因此公开指令使用 `!` 前缀，由 PalControlNative 实现，不能通过给玩家管理员
权限来规避。

MOD 使用 UE4SS 的 UFunction post hook 监听：

```text
/Script/Pal.PalGameStateInGame:BroadcastChatMessage
```

启动时必须同时证明以下反射结构，否则功能 fail-closed：

- 唯一参数为 `FPalChatMessage ChatMessage`；
- 结构中存在 `FString Message` 和 `FGuid SenderPlayerUId`；
- `PalPlayerController.PlayerState` 唯一映射到具有相同 `PlayerUId` 的在线玩家；
- `PlayerController:ClientMessage` 仍是可靠的 Client RPC，签名为
  `FString / FName / float`。

玩家可输入：

```text
!撤离
!extract
```

同时兼容直接输入 `撤离`、`extract`。`/撤离`、`/extract` 不是普通玩家指令。

命令只回复发起者，并返回构建期兼容主点：

- 撤离点名称：开发服撤离点；
- 地图坐标：`X 248, Y -504`；
- 半径：`100`；
- 路线：进入撤离圈，等玩家网页显示“已进入撤离区域”后再结算。

命令没有经济副作用，不创建报价、不扣物品、不写钱包或账本。单个玩家两秒内重复输入只会
回复一次，缓存最多 128 个玩家标识且会定期淘汰。

## 构建与部署验收

构建仍必须针对锁定的 UE4SS 提交 `c2ac246` 和游戏版本 `v1.0.0.100427`。部署 DLL
并重启 PalServer 后，在 `ue4ss/UE4SS.log` 中确认：

```text
[PalControlNative] Read-only public extraction chat aliases registered (!extract and non-slash Chinese equivalents).
```

若出现 `native chat, identity, or ClientMessage schema mismatch`，不要放宽签名检查；先对新游戏
版本重新做反射探针和专服实测。验收时使用普通非管理员玩家分别发送 `!撤离`、`!extract`，
确认仅发起者收到两行提示，其他玩家无私信，并核对商城余额、订单数、撤离 run 和账本均无变化。

当前提示是 MOD 的构建期只读副本，只能作为 `ExtractionZones[0]` 的快速路线提示；它不读取内容 current pointer，也不表示该点是今日热点。玩家门户地图才是多兑换点、开放窗口、下一开放时间、热点有效收益倍率与风险提示的权威视图。修改
`services/control-api/appsettings.json` 的 `ExtractionMode:ExtractionZones[0]` 时，必须同时更新
`mods/pal-control-native/Source/PalControl/Private/GameAdapter/ExtractionChatCommand.cpp` 并重新构建；新增或轮换其他点不应硬编码进聊天回复。
