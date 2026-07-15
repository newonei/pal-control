# PalDefender 集成运行手册

本集成把 PalDefender v1.8.1 作为仅限本机的上游能力提供者。浏览器和外部调用方只访问 Pal Control 的 `http://127.0.0.1:5180/api/v1`；PalDefender 的 bearer token 和 `17993` 端口不得直接暴露。上游接口说明以 [PalDefender REST API](https://ultimeit.github.io/PalDefender/zh/RESTAPI/) 为准，Pal Control 只开放下表列出的 27 项能力。

## 固定版本与完整性

当前已审查组合：

| 项目 | 固定值 |
| --- | --- |
| PalDefender release | `v1.8.1`，运行时版本 `1.8.1.3933`（核心程序闭源） |
| Steam app | `2394010` |
| Palworld Steam build | `24088465` |
| `d3d9.dll` SHA-256 | `8638fef6628d8c4c221696739d1ccf55cbf2d1ca02111e35dbb707f792325f21` |
| `PalDefender.dll` SHA-256 | `a88f4dfa056c2e4b1201d9a50ab0f74b13065257c4406bcfa42f97e2c60a3057` |

Palworld 更新后不要直接沿用旧的 `d3d9.dll` 旁加载。`deploy/windows/start-palserver-guarded.ps1` 会同时核对 Steam build 和两个 DLL 的 SHA-256；任何未知组合都会拒绝启动，直到重新做兼容性评审并显式更新固定值。

## 安装与启用

所有安装、升级和配置动作都必须在 PalServer 停止时执行。先确认存档备份可恢复，再在仓库根目录运行：

```powershell
$InstallRoot = 'C:\PalServerRuntime'
$ReleaseDirectory = 'C:\path\to\PalDefender\_release_v1.8.1'

.\deploy\windows\install-paldefender.ps1 `
  -InstallRoot $InstallRoot `
  -ReleaseDirectory $ReleaseDirectory
```

安装脚本会在复制前、复制后核对两个固定摘要；如果目标目录已有同名 DLL，会先备份到 `backups/native-overlay/paldefender-<timestamp>/`。

首次安装后先用受保护脚本启动一次，使 PalDefender 生成 `Pal/Binaries/Win64/PalDefender/Config.json` 和 `RESTAPI/RESTConfig.json`，随后停止服务器：

```powershell
.\deploy\windows\start-palserver-guarded.ps1 -InstallRoot $InstallRoot
```

配置 loopback REST、独立服务 token 和 monitor-only 防作弊策略：

```powershell
.\deploy\windows\configure-paldefender.ps1 `
  -InstallRoot $InstallRoot `
  -Port 17993 `
  -TokenName PalControl `
  -AllowedOrigin 'http://127.0.0.1:5180'
```

脚本会执行以下安全设置：

- REST 只绑定 `127.0.0.1:17993`，不对公网或局域网监听；
- token 文件只授予当前 Windows 身份和 `SYSTEM` 完全控制；
- `shouldWarnCheaters=true`，但自动 kick、ban、IP-ban 全部为 `false`；
- `exitServerOnStartupFailure=false`，避免 PalDefender 初始化异常把服务端带入自动重启循环；
- 默认 token 不含 `REST.Base.Delete`。

在未提交的本机配置或环境变量中启用适配器：

```json
{
  "Palworld": {
    "PalDefenderRestApi": {
      "Enabled": true,
      "BaseUrl": "http://127.0.0.1:17993/v1/pdapi/",
      "TokenFile": "C:\\PalServerRuntime\\Pal\\Binaries\\Win64\\PalDefender\\RESTAPI\\Tokens\\PalControl.json",
      "Origin": "http://127.0.0.1:5180",
      "TimeoutSeconds": 7
    }
  }
}
```

最后再次使用 `start-palserver-guarded.ps1` 启动。不要把 token 写进前端、日志、截图、命令 reason/payload 或已提交的配置文件。

## CORS 与 Origin 陷阱

PalDefender 的 CORS 检查也会作用于服务器到服务器的 HTTP 请求：实测不带 `Origin` 的请求会被拒绝。Pal Control 的 `PalDefenderRestClient` 因此固定发送：

```http
Origin: http://127.0.0.1:5180
```

这个值必须与 `RESTAPI/RESTConfig.json` 中 `Cors.Allowed-Origins` 完全一致。`configure-paldefender.ps1 -AllowedOrigin` 和 `PalDefenderRestApi.Origin` 必须成对修改；不能靠删除 `Origin` 绕过检查。直接做上游诊断时也要显式带上该 header：

```powershell
$tokenFile = Join-Path $InstallRoot 'Pal\Binaries\Win64\PalDefender\RESTAPI\Tokens\PalControl.json'
$token = (Get-Content -LiteralPath $tokenFile -Raw | ConvertFrom-Json).Token
$headers = @{
  Authorization = "Bearer $token"
  Origin = 'http://127.0.0.1:5180'
}
Invoke-RestMethod 'http://127.0.0.1:17993/v1/pdapi/version' -Headers $headers
```

## 27 项白名单能力

`GET /api/v1/servers/local/paldefender/catalog` 返回同一份静态目录，`count` 必须为 `27`。未列出的 PalDefender REST 路由（包括已废弃的统一 `/give`）不会被代理。

| 方法 | Pal Control 相对路径 | PalDefender 权限 | 功能 |
| --- | --- | --- | --- |
| GET | `players` | `REST.Players.Read` | 玩家列表 |
| GET | `player/{playerIdentifier}` | `REST.Player.Read` | 玩家详情 |
| GET | `pals/{playerIdentifier}` | `REST.Pals.Read` | 玩家 Pal 列表 |
| GET | `items/{playerIdentifier}` | `REST.Items.Read` | 玩家物品 |
| GET | `techs/{playerIdentifier}` | `REST.Techs.Read` | 玩家科技 |
| GET | `progression/{playerIdentifier}` | `REST.Progression.Read` | 玩家进度 |
| GET | `guilds` | `REST.Guilds.Read` | 公会列表 |
| GET | `guild/{guildId}` | `REST.Guild.Read` | 公会与基地详情 |
| GET | `banlist` | `REST.Banlist.Read` | 封禁记录检索 |
| GET | `version` | `REST.Version.Read` | PalDefender 与游戏版本 |
| POST | `give/items/{playerIdentifier}` | `REST.Items.Give` | 发放物品 |
| POST | `give/pals/{playerIdentifier}` | `REST.Pals.Give` | 按 Pal ID 发放 Pal |
| POST | `give/paltemplate/{playerIdentifier}` | `REST.PalTemplates.Give` | 按模板发放 Pal |
| POST | `give/paleggs/{playerIdentifier}` | `REST.PalEggs.Give` | 发放 Pal 蛋 |
| POST | `give/progression/{playerIdentifier}` | `REST.Progression.Give` | 发放经验和点数 |
| POST | `learntech/{playerIdentifier}` | `REST.Techs.Learn` | 学习科技 |
| POST | `forgettech/{playerIdentifier}` | `REST.Techs.Forget` | 遗忘科技 |
| POST | `deletebase/{baseCampId}` | `REST.Base.Delete` | 删除基地；默认 token **未授权** |
| POST | `ban/{playerIdentifier}` | `REST.Punishments.Ban` | 封禁玩家 |
| POST | `unban/{userId}` | `REST.Punishments.Unban` | 解除玩家封禁 |
| POST | `banip/{ip}` | `REST.Punishments.BanIP` | 封禁 IP |
| POST | `unbanip/{ip}` | `REST.Punishments.UnbanIP` | 解除 IP 封禁 |
| POST | `kick/{playerIdentifier}` | `REST.Punishments.Kick` | 踢出玩家 |
| POST | `SendPlayerMessage` | `REST.Messages.Send.*` | 玩家、全局、公会或日志消息；token 展开为 6 个最小权限 |
| POST | `Broadcast` | `REST.Messages.Broadcast` | 广播聊天消息 |
| POST | `Alert` | `REST.Messages.Alert` | 发送警报 |
| POST | `ReloadConfig` | `REST.Reload.Config` | 热重载 PalDefender 配置 |

`monitor-only` 只表示关闭 PalDefender 的自动惩罚，不表示上述显式管理 POST 是只读的。所有 POST 仍应按生产变更操作审批。

## 调用语义

读操作同步转发。Pal Control 会加 bearer token、`Accept: application/json` 和正确的 `Origin`；上游 JSON、文本和 HTTP 状态原样返回。网络或超时错误返回 `503`。`/status` 是例外：它始终为已配置的 server 返回 `200` 状态文档，用 `enabled`、`connected`、`upstreamStatus` 和 `error` 表达故障；`/catalog` 只是静态白名单，不代表上游健康。

所有 POST 必须携带 8–128 字符的 `Idempotency-Key`，并使用统一信封。`payload` 是按照 PalDefender 官方端点定义发送给上游的 JSON；紧凑 UTF-8 JSON 超过 64 KiB 会以 `PALDEFENDER_PAYLOAD_TOO_LARGE` 拒绝：

```http
Idempotency-Key: alert-20260711-0001
Content-Type: application/json

{
  "reason": "经值班管理员批准的维护提示",
  "payload": {
    "Message": "服务器将在十分钟后维护"
  }
}
```

`reason` 必须为 3–500 个无控制字符的可审计文本。服务会先在 `ExtractionMode:Persistence:DataDirectory/extraction-commerce.db` 的同一事务中写入 `paldefender_commands(state=accepted)` 和不可变 `paldefender_command_events`，然后由单实例 `PalDefenderCommandQueue` 后台租约派发；不要把 `202 Accepted` 当作上游成功。

```text
accepted -> dispatched -> succeeded
                       -> failed
                       -> uncertain
```

- 同一 server、同一 key、完全相同的路径、reason 和 payload 只对应一个命令；进行中重放返回 `202`，终态重放返回 `200`，`commandId` 不变。
- 同一 key 配不同请求返回 `409 IDEMPOTENCY_KEY_REUSED`。
- 队列容量只统计 `accepted/dispatched`；单 worker 保证同一时刻只有一个游戏写入。每次领取以 SQLite CAS 写入 30 秒租约，进程在派发前崩溃时可安全释放租约并继续。
- 上游明确的非 5xx 拒绝进入 `failed`；默认未授权的 `deletebase` 因此会进入 `failed`。
- 派发后超时、连接中断或上游 5xx 进入 `uncertain`，不会自动重发。
- 服务重启时，尚未派发的 `accepted` 可以继续处理；已经 `dispatched` 但没有终态的命令会记录 `recovered-uncertain`，不会盲目重发。
- 派发前内部故障采用有限退避；连续 5 次后写入 `failed/COMMAND_DEAD_LETTERED` 和 `deadLetteredAt`，`OUTBOX_DEAD_LETTER_PRESENT` 会关闭购买写熔断。若同一 delivery 的全部行都能证明从未 `dispatched`，receipt 可按明确失败退款；只要有任一行已成功、部分成功或不确定，就必须人工复核且不能全额自动退款。
- `uncertain` 必须先在游戏状态、PalDefender 日志和审计记录中人工对账，再决定是否使用一个新 key 发起补偿操作。

首次升级会在一个 SQLite 事务内只读导入旧 `CommandPersistence.DataDirectory/paldefender-command-audit.jsonl`。导入成功后源文件改名为 `.jsonl.migrated-to-sqlite-v1` 继续保留，但不再参与派发；重复 key 冲突、未知 command、损坏的非末行事件、迁移后的源文件篡改或任何部分导入都会使 Control API 启动失败。不要手工把归档改回原名；回滚旧版本前应先恢复配套经济快照。

购买扣款/订单事务与 PalDefender `accepted` 事务目前是同库的两个独立事务。中间崩溃由持久 delivery request、不可变 receipt request 和确定性 `shop:<orderId>:delivery:*` key 补齐，不能把它描述成一次跨步骤原子提交；恢复 worker 会优先查找已存在命令，绝不生成新 key 盲发。

查询命令和审计：

```powershell
$api = 'http://127.0.0.1:5180/api/v1'
$adminHeaders = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_VIEWER_KEY }
Invoke-RestMethod "$api/paldefender-commands/<commandId>" -Headers $adminHeaders
Invoke-RestMethod "$api/audit/paldefender-commands?limit=100" -Headers $adminHeaders
```

## 验证与验收记录

启动 PalServer 和 Control API 后先检查：

```powershell
$api = 'http://127.0.0.1:5180/api/v1'
$adminHeaders = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_VIEWER_KEY }
Invoke-RestMethod "$api/servers/local/paldefender/status" -Headers $adminHeaders
Invoke-RestMethod "$api/servers/local/paldefender/catalog" -Headers $adminHeaders
```

这里的 `PAL_CONTROL_VIEWER_KEY` 是 Control API 的只读管理凭据，不是 PalDefender token；两者都只能从密码管理器注入受控进程。

验收标准是 `status.connected=true`、`catalog.count=27`，且 `version` 与上述固定版本一致。2026-07-11 本机联调中，10 个白名单 GET 均返回 `200`；`Alert` 完成了 `202 accepted -> succeeded` 闭环，查询终态为 `200`；相同 key 和请求返回相同命令，复用 key 但改变请求返回 `409`。

升级 Palworld 或 PalDefender 后，必须重新完成摘要核对、10 个 GET 冒烟、至少一个低风险 POST 的幂等闭环、CORS/Origin 检查以及 `uncertain` 对账演练，才能更新本手册中的固定组合。

## 故障处置

- `PALDEFENDER_DISABLED`：启用本机配置，确认 token 或 token 文件存在，然后重启 Control API。
- `PALDEFENDER_TOKEN_UNAVAILABLE`：检查 token JSON 的 `Token` 字段和 ACL，不要把 token 复制到代码。
- 上游 `401/403`：核对具体目录项所需权限；`deletebase` 默认出现 `403` 是预期的最小权限行为。
- 带有效 bearer 仍被拒绝：首先核对请求 `Origin` 与 `Cors.Allowed-Origins` 是否逐字一致。
- `PALDEFENDER_COMMAND_QUEUE_UNAVAILABLE` 或持久化失败：停止写入，检查经济数据目录空间/ACL、`PRAGMA integrity_check`、migration marker 与是否有第二个 Control API 实例持有 queue lock。
- `COMMAND_DEAD_LETTERED` / `OUTBOX_DEAD_LETTER_PRESENT`：保持购买熔断关闭，核对该命令从未进入 `dispatched`，修复依赖后创建经过审计的人工处置；不要直接改 SQLite 状态或删除不可变事件。
- `uncertain`：禁止自动重试，按上节人工对账。
- guarded start 拒绝 Steam build 或摘要：保持服务器停止，重新做兼容性评审；不要跳过校验或只替换其中一个 DLL。

回滚时先停止 PalServer。首次安装没有旧版备份时，把 `d3d9.dll` 与 `PalDefender.dll` 一起移出 `Win64`；升级场景则恢复安装脚本创建的同批次备份，或重新安装一个已审查且摘要固定的完整 release。不要混用不同 release 的两个 DLL。
