# SQLite 经济迁移前后核对手册

## 边界

当前只支持单机 `extraction-commerce.db`、SQLite `user_version=1` 和仓库定义的 canonicalization v1。本流程是迁移/复制的只读验收门禁，不执行 PostgreSQL 迁移，不修复余额，不重发 delivery，也不允许绕过 `uncertain` 人工对账。

## 1. 取得迁移前基线

1. 进入 maintenance，确认 active operation、订单/run、delivery/settlement/outbox queue、`uncertain` 和未完成周换档全部为 0。
2. 创建并验证经济一致性快照；不要直接复制仍写入中的 `.db` 而遗漏 WAL。
3. 在 staging 副本上运行：

```powershell
dotnet run --project .\tools\economy-reconciliation\PalControl.EconomyReconciliation.csproj `
  -c Release -- `
  --database C:\staging\before\extraction-commerce.db `
  --output C:\staging\evidence\before.json
```

只有退出码为 `0`、`dataValid=true`、SQLite integrity/foreign keys 为 true 且 `issues=[]` 时，基线才可批准。报告只含 fingerprint/hash，但仍按生产证据限制访问。

## 2. 迁移后严格比较

先在隔离 staging 完成目标数据库生成和应用启动迁移，再保持停写运行：

```powershell
dotnet run --project .\tools\economy-reconciliation\PalControl.EconomyReconciliation.csproj `
  -c Release -- `
  --database C:\staging\after\extraction-commerce.db `
  --baseline C:\staging\evidence\before.json `
  --output C:\staging\evidence\after.json
```

批准条件全部为真：

- `success=true`、`dataValid=true`、`baselineComparison.match=true`；
- `domainMatch=true`：账户、钱包、ledger、订单、delivery、run/credit、幂等和副作用证据一致；
- `physicalMatch=true`、`changedTables=[]`、`changedRowHashCount=0`：所有 SQLite 应用表逐行 canonical hash 一致；
- 每个账户的 wallet scope、ledger/order/delivery/run 数量和聚合 hash 一致。

JSON 对象属性顺序和空白不会改变 canonical hash；真实字段、类型、引用、SQL 列或行集合变化必须失败。不要把“总行数相同”或“总余额相同”当作通过。

## 3. 失败处置

- 保持生产边界关闭，保留 before/after 数据库、WAL/SHM、两份报告、发布 manifest 和迁移日志；
- 按稳定 issue code 定位类别，使用 fingerprint 与受控库内查询关联，报告中不追加原始玩家标识；
- 钱包/ledger、run credit、订单退款或 delivery/outbox/receipt 任一不一致都禁止上线，不能用补写 SQL、生成新幂等键或盲重发“修平”；
- 若 schema 本来就应变化，必须先评审新的 canonicalization/mapping 版本和预期新增/转换行，不能使用旧报告强行通过。

## 4. 生产切换与回滚

两人复核快照、两份报告和目标版本后才可切换。切换后默认 maintenance，再执行 capabilities/readiness 和抽样账本/未决副作用检查。已恢复流量并产生新交易后，只允许同数据契约二进制回滚并保留当前数据库；不得恢复旧基线覆盖新交易。

完整部署回切见 [单实例生产部署、升级与恢复手册](production-deployment-and-recovery.md)，经济快照/周换档见 [经济备份、恢复与持久化周换档手册](economy-continuity-and-weekly-rollover.md)。

## 仍未完成

- PostgreSQL 目标 reader、字段/类型映射、transactional outbox 与 lease；
- 真实生产经济库的独立迁移和恢复演练；
- 空白 Windows VM 非实现者实操与 24 小时 soak。

仓库自动化入口：

```powershell
.\tests\integration\economy-reconciliation-smoke.ps1
```
