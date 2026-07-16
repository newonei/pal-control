import { useEffect, useMemo, useState } from "react";
import {
  adjustWallet,
  getEconomyOperationsOverview,
  getOrderEvidence,
  getRunEvidence,
  reconcileOrder,
  reconcileRun,
  setEconomyCircuit,
  setEconomyMaintenance,
  type EconomyAccount,
  type EconomyOperationsOrder,
  type EconomyOperationsOverview,
  type EconomyOperationsRun
} from "./api";
import "./operations.css";

type OperationsTab = "status" | "orders" | "runs" | "audit";

export type RiskAction =
  | { kind: "maintenance"; maintenance: boolean }
  | { kind: "circuit"; feature: "purchase" | "resource-exchange"; writesEnabled: boolean }
  | { kind: "order"; order: EconomyOperationsOrder; resolution: "delivered" | "refund" }
  | { kind: "run"; run: EconomyOperationsRun; resolution: "settled" | "failed" }
  | { kind: "wallet"; account: EconomyAccount; currency: "merchantCoin" | "weeklyTicket"; delta: number };

type PendingOperation = {
  actionKey: string;
  idempotencyKey: string;
  label: string;
  createdAt: string;
  action?: RiskAction;
};

const numberFormatter = new Intl.NumberFormat("zh-CN");
const dateFormatter = new Intl.DateTimeFormat("zh-CN", {
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
  hour12: false
});
const pendingPrefix = "pal-control.economy-operation.";

