# 不可变周档经济报告

本手册描述如何在周榜已经冻结后，从 `extraction-commerce.db` 生成一份可重放、可复核、可发现篡改的周档经济报告。该工具不会接受浏览器埋点，也不会修改钱包、订单、兑换 run、周榜或换档状态。

它只接受以下来源状态：

- 周档状态为 `Closed` 或 `Archived`；
- `season_leaderboard_snapshots` 已存在，且冻结 JSON、证据 JSON、SQL 身份列、`rules/source/snapshot` 哈希全部一致；
- 运营分析窗口覆盖该周档业务日期且已经稳定；
- SQLite 权威重算来源完整，不存在 critical 分析告警；
- 做同比时，上一份报告必须属于同一服务器、结束时刻与本周开始时刻完全一致，并已由两个不同复核主体批准。

任一条件不满足时必须先修复来源或完成冻结，不得复制旧报告改名。

## 产物和隐私边界

每个周档固定写入 `<ArchiveRoot>/<season-guid>/`：

| 文件 | 分级 | 内容 |
| --- | --- | --- |
| `report.json` | `operator-shareable-aggregate` 或 `restricted-small-cohort` | 双币流入/流出/净额、资源价格指数、热门商品/资源、异常规则及经过小群体抑制的异常计数、同比和来源 hash |
| `report.html` | 可选；与 `report.json` 相同 | 对 `report.json` 的 HTML 编码摘要，不包含账户明细 |
| `restricted-accounts.json` | 仅限授权运营 | 命中规则账户的 HMAC-SHA256 伪匿名、规则代码和冻结指标；不含 AccountId、Steam subject、PlayerUID、昵称或显示名 |
| `manifest.json` | 完整性元数据 | 每个产物的字节数、分级和 SHA-256，以及本周复核信任库的原始字节 SHA-256 |
| `manifest.sha256` | 完整性锚点 | `manifest.json` 的 SHA-256 |
| `reviews/*.json` | 受限、追加写 | 绑定 manifest/前一 revision 的 ECDSA P-256 签名复核事件和派生状态 |

`report.json`/`report.html` 的“可供运营共享”只表示可在已授权的内部运营范围流转，不表示已经达到对公网发布的匿名化标准。报告把冻结榜单实际参与人数写入 `privacy.frozenParticipantCohortSize`。当人数小于 `privacy.publicMinimumCohortSize`（当前边界为 5）时，生成器会把整份 JSON、HTML 以及 manifest 对应条目统一标记为 `restricted-small-cohort`；1 人和 4 人都必须受限，达到 5 人才恢复为 `operator-shareable-aggregate`。这是因为双币账户数、商品购买者和异常账户计数虽执行小群体抑制，资源篮子仍保留计算通胀所需的精确 ItemID、数量和价值。不得手工修改 manifest 分级，也不得把受限小群体报告转发到普通运营渠道。若未来需要公开周报，应从已批准归档另行生成只含足够大群体、区间化数值的发布物，并重新执行隐私复核与敏感信息扫描，不能直接公开这里的原始 JSON/HTML。

所有 JSON 都使用属性名序的 canonical JSON。生成器先在同一归档卷的 staging 目录写完并落盘，再原子改名；报告、manifest 和 review revision 被标记为只读。再次执行同一请求只验证并返回原 manifest，不重写文件。只读位不是安全边界，正式证据仍应复制到受 ACL 保护的离线或 WORM 存储。

review revision 自身只能证明“现存文件是一条合法链”，不能单独证明末尾 revision 没有被删除。每次 `generate`/`review` 成功后，工具都会输出当前 `Review head SHA-256`。必须立即把它连同 SeasonId、manifest SHA-256、revision 和状态，以单次事务写入归档目录之外、具有追加审计的独立 WORM/变更管理系统；不能把归档旁边的文本文件或同一 ACL 下的数据库当作外部 pin。后续 `verify`、`status`、`review`、已有归档重放和下一周同比都必须显式带回该 hash。不要从当前 `reviews` 目录重新计算 hash 后把它当作外部已发布值；即使截尾后某个旧 pin 仍对应合法历史前缀，也不得以此为理由回退外部登记，否则无法发现回滚。

同一服务器连续周档必须使用同一个随机伪匿名 key，才能让受限审计者识别跨周的同一异常账户。key 不得进入仓库、报告目录、命令行、日志或备份清单；轮换 key 后不得声称新旧伪匿名可直接关联。

