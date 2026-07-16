# 方案 A：经济平衡与反套利保护

本文说明“周世界资源经济服”在内容发布前可以自动证明什么、不能证明什么，以及如何执行固定种子的经济仿真。它是发布闸门和人工评审的辅助证据，不替代真实服务器压测。

## Attested 运营经济影子图

新内容在 `balancePolicy` 中声明两种货币的影子率、逐 ItemID 参考成本、类别回收目标/风险缓冲、运营影子转换 DAG、逐商品状态上限和完整性 attestation。attestation 必须固定授权资源目录 revision、覆盖 ItemID、审核主体、时间，并把 `evidenceKind` 固定为 `operator-audited-economic-shadow-graph`。

这张表不是实际 Palworld 配方数据库。内置每条转换的 `evidenceNote` 都明确写明“不是 Palworld 实际制作配方”；它表达运营在发布决策中必须保守评估的拆包/转换能力。未经授权的网络配方、玩家交易价格和未进入审计目录的外部转换不属于证明范围。

对每个启用 SKU，分析器从“一次购买后的完整礼包库存”开始，枚举可应用转换的所有顺序和次数。同一库存状态只保留影子费用最低的经济非劣路径；每个状态都计算“此刻把全部可售项送到允许的最高有效热点倍率区回收”的双币影子收益。因此礼包的一部分可先转换、另一部分直接出售，至少一层制作/转换和跨币收益都进入同一发布判断：

```text
购买影子成本 = 商品价格 × 购买币影子率
转换影子费用 = Σ(费用金额 × 费用币影子率)
有效回收单价 = ceil(资源基础单价 × 最高可能有效区域倍率 / 10000)
回收影子收益 = Σ(数量 × 有效回收单价 × 回收币影子率)
```

若任一可达状态满足 `回收影子收益 > 购买影子成本 + 转换影子费用`，返回 `PROVEN_REACHABLE_ARBITRAGE` 并给出完整 `BUY -> TRANSFORM -> SELL` 路径。相等时返回 `REACHABLE_ARBITRAGE_BREAK_EVEN` 警告。

完整性采用 fail-closed：

- 转换 ItemID 或 attestation ItemID 不在授权目录、共同输入不足导致转换实际不可达、可达终态仍含不可售 ItemID，均阻断发布；仅靠 ItemID 邻接关系不能冒充真实库存可达性。
- 转换图存在环时返回 `TRANSFORMATION_GRAPH_CYCLE`；分析器不以截断环路冒充穷尽。
- 可达状态达到 `stateLimitPerProduct` 时返回 `ARBITRAGE_ANALYSIS_STATE_LIMIT`；数值溢出同样阻断。
- 每个启用资源必须有参考成本和类别策略。最高热点回收率超过 `targetRecoveryBasisPoints - riskBufferBasisPoints` 时返回 `RESOURCE_RECOVERY_POLICY_EXCEEDED`。

旧 JSON 可以省略 `balancePolicy` 以保持读取和历史版本兼容。此时仅执行原有同币种直接回售闸门，稳定给出 `BALANCE_POLICY_NOT_CONFIGURED`、`INDIRECT_ARBITRAGE_NOT_EVALUATED` 和 `PERMANENT_CURRENCY_CONTRACT_NOT_ENFORCED`；这个兼容路径不应再用于发布新内容。

## 永久商域币的有界来源与长期消费

带完整 `balancePolicy` 的现代方案 A 内容还必须通过 `EconomyPermanentCurrencyAnalyzer`。只有进入日/周确定性任务池、奖励为正数商域币的可靠任务实例才计为可重复玩法来源；新手一次性活动、初始余额和管理员调账都不计入。可靠任务按账户、赛季、cadence 和 period key 唯一，日任务每业务日最多一个实例、周任务每周最多一个实例，钱包奖励再以任务 instance ID 幂等入账。

发布器对任务池中所有可能被选中的组合计算上下界：

```text
周流入下界 = 7 × 日池被选奖励最小和 + 周池被选奖励最小和
周流入上界 = 7 × 日池被选奖励最大和 + 周池被选奖励最大和
单 SKU 周流出上限 = 商域币单价 × 每赛季个人限购
周流出上限 = Σ(所有有效商域币 SKU 的单 SKU 上限)
```

若没有正数来源，返回 `BOUNDED_MARKET_COIN_GAMEPLAY_SOURCE_REQUIRED`；若轮换存在选出零商域币的一周，返回 `MARKET_COIN_SOURCE_NOT_GUARANTEED`。未进入对应 cadence 任务池的奖励会列出具体 task key 并明确不计入。消费侧至少需要两个启用的正价商域币 SKU；每个 SKU 必须有正数且有限的个人周限购、有效授权发放物和明确类别。无个人限购返回 `UNBOUNDED_MARKET_COIN_SINK`，不足两个返回 `MARKET_COIN_SINKS_REQUIRED`，诊断会列出实际符合条件的 SKU；全部消费场景同类别时另给出多样性 warning。

