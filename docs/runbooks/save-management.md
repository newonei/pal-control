# 存档中心运行手册

## 当前能力

Control API 存档中心 v1 提供以下受控操作：

- 识别当前运行的 `PalServer.exe`、`DedicatedServerName`、活动世界目录和官方 REST `worldguid`，四者不一致时拒绝写操作；
- 调用官方 `POST /save` 立即保存世界；
- 只读列出 `<world>/backup/world` 中由 Palworld 维护的原生轮转快照；
- 保存后等待一个新快照连续多次保持稳定，再复制到活动世界之外的受管备份根目录；
- 为每个复制文件记录大小、修改时间和 SHA-256，发布前完整复核；
- 通过独立 JSONL 队列持久化幂等键、状态变化和审计事件；派发后无法证明结果时进入 `uncertain`，不会自动重发。

Control API v1 明确不提供恢复、删除、上传、在线覆盖或原始 `.sav` 字段编辑。仓库另行提供只能由运维人员在服务器本机运行的离线世界恢复工具；它不是 HTTP API，也不能在 PalServer 运行时切换世界。

## 离线世界恢复

[`tools/world-restore`](../../tools/world-restore/) 只接受存档中心发布的受管备份。它不会信任“目录看起来像备份”，而会逐项验证：

- 备份根目录只能包含 `manifest.json`、`verification.json` 和 `data`；`data` 中不得有 manifest 未声明的文件或空目录；
- `verification.json` 中的 SHA-256 必须锚定 `manifest.json` 的原始字节，且两者都必须声明 `verified`；
- manifest 的 schema、备份 ID、服务器 ID、World GUID、稳定一致性状态、逐文件相对路径、长度和 SHA-256 必须全部有效；
- 备份、活动世界、配置文件、staging 及其祖先链中不得出现 symlink、junction 或其他 reparse point；绝对路径、`..`、重复/大小写冲突路径和 Windows 保留名均拒绝；
- `PalServer.exe`、`GameUserSettings.ini`、`DedicatedServerName`、活动世界目录名、命令行 World GUID 和备份 World GUID 必须属于同一个安装与世界身份。

### 1. 生成恢复计划

执行本节前必须先完成下一节的一次性审批信任库部署，并由独立的安全/变更负责人对信任库原始字节计算 SHA-256，将 64 位小写 pin 发布到不可变工单、制品清单或由恢复执行人无写权限的受控配置。恢复执行人只能抄录已发布 pin；不得在事故现场用当前 `trusted-approvers.json` 自算 hash、自建两把密钥并自行发布。工具没有“从当前信任库自动采用 pin”的功能。

以下路径均必须先解析为绝对路径。生成 plan 前就必须禁用 Windows Service、计划任务、看门狗和面板自动重启并停止三种 PalServer 进程；不能先在线生成 plan 再停服。计划命令会冻结活动世界完整文件及空目录 inventory 摘要，把备份复制到活动世界同一父目录下的唯一 staging 目录，再重新验证来源、原世界和 staging；它不会改名、覆盖或删除活动世界：

```powershell
$repo = (Resolve-Path .).Path
$backup = (Resolve-Path 'C:\ProgramData\PalControl\backups\savegames\local\<backup-id>').Path
$world = (Resolve-Path 'C:\PalServer\Pal\Saved\SaveGames\0\<world-guid>').Path
$settings = (Resolve-Path 'C:\PalServer\Pal\Saved\Config\WindowsServer\GameUserSettings.ini').Path
$server = (Resolve-Path 'C:\PalServer\PalServer.exe').Path
$evidence = 'C:\ProgramData\PalControl\restore-evidence'
$trustStorePin = '<外部已发布的 64 位小写 SHA-256>'

dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- `
  plan --backup-dir $backup --active-world-dir $world `
  --server-id local --world-guid '<world-guid>' `
  --settings-file $settings --palserver-executable $server `
  --evidence-dir $evidence --trust-store-sha256 $trustStorePin
```

命令返回 `planPath` 和 `planSha256`。计划文件是无尾随换行、键按 ordinal 排序的 canonical JSON；同名 `.sha256` 必须一同保留。schema v3 计划包含受管 manifest hash、身份、绝对路径、原世界与候选世界 inventory hash/摘要、停服门禁证据、staging 位置和外部信任库 pin。后续 apply 会在 plan-only 初验、rollback 前、journal 前及首次移动前重验原世界；文件、空目录、plan、sidecar、pin、备份或 staging 任一变化都会 fail closed。

### 2. 配置两个独立审批密钥