## 1. 首次准备

在仅管理员可读的目录创建 32 字节随机二进制 key：

```powershell
$secretDir = 'C:\ProgramData\PalControl\secrets'
New-Item -ItemType Directory -Force -Path $secretDir | Out-Null
$keyFile = Join-Path $secretDir 'weekly-report-pseudonym.key'
[IO.File]::WriteAllBytes($keyFile, [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
icacls $secretDir /inheritance:r /grant:r "$env:USERDOMAIN\$env:USERNAME:(OI)(CI)F"
```

把 key 纳入独立的密钥备份与轮换登记，但不要纳入 Git 或普通经济备份。另由至少两名复核者**分别在各自 Windows 登录上下文或受管密钥设备中**生成 ECDSA P-256 私钥，并只把公钥交给身份/安全管理员；不得由同一会话集中生成或保留两把私钥。以下片段每名复核者只执行一次：

```powershell
$reviewDir = 'C:\ProgramData\PalControl\weekly-review'
New-Item -ItemType Directory -Force -Path $reviewDir | Out-Null

function New-WeeklyReviewKey([string] $name) {
  $key = [Security.Cryptography.ECDsa]::Create()
  try {
    $key.GenerateKey([Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $private = Join-Path $reviewDir "$name.private.pem"
    [IO.File]::WriteAllText(
      $private,
      $key.ExportPkcs8PrivateKeyPem(),
      [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
      (Join-Path $reviewDir "$name.public.pem"),
      $key.ExportSubjectPublicKeyInfoPem(),
      [Text.UTF8Encoding]::new($false))
    $principal = "$env:USERDOMAIN\$env:USERNAME"
    & icacls $private /inheritance:r /grant:r "${principal}:R" | Out-Null
  } finally { $key.Dispose() }
}

New-WeeklyReviewKey '<当前复核人代号>'
```

身份/安全管理员只接收两个公钥，在不持有私钥的受控上下文生成信任库：

```powershell
$reviewDir = 'C:\ProgramData\PalControl\weekly-review'
$approvedPublicKeyDir = 'D:\ApprovedWeeklyReviewerPublicKeys'

$trust = [ordered]@{
  schemaVersion = 1
  policyId = 'weekly-economy-reviewers-2026'
  keys = @(
    [ordered]@{
      subject = 'subj:hmac-sha256:<复核人A的64位小写HMAC摘要>'
      algorithm = 'ecdsa-p256-sha256'
      publicKeyPem = [IO.File]::ReadAllText((Join-Path $approvedPublicKeyDir 'reviewer-a.public.pem'))
    },
    [ordered]@{
      subject = 'subj:hmac-sha256:<复核人B的64位小写HMAC摘要>'
      algorithm = 'ecdsa-p256-sha256'
      publicKeyPem = [IO.File]::ReadAllText((Join-Path $approvedPublicKeyDir 'reviewer-b.public.pem'))
    }
  )
}
[IO.File]::WriteAllText(
  (Join-Path $reviewDir 'trust.json'),
  ($trust | ConvertTo-Json -Depth 5 -Compress),
  [Text.UTF8Encoding]::new($false))
icacls $reviewDir /inheritance:r
```

`subject` 必须由组织验收专用 HMAC key 从真实 IdP 主体生成；原始身份到 subject/公钥的映射保存在组织审计系统。信任库纳入受控配置和备份，私钥分别只授权对应复核者。发布审批另行固定 `trust.json` 的 SHA-256，并通过 `PAL_CONTROL_WEEKLY_REVIEW_TRUST_SHA256` 注入工具；禁止在运行时从当前文件现算后自我批准。报告 manifest 也固定同一原始字节 hash，因此信任库轮换后仍须保留旧版本才能离线验证历史报告。生产业务时区必须与 `ExtractionMode` 配置一致；Windows 中国时区通常为 `China Standard Time`，测试用 UTC 时才传 `UTC`。

## 2. 在换档流程中生成

推荐时点是：旧周档已经关闭、late-settlement cutoff 已过、周榜冻结成功之后，且新世界写闸门尚未打开之前。先按[周榜冻结手册](season-leaderboard-settlement.md)完成冻结，再执行：

