# Economy Safety Gate 运行手册

商城购买与资源兑换共用同一套 fail-closed 检查，但保留两个互不影响的运行时熔断器。任何检查失败只关闭对应写路径；已有账户、余额、订单、兑换记录和 capabilities 仍可读取。

## 扣款或扣物前检查

`purchase` 在钱包扣款前依次验证：

1. 玩法已启用，且维护闸门和购买熔断器均开放；
2. SQLite 权威数据库能完成一次真实写入并回滚；
3. 当前活动赛季处于有效时间窗口，已绑定 world ID，且该 ID 与运行中存档一致；
4. 账户、赛季、平台主体和完整玩家 UID 的当前周绑定一致；
5. PalDefender token 文件（或显式 `Permissions` 配置）同时声明 `REST.Version.Read`、`REST.Players.Read`、`REST.Items.Read`、`REST.Items.Give`，且 `GET version` 返回批准的游戏与插件版本；
6. 持久化发货队列正在运行、未满，未决订单没有超过 backlog 上限；
7. 若配置要求 Native adapter，则协议、game/Steam build、MOD 版本、宿主 PalServer EXE、实际 Native DLL、实际 UE4SS DLL 的大小/SHA-256、独立 pipe-server PID/主 EXE identity、write mode 和逐项 capability 全部匹配；每次写派发还会再次核对当前 hello，不能沿用闸门检查后的旧会话。

`resourceExchange` 使用相同的存储、赛季、世界、绑定和维护检查，并额外要求结算队列容量、精确批准的 Native 协议/game build/MOD 版本、`inventory.probe` 与稳定 `inventory.consume` capability。报价会持久化三个必需容器的完整槽位/动态元数据快照；正式结算在派发前再次检查门禁，Native 回执还必须证明逐行扣物、完整回读和 `persistenceVerified=true`。experimental capability 不会被当成正式能力。RCON `delitems` 只有在宿主为 Development、`Security:DevelopmentMode=true`、`PlayerPortal:PublicSteam=false`、RCON 已启用、`ExtractionMode:Rcon:AllowDevelopmentSettlement=true` 且没有强制 Native 时才允许用于隔离诊断；任一条件不满足即选择 Native 并 fail-closed，生产环境不会 fallback。

商城后台发货有两层复查：`ExtractionDeliveryWorker` 在创建持久化命令前检查一次，`PalDefenderCommandQueue` 在把命令标为 `dispatched` 前再检查一次。若版本或世界漂移，命令保持 `accepted`，不会被错误标记为失败或自动退款；修复依赖后可继续处理。

## 查看有效门禁

```powershell
$headers = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_ADMIN_KEY }
Invoke-RestMethod `
  "http://127.0.0.1:5180/api/v1/extraction/capabilities" `
  -Headers $headers
```

重点查看 `writes.purchase`、`writes.resourceExchange` 下的 `enabled`、`blockers` 与 `circuit.writesEnabled/reason/actor/updatedAt`。

| blocker | 含义 |
| --- | --- |
| `ECONOMY_STORE_NOT_WRITABLE` | SQLite 真实写探针失败；只读不受影响 |
| `ECONOMY_DISK_SPACE_LOW` | 数据盘剩余空间低于配置阈值 |
| `SEASON_WORLD_MISMATCH` | 活动赛季绑定与运行中世界不一致 |
| `PLAYER_IDENTITY_BINDING_REQUIRED` | 当前周没有完整玩家 UID 绑定 |
| `PALDEFENDER_VERSION_NOT_APPROVED` | 商城 adapter 版本漂移 |
| `PALDEFENDER_CAPABILITY_NOT_APPROVED` | 发货凭据没有声明完整的读、发货 capability |
| `PALDEFENDER_GRANT_RECEIPT_UNVERIFIED` | 尚未确认 PalDefender 的结构化逐物品回执语义 |
| `SHOP_DELIVERY_QUEUE_FULL` | 发货命令队列达到上限 |
| `NATIVE_ECONOMY_ADAPTER_NOT_CONNECTED` | Native Bridge 未连接或 hello 尚未完成 |
| `NATIVE_ECONOMY_ADAPTER_VERSION_NOT_APPROVED` | Native 协议、game/Steam build、MOD、宿主 PalServer EXE、Native DLL 或 UE4SS DLL 身份不在批准组合中 |
| `NATIVE_RUNTIME_IDENTITY_UNVERIFIED` | hello 没有证明当前宿主 EXE 与审核目标完全一致 |
| `NATIVE_WRITE_CAPABILITIES_QUARANTINED` | 当前 build 仅允许只读探针，不可执行经济写入 |
| `NATIVE_ECONOMY_CAPABILITY_MISSING` | 缺少 `inventory.probe` 或稳定 `inventory.consume`；experimental 不满足 |
| `PURCHASE_CIRCUIT_OPEN` | 管理员仅关闭了购买 |
| `RESOURCE_EXCHANGE_CIRCUIT_OPEN` | 管理员仅关闭了资源兑换 |

