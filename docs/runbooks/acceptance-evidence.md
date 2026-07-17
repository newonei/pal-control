# 外部门禁验收证据运行手册

本手册用于 TODO 中不能由本机 mock、自动化 harness 或页面截图替代的真实验收。权威策略位于 `tools/acceptance-evidence/gate-catalog.v1.json`，manifest 契约位于 `tools/acceptance-evidence/acceptance-evidence.schema.v1.json`。

## 能证明什么

Verifier 能证明：证据包采用程序内嵌的固定 schema/catalog；记录了固定 Palworld、Steam build、UE4SS、Native、PalDefender、Caddy、Control API commit、部署包与配置摘要；所有 artifact 非空且 size/SHA-256 匹配；必需检查、样本量、时长与人员数量满足策略；执行人和复核人来自外部 SHA-256 固定的身份信任库，并分别用不同 ECDSA P-256 密钥签署了完整 canonical envelope；敏感信息扫描为零发现；P0 总复核引用的其他 manifest 没有缺失、换版本或被修改。

Verifier 不能自动证明操作员写下的事实真实，也不能认证身份系统或信任库审批流程本身。执行与复核必须由组织身份系统中的真实人员完成，信任库及其 pin 由发布审批系统控制，原始身份映射保存在受控审计系统中，不能提交仓库。伪造 `isSynthetic: false`、信任库或检查结论属于审计事件，不是技术绕过的合法方式。

## 目录和主体

每次验收使用独立、不可复用的 campaign 目录：

```text
campaign-2026w30/
  p0-02-production-steam-openid/
    manifest.json
    evidence/
  p0-04-current-native-probe/
    manifest.json
    evidence/
  ...
  p0-release-review.json
  evidence/
```

从仓库根目录一次生成完整 22 项工作清单，输出必须位于公开仓库之外：

```powershell
$project = "tools/acceptance-evidence/PalControl.AcceptanceEvidence.csproj"
dotnet run --project $project --configuration Release -- create-campaign `
  --output C:\acceptance\campaign-2026w30
dotnet run --project $project --configuration Release -- inspect-campaign `
  --root C:\acceptance\campaign-2026w30
```

生成结果包含 13 项 P0、6 项 P1、3 项 P2；这会把 PostgreSQL 容量/N-A 决策、团队经济真实周和运营分析真实周也显式列出，不能只搜索 `TODO.md` 的旧空框数量。所有新文件都是必然验证失败的 template，`inspect-campaign` 也永远不会把它们标为 verified。已有目录、仓库内目录和重复初始化均被拒绝。

`subjectId` 必须是使用验收专用组织密钥生成的稳定 keyed pseudonym：`subj:hmac-sha256:<64 lowercase hex>`。不要使用 SteamID、PlayerUID、邮件、昵称、IP 或未加密钥的直接 SHA。验收密钥不得写入 manifest、命令行、artifact 或仓库。相同人员在同一 campaign 内使用相同 pseudonym，才能可靠检查执行/复核分离；跨 campaign 是否轮换由审计保留策略决定。

身份信任库遵循 `tools/acceptance-evidence/identity-trust-store.schema.v1.json`。每个主体只允许一个明确的 role、实现者状态和 ECDSA P-256 SPKI 公钥；key id、subject id 以及导入后重新导出的 canonical SPKI 指纹都必须全局唯一，不能把同一个 EC 公钥换一种 Base64 或挂到两个身份名下。重复、吊销、算法错误或清单自报但库中不存在的主体都会失败。执行人与复核人必须使用两个不同 key 和两个不同 canonical 公钥。信任库 SHA-256 pin 必须来自受保护的发布记录或独立认证渠道，不能从待验证 manifest 中复制。私钥留在组织 HSM、签名服务或操作系统密钥库中，不能落入证据包或仓库。

## 执行顺序