首次部署时由两个审批人分别在受控介质生成 ECDSA P-256 密钥。工具核对 NIST P-256 曲线 OID `1.2.840.10045.3.1.7`，不会只凭 256 位长度接受 secp256k1 等其他曲线。私钥不得进入同一共享目录、仓库、聊天、工单正文或命令输出；恢复主机只配置公钥：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- keygen `
  --private-key-file 'E:\ops-a-private.pem' `
  --public-key-file 'C:\ProgramData\PalControl\approvers\ops-a-public.pem'
```

信任库示例（`publicKeyPem` 填入对应公钥 PEM 全文）。根对象和每个 key 只允许所列字段；嵌套重复属性、未知属性、注释和尾随逗号均拒绝，不能依赖 JSON last-wins：

```json
{
  "schemaVersion": 1,
  "keys": [
    {
      "subject": "ops-a",
      "algorithm": "ecdsa-p256-sha256",
      "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
    },
    {
      "subject": "ops-b",
      "algorithm": "ecdsa-p256-sha256",
      "publicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
    }
  ]
}
```

每位审批人分别签署刚生成的计划；默认有效期 10 分钟，允许范围为 1–15 分钟：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- approve --plan-file '<planPath>' `
  --subject ops-a --reason 'Approve incident restore after evidence review' `
  --private-key-file 'E:\ops-a-private.pem' `
  --output-file 'C:\ProgramData\PalControl\restore-evidence\approval-a.json' `
  --trust-store-sha256 $trustStorePin
```

schema v3 执行签名声明 `purpose=execute`，绑定 `operationId`、服务器、世界、备份 ID、manifest SHA-256、计划 SHA-256、原始/候选 inventory 摘要和外部信任库 SHA-256。旧版未绑定 pin/inventory 的审批、recovery-purpose 审批、pin 与计划不同、信任库原始字节 hash 不同、主体相同、审批 ID 重复、超过 15 分钟、已过期、非信任公钥签发或其他绑定值不同，执行都会拒绝。

### 3. 默认 plan-only 与显式执行

先在没有 `--execute` 的情况下复核。该命令只重新验证计划、sidecar、活动身份、备份和 staging，不读取审批，也不切换目录：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- apply --plan-file '<planPath>'
```

安排停机并确认玩家已离线后，先禁用所有可能启动该安装的 Windows Service、计划任务、看门狗和面板自动重启策略，再停止目标安装下的 `PalServer.exe`、`PalServer-Win64-Shipping-Cmd.exe` 和 `PalServer-Win64-Shipping.exe`；在 result 完整复核或 journal recovery 完成之前不得解除启动互锁。进程路径门禁只能发现检查当时已经存在的进程，无法消除外部 supervisor 在检查后立即拉起 PalServer 的竞态；不能证明自动拉起已禁用时不得执行恢复。只有显式 `--execute`、两个不同有效审批和信任库同时提供，工具才会继续；它会在初验、首次移动、最终候选移动和故障恢复移动前重复进程路径门禁：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- apply --plan-file '<planPath>' --execute `
  --trust-store 'C:\ProgramData\PalControl\approvers\trusted-approvers.json' `
  --trust-store-sha256 $trustStorePin `
  --approval-file 'C:\ProgramData\PalControl\restore-evidence\approval-a.json' `
  --approval-file 'C:\ProgramData\PalControl\restore-evidence\approval-b.json'
```

执行顺序固定如下：

1. 对活动世界做同父目录冷 rollback copy，并比较每个文件、空目录、长度和 SHA-256；复制前后的活动世界必须与 plan 冻结摘要一致。
2. 再次验证停服、身份、受管备份、staging、外部 pin 和两份当前执行审批。
3. 在 evidence 下创建 operation 专属且与活动世界同卷的 authorization 目录，以 create-new + `Flush(true)` 固化 pinned trust store 和两份执行审批；journal/result 只引用快照路径与 hash，临时审批删除或原信任库轮换不会改变已开始的恢复。
4. 写入 `prepared` journal 并再次重验原世界后，将活动世界原子改名为 `.palcontrol-world-retired-<operationId>`，再将 staging 原子改名为活动 World GUID。
5. 对新活动世界逐文件/目录复核；成功后生成 canonical result JSON 与 `.sha256`。
6. 同一 apply 进程捕获到切换异常时，可使用刚验证且已快照的执行上下文自动回退；候选世界保留为 `.palcontrol-world-failed-<operationId>`，原目录按 plan inventory 恢复，同时写入 failure JSON 与 `.sha256`。若进程已经崩溃，`apply` 不会代替人工 recover 移动任何目录。

工具代码没有删除世界或备份的路径。成功后受管备份、冷 rollback copy 和 retired 旧世界全部保留；失败后受管备份、冷 rollback copy、恢复的旧世界和已移动的候选世界也全部保留。空间清理必须作为另一项有审批、保留策略和独立证据的操作处理，不能附带在恢复中执行。