## 无重启切换独立熔断器

接口要求 `EconomyAdmin`（或更高角色）、TOTP 和审计原因。下面只关闭商城购买，不影响资源兑换：

```powershell
$headers = @{
  "X-Pal-Admin-Key"    = $env:PAL_CONTROL_ADMIN_KEY
  "X-Pal-Admin-Totp"   = Read-Host "TOTP"
  "X-Pal-Admin-Reason" = "PalDefender version incident"
  "Idempotency-Key"    = [guid]::NewGuid().ToString("D")
}
$body = @{
  writesEnabled = $false
  reason = "PalDefender version incident"
} | ConvertTo-Json
Invoke-RestMethod `
  "http://127.0.0.1:5180/api/v1/extraction/admin/safety-gate/purchase" `
  -Method Put -Headers $headers -ContentType "application/json" -Body $body
```

恢复时把 `writesEnabled` 改为 `true` 并生成新的 `Idempotency-Key`。同一逻辑请求超时或响应丢失时必须复用原 key；SQLite `admin_operation_keys` 会把 key、请求 hash 与认证主体跨重启绑定，同键不同目标、作用域或操作者返回 `ADMIN_IDEMPOTENCY_CONFLICT`。资源兑换使用路径 `resource-exchange`。权威状态会原子写入 `ExtractionMode:Persistence:DataDirectory/extraction-commerce.db` 的 `economy_gate_state` 表；旧版 `economy-safety-gate.json` 只会被一次性迁移，之后不再作为权威来源。关闭操作会先阻止新写入，再等待已准入操作排空，重启后状态不会丢失。

## 生产配置

```json
{
  "CommandPersistence": {
    "PalDefenderQueueCapacity": 256
  },
  "ExtractionMode": {
    "Safety": {
      "DeliveryBacklogCapacity": 128,
      "MinimumFreeSpaceBytes": 1073741824,
      "ApprovedGameVersion": "1.0.1.100619",
      "ApprovedPalDefenderVersion": "1.8.1.3933",
      "PalDefenderGrantReceiptSemanticsVerified": false,
      "RequireNativeForPurchase": false,
      "RequireNativeForResourceExchange": true,
      "ApprovedNativeProtocolVersion": "1.1",
      "ApprovedNativeGameBuild": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeSteamBuild": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeModVersion": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeExecutableSha256": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeExecutableSize": 0,
      "ApprovedPalServerExecutablePath": "SET_ABSOLUTE_PATH_AFTER_REAL_ACCEPTANCE",
      "ApprovedPalServerProcessSid": "SET_PROCESS_SID_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeDllSha256": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedNativeDllSize": 0,
      "ApprovedUe4ssDllSha256": "SET_AFTER_REAL_ACCEPTANCE",
      "ApprovedUe4ssDllSize": 0,
      "PurchaseNativeCapabilities": [],
      "ResourceExchangeNativeCapabilities": [
        "inventory.probe",
        "inventory.consume"
      ]
    }
  }
}
```

`ApprovedPalServerExecutablePath` 必须填写通过文件句柄解析后的绝对 Windows `.exe` 路径，`ApprovedPalServerProcessSid` 必须填写实际承载 PalServer 的服务账户 SID；Control API 会从命名管道服务端 PID 独立取得两者，并在 hello 和每次写入派发时与配置精确核对。PalServer 安装目录应由管理员所有且不可被普通本机用户写入。两个字段留空或使用上述占位符时，Native 写入会 fail-closed；同一管理员权限下的进程注入仍属于受信主机边界，不能由此机制单独防御。

不要把 `SET_AFTER_REAL_ACCEPTANCE` 替换成旧 dev36，也不要把当前 dev37-ro 的编译成功伪造成 stable capability。dev37-ro 的 `writeEnabled=false` 会被 Safety Gate 明确拒绝。只有先完成当前版本只读 ABI/schema probe，再由独立写候选和真实玩家完成“扣物 → 保存 → 停服 → 重启 → 重新登录”并保存脱敏证据后，才能同步 stable Native hello、批准版本和配置。启用 RCON 诊断时，host 必须是 loopback，端口和 timeout 必须有效，批准版本不能为空，并且 `Password` 与绝对路径 `PasswordFile` 必须二选一；它不改变生产资源兑换的 Native-only 规则。结构错误使用 `ValidateOnStart` 阻止启动；世界、版本、能力、队列或存储的运行时漂移不会杀死只读服务。

## 验证

```powershell
.\tests\contract\economy-safety-contract.ps1
.\tests\integration\admin-auth-smoke.ps1
```

契约测试覆盖独立熔断、热恢复、排空等待、状态持久化、队列压力、依赖漂移、写入准入和 SQLite 回滚写探针；HTTP smoke 覆盖实际管理员认证下的无重启关闭与恢复。
