# 经济内容发布与回滚运行手册

本手册适用于方案 A 的版本化经济内容：商城商品、价格、个人限购、可选全服库存、资源回收白名单、资源兑换区、可靠任务和日轮换策略。当前实现面向单实例、本机运营控制面；它不改变“Native 真实保存/停服/重启/重登尚未验收，因此 Production 资源兑换保持关闭”的发布边界。

## 权限与前置条件

- 查看当前版本、版本历史、草稿和 diff 至少需要 `Viewer`。
- 创建、保存和校验草稿需要 `EconomyAdmin`。
- 发布和回滚属于高风险经济操作，需要 `EconomyAdmin`（或继承该角色的更高角色）、有效 6 位 TOTP 和 8–512 字符审计原因。
- Control API、运营控制台和管理凭据必须留在本机或受信运营网络，不能通过玩家门户反向代理公开。
- 发布或回滚前必须进入经济维护模式并等待所有已准入操作排空。已经进入 `Consuming/Removed/Credited/Uncertain` 的资源结算必须先终结；尚未消费的 `Quoted` 可以保留，pointer 切换后会由冻结的 `contentVersionId + contentHash` 以 `QUOTE_CONTENT_CHANGED` 失效，且不会派发扣物。服务端会再次检查；条件不满足时返回 `423`，不能绕过。
- 经授权的 `services/control-api/Resources/palworld-resource-catalog.json` 必须存在，且内容定义中的目录 revision、游戏版本、PalDefender 版本和规则版本必须与当前批准组合一致。开源仓库不分发该快照；确认外部数据条款后，先从仓库根目录运行 `.\tools\catalogs\update-palworld-resource-catalog.ps1`，或提供经授权的替代快照，再启用 `ExtractionMode`。

`ExtractionMode` 启用时，启动初始化器会在接受 HTTP 流量前读取并严格校验目录，然后用同一原子激活路径创建首个内容版本。目录缺失/非法或首版无法完整激活都会使启动失败；不要以空目录、忽略异常或手工改数据库的方式绕过。

## 内容定义边界

“内容版本”是一份完整 JSON 定义，不是若干可独立上线的零散开关。当前内置方案 A 候选包含 10 个商品、51 个可售资源、2 个本地候选兑换区、3 个日任务和 3 个周任务；商品和资源只在其 ItemID 存在于本机授权目录时激活。候选区 2 只用于本地双点轮换证明，并明确标注待真实 Palworld 校准。新增内容时至少检查：

1. SKU、资源 ItemID、兑换区 ID 和任务 key 唯一；ItemID 必须存在于当前授权目录。
2. 商品分类、标签、推荐位、货币、价格、个人限购和可选全服库存使用显式字段；未配置 `globalStock` 就只显示和执行个人限购。
3. 可售资源必须使用战备券结算，并显式关联至少一个有效兑换区；目录外物品默认不可售。现代商品与资源还必须同时提供 `iconKey`、`rarity`、`usage`：图标键只能取有限的本地 SVG allowlist，稀有度只能为五档枚举，用途是 1–160 字符纯文本；未知键、URL、文件路径、控制符、HTML/XSS 或只提供三字段中的一部分都会阻断发布。
4. 兑换区的坐标、半径、路线、风险提示、开放窗口、grace 和基础收益倍率必须可解释；`hotspotYieldMultiplierBasisPoints` 必须大于 10000，选中热点后的有效倍率由服务端计算并同时用于报价、地图和直接回售校验。
5. 任务只能使用服务端可复核的经济事件。当前允许成功资源兑换、指定资源/价值结算、商城成功送达和指定货币消费；不得配置客户端自报的击杀、采集、死亡、PvP 或进入热点任务。
6. 新内容必须配置 `balancePolicy`：两种货币各有正影子率，每个启用资源有逐 ItemID 参考成本，每个资源类别有回收目标和风险缓冲，并提供与授权目录 revision 一致的完整性 attestation。转换表是运营经济影子 DAG，每条边的 `evidenceNote` 必须明确“不是实际 Palworld 配方”；禁止用未经授权的网络配方冒充证据。
7. 图中每个启用商品发放项必须是可售终点或可进入至少一层转换，所有可达输出最终必须进入启用回收项。环、未知 ItemID、缺少 attestation/参考成本/类别策略、数值溢出、无可回收终点或达到 `stateLimitPerProduct` 都会阻断发布。旧版本没有 policy 时只保留直接同币回售兼容检查与显式警告，不能作为新内容的发布依据。
8. 现代方案 A 必须有至少一个由日/周可靠任务实例约束的正数商域币来源，且任何轮换组合都不能形成零来源周；至少两个启用的正价商域币 SKU 必须各有有限个人周限购和有效发放物。校验结果要保存具体 task/SKU 清单、周流入上下界和周流出上限；新手活动、初始余额与管理员调账不计为稳定玩法来源。
9. 现代内容必须配置 `dynamicEconomyPolicy`：至少 2 个启用候选区并为每区声明风险等级，每个营业日至少确定性开放 1 区，限时热点必须完全位于动态开放窗口内；至少提供“资源繁荣”和“战备补给”两类纯经济事件。事件只能改变服务端权威回收倍率或整日商品价格，不能合成击杀、掉落、采集或客户端上报事实。
10. 同一营业日、content hash、规则版本和 policy 版本必须复算出相同的区域、风险、事件 ID/seed、窗口、热点、收益倍率和折扣。商城目录、玩家地图、报价与 run 都要携带对应证据。新报价不能跨热点/事件开始边界沿用旧倍率；活动中的报价只可结算到其 grace 末端；current pointer 已切换时，即使仍在 grace 内也必须先返回旧内容错误。