```powershell
$season = '<本周 SeasonId>'
$data = 'C:\ProgramData\PalControl\data'
$archive = 'D:\PalControlEvidence\weekly-economy'
$keyFile = 'C:\ProgramData\PalControl\secrets\weekly-report-pseudonym.key'
$reviewTrust = 'C:\ProgramData\PalControl\weekly-review\trust.json'
$env:PAL_CONTROL_WEEKLY_REVIEW_TRUST_SHA256 = '<发布审批中固定的 trust.json 64 位小写 SHA-256>'

dotnet run --project tools\weekly-economy-report\PalControl.WeeklyEconomyReport.csproj `
  --configuration Release -- `
  generate --data-dir $data --archive-root $archive --season $season `
  --time-zone 'China Standard Time' --pseudonym-key-file $keyFile `
  --review-trust-store $reviewTrust --html
```

首次成功会输出 revision 0 的 review head。先把它发布到外部换档证据登记，再继续复核。若命令因网络/终端中断而需要重放，或目标 SeasonId 的归档已经存在，必须从外部登记取回当前 head；不传 pin 的已有归档重放会失败：

```powershell
$currentReviewHead = '<外部登记的本周当前 review head 64 位小写 SHA-256>'

dotnet run --project tools\weekly-economy-report\PalControl.WeeklyEconomyReport.csproj `
  --configuration Release -- `
  generate --data-dir $data --archive-root $archive --season $season `
  --time-zone 'China Standard Time' --pseudonym-key-file $keyFile `
  --review-trust-store $reviewTrust `
  --expected-existing-review-head-sha256 $currentReviewHead --html
```

若当前目录已被截尾、替换或后来追加过 revision，旧 head 会导致重放失败。此时应保留现场并按证据异常处理，不得读取目录中的新末尾 hash 覆盖外部登记。

从第二周开始必须引用上一周已批准的报告：

```powershell
$previousReviewHead = '<外部登记的上一周 approved review head 64 位小写 SHA-256>'

dotnet run --project tools\weekly-economy-report\PalControl.WeeklyEconomyReport.csproj `
  --configuration Release -- `
  generate --data-dir $data --archive-root $archive --season $season `
  --previous-season '<上一周 SeasonId>' `
  --previous-review-head-sha256 $previousReviewHead `
  --time-zone 'China Standard Time' --pseudonym-key-file $keyFile `
  --review-trust-store $reviewTrust --html
```

工具输出 archive 路径、manifest SHA-256、当前 review head 和复核状态。把 manifest hash 与 review head 一并记入当周换档证据。`--previous-season` 与 `--previous-review-head-sha256` 必须成对出现；上一周必须是同服务器、时间完全相邻且该固定 head 对应 `approved (2/2)`。生成失败时保持维护/写闸门关闭；不要使用非相邻周或过时 head 绕过缺档。

## 3. 两人独立复核

两个复核者应使用不同的真实管理员主体、不同登录会话和信任库中的不同私钥，并各自核对：冻结 SeasonId/时间范围、source hash、双币产销、异常明细分级、HTML 无身份、上一周基线和归档 ACL。CLI 只归档 keyed subject、公钥指纹和签名，不归档原始主体；管理员认证、MFA、私钥 ACL、subject 映射和终端审计仍由操作环境负责。

把当前管理员的稳定 IdP subject 写入仅当前会话可读的文件，然后批准：

```powershell
$reviewerFile = Join-Path $env:TEMP 'pal-control-weekly-reviewer-subject.txt'
$expectedCurrentReviewHead = '<复核开始前从外部登记读取的当前 review head SHA-256>'
[IO.File]::WriteAllText(
  $reviewerFile,
  'subj:hmac-sha256:<当前真实管理员的64位小写HMAC摘要>',
  [Text.UTF8Encoding]::new($false))
