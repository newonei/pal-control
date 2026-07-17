# Windows 单实例生产部署入口

此目录提供方案 A 当前唯一受支持的生产拓扑：一台 Windows 主机、一个 Control API 进程及其进程内 hosted workers、一个 Caddy 进程、一个 SQLite 权威库。仓库目前没有 Dockerfile/Compose，也没有 PostgreSQL、多 Control API 实例或多 worker 节点实现；不要把本入口解释为容器或 HA 支持。

## 文件

- `Invoke-PalControlDeployment.ps1`：校验并暂存固定发布物，安装/更新最小权限 Windows Service，执行升级前排空检查、停服冷快照、启动迁移、readiness 验证、失败回切和同数据契约二进制回滚。
- `PalControl.Deployment.psm1`：路径/reparse 防护、发布清单逐文件 SHA-256、原子 JSON、冷快照与恢复函数。
- `../build-release.ps1`：从源码构建 self-contained `win-x64` 发布物，写入 commit、dirty 标记、数据契约和逐文件清单，并在外层生成 ZIP/安装器 SHA-256。

完整步骤、目录、密钥轮换和故障处置见 [生产部署、升级与恢复手册](../../../docs/runbooks/production-deployment-and-recovery.md)。

## 安全属性

- 生产入口拒绝 dirty/未知提交、错误 ZIP hash、未声明/篡改文件、路径逃逸、reparse point 和同 releaseId 不同内容。
- 程序文件位于不可变 `releases/<releaseId>`；配置、密钥、SQLite、日志、备份、Caddy TLS 数据与部署快照全部位于独立 `%ProgramData%` 状态树。
- Control API 与 Caddy 使用不同的 `NT SERVICE\...` 虚拟账户，自动启动，并由 SCM 以 5/15/60 秒失败重启策略守护。当前 dev37-ro Native pipe ACL 精确固定默认 `NT SERVICE\PalControl.ControlApi` 的 service SID；如自定义服务名，必须先更新 Native 锁、ACL 编译参数和候选摘要并重新走兼容评审，不能只改部署脚本参数。
- Control API 读取 `PAL_CONTROL_CONFIG_PATH` 指向的外部配置；环境变量和命令行仍具有更高优先级。资源目录通过 `Palworld:ResourceCatalogPath` 从状态树注入，不复制进发布物。
- Caddy 在切换期间先停止，Control API 通过 `/health/ready` 后才重新启动公网边界。失败时恢复停服冷快照、旧二进制和旧静态根；未完成迁移不会暴露给玩家。
- 已上线后的普通回滚只允许相同 `dataContract`，并保留当前账本和未决状态；跨契约降级被拒绝，必须走 staging 恢复和人工核对。

## 自动验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\tests\integration\windows-production-deployment-smoke.ps1
```

该 smoke 使用临时目录和显式测试 adapter 验证服务切换，再用外置配置真实启动 Control API 两次，检查 SQLite integrity/foreign keys、核心迁移表和迁移指纹。测试 adapter 必须同时提供 `-EnableTestHooks` 与进程级 `PAL_CONTROL_DEPLOYMENT_TEST_HOOKS=1`，且只允许临时目录；生产入口不能启用。