省略 `dynamicEconomyPolicy` 的历史 JSON 仍按原规范 JSON/hash 读取，并给出 `DYNAMIC_ECONOMY_POLICY_NOT_CONFIGURED` warning；这是历史兼容路径，不是新内容绕过动态校验的方式。

## 创建和校验草稿

1. 打开运营控制台的“内容版本”。记录当前版本号、营业日、完整 content hash 和规则版本。
2. 从当前或指定的已发布版本“复制为新草稿”，不要直接覆盖已发布版本。
3. 编辑完整 JSON 后保存。保存使用草稿 revision 做乐观并发控制；出现并发冲突时，先复制本地改动，再重新加载服务端 revision 并人工合并。
4. 查看 diff，逐项核对新增、删除和变更。重点确认被停用商品、资源、兑换区和任务不是误删。
5. 运行服务端校验。任何 error 都必须修复；warning 必须在审计原因或变更单中说明。对图校验还要保存具体 `BUY -> TRANSFORM -> SELL` 路径、穷尽状态数、双币影子值和逐资源回收率。记录校验后的完整 hash。
6. 对价格、库存、收益倍率、影子汇率、参考成本、类别风险缓冲或任务奖励的修改，另行完成人工经济复核。图分析证明的范围仅限 attested 运营影子模型；固定种子仿真也不模拟玩家交易、真实配方和掉落概率。

## 发布

1. 先关闭购买与资源兑换写入，等待活动操作为 0，并处理未终结订单以及已经开始消费或结果不确定的 settlement。未消费的 `Quoted` 不阻塞原子内容切换，但切换后必须按旧内容报价失效处理。使用 [Economy Safety Gate 运行手册](economy-safety-gate.md) 核对闸门；不要只关闭浏览器按钮。
2. 重新加载草稿，确认 revision、diff、校验 hash 和当前营业日没有变化。
3. 输入 8–512 字符审计原因、6 位 TOTP 和精确确认短语 `PUBLISH ECONOMY CONTENT`。
4. 提交一次。客户端会发送当前 revision 与稳定幂等键；超时或响应丢失时，不要创建新草稿或换新键，先读取当前版本，再用同一请求重放。
5. 发布成功后核对：current pointer、版本号、营业日、规则版本和完整 content hash；玩家商城返回相同内容证据，商品数量/分类/价格、图标/稀有度/用途、个人剩余限购和可选全服库存正确；资源源报价及选择性子报价冻结同一展示字段，旧内容缺省时明确返回 fallback/source；地图的动态开放/关闭区、风险等级、限时热点、事件窗口/seed、实际倍率和 `nextOpensAt` 与版本一致，六个前端图层开关只改变显示；任务实例固定到相同版本证据。
6. 完成只读核对后，再按 Safety Gate 手册逐项恢复购买与资源兑换。当前 dev39-ro quarantined 只读源码/制品候选、已 superseded 的 dev38-ro、已淘汰的 dev37-ro、旧 experimental 或任何未通过持久化门禁的 Native 都不能伪造 Production stable capability。dev39-ro 尚未实服加载或运行固定套件；dev38-ro 的历史 9 项非玩家成功与 3 项无人在线拒绝不能转记为 dev39-ro 的在线玩家、PalDefender 组合或独立复核证据。

