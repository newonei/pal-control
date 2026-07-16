# 经济运营工作台运行手册

经济运营工作台位于管理控制台的“经济运营”。它只读取 Control API 的全局权威投影，不会在浏览器中枚举 Steam 等平台主体，再逐玩家拼接订单或资源兑换记录。

## 可查看的权威状态

- 当前周世界、活动赛季、存档状态、内容版本、完整 content hash、业务日与热点；
- 购买和资源兑换独立熔断器、维护排空、活动操作与 blocker；
- 商城发货、资源扣物和 PalDefender outbox 的容量、积压、最老任务与 `uncertain`；
- 游戏备份、经济备份、活动告警和未完成周换档阶段；
- 全局订单、资源兑换、结构化 receipt/Native 回执证据及管理员审计 before/after。

全局投影不会返回原始 `PlayerUID` 或平台账号。玩家标识只以账户 UUID、显示名和截断 SHA-256 指纹出现；Cookie、验证码、TOTP、API Key、密码与适配器密钥不会进入响应。

## 权限和审批因子

只读投影与证据要求 `Viewer` 或更高角色。经济熔断、钱包调账、订单/兑换人工终结要求 `EconomyAdmin` 或 `Owner`；维护排空要求 `SeasonAdmin` 或 `Owner`。所有高风险操作还必须同时提供：

1. 当前管理员凭据；
2. 审计原因；
3. 页面显示的精确确认短语；
4. 当前 6 位 TOTP；
5. 与目标和请求绑定的 `Idempotency-Key`。

页面在提交前展示服务端当前值和预期目标值。人工对账只能在维护模式下处理 `uncertain`；必须先独立核对结构化 receipt、Native 回执、背包证据或明确的未执行证据，不能根据玩家口述直接选择结果。

## 响应丢失、刷新与服务重启

提交时，浏览器先把非敏感目标快照和操作 key 写入当前标签页的 `sessionStorage`；审计原因和 TOTP 从不持久化。Control API 同时在经济 SQLite 中把 key、请求 hash 和认证主体 hash 持久绑定：

- 相同 key、相同主体和相同请求可以跨页面刷新或服务重启重放；
- 同一 key 改变目标、payload、作用域或操作者时，返回 `ADMIN_IDEMPOTENCY_CONFLICT`；
- 熔断和维护的精确重放不会改变 `updatedAt`；钱包账本使用同一 key 最多创建一条记录；订单/兑换已达到同一人工终态时返回现有权威结果；
- 响应丢失后不要创建新操作。在黄色恢复区选择“核对 / 继续”：若权威状态已达到目标，页面完成恢复记录；否则重新输入原因和当前 TOTP，并复用原 key。

若网络错误发生在派发边界，先检查审计、订单/兑换状态和证据，不要盲目重复游戏命令。`uncertain` 不会被后台自动重试。

## 人工处置顺序

1. 记录告警 code、关联 ID、订单/兑换 ID 和 UTC 时间。
2. 关闭受影响的购买或资源兑换熔断器；需要人工终结时进入维护并等待活动操作归零。
3. 打开证据视图，核对 request hash、result ID、逐物品数量、Native disposition 与钱包账本。
4. 选择唯一有证据支持的终态。无法证明时保持 `uncertain` 并升级处理，不得猜测。
5. 执行后在审计页按关联 ID、操作者、路径或原因检索，确认 started/completed、before/after、HTTP 结果和关联账本/命令。
6. 修复依赖并确认队列、备份与 blocker 正常后，再以新的逻辑操作 key 恢复写入。

## API 与验证

全局投影和证据入口：

```text
GET /api/v1/extraction/admin/operations/overview
GET /api/v1/extraction/admin/operations/orders/{orderId}/evidence
GET /api/v1/extraction/admin/operations/runs/{runId}/evidence
```

本地验证：

```powershell
npm test --workspace apps/console-web
npm run build --workspace apps/console-web
./tests/integration/admin-operation-keys-smoke.ps1
./tests/integration/admin-auth-smoke.ps1
./tests/integration/control-api-boundary-smoke.ps1
```

`admin-operation-keys-smoke` 覆盖跨重启重放、100 路并发注册、请求/作用域/操作者冲突和原始输入不落库；边界 smoke 覆盖全局投影结构、隐私字段与稳定 404 错误。

当前代码还完成了一次真实 Chrome 人工故障验证：首次 POST 在浏览器离线时失败，刷新后恢复区仍显示同一非敏感操作快照；重新输入审计原因与当前 TOTP 后，第二次 POST 的 `Idempotency-Key` 与首次逐字相同，HTTP 200 后对应 `sessionStorage` 记录消失。存储检查确认恢复记录不含原因或 TOTP。桌面与 375×812 手机视口也已检查，README 中的经济运营截图来自该版本。