恢复工具只证明仓库内安全机械过程。合成测试通过不等于真实世界恢复验收；首次生产使用仍必须保留停机窗口、两人身份、原始计划/审批/result、重启后默认关闭经济、游戏内 World GUID/玩家/建筑抽查和独立恢复演练证据。

## 配置

开发环境默认配置位于 `services/control-api/appsettings.json`：

```json
{
  "SaveManagement": {
    "BackupRoot": "../../backups/savegames",
    "RequireRunningProcess": true,
    "SnapshotTimeoutSeconds": 45,
    "StabilitySampleMilliseconds": 750,
    "StabilityRequiredSamples": 3,
    "MinimumFreeSpaceBytes": 1073741824
  }
}
```

生产环境建议使用绝对路径，例如 `C:\\ProgramData\\PalControl\\backups\\savegames`。`BackupRoot` 必须位于活动世界目录之外；不要将其设为 `Pal/Saved/SaveGames`、某个世界目录或其子目录。命令事件目录同样应使用持久化绝对路径。

`CommandPersistence:DataDirectory/save-command-audit.jsonl` 在迁移到其他权威存储前只允许追加，不允许截断、原地压缩、轮转或自动删除。经济一致性快照会把它作为 `save-command-delivery` 通道原字节归档，并在 `command-side-state-archive.json` 记录 SHA-256、权威级别和继承的 bundle 保留策略；未知或损坏 JSONL 会让快照 fail closed。归档只能随整个经济快照进入 plan-only 清理候选，不能单独删除。详见[经济备份、恢复与持久化周换档手册](economy-continuity-and-weekly-rollover.md)。

## 操作与判定

1. 打开管理台“存档”页面，确认“存档链路就绪”以及进程路径、服务器名称和 World GUID 三项校验全部通过。
2. “仅保存世界”只触发官方保存，不创建 Pal Control 独立备份。
3. “保存并创建备份”要求名称和审计原因。命令依次经过 `queued`、`saving-world`、`waiting-snapshot`、`copying`、`verifying`、`completed`。
4. 受管备份显示“已校验”才表示文件与 manifest 一致；可从详情页提交带原因的重新校验。
5. `uncertain` 表示请求可能已经被游戏执行，但 Control API 无法证明最终结果。不要用相同或新幂等键盲目重试；先检查原生快照、受管备份目录和审计记录。

## 常用检查

```powershell
$headers = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_VIEWER_KEY }
Invoke-RestMethod http://127.0.0.1:5180/api/v1/servers/local/saves/status -Headers $headers
Invoke-RestMethod 'http://127.0.0.1:5180/api/v1/servers/local/backups?kind=managed' -Headers $headers
Invoke-RestMethod 'http://127.0.0.1:5180/api/v1/audit/save-commands?limit=100' -Headers $headers
```

`PAL_CONTROL_VIEWER_KEY` 只应从密码管理器注入当前受控进程；不要写入仓库、脚本、命令历史或截图。

写请求必须携带 8–128 字符的 `Idempotency-Key` 和至少 3 字符的操作原因。相同键与相同请求体返回原命令；相同键配不同请求体返回 `409 IDEMPOTENCY_KEY_REUSED`。

## 离线恢复崩溃处置

恢复工具的变更命令会在活动世界同一父目录、同一本地卷上持有跨进程独占锁，并在首次目录移动前写入 canonical crash journal。`plan` 会预先创建确定性的 lock 文件；`status` 只以 `FileMode.Open`、`FileAccess.Read`、`FileShare.Read` 获取该既有文件的共享只读租约，不创建、不截断、不写 owner，也不改变其字节或 mtime。lock 缺失时 `status` fail closed；`apply`/`recover` 持有 `FileShare.None` 时 `status` 同样拒绝，避免并发读取移动中的拓扑。journal 依次记录 `prepared`、`old-retired`、`candidate-active`、`committed`，每次写入都使用同目录原子替换和 `Flush(true)`。不要删除、改写或挪动 lock、journal、staging、rollback、retired 或 failed candidate。

停服门禁同时覆盖 `PalServer`、`PalServer-Win64-Shipping-Cmd` 和 `PalServer-Win64-Shipping`，并按计划中的安装根核对进程路径；任何匹配进程路径无法读取时均 fail closed。活动世界、staging、lock 和 journal 必须在可用本地卷，UNC/网络盘拒绝。工具拒绝所有祖先链重解析点；Windows 仍应使用 `Resolve-Path` 返回的普通盘符路径并通过 ACL 禁止 8.3/device-path 等替代别名访问，因为工具不宣称覆盖全部 NT 对象管理器别名。

