# Weekly economy report CLI

受控生成、验证和双人复核不可变周档经济报告。所有命令都要求受控环境注入 `PAL_CONTROL_WEEKLY_REVIEW_TRUST_SHA256`，并把它与 `--review-trust-store` 的原始字节严格比较；复核 revision 由信任库中的不同 ECDSA P-256 主体签名。信任库拒绝重复 JSON 属性、重复实际公钥和非 P-256 公钥。

每次成功操作都会输出 `Review head SHA-256`。必须把它保存在归档目录之外的追加审计/WORM 登记中，并在后续操作显式传回：

- `verify`/`status`：`--expected-review-head-sha256 <当前外部 head>`；
- `review`：`--expected-current-review-head-sha256 <追加前外部 head>`；
- 连续周 `generate`：`--previous-season` 与 `--previous-review-head-sha256 <上一周 approved head>` 成对使用；
- 已有归档幂等重放：`--expected-existing-review-head-sha256 <本周当前外部 head>`。

错误或过时 head、删除 review 链末尾、用旧 head 重放/追加都会失败。不要从当前归档重新计算一个 head 来替换外部登记。

报告把冻结参与人数写入隐私元数据。当前最小群体为 5：1–4 人时 `report.json`、可选 HTML 及 manifest 条目均为 `restricted-small-cohort`；达到 5 人后才是 `operator-shareable-aggregate`。后者仍仅表示授权内部运营可共享，不代表可直接公开。

完整命令、私钥分离、外部 head 登记、隐私边界和故障处置见 [`docs/runbooks/weekly-economy-report.md`](../../docs/runbooks/weekly-economy-report.md)。