export function EconomyOperationsWorkbench() {
  const [overview, setOverview] = useState<EconomyOperationsOverview>();
  const [tab, setTab] = useState<OperationsTab>("status");
  const [query, setQuery] = useState("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [evidence, setEvidence] = useState<{ title: string; value: Record<string, unknown> }>();
  const [evidenceLoading, setEvidenceLoading] = useState(false);
  const [action, setAction] = useState<RiskAction>();
  const [reason, setReason] = useState("");
  const [totp, setTotp] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [pending, setPending] = useState<PendingOperation[]>(readPendingOperations);
  const [walletAccountId, setWalletAccountId] = useState("");
  const [walletCurrency, setWalletCurrency] = useState<"merchantCoin" | "weeklyTicket">("merchantCoin");
  const [walletDelta, setWalletDelta] = useState(0);

  async function refresh(force = false, signal?: AbortSignal) {
    setError(undefined);
    try {
      const next = await getEconomyOperationsOverview(force, signal);
      if (signal?.aborted) return;
      setOverview(next);
      setWalletAccountId((current) => current || next.accounts[0]?.accountId || "");
    } catch (nextError) {
      if (!signal?.aborted) setError(errorMessage(nextError, "经济运营状态读取失败"));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    void refresh(false, controller.signal);
    return () => controller.abort();
  }, []);

  const hasLiveWork = Boolean(
    overview?.rollover ||
    overview?.orders.some((order) => order.requiresReconciliation) ||
    overview?.runs.some((run) => run.requiresReconciliation) ||
    (overview?.queues.delivery.pending ?? 0) > 0 ||
    (overview?.queues.settlement.pending ?? 0) > 0 ||
    (overview?.queues.outbox.pending ?? 0) > 0
  );

  useEffect(() => {
    if (!hasLiveWork || globalThis.document?.visibilityState === "hidden") return;
    const timer = globalThis.setInterval(() => void refresh(false), 5_000);
    return () => globalThis.clearInterval(timer);
  }, [hasLiveWork]);

  const filteredOrders = useMemo(
    () => (overview?.orders ?? []).filter((order) => matches(query, [
      order.orderId,
      order.productName,
      order.sku,
      order.state,
      order.outcome ?? "",
      order.account?.displayName ?? "",
      order.account?.platformSubjectHash ?? ""
    ])),
    [overview?.orders, query]
  );
  const filteredRuns = useMemo(
    () => (overview?.runs ?? []).filter((run) => matches(query, [
      run.runId,
      run.zoneName,
      run.state,
      run.errorCode ?? "",
      run.account?.displayName ?? "",
      run.account?.platformSubjectHash ?? ""
    ])),
    [overview?.runs, query]
  );
  const activeAlerts = overview?.alerts.filter((alert) => alert.active) ?? [];
  const uncertainCount = (overview?.orders.filter((order) => order.requiresReconciliation).length ?? 0) +
    (overview?.runs.filter((run) => run.requiresReconciliation).length ?? 0);

  function openAction(next: RiskAction) {
    setAction(next);
    setReason("");
    setTotp("");
    setConfirmation("");
    setError(undefined);
    setNotice(undefined);
  }

  function closeAction() {
    if (submitting) return;
    setAction(undefined);
    setReason("");
    setTotp("");
    setConfirmation("");
  }

  function resumePendingOperation(operation: PendingOperation) {
    if (!overview) {
      setError("权威状态尚未加载，无法核对操作结果。");
      return;
    }
    if (!operation.action) {
      setError("这是旧版恢复记录，缺少目标快照；请先从审计记录核对，再手工清除浏览器会话数据。");
      return;
    }
    if (isPendingTargetSatisfied(operation.action, overview)) {
      removePendingOperation(operation.actionKey);
      setPending(readPendingOperations());
      setNotice(`${operation.label}的目标状态已经由权威存储确认，恢复记录已完成。`);
      return;
    }
    openAction(operation.action);
    setNotice("将复用原 Idempotency-Key；请重新提供审计原因与当前 TOTP 后提交。");
  }

  async function showEvidence(kind: "order" | "run", id: string) {
    setEvidenceLoading(true);
    setError(undefined);
    try {
      const value = kind === "order" ? await getOrderEvidence(id) : await getRunEvidence(id);
      setEvidence({ title: `${kind === "order" ? "订单" : "资源兑换"}证据 · ${shortId(id)}`, value });
    } catch (nextError) {
      setError(errorMessage(nextError, "证据读取失败"));
    } finally {
      setEvidenceLoading(false);
    }
  }

  async function submitRiskAction() {
    if (!action || submitting) return;
    const expected = expectedConfirmation(action);
    if (confirmation !== expected) {
      setError(`确认短语必须精确填写：${expected}`);
      return;
    }
    if (!/^\d{6}$/.test(totp)) {
      setError("高风险操作需要 6 位 TOTP 动态验证码。");
      return;
    }
    if (reason.trim().length < 3) {
      setError("请填写至少 3 个字符的审计原因。");
      return;
    }

    const descriptor = actionDescriptor(action);
    const operation = getOrCreatePendingOperation(action, descriptor.label);
    setPending(readPendingOperations());
    setSubmitting(true);
    setError(undefined);
    try {
      const proof = { reason: reason.trim(), totp };
      if (action.kind === "maintenance") {
        await setEconomyMaintenance(action.maintenance, proof, operation.idempotencyKey);
      } else if (action.kind === "circuit") {
        await setEconomyCircuit(action.feature, action.writesEnabled, proof, operation.idempotencyKey);
      } else if (action.kind === "order") {
        await reconcileOrder(
          action.order.orderId,
          action.resolution,
          expected,
          proof,
          operation.idempotencyKey
        );
      } else if (action.kind === "run") {
        await reconcileRun(
          action.run.runId,
          action.resolution,
          expected,
          proof,
          operation.idempotencyKey
        );
      } else {
        await adjustWallet({
          accountId: action.account.accountId,
          currency: action.currency,
          delta: action.delta
        }, proof, operation.idempotencyKey);
      }
      removePendingOperation(descriptor.key);
      setPending(readPendingOperations());
      setNotice(`${descriptor.label}已完成；审计 before/after 将在刷新后显示。`);
      setAction(undefined);
      setReason("");
      setTotp("");
      setConfirmation("");
      await refresh(true);
    } catch (nextError) {
      // Keep the deterministic idempotency key in sessionStorage. A response
      // loss or page reload can safely resume the same logical operation.
      setError(errorMessage(nextError, `${descriptor.label}失败`));
    } finally {
      setSubmitting(false);
    }
  }

  const selectedWalletAccount = overview?.accounts.find((account) => account.accountId === walletAccountId);

  return (
    <section className="economy-ops">
      <header className="economy-ops-hero">
        <div>
          <p className="eyebrow">ECONOMY COMMAND CENTER</p>
          <h2>经济运营工作台</h2>
          <p>全局状态、证据、人工处置和审计都从权威存储读取；响应丢失时复用同一操作键。</p>
        </div>
        <div className="economy-ops-actions">
          <span className={overview?.gate.maintenance ? "ops-state maintenance" : "ops-state live"}>
            {overview?.gate.maintenance ? "维护排空中" : "经济运行中"}
          </span>
          <button disabled={loading} onClick={() => void refresh(true)} type="button">
            {loading ? "同步中…" : "强制刷新"}
          </button>
        </div>
      </header>

      {error ? <div className="ops-feedback error" role="alert"><span>{error}</span><button onClick={() => setError(undefined)} type="button">关闭</button></div> : null}
      {notice ? <div className="ops-feedback success" role="status"><span>{notice}</span><button onClick={() => setNotice(undefined)} type="button">关闭</button></div> : null}

      {pending.length > 0 ? (
        <section className="ops-resume" aria-label="可恢复的高风险操作">
          <strong>检测到 {pending.length} 个尚未确认响应的操作键</strong>
          <p>不要创建新操作。核对当前权威状态和审计记录后，以相同目标重新提交即可复用原 Idempotency-Key。</p>
          <div>{pending.map((item) => <span key={item.actionKey}><code>{item.label} · {formatDate(item.createdAt)}</code><button onClick={() => resumePendingOperation(item)} type="button">核对 / 继续</button></span>)}</div>
        </section>
      ) : null}

      <div className="ops-summary-grid">
        <SummaryCard
          label="当前周世界"
          value={overview?.world.season?.code ?? "未绑定"}
          detail={overview?.world.season?.worldId ?? "没有活动世界证据"}
          state={overview?.world.season?.worldId ? "good" : "warning"}
        />
        <SummaryCard
          label="内容版本"
          value={overview?.content ? `v${overview.content.versionNumber}` : "未发布"}
          detail={overview?.content ? `${overview.content.businessDate} · ${shortHash(overview.content.contentHash)}` : "current pointer 不存在"}
          state={overview?.content ? "good" : "warning"}
        />
        <SummaryCard
          label="待人工核验"
          value={String(uncertainCount)}
          detail={`订单 ${overview?.orders.filter((item) => item.requiresReconciliation).length ?? 0} · 兑换 ${overview?.runs.filter((item) => item.requiresReconciliation).length ?? 0}`}
          state={uncertainCount === 0 ? "good" : "danger"}
        />
        <SummaryCard
          label="活动告警"
          value={String(activeAlerts.length)}
          detail={activeAlerts[0]?.code ?? "没有活动告警"}
          state={activeAlerts.length === 0 ? "good" : "danger"}
        />
        <SummaryCard
          label="游戏备份"
          value={backupLabel(overview?.backups.game)}
          detail={backupDetail(overview?.backups.game)}
          state={overview?.backups.game.fresh ? "good" : "warning"}
        />
        <SummaryCard
          label="经济备份"
          value={backupLabel(overview?.backups.economy)}
          detail={backupDetail(overview?.backups.economy)}
          state={overview?.backups.economy.fresh ? "good" : "warning"}
        />
      </div>

      <nav className="ops-tabs" aria-label="经济运营视图">
        {([
          ["status", "总览与闸门"],
          ["orders", `全局订单 ${overview?.orders.length ?? 0}`],
          ["runs", `资源兑换 ${overview?.runs.length ?? 0}`],
          ["audit", `审计 ${overview?.audit.length ?? 0}`]
        ] as Array<[OperationsTab, string]>).map(([key, label]) => (
          <button aria-current={tab === key ? "page" : undefined} className={tab === key ? "active" : ""} key={key} onClick={() => setTab(key)} type="button">{label}</button>
        ))}
      </nav>

      {tab !== "status" ? (
        <label className="ops-search">
          <span>筛选当前表格</span>
          <input onChange={(event) => setQuery(event.target.value)} placeholder="账户、状态、ID、错误码…" value={query} />
        </label>
      ) : null}

      {tab === "status" ? (
        <div className="ops-status-layout">
          <section className="ops-panel">
            <header><div><p className="eyebrow">SAFETY GATES</p><h3>经济闸门</h3></div><span>{overview?.gate.activeOperations ?? 0} 个活动操作</span></header>
            <div className="ops-gate-list">
              <GateRow
                label="维护模式"
                enabled={!overview?.gate.maintenance}
                detail={overview?.gate.reason ?? "状态未加载"}
                onToggle={() => openAction({ kind: "maintenance", maintenance: !overview?.gate.maintenance })}
              />
              <GateRow
                label="商城购买"
                enabled={overview?.gate.circuits.purchase.writesEnabled ?? false}
                detail={(overview?.gate.blockers.purchase ?? []).join("、") || "没有运行时 blocker"}
                onToggle={() => openAction({ kind: "circuit", feature: "purchase", writesEnabled: !(overview?.gate.circuits.purchase.writesEnabled ?? false) })}
              />
              <GateRow
                label="资源兑换"
                enabled={overview?.gate.circuits.resourceExchange.writesEnabled ?? false}
                detail={(overview?.gate.blockers.resourceExchange ?? []).join("、") || "没有运行时 blocker"}
                onToggle={() => openAction({ kind: "circuit", feature: "resource-exchange", writesEnabled: !(overview?.gate.circuits.resourceExchange.writesEnabled ?? false) })}
              />
            </div>
          </section>

          <section className="ops-panel">
            <header><div><p className="eyebrow">DURABLE QUEUES</p><h3>队列与 Outbox</h3></div></header>
            <div className="ops-queue-list">
              <QueueRow label="商城发货" value={overview?.queues.delivery} />
              <QueueRow label="资源扣物" value={overview?.queues.settlement} />
              <QueueRow label="PalDefender Outbox" value={overview?.queues.outbox} />
            </div>
          </section>

          <section className="ops-panel ops-rollover">
            <header><div><p className="eyebrow">WEEKLY ROLLOVER</p><h3>换档阶段</h3></div></header>
            {overview?.rollover ? <>
              <div className="ops-rollover-current"><strong>{overview.rollover.currentStep}</strong><span>revision {overview.rollover.revision}</span></div>
              <dl>
                <div><dt>操作</dt><dd>{overview.rollover.operationId}</dd></div>
                <div><dt>旧世界</dt><dd>{overview.rollover.fromWorldId}</dd></div>
                <div><dt>目标世界</dt><dd>{overview.rollover.targetWorldId}</dd></div>
                <div><dt>已完成证据</dt><dd>{overview.rollover.completedSteps.length} 步</dd></div>
              </dl>
            </> : <div className="ops-empty"><strong>没有未完成换档</strong><span>页面刷新或服务重启后会从持久状态恢复活动操作。</span></div>}
          </section>

          <section className="ops-panel ops-adjustment">
            <header><div><p className="eyebrow">MANUAL ADJUSTMENT</p><h3>审计调账</h3></div></header>
            <div className="ops-adjustment-form">
              <label><span>账户</span><select onChange={(event) => setWalletAccountId(event.target.value)} value={walletAccountId}>
                {(overview?.accounts ?? []).map((account) => <option key={account.accountId} value={account.accountId}>{account.displayName} · {account.platform}</option>)}
              </select></label>
              <label><span>货币</span><select onChange={(event) => setWalletCurrency(event.target.value as typeof walletCurrency)} value={walletCurrency}>
                <option value="merchantCoin">永久商域币</option><option value="weeklyTicket">本周战备券</option>
              </select></label>
              <label><span>变化量（可为负）</span><input onChange={(event) => setWalletDelta(Number.parseInt(event.target.value, 10) || 0)} type="number" value={walletDelta} /></label>
              <button disabled={!selectedWalletAccount || walletDelta === 0} onClick={() => selectedWalletAccount && openAction({ kind: "wallet", account: selectedWalletAccount, currency: walletCurrency, delta: walletDelta })} type="button">进入双重确认</button>
            </div>
          </section>
        </div>
      ) : null}

      {tab === "orders" ? (
        <section className="ops-table-panel">
          <table><thead><tr><th>订单 / 账户</th><th>商品</th><th>金额</th><th>状态</th><th>版本证据</th><th>操作</th></tr></thead><tbody>
            {filteredOrders.map((order) => <tr className={order.requiresReconciliation ? "needs-review" : ""} key={order.orderId}>
              <td><strong>{shortId(order.orderId)}</strong><small>{order.account?.displayName ?? shortId(order.accountId)} · {formatDate(order.createdAt)}</small></td>
              <td><strong>{order.productName} × {order.quantity}</strong><small>{order.sku}</small></td>
              <td>{formatNumber(order.totalAmount)} {currencyLabel(order.currency)}</td>
              <td><StateBadge value={order.outcome ?? order.state} /><small>attempt {order.deliveryAttempt}</small></td>
              <td><code>{shortId(order.contentVersionId ?? "none")}</code><small>{shortHash(order.contentHash)}</small></td>
              <td><div className="ops-row-actions"><button disabled={evidenceLoading} onClick={() => void showEvidence("order", order.orderId)} type="button">查看证据</button>{order.requiresReconciliation ? <><button onClick={() => openAction({ kind: "order", order, resolution: "delivered" })} type="button">确认到账</button><button className="danger" onClick={() => openAction({ kind: "order", order, resolution: "refund" })} type="button">退款终结</button></> : null}</div></td>
            </tr>)}
          </tbody></table>
          {filteredOrders.length === 0 ? <div className="ops-empty">没有匹配的订单。</div> : null}
        </section>
      ) : null}

      {tab === "runs" ? (
        <section className="ops-table-panel">
          <table><thead><tr><th>兑换 / 账户</th><th>区域</th><th>价值</th><th>状态</th><th>证据</th><th>操作</th></tr></thead><tbody>
            {filteredRuns.map((run) => <tr className={run.requiresReconciliation ? "needs-review" : ""} key={run.runId}>
              <td><strong>{shortId(run.runId)}</strong><small>{run.account?.displayName ?? shortId(run.accountId)} · {formatDate(run.quotedAt)}</small></td>
              <td><strong>{run.zoneName}</strong><small>{run.zoneId}</small></td>
              <td>{formatNumber(run.totalValue)} 战备券<small>{formatNumber(run.itemCount)} 件</small></td>
              <td><StateBadge value={run.state} /><small>{run.errorCode ?? `revision ${run.revision}`}</small></td>
              <td><code>{shortHash(run.quoteSnapshotHash)}</code><small>{shortHash(run.contentHash)}</small></td>
              <td><div className="ops-row-actions"><button disabled={evidenceLoading} onClick={() => void showEvidence("run", run.runId)} type="button">查看证据</button>{run.requiresReconciliation ? <><button onClick={() => openAction({ kind: "run", run, resolution: "settled" })} type="button">确认扣物并入账</button><button className="danger" onClick={() => openAction({ kind: "run", run, resolution: "failed" })} type="button">确认未扣物</button></> : null}</div></td>
            </tr>)}
          </tbody></table>
          {filteredRuns.length === 0 ? <div className="ops-empty">没有匹配的资源兑换记录。</div> : null}
        </section>
      ) : null}

      {tab === "audit" ? (
        <section className="ops-audit-list">
          {(overview?.audit ?? []).filter((item) => matches(query, [item.subject, item.path, item.correlationId, item.reason ?? "", item.phase])).map((item) => (
            <article key={item.auditId}>
              <header><StateBadge value={item.phase} /><strong>{item.method} {item.path}</strong><time>{formatDate(item.occurredAt)}</time></header>
              <div><span>操作者 <code>{item.subject}</code></span><span>关联 <code>{shortId(item.correlationId)}</code></span><span>HTTP {item.resultStatus ?? "—"}</span></div>
              <p>{item.reason ?? "未记录操作原因"}</p>
              {(item.beforeJson || item.afterJson) ? <details><summary>查看 before / after</summary><div className="ops-audit-diff"><pre>{prettyJson(item.beforeJson)}</pre><pre>{prettyJson(item.afterJson)}</pre></div></details> : null}
            </article>
          ))}
        </section>
      ) : null}

      {action ? (
        <div className="ops-dialog-backdrop">
          <section aria-labelledby="ops-risk-title" aria-modal="true" className="ops-dialog" role="dialog">
            <header><div><p className="eyebrow">HIGH RISK OPERATION</p><h3 id="ops-risk-title">{actionDescriptor(action).label}</h3></div><button aria-label="关闭" disabled={submitting} onClick={closeAction} type="button">×</button></header>
            <div className="ops-before-after"><article><span>执行前</span><pre>{JSON.stringify(actionBefore(action), null, 2)}</pre></article><article><span>期望执行后</span><pre>{JSON.stringify(actionAfter(action), null, 2)}</pre></article></div>
            <label><span>审计原因</span><textarea maxLength={500} onChange={(event) => setReason(event.target.value)} placeholder="说明证据、工单或处置依据" value={reason} /></label>
            <label><span>确认短语</span><code>{expectedConfirmation(action)}</code><input autoComplete="off" onChange={(event) => setConfirmation(event.target.value)} value={confirmation} /></label>
            <label><span>6 位 TOTP</span><input autoComplete="one-time-code" inputMode="numeric" maxLength={6} onChange={(event) => setTotp(event.target.value.replace(/\D/g, ""))} value={totp} /></label>
            <dl className="ops-authorization"><div><dt>授权角色</dt><dd>{requiredAuthorization(action).role}</dd></div><div><dt>审批因子</dt><dd>当前管理员身份 + 审计原因 + 精确确认短语 + TOTP</dd></div></dl>
            <p className="ops-risk-note">操作提交前会持久化确定性 Idempotency-Key；响应丢失或刷新后必须复用该键，不得新建重复操作。</p>
            <footer><button disabled={submitting} onClick={closeAction} type="button">取消</button><button className="danger" disabled={submitting} onClick={() => void submitRiskAction()} type="button">{submitting ? "提交并等待权威结果…" : "确认并执行"}</button></footer>
          </section>
        </div>
      ) : null}

      {evidence ? (
        <div className="ops-dialog-backdrop">
          <section aria-labelledby="ops-evidence-title" aria-modal="true" className="ops-dialog ops-evidence-dialog" role="dialog">
            <header><div><p className="eyebrow">AUTHORITATIVE EVIDENCE</p><h3 id="ops-evidence-title">{evidence.title}</h3></div><button aria-label="关闭" onClick={() => setEvidence(undefined)} type="button">×</button></header>
            <p>PlayerUID 已哈希；Cookie、验证码、Token、密码和适配器密钥不会进入此投影。</p>
            <pre>{JSON.stringify(evidence.value, null, 2)}</pre>
            <footer><button onClick={() => setEvidence(undefined)} type="button">关闭</button></footer>
          </section>
        </div>
      ) : null}
    </section>
  );
}

function SummaryCard({ label, value, detail, state }: { label: string; value: string; detail: string; state: string }) {
  return <article className={`ops-summary ${state}`}><span>{label}</span><strong>{value}</strong><small>{detail}</small></article>;
}

function GateRow({ label, enabled, detail, onToggle }: { label: string; enabled: boolean; detail: string; onToggle: () => void }) {
  return <article><div><strong>{label}</strong><small>{detail}</small></div><span className={enabled ? "enabled" : "disabled"}>{enabled ? "写入开启" : "写入关闭"}</span><button onClick={onToggle} type="button">{enabled ? "关闭" : "开启"}</button></article>;
}

function QueueRow({ label, value }: { label: string; value: EconomyOperationsOverview["queues"]["delivery"] | undefined }) {
  const percent = Math.max(0, Math.min(100, value?.utilizationPercent ?? 0));
  return <article><div><strong>{label}</strong><small>最老任务 {formatAge(value?.oldestAgeSeconds)}</small></div><span>{value?.pending ?? 0} / {value?.capacity ?? 0}</span><i><b style={{ width: `${percent}%` }} /></i></article>;
}

function StateBadge({ value }: { value: string }) {
  return <span className={`ops-badge ${value.toLocaleLowerCase()}`}>{stateLabel(value)}</span>;
}

export function actionDescriptor(action: RiskAction) {
  if (action.kind === "maintenance") return { key: `maintenance:${action.maintenance}`, label: action.maintenance ? "进入维护并排空" : "退出维护并恢复运行" };
  if (action.kind === "circuit") return { key: `circuit:${action.feature}:${action.writesEnabled}`, label: `${action.writesEnabled ? "开启" : "关闭"}${action.feature === "purchase" ? "商城购买" : "资源兑换"}` };
  if (action.kind === "order") return { key: `order:${action.order.orderId}:${action.resolution}`, label: action.resolution === "delivered" ? "确认订单已完整到账" : "退款并终结不确定订单" };
  if (action.kind === "run") return { key: `run:${action.run.runId}:${action.resolution}`, label: action.resolution === "settled" ? "确认资源已扣除并入账" : "确认资源未扣除并终结" };
  return { key: `wallet:${action.account.accountId}:${action.currency}:${action.delta}`, label: `调账 ${action.account.displayName} ${action.delta > 0 ? "+" : ""}${action.delta}` };
}

export function expectedConfirmation(action: RiskAction) {
  if (action.kind === "maintenance") return action.maintenance ? "ENABLE-MAINTENANCE" : "DISABLE-MAINTENANCE";
  if (action.kind === "circuit") return `${action.writesEnabled ? "ENABLE" : "DISABLE"}-${action.feature.toUpperCase()}`;
  if (action.kind === "order") return `ORDER-${action.order.orderId.replaceAll("-", "")}-${action.resolution.toUpperCase()}`;
  if (action.kind === "run") return `RUN-${action.run.runId.replaceAll("-", "")}-${action.resolution.toUpperCase()}`;
  return `ADJUST-${action.account.accountId.replaceAll("-", "")}-${action.currency.toUpperCase()}-${action.delta}`;
}

export function requiredAuthorization(action: RiskAction) {
  return {
    role: action.kind === "maintenance" ? "SeasonAdmin / Owner" : "EconomyAdmin / Owner",
    reasonRequired: true,
    exactConfirmationRequired: true,
    totpRequired: true
  };
}

export function isPendingTargetSatisfied(
  action: RiskAction,
  overview: EconomyOperationsOverview
) {
  if (action.kind === "maintenance") return overview.gate.maintenance === action.maintenance;
  if (action.kind === "circuit") {
    const state = action.feature === "purchase"
      ? overview.gate.circuits.purchase
      : overview.gate.circuits.resourceExchange;
    return state.writesEnabled === action.writesEnabled;
  }
  if (action.kind === "order") {
    const current = overview.orders.find((order) => order.orderId === action.order.orderId);
    return current?.state.toLocaleLowerCase() === (action.resolution === "delivered" ? "delivered" : "refunded");
  }
  if (action.kind === "run") {
    const current = overview.runs.find((run) => run.runId === action.run.runId);
    return current?.state.toLocaleLowerCase() === (action.resolution === "settled" ? "settled" : "failed");
  }
  // A wallet balance can legitimately reach the same numeric value through a
  // different ledger entry. Only replaying the original server operation key
  // can authoritatively prove this target, so never infer completion here.
  return false;
}

function actionBefore(action: RiskAction): Record<string, unknown> {
  if (action.kind === "maintenance") return { maintenance: !action.maintenance };
  if (action.kind === "circuit") return { feature: action.feature, writesEnabled: !action.writesEnabled };
  if (action.kind === "order") return { orderId: action.order.orderId, state: action.order.state, outcome: action.order.outcome, totalAmount: action.order.totalAmount };
  if (action.kind === "run") return { runId: action.run.runId, state: action.run.state, revision: action.run.revision, totalValue: action.run.totalValue };
  return { accountId: action.account.accountId, currency: action.currency, delta: 0 };
}

function actionAfter(action: RiskAction): Record<string, unknown> {
  if (action.kind === "maintenance") return { maintenance: action.maintenance };
  if (action.kind === "circuit") return { feature: action.feature, writesEnabled: action.writesEnabled };
  if (action.kind === "order") return { orderId: action.order.orderId, resolution: action.resolution, requiresReconciliation: false };
  if (action.kind === "run") return { runId: action.run.runId, resolution: action.resolution, requiresReconciliation: false };
  return { accountId: action.account.accountId, currency: action.currency, delta: action.delta, ledger: "exactly-once" };
}

function getOrCreatePendingOperation(action: RiskAction, label: string): PendingOperation {
  const actionKey = actionDescriptor(action).key;
  const storageKey = pendingPrefix + actionKey;
  try {
    const stored = globalThis.sessionStorage?.getItem(storageKey);
    if (stored) {
      const parsed = JSON.parse(stored) as PendingOperation;
      if (!parsed.action) {
        parsed.action = action;
        globalThis.sessionStorage?.setItem(storageKey, JSON.stringify(parsed));
      }
      return parsed;
    }
    const created: PendingOperation = {
      actionKey,
      idempotencyKey: globalThis.crypto?.randomUUID?.() ?? `ops-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      label,
      createdAt: new Date().toISOString(),
      action
    };
    globalThis.sessionStorage?.setItem(storageKey, JSON.stringify(created));
    return created;
  } catch {
    return { actionKey, idempotencyKey: `ops-${Date.now()}-${Math.random().toString(16).slice(2)}`, label, createdAt: new Date().toISOString(), action };
  }
}

function removePendingOperation(actionKey: string) {
  try { globalThis.sessionStorage?.removeItem(pendingPrefix + actionKey); } catch { /* visible refresh still reconciles authority */ }
}

function readPendingOperations(): PendingOperation[] {
  try {
    const storage = globalThis.sessionStorage;
    if (!storage) return [];
    const result: PendingOperation[] = [];
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index);
      if (!key?.startsWith(pendingPrefix)) continue;
      const raw = storage.getItem(key);
      if (raw) result.push(JSON.parse(raw) as PendingOperation);
    }
    return result.sort((left, right) => left.createdAt.localeCompare(right.createdAt));
  } catch { return []; }
}

function matches(query: string, values: string[]) {
  const needle = query.trim().toLocaleLowerCase();
  return !needle || values.some((value) => value.toLocaleLowerCase().includes(needle));
}

function shortId(value: string) { return value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value; }
function shortHash(value: string | null | undefined) { return value ? `${value.slice(0, 12)}…` : "—"; }
function formatNumber(value: number) { return numberFormatter.format(value); }
function formatDate(value: string) { const date = new Date(value); return Number.isNaN(date.getTime()) ? "时间未知" : dateFormatter.format(date); }
function formatAge(value: number | null | undefined) { if (value == null) return "—"; if (value < 60) return `${Math.round(value)} 秒`; if (value < 3600) return `${Math.round(value / 60)} 分`; return `${Math.round(value / 3600)} 小时`; }
function currencyLabel(value: string) { return value === "merchantCoin" ? "商域币" : "战备券"; }
function backupLabel(value: { fresh: boolean; available: boolean } | undefined) { return value?.fresh ? "新鲜" : value?.available ? "已过期" : "不可用"; }
function backupDetail(value: { latestCreatedAt: string | null; ageSeconds: number | null } | undefined) { return value?.latestCreatedAt ? `${formatDate(value.latestCreatedAt)} · ${formatAge(value.ageSeconds)}` : "没有可验证备份"; }
function prettyJson(value: string | null) { if (!value) return "—"; try { return JSON.stringify(JSON.parse(value), null, 2); } catch { return value; } }
function stateLabel(value: string) { const labels: Record<string, string> = { completed: "完成", failed: "失败", started: "开始", succeeded: "成功", delivered: "已到账", deliveryuncertain: "待核验", uncertain: "待核验", settled: "已结算", refunded: "已退款", pendingdelivery: "待发货", dispatching: "派发中", quoted: "已报价", expired: "已过期" }; return labels[value.toLocaleLowerCase()] ?? value; }
function errorMessage(error: unknown, fallback: string) { return error instanceof Error ? error.message : fallback; }
