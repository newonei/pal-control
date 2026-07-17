# Pal Control Native

这是 Palworld Windows Dedicated Server 原生模组工程。当前已实现 UE4SS C++ 入口、仅本机 Named Pipe、长度前缀帧、hello/heartbeat，以及受保护的玩家/背包/帕鲁适配器。

当前源码候选锁定 Palworld `v1.0.1.100619`、Steam build `24181105`、Palworld 专用 UE4SS 提交 `c2ac246` 和精确 PalServer EXE 大小/SHA-256。候选版本为 `0.3.0-dev.37-ro`、Bridge 协议 `1.1`，默认只宣传和执行只读探针；两个全新独立、Cargo `--locked` 的受控目录均复现 886,272 字节、SHA-256 `c91bee8f943b6c151a59c41ff0a51aebf36469b0c60f4201494cc5a3a416f8a7`，但**尚未部署或在当前 PalServer 中加载**，因此仍是 `read-only-candidate-unverified`，不构成 ABI/schema 实服验收。C++ 模组必须针对运行时的精确 ABI 重新编译；`CppUserModBase` 的虚函数表会随 UE4SS 提交变化，产物不能跨提交复用。

候选在注册任何 Unreal hook 或创建 Named Pipe 前，会用拒绝并发写入/删除的文件句柄流式计算宿主 PalServer EXE、当前 PalControlNative DLL 和已加载 UE4SS DLL 的 SHA-256/大小，并在结束前复核 file id、长度与修改时间。宿主 EXE 或 UE4SS 任一值与锁文件不一致时直接退出初始化；Native 自身摘要由 hello 上报并与锁比较，Control API 还经 pipe server PID 独立哈希主 EXE，避免普通同名 pipe 进程伪造宿主。即使本机客户端绕过 capability 协商发送写命令，游戏线程 adapter 仍返回 `NATIVE_WRITE_CAPABILITIES_QUARANTINED`。当前构建同时关闭聊天 hook、随机复活 hook 和所有背包/成长/帕鲁/公告写能力。

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

.\mods\pal-control-native\scripts\Prepare-Ue4ssSource.ps1 `
  -Ue4ssRoot .\third_party\RE-UE4SS-Palworld-c2ac246

.\mods\pal-control-native\scripts\Build-PalControlNative.ps1 `
  -Ue4ssRoot .\third_party\RE-UE4SS-Palworld-c2ac246 `
  -BuildDirectory .\.native-build\pal-control-native-c2ac246
```

`Prepare-Ue4ssSource.ps1` 不会 clone、fetch、checkout、编译或部署。它只接受锁定
UE4SS 提交、该提交记录的 Unreal gitlink，以及两种输入状态：完全干净的初始工作树，或它
自己已精确准备好的可重复运行状态；其他 tracked/untracked 漂移、暂存改动、reparse point
（包括 UE4SS 根目录的现存祖先链）、ignored 文件、递归子模块漂移或 anchor 变化都会拒绝。
写入文件统一为 UTF-8 无 BOM，完成后它自动调用
`Build-PalControlNative.ps1 -GuardOnly` 复核源码和锁定工具链。

准备入口只应用以下四组受审补丁：

1. 在 `cppmods/CMakeLists.txt` 的精确 anchor 后挂接 `PalControlNative`，并把本仓库
   canonical `CMakeLists.txt` 与 `Source/` 按字节复制到 `cppmods/PalControlNative/`。
2. 用 `dependencies.lock.json` 记录的大小和 SHA-256 校验后，复制受审
   `patches/patternsleuth_bind.Cargo.lock`。
3. 把 `patternsleuth_bind` 的 Corrosion import 改为 `LOCKED`，令 Cargo 拒绝重写锁文件。
4. 把 Corrosion 与 IconFontCppHeaders 都固定到锁文件中的完整 40 位提交，并设置
   `GIT_SHALLOW FALSE`，确保完整提交可解析、可复核。

构建脚本在配置前后继续核对锁定的 Palworld/Steam/EXE 身份、只读候选状态、pipe/service SID、UE4SS/Unreal 提交、上述精确补丁和无其他 tracked source 漂移，并核对 CMake、Rust、MSVC 工具链；构建目录必须位于 UE4SS 源码树之外。它强制向 CMake 传入 `PALCONTROL_ENABLE_WRITE_CAPABILITIES=OFF` 与 `PALCONTROL_ENABLE_RANDOM_SAFE_RESPAWN=OFF`，不会自动 clone、fetch、切换提交、部署 DLL 或重启服务器。只检查环境不编译时使用 `-GuardOnly`；若同时传入 `-Ue4ssReleaseArchivePath` 与 `-Ue4ssRuntimeDllPath`，还会核对发布压缩包及实际 runtime 的锁定摘要。成功后终端打印产物绝对路径、字节数和 SHA-256。仓库记录的候选 DLL SHA-256 只对应受控构建产物，DLL 本身不提交公开仓库。

`xmake.lua` 仅镜像 UE4SS 模板所需的 IDE/开发元数据，不执行依赖树前后复核、工具链锁或最终 artifact hash gate；它不能产出可部署/可签审候选。正式候选只能使用上述 PowerShell 入口。

旧 `v1.0.0.100427`/dev36 组合曾验证真实 `hello` 和持续 heartbeat；这不能替代当前版本候选的加载与探针。当前 dev37-ro 必须先按 `p0-04-current-native-probe` 在受控实服完成只读加载、schema 和三容器 inventory probe，才允许另行评审写能力构建。

## 游戏内撤离点查询

以下实现保留在源码中，但 dev37-ro 只读候选不会安装该聊天 hook。只有后续独立写能力评审明确批准并重新构建时才可能启用。

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

以下实现保留在源码中，但 dev37-ro 只读候选的 CMake 默认值和受控构建脚本均强制关闭该 hook。

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

1. 旧 dev36 与 Control API 的 `hello`/heartbeat 已在旧版真实 PalServer + UE4SS 进程中验证；当前 dev37-ro 仍待受控加载。
2. 在当前 build `24181105` 只读列出在线玩家和稳定玩家 ID。
3. 在当前 build 只读导出三个背包容器/slot DTO，并保存签名证据。
4. 在当前 build 只读导出 PalBox/party DTO，证明 `instanceId` 稳定。
5. `p0-04-current-native-probe` 通过且由独立复核人批准后，才能另建显式写能力候选；当前 dev37-ro 不可用于持久化扣物验收。