1. 从 canonical catalog 为目标 gate 执行 `create-template`。记录当前部署 ZIP 与外置配置 SHA-256，并填写完整版本组合。
2. 用 `combination-id` 计算版本组合摘要，回填 `combinationId`。该摘要来自带 domain schema 的无歧义 canonical JSON，不使用可注入的换行 `key=value` 拼接；所有版本文本拒绝控制字符。递归总复核还会逐字段比较完整 canonical 版本组合。运行期间任何组件、包或配置发生变化，当前 manifest 作废并重开 campaign，不能把两套版本拼为一次验收。
3. 在 catalog 允许的真实环境执行运行手册。原始数据先在受控区域脱敏；每项 required check 和 metric 必须引用能复核该结论的 artifact。不能以空文件、模板、mock、fixture、设计文档或单纯截图替代协议、账本、生命周期或恢复证据。
4. 所有业务 artifact 生成后运行敏感信息扫描。`scannedArtifactIds` 必须覆盖除 scan report 与 review record 外的全部 artifact，扫描结果必须为 `pass` 且 `findingCount` 为 `0`。
5. 独立复核人检查版本、时间线、样本矩阵、账本守恒、日志脱敏和处置结论，生成 `review-record`。需要非实现者的 gate 必须把 `implementationContributor` 设为 `false`，且复核 subject 不能等于执行 subject。
6. 将每个文件的实际 size/SHA-256、扫描命令和时间回填，设置 `evidenceMode: live`、`isSynthetic: false`、所有 check 为 `pass`、review 为 `approved`，最后才把 conclusion 设为 `pass`。
7. 用 `signature-payload` 生成固定字节。该载荷包含 payload schema、信任库摘要、执行/复核 key id 以及除 `signatures` 自身之外的完整 manifest；分别交给执行人与独立复核人的组织密钥，以 `ecdsa-p256-sha256-p1363` 生成 64 字节签名并回填。任何字段或 artifact hash 变化后，两份签名都必须重做。
8. 用外部渠道取得的 pin 执行 `verify --manifest ... --trust-store ... --trust-store-sha256 ...`。正式 verifier 不接受 `--now`、外置 schema 或外置 catalog。Manifest 最大 1 MiB，必须是无 BOM 的严格 UTF-8，所有层级禁止重复 property，根对象后只能没有字节或恰好一个 LF；因此不能在未读取的尾部隐藏第二套值。单次验证最多租约 1024 个文件：root/related manifest 和全部 artifact 的 parser/hash 直接读取持有句柄；外部 trust store 与测试 harness 外置 policy 先解析有界快照，再仅在租约句柄 SHA-256 与该快照一致时继续。所有租约都保持到 Summary 构造完成，Windows 使用 `FileShare.Read` 明确拒绝并发写入/删除。成功返回前还会从持有句柄和当前路径分别重算并比较，路径替换、原 inode 内容变化或 reparse 变化都会失败。Unix-like 系统对不遵守 advisory sharing 的进程只能做到“持有 descriptor + 最终 handle/path 复核”的 best effort。验证器还会对原始 manifest、canonical envelope、scan report 和文本 review record 再做一次凭据特征扫描；退出码非零时保持对应玩法/发布门禁关闭，不得手工忽略错误码。
9. 每个 manifest 单独通过后，再运行 `verify-campaign --root ... --trust-store ... --trust-store-sha256 ...`。它逐项验证固定 catalog 中全部 22 项并汇总失败 code；少一个 manifest、仍是 template、签名/哈希/人员/时长不合格或 P0 总复核递归失败时均退出 `2`。只有 `complete: true` 且进程退出 `0` 才表示证据包层面全部通过，仍不取代执行人/复核人的事实责任。

## 特殊门禁

- `p0-06-paldefender-delivery-campaign` 同时要求总发货不少于 200，timeout、partial、进程重启及故障矩阵中每个边界不少于 20；所有重复、余额差和无依据退款指标必须为 0。
- `p0-10-three-live-rollovers` 要求三套独立 rollover/backup/transition artifact，而不是一份日志中声明三次成功。
- `p0-11-seven-day-player-trial` 直接由 UTC 开始/结束时间验证至少 604800 秒，并要求 5–10 个不同玩家 pseudonym。
- `p1-03-two-season-economy-reports` 要求至少 1209600 秒和两份周档报告。
- `p1-07-postgresql-capacity-migration` 是唯一允许 `not-applicable` 的 gate；仍须提供容量/SLO 决策、SQLite 范围复核和独立批准。容量目标已经触发时不得使用 N/A。
- `p1-07-24-hour-soak` 的 `soak-metric-series` 必须是 `tools/soak` 生成的原始 canonical `report.json`，并提供 `soak-report-hash` sidecar。Verifier 会冻结生产阈值、拒绝 `ci-non-acceptance`/自定义 profile，并从 samples 重算完整 analysis。序列必须从 0 连续增长，phase 只能按 `load` 后 `recovery` 排列，采样间隔、UTC/elapsed、load/recovery 覆盖必须完整；每个工作量窗口必须满足 `attempted = succeeded + failed`，恢复期为零请求，attempted 不得超出采样窗口容量，发生请求时 P95 不得为空。Manifest 中 24 小时、样本数和 finding 数必须与报告完全一致。
- `p0-11-independent-release-review` 递归验证所有 P0 外部 manifest。相关路径必须位于总复核 manifest 所在目录之下，禁止 `..`、绝对路径、符号链接或 reparse point。

所有 artifact 与 related manifest 路径只能使用 `/` 分隔的 portable ASCII segment；绝对路径、空 segment、`.`、`..`、ADS、Windows 设备名、尾点/尾空格以及 manifest 根到文件系统根任一层的符号链接/reparse point 均会失败。

## 保留与泄露处置

证据包可能包含安全与运营信息，不应直接提交公开 GitHub。验证租约只覆盖 verifier 进程运行期间；返回后必须立即保存在 WORM/对象锁存储，或使用明确拒绝写入和删除的 ACL，不得把一次成功退出当作长期防篡改。应同时保存 verifier 输出、catalog/schema 文件和对应 Git commit。若扫描发现秘密、原始玩家标识或 Cookie，立即把 gate 判定为失败，撤销泄露凭据，重新脱敏、重新扫描并由复核人重新作结论；不得只编辑 findingCount。
