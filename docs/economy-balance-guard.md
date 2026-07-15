# 方案 A：经济平衡与反套利保护

本文说明“周世界资源经济服”在内容发布前可以自动证明什么、不能证明什么，以及如何执行固定种子的经济仿真。它是发布闸门和人工评审的辅助证据，不替代真实服务器压测。

## 直接回购套利发布闸门

内容校验器会检查当前版本中每个启用的商城 SKU：购买一份商品后，把其中同时属于启用回收白名单的物品，分别带到该资源允许进入且倍率最高的启用区域出售。

计算与真实结算保持一致：

```text
有效单价 = ceil(资源基础单价 × 区域倍率基点 / 10000)
回收收入 = 发放数量 × 有效单价
可比较总收入 = 所有同币种、可直接回收发放项的回收收入之和
```

如果 `可比较总收入 > 商品价格`，校验返回 `PROVEN_DIRECT_RESALE_ARBITRAGE`，内容版本不可发布。错误会包含 SKU、ItemID、发放数量、选择的最大倍率区域、收入和确定利润。例如：

```text
LOOP-PACK -> ResaleItem x2 -> ridge(12500bp) -> 24 SeasonVoucher
购买成本 20 SeasonVoucher，确定利润 4 SeasonVoucher，阻止发布。
```

即使商品还发放了无法估值的其他物品，只要其中可比较部分已经大于购买价格，正收益仍然成立，因此必须阻止发布。收入等于价格时返回 `DIRECT_RESALE_BREAK_EVEN` 警告，提醒运营考虑任务奖励、手续费缺失或其他组合路径。

## 明确的“不可判定”边界

当前内容定义没有制造配方、加工损耗、玩家间交易价格和手续费图，因此校验器不会宣称完成了全局套利证明：

- `INDIRECT_ARBITRAGE_NOT_EVALUATED`：整个版本没有可用于推导制造/加工回路的配方图，只完成直接回购检查。
- `DIRECT_RESALE_ANALYSIS_INCOMPLETE`：某个 SKU 的发放物不在启用回收白名单、没有有效回收区，或回收币种与购买币种不同，无法直接比较。
- 跨币种数值不会相加；在没有受控汇率时，把 MarketCoin 和 SeasonVoucher 相加会产生虚假的安全结论。

这些警告不会单独阻止发布，但必须在内容评审中保留。未来若引入配方数据，应增加基于有向转换图的多步环路检查，而不是删除这些警告。

## 固定种子 100 人 × 7 业务日仿真

工具位于 `tools/economy-simulator`。不传入内容文件时，它使用固定内容版本、固定种子 `5782987248497480786`、100 个模拟玩家、7 个业务日和起始业务日 `2026-07-13`：

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
| 独立购买率 | 4000–9500 bp | 至少购买过一次的玩家数 ÷ 100 |
| 曝光购买率 | 300–2000 bp | 成功购买次数 ÷ 启用且在售商品曝光次数 |
| 资源兑换收益/玩家 | 300–2500 | 7 日 SeasonVoucher 回收收益 ÷ 100 |

固定默认场景的当前基线为：SeasonVoucher 产出 96167、消耗 24110、期末余额中位数 436、P95 2016、独立购买率 9100 bp、曝光购买率 656 bp、资源兑换收益/玩家 961。修改算法或基准内容造成这些数字变化时，必须解释变化并有意识地更新测试快照。

## 验证与使用顺序

1. 对草稿执行内容校验；任何 `PROVEN_DIRECT_RESALE_ARBITRAGE` 都必须修改价格、发放数量、资源价值或区域倍率后再发布。
2. 人工阅读所有“不可判定”警告，单独检查制造配方、玩家交易和跨币种入口。
3. 对待发布版本运行仿真工具并保存 JSON 作为版本评审证据。
4. 运行独立回归测试：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass `
     -File tests/integration/economy-balance-guard-smoke.ps1
   ```

5. 上线后用真实账本重新计算产出、消耗、余额分位数和购买率。固定种子模型没有模拟掉落表概率、制造配方、玩家交易、通胀反馈、流失或真实服务器延迟，不能替代 5–10 名真实玩家的七日观察。
