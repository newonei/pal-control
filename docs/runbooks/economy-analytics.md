# 运营分析面板与指标口径

运营分析用于回答方案 A 的五类问题：玩家是否进入玩法、商城商品是否被查看并完成送达、资源报价是否完成结算、哪些兑换区被实际使用、双币供需和异常终态是否健康。它是管理侧只读能力，不控制价格、内容版本、发货、扣物、调账或熔断。

## 安全与数据边界

- 管理台只调用 Viewer 权限的 `GET /api/v1/economy/analytics`，不存在客户端事件上传接口。
- 订单、发货、资源结算、钱包账本、身份绑定和内容版本均从 `extraction-commerce.db` 的权威记录重新计算。
- 门户会话与目录访问是服务端在已认证的 `/player/me/overview`、`/player/me/catalog` 成功返回时写入的辅助分母事实。事实按 `账户 + 服务器 + 赛季 + 内容版本 + 业务日 + 类型` 唯一，刷新或重试不会重复计数。
- 响应不含 account ID、PlayerUID、Steam/platform subject、显示名、会话或原始事件明细。非零账户群小于 5 时，人数、次数、金额和比率一并隐藏。
- 管理接口必须继续限制在 loopback/受信管理网络。不要把 `/api/v1/economy/*` 加入玩家门户的公网反向代理白名单。

辅助分母从安装该版本后才开始积累。历史订单和资源结算可以重算，但上线前发生的目录访问无法追溯；出现 `CATALOG_DENOMINATOR_INCOMPLETE` 时，不得用订单数反推访问数，也不得把购买率当作 0%。

## 使用方法

1. 启动 Control API 和管理台，并用至少具有 `Viewer` 角色的管理凭据登录。
2. 在左侧打开“运营分析”。默认读取最近 7 个已完成业务日，默认不包含当天。
3. 选择服务器、开始/结束日期和日期口径。需要复盘单周或单次内容发布时，再填写赛季 ID 或内容版本 ID。
4. 先确认顶部显示“稳定窗口”，并检查“来源覆盖完整”和告警区，再解释比率。
5. 翻页只影响商品和区域明细；漏斗、资源兑换汇总、双币健康和来源 hash 在各页保持一致。

也可以在本机管理边界直接读取：

```powershell
$headers = @{ "X-Pal-Admin-Key" = $env:PAL_CONTROL_ADMIN_KEY }
$uri = "http://127.0.0.1:5180/api/v1/economy/analytics?serverId=local&from=2026-07-01&to=2026-07-07&dateBasis=business&limit=50"
$report = Invoke-RestMethod -Headers $headers -Uri $uri
$report.source
$report.alerts
```

窗口最长 93 天，`limit` 为 1–100。`cursor` 只能使用上一个响应的 `page.nextCursor`；不要自行把它当作记录 ID。

## 日期、版本与稳定性

| 筛选 | 含义 |
| --- | --- |
| `business` | 优先使用内容/结算冻结的业务日；旧记录才按配置的业务时区换算。适合周世界运营复盘。 |
| `utc` | 按事实的 UTC 时间戳归日。适合故障排查和跨系统日志对齐。 |
| `seasonId` | 只统计属于所选服务器的该赛季；不匹配时返回稳定 404。 |
| `contentVersionId` | 只统计属于所选服务器的不可变内容版本。身份绑定本身没有内容版本，因此“已绑定或登录”只使用同版本的服务端会话事实。 |

`window.stable=false` 表示窗口包含当前业务日或 UTC 日，数据仍会增长；默认查询避开当天。`stableThrough` 是当前可视为封口的最后日期。稳定只表示日期已结束，不代表所有 `uncertain` 已人工对账，也不替代周换档冻结证据。

## 指标定义

“玩法漏斗”包含两个业务分支，不能把资源报价误解为商城送达后的下一步：

