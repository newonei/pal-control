import { useEffect, useId, useState, type FormEvent, type ReactNode } from "react";
import {
  getEconomyAnalytics,
  type AnalyticsCount,
  type AnalyticsRate,
  type EconomyAnalyticsFilters,
  type EconomyAnalyticsReport
} from "./api";
import "./economy-analytics.css";

const initialFilters = defaultFilters();

export function EconomyAnalyticsDashboard() {
  const headingId = useId();
  const [draft, setDraft] = useState<EconomyAnalyticsFilters>(initialFilters);
  const [request, setRequest] = useState<EconomyAnalyticsFilters>(initialFilters);
  const [report, setReport] = useState<EconomyAnalyticsReport>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(undefined);
    void getEconomyAnalytics(request, controller.signal)
      .then((next) => {
        if (!controller.signal.aborted) setReport(next);
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) {
          setError(reason instanceof Error ? reason.message : "运营分析读取失败。");
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [request]);

  function apply(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setRequest({ ...draft, cursor: undefined });
  }

  function page(cursor: string) {
    setRequest((current) => ({ ...current, cursor }));
  }

  const previousOffset = Math.max(0, (report?.page.offset ?? 0) - (report?.page.limit ?? 50));
  return (
    <section className="analytics-page" aria-labelledby={headingId}>
      <header className="analytics-hero">
        <div>
          <p className="eyebrow">AUTHORITATIVE ECONOMY ANALYTICS</p>
          <h2 id={headingId}>运营分析</h2>
          <p>所有玩法事实由服务器从 SQLite 权威事件重算；刷新页面不会增加目录访问次数。</p>
        </div>
        {report ? <div className={report.window.stable ? "analytics-badge stable" : "analytics-badge partial"}>
          {report.window.stable ? "稳定窗口" : "当日数据未封口"}
        </div> : null}
      </header>

      <form className="analytics-filters" onSubmit={apply} aria-label="运营分析筛选">
        <Filter label="服务器">
          <input value={draft.serverId} maxLength={64} required
            onChange={(event) => setDraft({ ...draft, serverId: event.target.value })} />
        </Filter>
        <Filter label="开始日期">
          <input type="date" value={draft.from} required
            onChange={(event) => setDraft({ ...draft, from: event.target.value })} />
        </Filter>
        <Filter label="结束日期">
          <input type="date" value={draft.to} required
            onChange={(event) => setDraft({ ...draft, to: event.target.value })} />
        </Filter>
        <Filter label="日期口径">
          <select value={draft.dateBasis}
            onChange={(event) => setDraft({ ...draft, dateBasis: event.target.value as "business" | "utc" })}>
            <option value="business">业务日</option><option value="utc">UTC 日</option>
          </select>
        </Filter>
        <Filter label="赛季 ID（可选）">
          <input value={draft.seasonId ?? ""} placeholder="UUID"
            onChange={(event) => setDraft({ ...draft, seasonId: event.target.value || undefined })} />
        </Filter>
        <Filter label="内容版本 ID（可选）">
          <input value={draft.contentVersionId ?? ""} placeholder="UUID"
            onChange={(event) => setDraft({ ...draft, contentVersionId: event.target.value || undefined })} />
        </Filter>
        <button className="primary-button analytics-apply" type="submit">应用筛选</button>
      </form>

      <div className="analytics-live" aria-live="polite" role="status">
        {loading ? "正在从权威 SQLite 重新计算…" : report ? `数据截至 ${formatTime(report.source.asOf)}` : ""}
      </div>
      {error ? <div className="analytics-error" role="alert">{error}</div> : null}
      {report ? <>
        {!report.source.complete ? <div className="analytics-warning" role="alert">
          来源覆盖不完整：比率保持不可用，请检查告警后再做运营判断。
        </div> : null}
        <section aria-labelledby="analytics-funnel-heading">
          <SectionHeading id="analytics-funnel-heading" title="玩法漏斗" note="退款、失败和 uncertain 不计入成功阶段" />
          <div className="analytics-funnel">
            {report.funnel.map((stage, index) => <article className="analytics-stage" key={stage.key}>
              <span>{String(index + 1).padStart(2, "0")}</span>
              <h3>{stage.label}</h3>
              <strong>{formatCount(stage.accounts)}</strong>
              <small>{stage.successOnly ? "仅成功终态" : "权威事实"} · {stage.facts ?? "已隐藏"} 条</small>
            </article>)}
          </div>
        </section>

        <div className="analytics-grid">
          <section className="analytics-panel" aria-labelledby="analytics-shop-heading">
            <SectionHeading id="analytics-shop-heading" title="商品购买率" note="已送达买家 ÷ 同版本目录访客" />
            <div className="table-scroll"><table>
              <caption className="sr-only">商品购买率</caption>
              <thead><tr><th scope="col">SKU</th><th scope="col">目录访客</th><th scope="col">送达买家</th><th scope="col">送达数量</th><th scope="col">购买率</th></tr></thead>
              <tbody>{report.products.map((item) => <tr key={item.sku}>
                <th scope="row">{item.sku}</th><td>{formatCount(item.catalogViewers)}</td><td>{formatCount(item.deliveredBuyers)}</td>
                <td>{formatNumber(item.deliveredQuantity)}</td><td>{formatRate(item.purchaseRate)}</td>
              </tr>)}</tbody>
            </table></div>
          </section>
          <section className="analytics-panel" aria-labelledby="analytics-exchange-heading">
            <SectionHeading id="analytics-exchange-heading" title="资源兑换转化" note="已结算 ÷ 已报价；uncertain 单列" />
            <dl className="analytics-stats">
              <Stat label="报价账户" value={formatCount(report.resourceExchange.quotingAccounts)} />
              <Stat label="结算账户" value={formatCount(report.resourceExchange.settledAccounts)} />
              <Stat label="兑换转化率" value={formatRate(report.resourceExchange.conversionRate)} />
              <Stat label="结算价值" value={formatNumber(report.resourceExchange.settledValue)} />
              <Stat label="Uncertain" value={formatNumber(report.resourceExchange.uncertainRuns)} warning />
            </dl>
          </section>
        </div>

        <section className="analytics-panel" aria-labelledby="analytics-zones-heading">
          <SectionHeading id="analytics-zones-heading" title="区域热度" note="报价、成功结算与异常分开统计" />
          <div className="table-scroll"><table>
            <caption className="sr-only">资源兑换区域热度</caption>
            <thead><tr><th scope="col">区域</th><th scope="col">账户</th><th scope="col">报价</th><th scope="col">结算</th><th scope="col">Uncertain</th><th scope="col">结算价值</th></tr></thead>
            <tbody>{report.zones.map((zone) => <tr key={zone.zoneId}>
              <th scope="row">{zone.zoneId}</th><td>{formatCount(zone.accounts)}</td><td>{formatNumber(zone.quotedRuns)}</td>
              <td>{formatNumber(zone.settledRuns)}</td><td>{formatNumber(zone.uncertainRuns)}</td><td>{formatNumber(zone.settledValue)}</td>
            </tr>)}</tbody>
          </table></div>
        </section>

        <section className="analytics-panel" aria-labelledby="analytics-health-heading">
          <SectionHeading id="analytics-health-heading" title="经济健康度" note="双币产销、余额分位数与异常终态" />
          <div className="analytics-currencies">{report.currencies.map((currency) => <article key={currency.currency}>
            <h3>{currency.currency === "merchantCoin" ? "永久商币" : "本周战备券"}</h3>
            <dl className="analytics-stats compact">
              <Stat label="流入" value={formatNumber(currency.inflow)} /><Stat label="流出" value={formatNumber(currency.outflow)} />
              <Stat label="净值" value={formatNumber(currency.net)} /><Stat label="余额 P50" value={formatNumber(currency.balanceP50)} />
              <Stat label="余额 P95" value={formatNumber(currency.balanceP95)} />
            </dl>
          </article>)}</div>
          <div className="analytics-uncertain" role="note">
            <strong>Uncertain：</strong>订单 {formatNumber(report.uncertain.orders)} · 发货 {formatNumber(report.uncertain.deliveries)} · 资源结算 {formatNumber(report.uncertain.resourceSettlements)}
          </div>
        </section>

        <section className="analytics-panel" aria-labelledby="analytics-alerts-heading">
          <SectionHeading id="analytics-alerts-heading" title="告警与证据" note={`小样本阈值 ${report.privacy.minimumCohortSize} 个账户`} />
          {report.alerts.length ? <ul className="analytics-alerts">{report.alerts.map((alert) =>
            <li key={alert.code} className={`severity-${alert.severity}`}><strong>{alert.code}</strong><span>{alert.message}</span></li>)}</ul>
            : <p className="analytics-empty">当前切片没有经济告警。</p>}
          <dl className="analytics-source">
            <Stat label="来源" value={report.source.kind} /><Stat label="读取行数" value={formatNumber(report.source.rowsRead)} />
            <Stat label="重算 Hash" value={report.source.recomputationHash} />
          </dl>
        </section>

        <nav className="analytics-pagination" aria-label="分析维度分页">
          <button type="button" className="ghost-button" disabled={report.page.offset === 0}
            onClick={() => page(String(previousOffset))}>上一页</button>
          <span>偏移 {report.page.offset} · 商品 {report.page.totalProducts} · 区域 {report.page.totalZones}</span>
          <button type="button" className="ghost-button" disabled={!report.page.nextCursor}
            onClick={() => report.page.nextCursor && page(report.page.nextCursor)}>下一页</button>
        </nav>
      </> : null}
    </section>
  );
}

function Filter({ label, children }: { label: string; children: ReactNode }) {
  return <label><span>{label}</span>{children}</label>;
}

function SectionHeading({ id, title, note }: { id: string; title: string; note: string }) {
  return <header className="analytics-section-heading"><div><h2 id={id}>{title}</h2><p>{note}</p></div></header>;
}

function Stat({ label, value, warning = false }: { label: string; value: string; warning?: boolean }) {
  return <div><dt>{label}</dt><dd className={warning ? "warning" : undefined}>{value}</dd></div>;
}

export function formatCount(count: AnalyticsCount): string {
  return count.suppressed || count.value === null ? "少样本隐藏" : count.value.toLocaleString("zh-CN");
}

export function formatRate(rate: AnalyticsRate): string {
  if (rate.suppressed) return "少样本隐藏";
  if (!rate.denominatorComplete) return "分母不完整";
  return rate.basisPoints === null ? "暂无分母" : `${(rate.basisPoints / 100).toFixed(2)}%`;
}

function formatNumber(value: number | null): string {
  return value === null ? "少样本隐藏" : value.toLocaleString("zh-CN");
}

function formatTime(value: string): string {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString("zh-CN", { hour12: false });
}

function defaultFilters(): EconomyAnalyticsFilters {
  const to = new Date();
  to.setDate(to.getDate() - 1);
  const from = new Date(to);
  from.setDate(from.getDate() - 6);
  return {
    serverId: "local",
    from: dateInput(from),
    to: dateInput(to),
    dateBasis: "business",
    limit: 50
  };
}

function dateInput(value: Date): string {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}
