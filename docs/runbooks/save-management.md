# 存档中心运行手册

## 当前能力

存档中心 v1 提供以下受控操作：

- 识别当前运行的 `PalServer.exe`、`DedicatedServerName`、活动世界目录和官方 REST `worldguid`，四者不一致时拒绝写操作；
- 调用官方 `POST /save` 立即保存世界；
- 只读列出 `<world>/backup/world` 中由 Palworld 维护的原生轮转快照；
- 保存后等待一个新快照连续多次保持稳定，再复制到活动世界之外的受管备份根目录；
- 为每个复制文件记录大小、修改时间和 SHA-256，发布前完整复核；
- 通过独立 JSONL 队列持久化幂等键、状态变化和审计事件；派发后无法证明结果时进入 `uncertain`，不会自动重发。

v1 明确不提供恢复、删除、上传、在线覆盖或原始 `.sav` 字段编辑。

## 配置

开发环境默认配置位于 `services/control-api/appsettings.json`：

```json
"SaveManagement": {
  "BackupRoot": "../../backups/savegames",
  "RequireRunningProcess": true,
  "SnapshotTimeoutSeconds": 45,
  "StabilitySampleMilliseconds": 750,
  "StabilityRequiredSamples": 3,
  "MinimumFreeSpaceBytes": 1073741824
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

## 故障处理

- `ACTIVE_WORLD_IDENTITY_MISMATCH`：核对 `GameUserSettings.ini` 的 `DedicatedServerName`、实际世界目录名及官方 `/info` 的 `worldguid`。
- `PALWORLD_PROCESS_MISMATCH`：当前运行的 `PalServer.exe` 不来自配置的安装根目录，或服务尚未运行。
- `NEW_NATIVE_SNAPSHOT_NOT_OBSERVED`：游戏接受了保存请求，但超时前没有观察到新的稳定原生快照；命令会标记为 `uncertain`，且不会发布受管备份。
- `BACKUP_DISK_SPACE_INSUFFICIENT`：释放备份盘空间或调整容量规划；不建议取消保留余量。
- `BACKUP_INTEGRITY_FAILED`：受管备份与 manifest 不一致。将其视为不可用证据，保留现场和审计，不要手工改写 manifest。
- `SAVE_REPARSE_POINT_REJECTED`：存档或备份路径中检测到 junction、symlink 或其他重解析点。移除该路径结构后再检查。

## 验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests/integration/save-backup-smoke.ps1
```

该冒烟测试使用临时 Palworld fixture，覆盖保存幂等、稳定快照复制、manifest、正常与篡改校验、缺少新快照、REST 结果不确定、审计、重启恢复和禁止重复发送。