内置方案 A 不新增凑数商品，直接复用已有配置：6 个冻结可靠任务给出每玩家每周商域币流入上下界 `480/480`；`STARTER-CAPTURE`、`STARTER-CROSSBOW`、`BUILDER-REPAIR` 三个不同用途商品的个人限购给出周流出上限 `1200`。这些是发布内容的理论上限，不代表玩家一定完成全部任务或买满全部限购。

## 固定种子 100 人 × 7 业务日仿真

工具位于 `tools/economy-simulator`。不传入内容文件时，它使用固定内容版本、固定种子 `5782987248497480786`、100 个模拟玩家、7 个业务日和起始业务日 `2026-07-13`：

仿真按跨日可实现的保守组合施压：资源出售使用该区域可能达到的“基础倍率 × 限时热点 × 资源繁荣事件”最高倍率，购买使用“最低日价 × 战备补给折扣”最低价格。这样会覆盖玩家在折扣日买入、在高收益日卖出的时间套利路径；只模拟某一天被选中的单一事件会漏掉该组合。动态开放区仍按营业日种子确定性选择。缺少折扣事件、收益事件或可表示的倍率组合会在内容校验阶段 fail closed。

```powershell
dotnet run --project tools/economy-simulator/PalControl.EconomySimulator.csproj `
  --configuration Release -- --strict
```

`--strict` 会在任一默认目标区间不通过时返回退出码 2。报告为 JSON，包含：

- 每种币的产出、消耗、总余额、余额中位数和 P95；币种之间不会合并。
- 资源兑换次数、出售数量、SeasonVoucher 兑换收益及每玩家收益。
- 每个 SKU 的购买次数和消耗、独立购买玩家数、独立购买率、曝光购买率。
- 内容版本 ID 与规范化内容哈希，保证结果能追溯到具体版本。
- 每个默认目标的实际值、上下界和是否通过。

可以读取后台导出的原始 `EconomyContentDefinition` JSON，或包含 `definition` 字段的已发布 `EconomyContentVersion` JSON：

```powershell
dotnet run --project tools/economy-simulator/PalControl.EconomySimulator.csproj `
  --configuration Release -- `
  --content C:\review\published-content.json `
  --seed 5782987248497480786 `
  --players 100 `
  --days 7 `
  --output C:\review\economy-report.json `
  --strict
```

同一 .NET 工具版本、内容 JSON、种子和参数会生成完全相同的报告；自动测试验证完整序列化在相同输入下保持一致，并锁定关键快照值。

## 默认目标区间

默认区间是首轮 MVP 的回归护栏，不是已经由真实玩家数据证明的最终平衡值：

| 指标 | 默认区间 | 定义 |
| --- | ---: | --- |
| 主币产出/玩家 | 300–2500 | 7 业务日 SeasonVoucher 总产出 ÷ 100 |
| 主币消耗率 | 1500–9000 bp | SeasonVoucher 商城消耗 ÷ 产出 |
| 期末余额中位数 | 50–2000 | 100 名玩家 SeasonVoucher 余额的 nearest-rank P50 |
| 期末余额 P95 | 150–5000 | 100 名玩家 SeasonVoucher 余额的 nearest-rank P95 |
| 独立购买率 | 4000–10000 bp | 至少购买过一次的玩家数 ÷ 100 |
| 曝光购买率 | 300–2000 bp | 成功购买次数 ÷ 启用且在售商品曝光次数 |
| 资源兑换收益/玩家 | 300–2500 | 7 日 SeasonVoucher 回收收益 ÷ 100 |

固定默认场景的当前极值压力基线为：SeasonVoucher 产出 137292、消耗 24922、期末余额中位数 692、P95 2829、独立购买率 9800 bp、曝光购买率 777 bp、资源兑换收益/玩家 1372。修改算法或基准内容造成这些数字变化时，必须解释变化并有意识地更新测试快照。

## 验证与使用顺序

1. 对草稿执行内容校验；任何图完整性、参考成本、类别策略或 `PROVEN_REACHABLE_ARBITRAGE` error 都必须修复后再发布。旧内容的 `PROVEN_DIRECT_RESALE_ARBITRAGE` 仍保持阻断。
2. 人工核对 attestation、目录 revision、影子率、每条转换的运营依据和具体最优路径；另行检查真实游戏配方与玩家交易等模型外入口。
3. 对待发布版本运行仿真工具并保存 JSON 作为版本评审证据。
4. 运行独立回归测试：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass `
     -File tests/integration/economy-balance-guard-smoke.ps1
   powershell -NoProfile -ExecutionPolicy Bypass `
     -File tests/integration/permanent-currency-contract-smoke.ps1
   ```

5. 上线后用真实账本重新计算产出、消耗、余额分位数和购买率。固定种子工具不执行影子转换，也没有模拟掉落表概率、真实制造、玩家交易、通胀反馈、流失或服务器延迟；连续两个真实周档报告仍是独立未完成门禁。
