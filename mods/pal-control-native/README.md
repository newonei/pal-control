# Pal Control Native

这是 Palworld Windows Dedicated Server 原生模组工程。当前已实现 UE4SS C++ 入口、仅本机 Named Pipe、长度前缀帧、hello/heartbeat、受保护的玩家/背包/帕鲁适配器，以及只读撤离点聊天查询。

当前目标版本：Palworld `v1.0.0.100427`、Palworld 专用 UE4SS 提交 `c2ac246`。C++ 模组必须针对运行时的精确 ABI 重新编译；`CppUserModBase` 的虚函数表会随 UE4SS 提交变化，产物不能跨提交复用。

## 目标部署结构

官方服务端会根据 Workshop `Info.json` 的安装规则，在重启时将 UE4SS 内容部署到 `Mods\NativeMods\UE4SS`。正式打包前应使用当前 Palworld Mod Uploader 生成 `Info.json`，并确认服务端安装规则包含 `"IsServer": true`。

`Info.template.json` 仅记录预期元数据，不应直接发布；`InstallRule.Targets` 必须在实际产物目录确定后由上传工具生成/校验。

当前自托管开发服使用 UE4SS 发布包的代理加载方式，实际结构如下：

```text
Pal\Binaries\Win64\dwmapi.dll
Pal\Binaries\Win64\ue4ss\UE4SS.dll
Pal\Binaries\Win64\ue4ss\MemberVariableLayout.ini
Pal\Binaries\Win64\ue4ss\Mods\mods.txt
Pal\Binaries\Win64\ue4ss\Mods\PalControlNative\dlls\main.dll
```

## 锁定依赖构建

Native MOD 与 Palworld、UE4SS 和 Unreal ABI 精确绑定。先按
[`dependencies.lock.json`](dependencies.lock.json) 获取 UE4SS 源码和子模块，并确保
工作树停在锁定提交；不要提交 `third_party/`、独立构建目录或本机生成的 DLL。

```powershell
git clone --recurse-submodules https://github.com/Okaetsu/RE-UE4SS `
  .\third_party\RE-UE4SS-Palworld-c2ac246
git -C .\third_party\RE-UE4SS-Palworld-c2ac246 checkout c2ac246447a8bcd92541070cb474044e7a2bbbe6
git -C .\third_party\RE-UE4SS-Palworld-c2ac246 submodule update --init --recursive

.\mods\pal-control-native\scripts\Build-PalControlNative.ps1 `
  -Ue4ssRoot .\third_party\RE-UE4SS-Palworld-c2ac246
```

脚本在配置前核对锁定的 UE4SS/Unreal 提交和 CMake、Rust、MSVC 工具链，且要求构建目录位于 UE4SS 源码树之外。它不会自动 clone、切换提交、部署 DLL 或重启服务器。只检查环境而不编译时使用 `-GuardOnly`；成功后终端会打印产物绝对路径、字节数和 SHA-256。

开发服已验证真实 `hello` 和持续 heartbeat。所有原生读写仍会校验精确游戏版本、反射签名、玩家身份、revision 与回读结果，不匹配时 fail-closed。

## 游戏内撤离点查询

普通玩家可在全局聊天输入 `!撤离` 或 `!extract`，并兼容直接输入 `撤离` 或 `extract`。Palworld 会在普通聊天广播之前把 `/` 前缀路由给管理员命令解析器，因此公开指令不能使用 `/撤离`。MOD 在游戏线程监听原生
`/Script/Pal.PalGameStateInGame:BroadcastChatMessage`，只接受完全匹配的命令，
再用 `SenderPlayerUId -> PalPlayerState -> PalPlayerController` 的唯一映射通过可靠
`PlayerController:ClientMessage` 仅回复发起者。回复只包含撤离点名称、坐标、半径和路线，
不会调用商城、背包、账本或结算写入。单玩家回复有 2 秒冷却，反射签名或身份映射不唯一时
功能会保持禁用/拒绝回复。

当前内置提示与 `ExtractionMode:ExtractionZones[0]` 保持一致：开发服撤离点，
`X 248 / Y -504 / 半径 100`。修改撤离点配置时必须同步
`GameAdapter/ExtractionChatCommand.cpp` 中的两行玩家提示并重新构建 MOD；后续应改为由
Native Bridge 下发只读、带版本的撤离点快照，消除这处构建期副本。
该聊天提示只覆盖兼容主点，不读取 current content，也不代表今日热点；多点开放状态、
下一开放时间、有效收益倍率和风险提示以玩家门户地图为准。

## 随机安全复活

服务端挂接当前版本的权威 `PalPlayerState:RequestRespawn` RPC。每次请求执行前，MOD
只从游戏已加载的 `PalLocationPoint_Respawn` 安全出生点中随机选取一个，通过原生
`PalNetworkPlayerComponent:RegisterRespawnPoint_ToServer` 注册其位置与朝向，再让游戏
继续完成死亡惩罚、世界分区加载和复活。选择按玩家 UID 记录上一次结果，候选点不少于
两个时连续两次不会相同。反射类型、RPC 标志、参数签名、玩家身份或服务端权威上下文
任一不匹配时功能保持禁用或回退原版复活，不会使用客户端坐标或未经游戏登记的地点。

## 线程模型

```text
Named Pipe worker -> validate JSON -> bounded MPSC queue
                                      |
                                      v
                                  game tick
                                      |
               lookup live object -> validate -> mutate -> DTO result
```

IPC 线程不可缓存或解引用 Unreal 对象。游戏线程每 tick 只处理受限数量命令；网络、数据库、磁盘和等待均不得出现在游戏线程。

## 首个里程碑

1. 与 Control API 完成 `hello`/heartbeat。**已在真实 PalServer + UE4SS 进程中验证。**
2. 只读列出在线玩家和稳定玩家 ID。
3. 只读导出背包容器/slot DTO。
4. 只读导出 PalBox/party DTO，证明 `instanceId` 稳定。
5. 为当前 game build 建立探针与烟雾测试后，才实现 dry-run 和低风险写入。
