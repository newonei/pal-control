# 第二资源兑换区实服校准与验收

本手册用于收集“第二资源兑换区”的真实 Palworld 校准证据。仓库测试只证明验证器会按规则拒绝伪造或不完整的数据，不能替代实服验收，也不能据此勾选 TODO。

## 1. 冻结外部期望值

维护窗口开始前，由变更单/值班记录独立冻结以下值：

1. `serverId`；
2. Palworld Dedicated Server 的 game build 与 Steam build id；
3. Control API 当前已发布内容的 `versionId`、`versionNumber`、`contentHash`；
4. 候选兑换区的 `zoneId`、中心与半径；
5. 最大证据年龄、证据 `expiresAt`；
6. 受控 trust store 的 SHA-256。

这些值必须作为 `--expected-*` 参数传给验证器，不能从待验 `evidence.json` 反向复制。采样期间若游戏、Steam build、内容 pointer 或区域几何发生变化，整包作废并重新采样。

半径上限为 10000。内容发布、启动配置、玩家地图和结算门禁均使用同一份 `ZoneGeometryLimits` / `ExtractionZoneGeometry`；不要用网页像素或人工距离替代服务端坐标。

## 2. 密钥与主体隔离

使用两类独立 P-256 公钥：

- capture key：由受控采集器/HSM 持有私钥，签每一份原始 capture；
- reviewer key：由另一位复核者持有，只签最终 review challenge。

两类 key id 与公钥不得复用。执行者和复核者必须是两个不同的 HMAC 假名；假名域固定为 `zone-calibration:<calibrationId>`。trust store 只保存公钥、假名、有效期和撤销状态；私钥、HMAC key 与源身份不得写入证据。

trust store 的 hash pin 必须来自独立审批记录。不要在同一条自动命令中先计算文件 hash 再把它作为 pin，这只能证明文件自洽，不能证明它受信。

## 3. 采样中心与八方向边界

所有时间使用 UTC；坐标来源固定为 `palworld-authoritative-live-position`。

1. 保存权威中心位置，中心误差不得超过 `centerTolerance`。
2. 至少采样 `0/45/90/135/180/225/270/315` 八个方向。
3. 每个方向先 inside、后 outside，两次间隔不超过十分钟，各使用独立 artifact 与 nonce。
4. safety margin 至少为 `max(1, radius*2%)`，最多为半径的 25%。inside 目标带为 `[radius-2*margin, radius-margin]`；outside 为 `[radius+margin, radius+2*margin]`。
5. 不使用数学边界点。验证器按 `dx²+dy²<=radius²` 复算 inside/outside 与方位角。

采集器输出的每个 JSON 必须套签名 envelope。签名同时绑定 schema、campaign、server、game/Steam build、content、zone、artifact id/role、nonce、时间和 body hash。

## 4. 路线与真实报价行为

至少执行一次 ingress 和一次 egress。每条路线保存 3–512 个严格递增时间的权威坐标点：

- ingress：首点必须在区外，末点必须在区内；
- egress：首点必须在区内，末点必须在区外。

每条路线还必须绑定四份独立签名 artifact：

1. inside quote request；
2. inside quote response（HTTP 2xx、`success`、无 error code）；
3. outside quote request；
4. outside quote response（HTTP 409、`rejected`、`PLAYER_OUTSIDE_EXTRACTION_ZONE`）。

请求坐标必须等于对应路线首/末锚点，request/response 的 attempt、server、zone 和时间必须一致。只写“路线可达”布尔值或手工写报价结论不能通过。

随后记录可达性与风险。风险等级为 `low/medium/high`，处置必须为 `approved` 或 `acceptable`，并确认地形、敌对暴露与复活返程路线。任一项失败时调整点位后整轮重测。

## 5. 文件与隐私

建议把证据放在仓库外或被 `.gitignore` 排除的 `artifacts/`：

```text
artifacts/zone-calibration/2026w30-zone-02/
├── evidence.json
└── captures/
    ├── server-build.json
    ├── content-zone.json
    ├── center-position.json
    ├── direction-000-inside.json
    ├── direction-000-outside.json
    ├── route-ingress-a.json
    ├── route-ingress-a-inside-request.json
    └── ...
```

八方向最少为 31 份签名 artifact。每份文件记录唯一 id/role、相对路径、精确字节数、捕获时间与 SHA-256。禁止路径穿越、冒号/ADS、Windows 设备名、尾随点或空格、junction/symlink/reparse point、重复 JSON 属性和未声明字段。

证据不得含玩家名、PlayerUID、Steam64、账号、IP、邮箱、Cookie、token、目录用户名或私钥。验证器会扫描嵌入文本中的 IPv4/IPv6、凭据形态和 mock/fixture/template 标记。

## 6. 独立复核

先运行 `schema-info` 记录本构建内嵌的官方 schema hash。把 intended `reviewedAt/result/reviewerKeyId` 写入证据后，运行 `prepare-review`。此步骤会先验证全部 capture 签名、外部期望值、年龄、几何、路线、报价、风险和 artifact manifest，然后输出：

- `evidencePayloadSha256`：完整 canonical unsigned evidence；
- `artifactManifestSha256`：全部 31+ artifact 的 id/role/hash/size manifest；
- `statementBase64`：复核者必须签名的固定语句。

canonical review payload 还会按 artifact id 稳定排序并纳入每份完整元数据：相对路径、producer、media/capture mode、捕获时间、长度和签名 envelope 的 SHA-256。复核后复制同一文件到新路径或改写 producer 都会使签名失效。

复核者逐项核对后，用独立 reviewer private key 对解码后的 statement 做 ECDSA P-256/SHA-256、IEEE-P1363 签名，再把两个 hash 和签名写入 `evidence.review`。验证器会拒绝同 key、同公钥、同主体、错误域、过期或撤销 key。

完整命令与参数见 [工具 README](../../tools/zone-calibration/README.md)。正式 `verify` 必须显式提供 trust-store pin 和全部 `--expected-*`；不存在 `--test` 或自定义宽松 policy。

## 7. 最终报告与失效条件

`verify` 成功后会 create-new 写入 canonical report 与 sidecar。再用相同外部期望值运行 `verify-report`，确认：

- report 是严格 canonical JSON；
- sidecar 等于精确 report bytes 的 SHA-256；
- evidence/report schema hash 来自程序集内嵌资源；
- trust-store hash 等于外部 pin；
- reviewer signature 仍可由受信公钥验证；
- server/build/content/zone/年龄仍等于受控值。

各成功出口都会再次打开 evidence、trust store、全部 artifact、report 与 sidecar，按规范绝对路径、字节数和 SHA-256 复核；验证期间发生并发替换或追加时必须失败，不能使用第一次读取的旧快照继续放行。

以下任一变化会使证据失效：游戏或 Steam build 更新、内容版本/hash/zone 变化、证据过期、trust store pin 变化、key 撤销、任一 artifact 字节变化、review signature 不匹配，或实服路线/风险条件变化。

最终还要把 evidence/report/hash、trust-store pin 来源、执行者/复核者审批记录纳入外部验收 manifest。只有真实服务器的完整门禁通过后，才能更新对应 TODO。
