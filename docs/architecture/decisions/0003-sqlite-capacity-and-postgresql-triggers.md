# ADR-0003：单实例 SQLite 容量基线与 PostgreSQL 触发条件

- 状态：已接受
- 决策日期：2026-07-16
- 决策人：项目维护者
- 关联路线图：[玩法 TODO](../../../TODO.md)

## 背景

方案 A 当前面向一台 Palworld 服务器和 5–10 名真实玩家试运营。Control API 与 hosted workers 运行在同一个 Windows Service 中，经济账本、资源兑换、订单、发货 outbox、审计和周换档状态由同一个 SQLite `dataContract=1` 文件负责。当前没有多写节点、跨机 worker 接管或无停机高可用目标。

仓库已经具备以下可重复容量证据：

- 团队经济投影覆盖 52 个历史周、10,000 个账户、并发 SQLite/团队写入，并证明活动周选择、历史周隔离和写锁上限；
- `ExtractionRunStore` 覆盖 52 周、10,000 个 run 和 256 路并发结算，普通状态推进只写变化行，credit 保持 SQLite 原子提交；
- 生产 soak 工具能够连续采集进程内存、句柄、线程、GC、SQLite/WAL/SHM、日志、会话、队列、health/readiness 和只读工作负载，并使用冻结阈值生成可校验报告。

这些证据说明当前试运营规模没有理由预先引入第二个权威数据库，但自动化容量测试不能替代 24 小时实机 soak、真实备份恢复或完整周档。

## 决策

当前继续使用单实例 SQLite，不实施 PostgreSQL、多 Control API 写实例、跨机 worker 或 HA，也不把这些能力宣称为已完成。P1-07 中的 PostgreSQL 项按“条件当前未触发”关闭，而不是按“已完成 PostgreSQL 迁移”关闭。

只要满足以下任一条件，就必须重新打开该项并先写新的迁移 ADR：

1. 产品要求两个或更多 Control API/worker 实例同时写入，或要求单机故障期间自动接管；
2. 预计保留窗口内的账户或资源兑换 run 超过当前 52 周/10,000 条容量门禁，且不能通过受控归档保持在门禁内；
3. 固定生产负载的 24 小时 soak 违反已冻结阈值，并在索引、查询、WAL checkpoint、批处理或有界队列调整后仍能重复归因到 SQLite 写争用；
4. 连续真实周档出现无法解释的 `SQLITE_BUSY`/`SQLITE_LOCKED`、写入超时、队列持续增长或 readiness 失败，且应用层限流与单写 worker 不能消除；
5. SQLite 在线备份、停服校验或 staging 恢复无法满足批准的 RPO/RTO 和维护窗口。

触发并不等于可以直接切库。迁移实现必须同时包含 PostgreSQL 目标 reader、字段/类型/排序映射、所有写路径的 transactional outbox、worker lease/接管、节点断连和并发故障测试，以及使用脱敏真实库进行逐账户逻辑核对和逐表物理核对。只有核对全部通过后才能切换权威写面。

当前 canonicalization v1 只用于 SQLite 基线与同契约核对。若迁移设计增加事件 hash 链，必须单独版本化 canonical serialization，不能复用现有 hash 名称改变含义。

## 结果

- 试运营保持更小的故障面、单一事务边界和已有的恢复路径；
- 24 小时 soak、真实周档和恢复演练继续是发布门禁，不能因本 ADR 被跳过；
- 容量或可用性越线时有明确、可复查的重新立项条件；
- SQLite 基线工具只能证明源数据和同契约副本一致，不能冒充 PostgreSQL 迁移证据。
