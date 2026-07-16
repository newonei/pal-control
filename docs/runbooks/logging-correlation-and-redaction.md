# 日志关联与脱敏审计

本页定义 Control API 的生产日志边界。目标是让一次 HTTP 请求、一个持久化后台任务及其下游 adapter 调用可以用同一个 `CorrelationId` 追踪，同时保证认证信息、玩家身份和未经信任的上游文本不会进入日志提供程序。

## 结构化 scope 契约

`ControlPlaneLog` 只允许下列公共字段：

| 字段 | 含义 | 数据规则 |
| --- | --- | --- |
| `CorrelationId` | 一次 HTTP 或后台业务操作的关联 ID | HTTP 只接受规范 `Guid`；持久化任务按组件、操作和持久 ID 生成稳定 ID；临时轮询使用新 ID |
| `OperationId` | 当前操作的不可逆标识 | 对稳定 ID 做 SHA-256 截断；不记录 idempotency key、UID 或用户输入原文 |
| `Operation` | 操作名称 | 只接受最长 96 字符的 ASCII 字母、数字、点、横线和下划线，否则替换为指纹 |
| `Component` | 组件名称 | 与 `Operation` 使用相同白名单 |
| `ScopeKind` | `http`、`worker`、`adapter` 或 `service` | 固定枚举语义 |
| `ServerFingerprint` | 服务器配置标识 | SHA-256 指纹，不保留原值 |
| `SubjectFingerprint` | 管理员或玩家标识 | SHA-256 指纹，不保留 SteamID、PlayerUID 或账号原值 |
| `ExceptionType` | 异常 CLR 类型 | 仅类型名 |
| `ErrorFingerprint` | 异常分类指纹 | 仅由异常类型和 `HResult` 生成；不读取 `Exception.Message` |

同一个持久化 worker 操作在重试或重启后得到相同的 `CorrelationId`。嵌套 adapter 保留父级关联 ID，只更新当前组件、操作及操作指纹。HTTP 请求的 `X-Correlation-ID` 只有在它是规范 `Guid` 时才会被接受；其他值会被替换，且请求路径、查询字符串和 route value 不进入 scope。

## Hosted worker 逐项审计

| Worker | 稳定操作边界 | 已复核的敏感字段处理 |
| --- | --- | --- |
| `EconomyContentStartupInitializer` | `content.startup` / host startup | 服务器只记指纹；只输出内容版本和业务日期 |
| `ReliableTaskProjectionRecoveryWorker` | settlement run ID 或 order ID | 不读取玩家身份；异常只记类型/指纹 |
| `AnnouncementCommandQueue` | announcement command ID | 服务器只记指纹；标题、正文、reason 与 idempotency key 均不写日志 |
| `InGameNotificationCommandQueue` | notification command ID | 服务器只记指纹；audience、模板参数、reason 均不写日志 |
| `PalDefenderCommandQueue` | command ID | 服务器和经济交付玩家只记指纹；body、upstream path、token 均不写日志 |
| `NativeBridgeClient` | 连接尝试为临时 ID；命令使用 command ID | pipe 名、server ID 只记指纹；payload、reason、玩家 ID 均不写日志 |
| `ExtractionDeliveryWorker` | delivery ID 或 order ID | 玩家只记指纹；上游错误消息只记指纹 |
| `ExtractionSettlementQueue` | settlement run ID | 玩家只记指纹；idempotency key 不进入 scope |
| `ExtractionSettlementRecoveryWorker` | settlement run ID | 只输出 run/revision；不输出玩家身份或 receipt 内容 |
| `PlayerNotificationProjectionWorker` | 固定 `notification.projection` 扫描边界 | 不输出账户、平台目标、PlayerUID、通知正文或 source event key；异常只记类型/指纹 |
| `TeamEconomyProjectionWorker` | 固定 `team-economy.projection` 扫描边界 | 不输出账户、成员、邀请 token 或原始身份；异常只记类型/指纹 |
| `EconomyObservabilityService` | 观测时间 | 服务器只记指纹；输出固定 feature/alert code，不输出身份样本 |
| `SaveCommandQueue` | save command ID | 服务器只记指纹；label、路径、reason 和上游响应正文均不写日志 |
| `LiveMapService` | 采样时间 | 服务器只记指纹；不输出玩家名称、SteamID、PlayerUID 或坐标集合 |

启动时读取 JSONL/SQLite 投影的日志使用单独的 `persistence.load` scope，因此在 hosted loop 尚未运行时也不会产生无关联日志。

## Adapter 逐项审计