发布先准备不可变内容版本，但此时不移动 current pointer；随后在一个 SQLite 事务中写入整套商品投影、以 expected version CAS 切换 pointer 并记录激活。第 N 个商品写入后故障会回滚整个事务，玩家目录和购买都继续使用整套旧版；进程重启后可用原幂等请求重试同一已准备版本。即使已有这项自动化，发布请求失败或响应不确定时仍必须保持维护模式：读取 pointer、hash 与完整目录，确认它们仍一致，再用原请求重试；成功并完成只读核对后才能开闸。

## 回滚

回滚选择一个已有的完整不可变版本，并在同一激活事务中切换 current pointer 与整套商品投影；不会改写或删除历史版本，也不会撤销已经完成的订单、资源扣除、钱包分录、任务奖励或周档交易。

1. 保持维护模式并再次确认活动操作、已开始消费/不确定的资源结算已经排空；记录仍为 `Quoted` 的 run，回滚切换后它们会因内容证据不匹配而失效。
2. 在版本历史中选定目标版本；留空时服务端选择上一完整版本。记录目标版本号、营业日和 hash。
3. 输入 8–512 字符回滚原因、6 位 TOTP 和精确确认短语 `ROLLBACK ECONOMY CONTENT`。
4. 提交一次。响应不确定时先读取 current pointer，并用原幂等键重放；不要连续点击或换键。
5. 按发布后的同一清单验证运行投影、商城、地图、任务和旧 offer 行为。切换后，携带旧 `contentVersionId + contentHash + SKU` 的商城提交必须稳定返回 `OFFER_NOT_AVAILABLE`，玩家应刷新目录，不得按旧价继续购买。
6. 回滚只解决内容版本问题。若已经出现余额、发货、扣物或奖励异常，继续保持维护，并按相应订单/资源结算对账流程处理。

## 自动化证据与未覆盖项

- `tests/content-definitions/`：严格校验、规范 hash、diff、20 次发布重放、不可变版本、回滚、旧 offer 拒绝和跨重启持久化。
- `tests/content-projection-atomicity/`：bootstrap/publish/rollback 第 N 个商品故障、pointer 与完整目录同事务、重启重试、20 次连续激活、旧 offer、新订单版本冻结、事件回放、全服库存和稳定 ProductID。
- `tests/economy-invariants/`：个人/全服库存并发、退款释放库存以及商品版本证据冻结。
- `apps/console-web/src/features/content/api.test.ts`：revision、幂等键、TOTP、审计原因和确认短语的客户端请求契约。
- `tests/reliable-tasks/`：可靠任务实例、权威事件重放、唯一奖励和跨重启恢复。
- `tests/content-definitions/` 还覆盖旧 JSON/literal hash 兼容、默认 10 商品/51 资源展示完整性、图标/稀有度/用途安全校验、同一营业日 20 次区域/事件/价格重放、相邻日开放区与事件切换、显式风险、限时热点、严格错误和全关 next-open；玩家单测及真实 system Chrome 覆盖展示 fallback、选择子报价、六图层键盘/移动端/Axe 和底图/位置降级。
- `tests/integration/player-economy-security-smoke.ps1`：内容 A 报价后在维护中原子发布 B，旧报价返回 `QUOTE_CONTENT_CHANGED`，并逐项证明 inventory、钱包、账本和 run/evidence 无副作用；`tests/native-settlement/` 还证明事件证据变化在派发前拒绝、热点/事件开始与 grace 边界正确，且完整动态证据跨重启保留。
- `tests/economy-balance-guard/`：旧内容直接回售兼容警告，以及完整 policy 下的礼包拆分、至少一层转换、跨币影子率、具体路径、图环、state limit、缺数据、逐 ItemID 参考成本、类别风险缓冲和“最低折扣买入 → 最高事件/热点卖出”fail-closed；固定种子 100×7 仿真使用同一极值压力组合。
- `tests/permanent-currency-contract/`：默认 6 个任务来源、3 个长期消费 SKU、`480/1200` 周上下限推导，任务池/限购变更后的重新计算，以及无来源、可能零来源、无上限 sink、不足两个 sink 的发布拒绝；同时用真实 SQLite 证明任务奖励 20 次重放唯一、商城同键扣币唯一和个人限购。

这些自动化不覆盖第二个候选区的真实 Palworld 坐标/路线/半径实测、实际 Palworld 配方数据库、玩家交易价格、连续两个真实周档报告、真实 Palworld E2E、多人周档或生产内容发布/恢复演练；相应 TODO 必须保持未完成。