| 阶段 | 权威定义 |
| --- | --- |
| 账户 | 截至窗口结束，在所选服务器/赛季/版本存在会话、绑定、订单或兑换事实且账户记录有效的去重账户。 |
| 已绑定或登录 | 窗口内完成身份绑定复核，或成功读取本人门户的去重账户。 |
| 目录访问 | 窗口内成功读取当前内容目录的去重账户；同一账户同一业务日/版本重复刷新只计一次。 |
| 创建订单 | 窗口内创建不可变商城订单的去重账户和订单数。 |
| 已送达 | 上述订单中最终状态为 `Delivered` 的去重账户和订单数；失败、退款、处理中和 `uncertain` 均不算成功。 |
| 资源报价 | 窗口内形成持久化资源报价 run 的去重账户和 run 数。 |
| 资源已结算 | 上述 run 中最终状态为 `Settled` 的去重账户和 run 数；失败与 `uncertain` 不算成功。 |

其余指标：

- 商品购买率 = 同 SKU 已送达去重买家 ÷ 看过包含该 SKU 的同内容版本目录的去重账户。买家不属于可证明的访问分母时，`denominatorComplete=false`，页面显示“分母不完整”。
- 资源兑换转化率 = 已结算去重账户 ÷ 已报价去重账户。次数、结算价值和 `uncertain` run 单列，不以失败重试制造成功率。
- 区域热度按冻结在 run 上的 `zoneId` 汇总报价、结算、`uncertain` 和成功结算价值，不使用前端地图点击。
- 双币健康从账本重放永久商域币和本周战备券的流入、流出、净值、窗口末余额 P50/P95、最小值和最大值。账本 `BalanceAfter` 不一致或出现负余额会产生 critical 告警。
- `uncertain` 分别展示订单、发货与资源结算。任何非零值都必须进入人工对账，不能并入成功或失败。

账户群为 0 时可以显示 0；非零但少于 5 时显示“少样本隐藏”。隐藏值不能通过相邻总计、比率或金额倒推出个体贡献。

## 来源证据与故障处理

`source.asOf` 是本次一致性读取所见权威事实中的最新时间；`tables` 和 `rowsRead` 说明本次重算读取范围；`recomputationHash` 将筛选条件与排序后的来源事实绑定。相同数据库快照和相同业务筛选在重启、刷新和翻页后应得到相同 hash。它是重算证据，不是内容版本 hash；数据库新增无关事实后也可能变化。

按以下顺序处理告警：

1. `LEDGER_PROJECTION_MISMATCH`、`NEGATIVE_WALLET_BALANCE`、`ECONOMY_UNCERTAIN_PRESENT`：停止用面板结论调整经济，先到经济运营工作台核对账本和不确定终态。
2. `CATALOG_DENOMINATOR_INCOMPLETE`：商品购买率不可用；检查部署时间、内容版本以及玩家目录成功响应是否已持续写入辅助事实。
3. `SMALL_SAMPLE_SUPPRESSED`：这是隐私提示，不是数据丢失；扩大日期范围仍需遵守最长 93 天和运营目的限制。

SQLite integrity/foreign-key 失败、重复权威 ID、事件 envelope 与 JSON 不一致、非法 hash/时间/来源、Int64 溢出等会以稳定 `ANALYTICS_*` 错误 fail closed，不返回部分拼装结果。保存错误码、HTTP 状态、查询筛选、`traceId`（若有）和发生时间；不要手改 SQLite 绕过校验。先复制停写快照，再按恢复和迁移核对手册处理。

## 验证

相关自动化使用 120 个合成账户，覆盖唯一分母、60 个送达订单、10 个失败/退款、10 个 `uncertain` 订单、70 个已结算资源 run、10 个 `uncertain` run、双币、三区域、分页、重启 hash、赛季/版本筛选、小样本隐藏和损坏数据 fail closed：

```powershell
npm run test --workspace @pal-control/console-web
npm run build --workspace @pal-control/console-web
npm run lint:openapi
.	ests\contract\economy-analytics-contract.ps1
.	ests\integration\economy-analytics-smoke.ps1
```

这些合成测试不能替代真实 7 天玩家样本、内容运营复盘或独立隐私检查。正式使用前应先完成当前游戏版本 Native 门禁和 TODO 中的生产验收。