若执行进程异常退出，保持 PalServer 停止，先只读查看：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- status --plan-file '<planPath>'
```

确认 plan、journal、authorization 快照和所有保留目录未被人工修改后，两名独立审批人分别针对当前 journal 生成短期 recovery-purpose 审批：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- approve-recovery --plan-file '<planPath>' `
  --subject ops-a --reason 'Approve exact crash recovery after evidence review' `
  --private-key-file 'E:\ops-a-private.pem' `
  --output-file 'C:\ProgramData\PalControl\restore-evidence\recovery-a.json' `
  --trust-store-sha256 $trustStorePin
```

`ops-b` 独立重复后才能恢复：

```powershell
dotnet run --project "$repo\tools\world-restore\PalControl.WorldRestore.csproj" `
  --configuration Release -- recover --plan-file '<planPath>' `
  --trust-store-sha256 $trustStorePin `
  --approval-file 'C:\ProgramData\PalControl\restore-evidence\recovery-a.json' `
  --approval-file 'C:\ProgramData\PalControl\restore-evidence\recovery-b.json'
```

每份 recovery 审批绑定 `purpose=recover`、plan hash、当前 journal 原始字节 hash/state/outcome、原始/候选 inventory 摘要和外部 pin，最长 15 分钟且执行时必须仍有效。旧执行审批、旧 journal 的审批、过期审批或同一主体的两份审批均拒绝。`recover` 先固化 recovery 审批快照，再持有相同独占锁并重复停服门禁，对 rollback、retired、active/staging/failed candidate 做完整 inventory 校验；必要时先保留候选世界，再把原世界移回活动路径并复核。`status` 始终为零持久化写的共享只读租约；若锁文件不存在或变更命令正在持锁则拒绝。若 result 已完整发布但 journal 尚未来得及提交，新双审批后的 recovery 会验证 result 并提交 `restored`，不会错误回退。journal/recovery 会重新核对 schema v3 执行与恢复审批签名、计划 pin、authorization trust 快照 hash 和公钥 fingerprint；不会在恢复时改信任根。result/failure 证据包含审批快照路径/hash、公钥 fingerprint、外部固定的信任库 hash、rollback/retired/candidate 摘要、阶段和每次进程门禁证据。

本工具没有另行配置机器签名密钥；两份当前且不同主体的 recovery 审批就是手工崩溃恢复的授权根。因此两把私钥必须由不同人员或独立系统掌管，不能由单一运维同时持有。

## 故障处理

- `ACTIVE_WORLD_IDENTITY_MISMATCH`：核对 `GameUserSettings.ini` 的 `DedicatedServerName`、实际世界目录名及官方 `/info` 的 `worldguid`。
- `PALWORLD_PROCESS_MISMATCH`：当前运行的 `PalServer.exe` 不来自配置的安装根目录，或服务尚未运行。
- `NEW_NATIVE_SNAPSHOT_NOT_OBSERVED`：游戏接受了保存请求，但超时前没有观察到新的稳定原生快照；命令会标记为 `uncertain`，且不会发布受管备份。
- `BACKUP_DISK_SPACE_INSUFFICIENT`：释放备份盘空间或调整容量规划；不建议取消保留余量。
- `BACKUP_INTEGRITY_FAILED`：受管备份与 manifest 不一致。将其视为不可用证据，保留现场和审计，不要手工改写 manifest。
- `SAVE_REPARSE_POINT_REJECTED`：存档或备份路径中检测到 junction、symlink 或其他重解析点。移除该路径结构后再检查。
- 离线恢复返回 `ERROR`：不要改写 plan、manifest、verification 或 SHA sidecar 来“修复”证据。保留 staging、rollback、retired/failed candidate 和 failure report，先确认 `oldWorldRecovered`；若为 `false`，保持 PalServer 停止并升级为人工事故处置。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/integration/save-backup-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File tests/integration/world-restore-smoke.ps1
```

第一项冒烟测试使用临时 Palworld fixture，覆盖保存幂等、稳定快照复制、manifest、正常与篡改校验、缺少新快照、REST 结果不确定、审计、重启恢复和禁止重复发送。

第二项测试只使用操作系统临时目录、合成 `.sav` 文本和改名后的短时宿主进程，覆盖备份篡改、`..` 路径逃逸、额外文件、Windows junction、三种 PalServer 进程名、停服 plan 与原世界冻结、同世界跨进程锁、P-256 曲线 OID、执行 authorization 快照、apply 默认 plan-only、双审批成功切换、两次目录移动间与候选激活后的真实子进程强杀，以及新鲜双 recovery 审批。缺/错 pin、执行审批复用、过期/同主体 recovery 审批、伪造 OriginalInventory、临时执行审批删除和源信任库轮换均 fail closed；测试还逐字节及 mtime 核对 lock、核对完整 fixture 文件集合，验证 `status` 零持久化写、锁缺失拒绝并与并发 execute 互斥。它不会发现、停止或修改真实 PalServer 与真实存档，也不能替代真实恢复演练。
