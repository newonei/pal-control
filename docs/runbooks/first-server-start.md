# 首次启动运行手册

本手册适用于尚未生成 `Pal/Saved` 和 `Mods/PalModSettings.ini` 的新服务端。首次启动的目标是生成真实配置并确认版本，不是马上启用写模组。

## 启动前

1. 备份当前 `PalServer` 目录或确认可以用 SteamCMD 重装。
2. 使用显式安装路径执行更新，避免现有 SteamCMD 元数据中的旧路径被继续使用：

   ```powershell
   & "C:\SteamCMD\steamcmd.exe" `
     +force_install_dir "C:\Palworld\PalServer" `
     +login anonymous `
     +app_update 2394010 validate `
     +quit
   ```

3. 不要修改 `DefaultPalWorldSettings.ini`；它是示例文件。
4. 首次启动不为 REST/RCON 添加公网防火墙放行或端口映射；Control API 始终通过 loopback 地址访问。Palworld 进程可能显示为 `0.0.0.0` 监听，因此不能把“使用 127.0.0.1 连接”误当成入站隔离。

## 首次启动后

确认生成：

```text
PalServer\Pal\Saved\Config\WindowsServer\PalWorldSettings.ini
PalServer\Pal\Saved\Logs\
PalServer\Mods\PalModSettings.ini
```

然后完成：

1. 为 `AdminPassword` 设置强随机值。
2. 设置 `RESTAPIEnabled=True` 和 `RESTAPIPort=8212`。
3. Windows 防火墙不放行 8212 公网入站；Control API 通过 loopback 访问。
4. 请求官方 `/info`、`/players`、`/metrics` 并保存脱敏响应作为集成测试 fixture。
5. 记录游戏版本、Steam buildid、字段差异和实际日志格式。

## 启用只读 Native 候选

当前源码候选为 `v1.0.1.100619` / Steam build `24181105` / `0.3.0-dev.39-ro`。两个全新独立构建均得到 893,440 字节、SHA-256 `c2dab9f9bfd3c47ac1a244139fb96ce1de6f598c4bce438ebddde96185063b34` 的字节一致 DLL。该精确候选尚未实服加载或运行固定探针套件，Bridge 可用性仍是 unknown。只有先核对 `dependencies.lock.json`、当前 PalServer EXE、UE4SS runtime、候选 Native DLL 的大小/SHA-256 与受控构建结果，并进入明确的维护窗口后，才可部署候选做只读 smoke；构建成功本身不能替代本节。候选在注册 hook 前还会再次哈希宿主 EXE、Native 与 UE4SS 文件；Control API 随后经 pipe server PID 用低权限查询独立复核主 EXE，模块摘要继续与锁比较，不匹配时不会接受 Bridge hello。

2026-07-17 的最近受控运行结果属于 dev38-ro：该精确 DLL 完成运行时身份校验且写能力关闭；固定 12 项套件中 9 项不依赖在线玩家的操作成功，`players.probe`、`players.progression.probe`、`inventory.probe` 因无人在线按规则拒绝，0 项意外失败。官方保存与优雅关服成功后服务器已停止，但该维护流程不证明玩家数据或扣物持久化。dev38-ro 现已被 dev39-ro 源码候选取代并保持 `superseded/quarantined`；其结果不能当作 dev39-ro 运行证据。P0-04 仍缺 dev39-ro 固定套件、受控在线玩家三项、PalDefender 组合和独立复核。dev37-ro 曾把持久离线库存误判为 live inventory，也不得重新部署。

受控加载步骤：