try {
  dotnet run --project tools\weekly-economy-report\PalControl.WeeklyEconomyReport.csproj `
    --configuration Release -- `
    review --archive-root $archive --season $season `
    --review-trust-store $reviewTrust `
    --reviewer-subject-file $reviewerFile `
    --reviewer-private-key-file 'C:\受限目录\当前复核人.private.pem' `
    --decision approve --reason '冻结来源、隐私分级和经济指标复核通过' `
    --expected-current-review-head-sha256 $expectedCurrentReviewHead
} finally {
  Remove-Item -LiteralPath $reviewerFile -Force -ErrorAction SilentlyContinue
}
```

每名复核者都必须在操作开始时从独立 WORM/变更管理登记读取 head，不能接受上一名复核者通过聊天转述、也不能从归档目录现算。追加采用 compare-and-append：若另一名复核者已经追加、传入的是旧 head、链被截尾或 pin 错误，命令会在写入前失败。成功后，把工具输出的新 review head 和新 revision/状态以单次事务追加到外部登记，第二名复核者再使用这个新 head；登记事务失败时不得继续下一次复核。

同一主体重复提交会失败。第一人批准后状态仍为 `pending (1/2)`；第二个不同主体批准后才是 `approved (2/2)`。任何一次 `reject` 会把该 manifest 永久置为 `rejected`，不能在同一归档上追加批准；外部固定 rejected head 后，删除 rejection revision 会在验证时显示 head 不一致。修复权威来源后应按新的正式处置流程生成新证据，不能删除 rejection revision 或把外部登记回退到旧 head。

## 4. 离线验证和查看状态

验证不需要连接运行中的 Control API 或 Palworld，也不需要 SQLite：

```powershell
$expectedReviewHead = '<从外部换档证据登记读取的当前 review head SHA-256>'

dotnet run --project tools\weekly-economy-report\PalControl.WeeklyEconomyReport.csproj `
  --configuration Release -- `
  verify --archive-root $archive --season $season `
  --review-trust-store $reviewTrust `
  --expected-review-head-sha256 $expectedReviewHead
```

`status` 使用同一个 `--expected-review-head-sha256`，并且同样执行完整离线验证；它不是跳过完整性检查的目录查看命令。验证会拒绝：缺少/格式错误/过时/错误的外部 head pin、review 链末尾被删除、非 canonical JSON、manifest/sidecar/hash/字节数不符、缺失或额外顶层文件、reparse point、信任库 hash 漂移或重复 JSON 属性、重复实际公钥、非 P-256 公钥、跨文件 SeasonId/source hash 冲突、原始结构不合法、复核签名错误、revision 缺号/改写/重复主体、复核私钥与可信主体不匹配、从 terminal 状态继续追加等情况。

如果 head 不匹配，先冻结证据目录并比较外部登记历史、文件系统审计和 WORM 副本。不要尝试“修复”只读位、删除末尾文件、把外部登记改成当前目录的 hash，或在不受控副本上继续复核。

## 5. 指标口径

- 双币产销直接沿用 SQLite 权威运营分析的 `merchantCoin` 与 `weeklyTicket` 流入、流出、净额和余额分位数；小于 5 个账户的非零群体继续抑制。
- 热门商品只统计最终 `Delivered` 订单，按送达数量倒序；退款、失败和 uncertain 不算成功销量。
- 热门资源来自冻结周榜的完整 settled 资源证据，按价值、数量、ItemID 排序。
- 通胀采用共同 ItemID 的 Laspeyres 资源回收价篮子，以上周数量为权重；第一周或没有共同资源时不输出指数，不把它解释成 Palworld 全市场 CPI。
- `resource-value-outlier`、`exchange-frequency-outlier`、`task-points-outlier` 分别使用 `max(固定下限, 当周中位数 × 5)`；封禁/人工排除单列高优先级规则。公共异常计数继续执行最小群体抑制，精确账户只在受限文件中出现。
- source hash 组合冻结周榜 snapshot/source hash、analytics recomputation hash、服务器和周档时间范围。它证明报告来自该次权威快照，不是数字签名；归档真实性还依赖受控主机、ACL、外部审计和离线副本。

## 6. 自动化证据

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\integration\weekly-economy-report-smoke.ps1
```

该 harness 创建两个相邻的 SQLite 合成周并走正式周榜冻结，验证源库拒绝写入、canonical 归档、双币、商品、资源通胀、受信 P-256 双人签名、重复/非 P-256 公钥和重复 JSON 属性信任库拒绝、错误私钥/签名拒绝、review head 缺失/错误/过时与删尾拒绝、第二周用已批准 head 固定同比、带 pin 同请求重放、1/4/5 人隐私分级边界、跨周稳定伪匿名、原始身份不泄露，以及 SQLite 冻结包和归档文件任一被篡改时 fail-closed。它不是“连续两个真实周档”的生产验收证据。
