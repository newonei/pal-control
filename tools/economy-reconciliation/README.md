# SQLite 经济迁移一致性审计器

这个工具为当前单机 SQLite `dataContract=1` 提供只读、可重复的迁移前后核对。它不会执行迁移、修库、退款、重发或 PostgreSQL 导入，也不能代替真实备份恢复演练。

## 核对内容

- 在一个只读 SQLite 一致性事务中执行 `integrity_check`、`foreign_key_check` 和 `user_version` 检查；
- 按 `extraction_events.sequence` 逐账户重算每个永久/本周钱包，逐条核对 ledger `BalanceAfter`、最终余额和 revision；
- 核对订单行价、charges、扣款/退款 ledger、当前 delivery attempt 与状态；
- 核对 delivery 与 PalDefender outbox command、结构化 receipt、库存 evidence 的 ID/幂等键关联；
- 核对资源兑换 run 的 SQL CAS 列、payload、唯一 run credit 和 `extraction_run` ledger；
- 核对 purchase/refund/wallet 幂等 scope、request hash 和目标资源；
- 为当前逻辑投影和每个 SQLite 应用表的每一行生成带域隔离的 canonical SHA-256，并提供逐账户聚合 hash。

报告不输出账户 ID、Steam ID、PlayerUID、显示名、订单 ID、run ID、幂等键、请求体或凭据。定位键统一为不可逆 fingerprint；报告仍应作为受控运维证据保存。

## 使用

先在维护排空并停写后的已验证副本上生成基线：

```powershell
dotnet run --project .\tools\economy-reconciliation\PalControl.EconomyReconciliation.csproj `
  -c Release -- `
  --database C:\staging\before\extraction-commerce.db `
  --output C:\staging\evidence\before.json
```

迁移/复制后严格比较：

```powershell
dotnet run --project .\tools\economy-reconciliation\PalControl.EconomyReconciliation.csproj `
  -c Release -- `
  --database C:\staging\after\extraction-commerce.db `
  --baseline C:\staging\evidence\before.json `
  --output C:\staging\evidence\after.json
```

退出码 `0` 表示当前库自身有效且逻辑、物理表、逐行 hash 均与基线一致；`2` 表示稳定核对问题或基线差异；`3` 表示输入、SQLite 打开或审计本身失败。失败时不要手工改 hash、删 row 或继续切换生产，先保留源/目标库、WAL/SHM、报告和迁移日志。

当前比较是同一 SQLite 数据契约的严格比较。未来若真正迁移 PostgreSQL，必须另外实现目标端 reader、类型/排序规范和逐表映射评审；不能把本工具的 SQLite 复制通过宣称为 PostgreSQL/HA 已完成。

## 自动验证

```powershell
.\tests\integration\economy-reconciliation-smoke.ps1
```

测试使用真实 `SqliteExtractionRepository` 创建账户、双钱包、已交付订单/delivery、PalDefender command/receipt/evidence、资源兑换 run、唯一 credit 与幂等记录；随后验证 JSON 格式变化 hash 不变，并分别注入余额、幂等、run credit、delivery/receipt/逐物品 command 引用和物理事件列篡改，要求稳定失败码和基线行差异。