1. 把正式 Workshop 包放到 `PalServer\Mods\Workshop\PalControlNative\`。
2. 确认包根目录有经工具生成并校验的 `Info.json`。
3. 确认 `InstallRule` 中服务端规则含 `"IsServer": true`。
4. 在 `PalModSettings.ini` 中添加：

   ```ini
   [PalModSettings]
   bGlobalEnableMod=true
   ActiveModList=PalControlNative
   ```

5. 重启服务端，检查 `Mods\ManagedMods\PalControlNative\InstallManifest.json`。
6. 将 `PalControl.ini` 保持 `ReadOnly=true`；dev39-ro 的编译选项还会独立关闭所有写 capability、聊天 hook 与随机复活 hook。当前构建固定 pipe `pal-control.local.v1`，并只额外放行默认低权限服务账户 `NT SERVICE\PalControl.ControlApi` 的锁定 service SID；自定义服务名必须先重新评审、更新锁并受控重建，不能只改配置。自托管开发服若使用直接 overlay，不要手工复制 DLL；先对 `activate-client-overlay.ps1` 做 plan-only 复核，再显式 `-IncludeSavedStateBackup -QuarantineLegacyWorkshopPackages -Execute`。它会要求 PalServer 已停止、完整备份 `Pal\Saved`、校验候选/UE4SS proxy/runtime 摘要并在失败时恢复旧 overlay。
7. 先以管理员终端从正在运行的 PalServer 进程取得实际 EXE 路径和进程账户 SID，再把二者作为每次只读探针的必填批准值；不要从仓库示例、用户名或另一台机器猜测 SID：

   ```powershell
   $pal = Get-Process -Name "PalServer-Win64-Shipping-Cmd" -ErrorAction Stop |
     Select-Object -First 1
   $process = Get-CimInstance Win32_Process -Filter "ProcessId = $($pal.Id)"
   $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
   if ($owner.ReturnValue -ne 0 -or [string]::IsNullOrWhiteSpace($owner.Sid)) {
     throw "无法取得 PalServer 运行账户 SID。"
   }
   $probe = @{
     ExpectedPalServerExecutablePath = $pal.Path
     ExpectedPalServerProcessSid = $owner.Sid
     ServerId = "production-01"
     TimeoutSeconds = 15
   }
   $operations = @(
     "players.schema", "players.probe",
     "players.progression.schema", "players.progression.probe",
     "inventory.schema", "inventory.probe",
     "pals.schema", "pals.probe", "pals.skills.catalog",
     "announcements.overlay.probe", "announcements.banner.probe",
     "ui.notifications.probe"
   )
   foreach ($operation in $operations) {
     .\tools\native-bridge-probe.ps1 @probe -Operation $operation
   }
   ```

   脚本会从 Pipe 服务端 PID 独立取得真实 EXE、文件句柄最终路径和进程 token SID，并在同一绝对超时内严格校验 JSON 类型、成功状态、空错误、game/Steam/MOD/EXE/Native/UE4SS 精确身份、runtime 验证位、只读模式、完整 capability/probe 集以及每种操作的数据结构；第二个 hello、未知消息、跨 session result 或任一漂移都会立即非零退出。`inventory.probe` 还要求至少一个已识别在线玩家的 common/dropSlot/food 三容器全部 resolved，并要求该库存由当前权威世界的有效玩家控制器证明 `ownerOnline=true`。将最终路径与 SID 同步写入私有生产配置的 `ApprovedPalServerExecutablePath` 和 `ApprovedPalServerProcessSid`，不得提交真实机器路径。

   也可以使用 `tools/run-native-bridge-probe-suite.ps1` 一次运行固定的 12 项只读探针。输出目录只能位于仓库外私有目录或已忽略的 `.agent-build`；工具只在终端显示有界汇总，可能包含玩家/帕鲁标识的原始 JSON 不得提交。无人在线时，`players.probe`、`players.progression.probe` 和 `inventory.probe` 的拒绝只能证明 fail-closed，不能算通过 P0-04。
8. 对探针结果脱敏、扫描并按 `p0-04-current-native-probe` 生成双签名 live evidence；未通过前卸载候选或继续保持所有经济写入关闭，不得改写兼容矩阵为 stable。维护结束使用 `stop-native-probe-palserver.ps1`：它要求官方 REST 回环、零在线玩家、先保存并等待 `Level.sav` 稳定，再发送一次 graceful shutdown；响应不确定时不盲目重试，也不使用 `Stop-Process`/`taskkill`。

## 开放写入前的门禁

- 当前 game/Steam build 与宿主 EXE、Native DLL、UE4SS DLL 身份已加入兼容矩阵，且 `p0-04-current-native-probe` 已由独立复核者批准。
- 关键探针全部通过。
- 真实备份已验证可以恢复。
- dry-run diff 与游戏内结果一致。
- 重复 idempotency key 不会重复发物品。
- ACK 丢失测试进入 `uncertain` 并成功对账。
- `inventory.consume` 只有完成真实“扣物 → 保存 → 停服 → 重启 → 重新登录”后才声明 stable；experimental 或 `persistenceVerified=false` 时资源兑换保持关闭。
- 玩家掉线/换 session 返回冲突。
- Pal 不在 Box 中的高风险操作被拒绝。