| Adapter | 日志策略 | 结论 |
| --- | --- | --- |
| `PalworldRestClient` | `official-rest.*` adapter scope | Basic Auth 用户名/密码、响应正文和玩家数组不写日志；只输出固定 endpoint 和 HTTP 状态 |
| `PalDefenderRestClient` | `paldefender-rest.read/write` adapter scope | Bearer token、token file、body 和含玩家 ID 的路径不写日志；endpoint 仅保留安全形态 |
| `NativeBridgeClient` | `BeginAdapter` 嵌套父 worker/HTTP scope | payload、reason 不写日志；命令及服务器使用 GUID/指纹 |
| `ExtractionRconAdapter` | 无直接 `ILogger` 依赖 | 错误返回调用方，由调用方 scope 记录安全异常元数据；RCON password 永不记录 |
| `SourceRconTransport` | 无直接 `ILogger` 依赖 | 不记录 packet、password 或原始响应 |
| `ExtractionNativeInventoryAdapter` | 无直接 `ILogger` 依赖 | 继承 settlement worker/HTTP 与 Native Bridge adapter scope |
| `PalDefenderItemGrantAdapter` | 无直接 `ILogger` 依赖 | 继承 delivery worker 与 PalDefender queue/REST scope |
| `SteamOpenIdProviderClient` | 无直接 `ILogger` 依赖 | OpenID code、return URL 参数和 Cookie 不进入日志 |

## 其他日志拥有者审计

| 组件 | scope 来源 | 复核结论 |
| --- | --- | --- |
| `ControlPlaneCorrelationMiddleware` | HTTP 根 scope | 只记录规范 correlation GUID 与白名单 HTTP method；不记录 path/query/header |
| `AdminAuditMiddleware` | HTTP 子 scope | 运行日志仅记管理员指纹；完整 subject、source IP、reason 只保存在受控审计库中，用于不可抵赖审计 |
| `AnnouncementStore`、`InGameNotificationStore` | HTTP/worker 父 scope或 `persistence.load` | 日志只包含固定状态和业务 GUID |
| `EconomyContentRuntimeService` | HTTP 或 startup 父 scope | 只记录内容版本和业务日期 |
| `ExtractionModeCoordinator` | HTTP 或 delivery worker 父 scope | 已有账号字段使用指纹；不记录库存或 PlayerUID 原文 |
| `ExtractionSettlementService` | HTTP、queue 或 recovery 父 scope | 只记录 run/lease GUID 与固定状态；receipt/payload 不记录 |
| `PalworldResourceCatalogService` | HTTP/startup 父 scope | 模板文件名改为指纹；文件路径和 JSON 正文不记录 |
| `SaveManagementService` | HTTP 或 save worker 父 scope | backup ID 可追踪；进程路径、manifest 正文及异常消息不记录 |

## 禁止进入日志的内容

- `Cookie`、session 值、TOTP/一次性 code、API key、Bearer token、Basic Auth 密码、RCON password。
- 原始 SteamID、PlayerUID、账号 subject、玩家姓名，以及包含这些值的 URL/path/query。
- adapter request/response body、Native payload、公告/通知正文、管理员 reason、idempotency key。
- 完整 `Exception` 对象、`Exception.Message`、inner exception 和 stack trace。生产日志只保留异常类型与不可逆错误指纹。

管理员审计数据库不是运行日志：它按安全设计保留管理员 subject、来源 IP、reason 和请求摘要，以满足操作归因；该数据不得转抄到普通日志。

## 自动验证

从仓库根目录执行：

```powershell
.\tests\integration\logging-correlation-smoke.ps1
```

该 harness 不依赖文本关键词扫描，而是：

1. 反射枚举程序集内全部 `IHostedService`，与本页 worker 清单做精确集合比较；新增 worker 未审计会直接失败。
2. 解析编译后 IL，确认每个 hosted worker 调用 `BeginWorker`、每个有日志的外部 adapter 调用 `BeginAdapter`。
3. 解析编译后 IL，禁止服务程序集调用任何接收 `Exception` 的 `LoggerExtensions.Log*` overload。
4. 反射枚举全部 `ILogger<T>` 拥有者，与显式敏感字段决策清单做精确集合比较。
5. 注入同时含 Cookie、code、token、password 和 PlayerUID 的恶意异常，确认原异常及这些值均未到达日志 provider。
6. 验证稳定 worker ID、嵌套 adapter 传播、HTTP header 校验、scope 释放及玩家指纹行为。

完整统一测试仍可执行：

```powershell
npm test
```
