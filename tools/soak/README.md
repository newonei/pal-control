# Control API 24 小时浸泡验证器

该工具对**一个已经启动的 Control API 进程**施加固定速率的只读 GET 负载，默认持续 24 小时、每 60 秒采样一次，再停止负载观察 120 秒恢复情况。它不会创建玩家会话、不会调用写接口，也不会连接 Palworld 或修改经济数据库。

每个样本包含以下聚合数据：

- 目标进程 working set、private bytes、句柄数和线程数；
- Control API 自报的 .NET GC heap、累计分配量和 Gen 0/1/2 回收次数（接口可用时）；
- 数据目录中 SQLite 主库、`-wal` 和 `-shm` 的文件总数/总字节数；
- 日志目录的文件数和总字节数；
- 活跃玩家门户会话总数；
- delivery、settlement 和 outbox 队列的 pending/capacity；
- liveness、readiness、Viewer 运维接口和固定负载的成功率、状态码与延迟。

报告不会记录 API key、请求/响应正文、会话标识、Cookie、CSRF、玩家身份、进程命令行、机器名、目录路径、URL 或文件名。它只保存进程/目录绑定字段的组合 SHA-256。探针会完整读取有界响应体后计时；异常只写固定错误码，不写可能包含敏感内容的异常消息。

## 运行前准备

1. 使用专门的 `Viewer` 管理员主体，不要使用 Owner 密钥。工具会先匿名读取 `/health/instance`，把 PID、进程启动时间、数据目录指纹和日志目录指纹与本机参数匹配；只有匹配后才发送 Viewer 密钥。
2. 确认 Control API 仅监听回环地址，`/health/live`、`/health/ready` 和运维 overview 正常。
3. 找到 Control API 的 PID、外置 SQLite 数据目录和日志目录。
4. 选择一个尚不存在、且与数据/日志目录完全分离的报告目录；工具会在 sibling staging 中写完报告与 hash 后原子提交目录，并拒绝重解析路径、目录嵌套和覆盖既有证据。
5. 在隔离终端中临时设置 `PAL_CONTROL_SOAK_API_KEY`；不要把密钥写进命令参数、脚本、历史或报告目录。运行结束后立即清除该进程环境变量。

构建发布物：

```powershell
dotnet publish .\tools\soak\PalControl.Soak.csproj -c Release -o C:\PalControl\tools\soak
```

默认的 24 小时正式运行：

```powershell
$env:PAL_CONTROL_SOAK_API_KEY = '<从密码管理器注入的 Viewer 密钥>'
try {
    dotnet C:\PalControl\tools\soak\PalControl.Soak.dll `
        --pid 1234 `
        --base-uri http://127.0.0.1:5180 `
        --data-directory C:\PalControl\data\economy `
        --log-directory C:\PalControl\logs `
        --output-directory C:\PalControl\evidence\soak-20260717 `
        --duration-seconds 86400 `
        --sample-interval-seconds 60 `
        --recovery-seconds 120 `
        --requests-per-second 1 `
        --thresholds C:\PalControl\tools\soak\thresholds.production.json
}
finally {
    Remove-Item Env:\PAL_CONTROL_SOAK_API_KEY -ErrorAction SilentlyContinue
}
```

`--base-uri` 只接受无用户名/密码、无路径/查询串的 loopback HTTP(S) origin。固定负载路径有硬编码只读 allow-list，默认是 `/health/live`；工具不会接受任意 URL 或 HTTP 方法。

## 判定与证据

成功时退出码为 `0`，阈值或完整性失败为 `2`，启动配置不可用为 `1`。无论运行期是否中断，只要已经进入采样阶段，工具都会尽力输出：

- `report.json`：`pal-control-soak-canonical-json-v1` 规范化 UTF-8 JSON；对象键按 ordinal 排序，数组保持采样顺序，没有 BOM 和多余空白；
- `report.json.sha256`：上述 JSON 精确字节的小写 SHA-256。

分析器对以下情况 fail-closed：样本数或实际持续时间不足、PID 退出/复用、关键指标缺失、探针/负载失败率超限，以及 working set、private bytes、GC heap、句柄、线程、SQLite DB/WAL/SHM、日志、会话或队列的持续斜率、峰值或恢复值超限。GC 是唯一可选指标；旧版本接口没有 GC 聚合时会产生 `gc_metrics_unavailable` warning，其他核心指标缺失均失败。

生产阈值在 [`thresholds.production.json`](thresholds.production.json) 中逐项显式固定并嵌入程序集。正式模式至少运行 24 小时；即使传入 `--thresholds`，其原始字节也必须与内嵌版本完全一致。报告记录 `evidenceProfile=production-24h-v1` 和阈值 SHA-256。CI 才允许自定义阈值，且报告固定标记 `ci-non-acceptance`。分析同时要求至少完成计划请求数的 95%、请求失败率和窗口 P95 不超限；慢响应、漏发或只快速返回 header 都不能通过。调整正式阈值必须发布新的版本化 profile，不能为了让失败结果变绿而事后放宽阈值。输出目录是一次性证据目录；重复执行必须使用新目录，工具不会覆盖已有报告。`totalAllocatedBytes` 和 GC collection 次数按设计单调增长，只作为诊断证据；GC heap 才参与泄漏判定。

仓库测试中的 `--ci-mode` 仅允许 3–300 秒的短运行，用来验证真实子进程采样、规范化/哈希和分析器故障路径。它不能替代真实环境 24 小时证据，因此不能据此勾选 TODO 的外部 soak 验收项。
