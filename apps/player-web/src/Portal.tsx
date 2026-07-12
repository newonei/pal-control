import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ApiClientError,
  Catalog,
  createOrder,
  Currency,
  ExtractionZoneList,
  ExtractionQuote,
  getCatalog,
  getExtractionZones,
  getLedger,
  getOrders,
  getOverview,
  getRuns,
  LedgerEntry,
  Order,
  Overview,
  PlayerSession,
  Product,
  quoteRun,
  RunList,
  settleRun
} from "./api";
import { ExtractionMap } from "./ExtractionMap";

type Tab = "shop" | "map" | "orders" | "ledger" | "runs";

type Props = {
  session: PlayerSession;
  csrfToken: string;
  onLogout: () => Promise<void>;
  onSessionExpired: () => void;
};

type PurchaseDraft = {
  product: Product;
  quantity: number;
  idempotencyKey: string;
};

const tabs: Array<{ key: Tab; label: string; shortLabel: string; icon: string }> = [
  { key: "shop", label: "战备商城", shortLabel: "商城", icon: "商" },
  { key: "map", label: "撤离地图", shortLabel: "地图", icon: "图" },
  { key: "orders", label: "我的订单", shortLabel: "订单", icon: "单" },
  { key: "ledger", label: "资金流水", shortLabel: "资金", icon: "账" },
  { key: "runs", label: "撤离记录", shortLabel: "记录", icon: "撤" }
];

export function Portal({ session, csrfToken, onLogout, onSessionExpired }: Props) {
  const [tab, setTab] = useState<Tab>("shop");
  const [overview, setOverview] = useState<Overview | null>(null);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [orders, setOrders] = useState<Order[]>([]);
  const [ledger, setLedger] = useState<LedgerEntry[]>([]);
  const [runs, setRuns] = useState<RunList | null>(null);
  const [extractionZones, setExtractionZones] = useState<ExtractionZoneList | null>(null);
  const [extractionZonesLoading, setExtractionZonesLoading] = useState(true);
  const [extractionZonesError, setExtractionZonesError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("全部");
  const [purchase, setPurchase] = useState<PurchaseDraft | null>(null);
  const [purchaseBusy, setPurchaseBusy] = useState(false);
  const [quote, setQuote] = useState<ExtractionQuote | null>(null);
  const [quoteBusy, setQuoteBusy] = useState(false);
  const [settleKey, setSettleKey] = useState<string | null>(null);

  const handleError = useCallback((reason: unknown) => {
    if (reason instanceof ApiClientError && reason.status === 401) {
      onSessionExpired();
      return;
    }
    setError(reason instanceof Error ? reason.message : "数据加载失败");
  }, [onSessionExpired]);

  const reload = useCallback(async (quiet = false) => {
    quiet ? setRefreshing(true) : setLoading(true);
    setError(null);
    try {
      const [nextOverview, nextCatalog, nextOrders, nextLedger, nextRuns] = await Promise.all([
        getOverview(), getCatalog(), getOrders(), getLedger(), getRuns()
      ]);
      setOverview(nextOverview);
      setCatalog(nextCatalog);
      setOrders(nextOrders.items);
      setLedger(nextLedger.items);
      setRuns(nextRuns);
    } catch (reason) {
      handleError(reason);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [handleError]);

  const reloadExtractionZones = useCallback(async (quiet = false) => {
    if (!quiet) setExtractionZonesLoading(true);
    setExtractionZonesError(null);
    try {
      setExtractionZones(await getExtractionZones());
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setExtractionZonesError(reason instanceof Error ? reason.message : "撤离点数据加载失败");
    } finally {
      setExtractionZonesLoading(false);
    }
  }, [onSessionExpired]);

  useEffect(() => {
    void reload();
    void reloadExtractionZones();
  }, [reload, reloadExtractionZones]);

  useEffect(() => {
    if (tab !== "map") return;
    void reloadExtractionZones(true);
    const timer = globalThis.setInterval(() => {
      if (document.visibilityState === "visible") void reloadExtractionZones(true);
    }, 5_000);
    return () => globalThis.clearInterval(timer);
  }, [reloadExtractionZones, tab]);

  const categories = useMemo(() => [
    "全部",
    ...Array.from(new Set((catalog?.items ?? []).map((item) => item.category))).sort()
  ], [catalog]);

  const products = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase("zh-CN");
    return (catalog?.items ?? []).filter((item) => {
      if (category !== "全部" && item.category !== category) return false;
      if (!needle) return true;
      return [item.name, item.description, item.category, ...item.tags]
        .some((part) => part.toLocaleLowerCase("zh-CN").includes(needle));
    });
  }, [catalog, category, query]);

  function openPurchase(product: Product) {
    setNotice(null);
    setPurchase({ product, quantity: 1, idempotencyKey: crypto.randomUUID() });
  }

  async function confirmPurchase() {
    if (!purchase) return;
    setPurchaseBusy(true);
    setError(null);
    try {
      const result = await createOrder(
        purchase.product.productId,
        purchase.quantity,
        purchase.idempotencyKey,
        csrfToken
      );
      setPurchase(null);
      setNotice(`订单已受理：${result.productName} × ${result.quantity}`);
      await reload(true);
      setTab("orders");
    } catch (reason) {
      handleError(reason);
    } finally {
      setPurchaseBusy(false);
    }
  }

  async function scanExtraction() {
    setQuoteBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await quoteRun(csrfToken);
      setQuote(result);
      setSettleKey(crypto.randomUUID());
    } catch (reason) {
      handleError(reason);
    } finally {
      setQuoteBusy(false);
    }
  }

  async function confirmSettlement() {
    if (!quote || !settleKey) return;
    setQuoteBusy(true);
    setError(null);
    try {
      const result = await settleRun(quote.runId, settleKey, csrfToken);
      setNotice(result.state === "extracted" || result.state === "settled"
        ? `撤离结算成功，已获得 ${result.rewardAmount.toLocaleString("zh-CN")} ${currencyName(result.rewardCurrency)}。`
        : result.statusMessage || "撤离结算已提交，请等待结果核验。");
      setQuote(null);
      setSettleKey(null);
      await Promise.all([reload(true), reloadExtractionZones(true)]);
    } catch (reason) {
      handleError(reason);
    } finally {
      setQuoteBusy(false);
    }
  }

  const balance = overview?.balances;
  const displayName = overview?.displayName ?? session.displayName ?? "调查员";

  return (
    <div className="portal-shell">
      <header className="topbar">
        <a className="brand" href="#main-content" aria-label="幻兽商域玩家中心">
          <span className="brand-mark" aria-hidden="true">幻</span>
          <span><strong>幻兽商域</strong><small>玩家战备中心</small></span>
        </a>
        <div className="season-chip">
          <span className="signal" aria-hidden="true" />
          {overview?.season.name ?? "正在读取赛季"}
        </div>
        <div className="account-menu">
          <span><strong>{displayName}</strong><small>{session.userId}</small></span>
          <button className="secondary compact" onClick={() => void onLogout()}>安全退出</button>
        </div>
      </header>

      <div className="portal-grid">
        <aside className="sidebar" aria-label="玩家功能">
          <div className="identity-card">
            <div className="avatar" aria-hidden="true">{displayName.slice(0, 1)}</div>
            <strong>{displayName}</strong>
            <span className={overview?.online ? "online" : "offline"}>
              {overview?.online ? "游戏中在线" : "当前离线"}
            </span>
          </div>
          <nav>
            {tabs.map((item) => (
              <button
                key={item.key}
                className={tab === item.key ? "active" : ""}
                onClick={() => setTab(item.key)}
                aria-current={tab === item.key ? "page" : undefined}
              >
                <span aria-hidden="true">{item.icon}</span>{item.label}
                {item.key === "orders" && orders.some((order) => ["accepted", "pending", "delivering"].includes(order.state)) && (
                  <i aria-label="有处理中的订单" />
                )}
              </button>
            ))}
          </nav>
          <div className="sidebar-note">
            <strong>本周行动结束</strong>
            <span>{overview ? formatDate(overview.season.endsAt) : "--"}</span>
            <small>商域币永久保留，周券随新档更新。</small>
          </div>
        </aside>

        <main id="main-content" className="content" tabIndex={-1}>
          <section className="balance-row" aria-label="我的资产">
            <div className="welcome">
              <span className="eyebrow">WELCOME BACK</span>
              <h1>{greeting()}，{displayName}</h1>
              <p>准备下一趟行动，或者结算刚带回来的战利品。</p>
            </div>
            <BalanceCard icon="币" name="永久商域币" value={balance?.merchantCoin} hint="跨周永久保留" />
            <BalanceCard icon="券" name="周战备券" value={balance?.weeklyTicket} hint="仅本周有效" tone="amber" />
          </section>

          {error && <div className="alert error" role="alert"><span>{error}</span><button onClick={() => setError(null)}>关闭</button></div>}
          {notice && <div className="alert success" role="status"><span>{notice}</span><button onClick={() => setNotice(null)}>关闭</button></div>}

          <div className="section-heading">
            <div><span className="eyebrow">{tabEyebrow(tab)}</span><h2>{tabs.find((item) => item.key === tab)?.label}</h2></div>
            <button className="secondary" disabled={refreshing} onClick={() => {
              void reload(true);
              if (tab === "map") void reloadExtractionZones(true);
            }}>
              {refreshing ? "刷新中…" : "刷新数据"}
            </button>
          </div>

          {loading ? <Loading /> : (
            <>
              {tab === "shop" && (
                <Shop
                  products={products}
                  categories={categories}
                  category={category}
                  query={query}
                  online={overview?.online ?? false}
                  onCategory={setCategory}
                  onQuery={setQuery}
                  onPurchase={openPurchase}
                />
              )}
              {tab === "orders" && <Orders orders={orders} />}
              {tab === "ledger" && <Ledger entries={ledger} />}
              {tab === "map" && (
                <ExtractionMap
                  data={extractionZones}
                  error={extractionZonesError}
                  loading={extractionZonesLoading}
                  online={overview?.online ?? false}
                />
              )}
              {tab === "runs" && (
                <Runs
                  runs={runs}
                  busy={quoteBusy}
                  online={overview?.online ?? false}
                  onQuote={() => void scanExtraction()}
                />
              )}
            </>
          )}
        </main>
      </div>

      <nav className="mobile-nav" aria-label="玩家功能">
        {tabs.map((item) => (
          <button key={item.key} className={tab === item.key ? "active" : ""} onClick={() => setTab(item.key)}>
            <span aria-hidden="true">{item.icon}</span>{item.shortLabel}
          </button>
        ))}
      </nav>

      {purchase && (
        <div className="modal-backdrop" role="presentation" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !purchaseBusy) setPurchase(null);
        }}>
          <section className="modal" role="dialog" aria-modal="true" aria-labelledby="purchase-title">
            <span className="eyebrow">PURCHASE CONFIRMATION</span>
            <h2 id="purchase-title">确认购买</h2>
            <div className="purchase-item"><strong>{purchase.product.name}</strong><span>{purchase.product.deliverySummary}</span></div>
            <label htmlFor="purchase-quantity">购买数量</label>
            <div className="stepper">
              <button aria-label="减少数量" onClick={() => setPurchase({
                ...purchase,
                quantity: Math.max(1, purchase.quantity - 1),
                idempotencyKey: crypto.randomUUID()
              })}>−</button>
              <input
                id="purchase-quantity"
                type="number"
                min={1}
                max={maxQuantity(purchase.product)}
                value={purchase.quantity}
                onChange={(event) => setPurchase({
                  ...purchase,
                  quantity: clamp(Number(event.target.value) || 1, 1, maxQuantity(purchase.product)),
                  idempotencyKey: crypto.randomUUID()
                })}
              />
              <button aria-label="增加数量" onClick={() => setPurchase({
                ...purchase,
                quantity: Math.min(maxQuantity(purchase.product), purchase.quantity + 1),
                idempotencyKey: crypto.randomUUID()
              })}>＋</button>
            </div>
            <div className="purchase-total">
              <span>合计</span>
              <strong>{currencyIcon(purchase.product.price.currency)} {(purchase.product.price.amount * purchase.quantity).toLocaleString("zh-CN")}</strong>
            </div>
            <p className="modal-note">确认后立即扣款，并向当前在线角色发货。请保持背包有足够空间。</p>
            <div className="modal-actions">
              <button className="secondary" disabled={purchaseBusy} onClick={() => setPurchase(null)}>取消</button>
              <button className="primary" disabled={purchaseBusy} onClick={() => void confirmPurchase()}>{purchaseBusy ? "提交中…" : "确认并购买"}</button>
            </div>
          </section>
        </div>
      )}

      {quote && (
        <div className="modal-backdrop" role="presentation">
          <section className="modal quote-modal" role="dialog" aria-modal="true" aria-labelledby="quote-title">
            <span className="eyebrow">EXTRACTION QUOTE</span>
            <h2 id="quote-title">确认撤离战利品</h2>
            <p>撤离点：<strong>{quote.zoneName}</strong> · 报价有效至 {formatTime(quote.expiresAt)}</p>
            <div className="quote-items">
              {quote.items.map((item) => <div key={item.itemId}><span>{item.name} × {item.quantity}</span><strong>券 {item.totalValue.toLocaleString("zh-CN")}</strong></div>)}
            </div>
            <div className="purchase-total"><span>{quote.itemCount} 件战利品</span><strong>券 {quote.totalValue.toLocaleString("zh-CN")}</strong></div>
            <p className="modal-note warning">结算会从背包扣除以上物品。结果不确定时不会重复入账，请等待管理员核对。</p>
            <div className="modal-actions">
              <button className="secondary" disabled={quoteBusy} onClick={() => setQuote(null)}>暂不结算</button>
              <button className="primary" disabled={quoteBusy} onClick={() => void confirmSettlement()}>{quoteBusy ? "结算中…" : "确认扣除并结算"}</button>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

function BalanceCard({ icon, name, value, hint, tone = "cyan" }: { icon: string; name: string; value?: number; hint: string; tone?: string }) {
  return <article className={`balance-card ${tone}`}><span className="coin-icon">{icon}</span><div><small>{name}</small><strong>{value?.toLocaleString("zh-CN") ?? "--"}</strong><span>{hint}</span></div></article>;
}

function Shop({ products, categories, category, query, online, onCategory, onQuery, onPurchase }: {
  products: Product[]; categories: string[]; category: string; query: string; online: boolean;
  onCategory: (value: string) => void; onQuery: (value: string) => void; onPurchase: (product: Product) => void;
}) {
  return <>
    <div className="shop-tools">
      <label className="search"><span aria-hidden="true">⌕</span><span className="sr-only">搜索商品</span><input value={query} onChange={(event) => onQuery(event.target.value)} placeholder="搜索商品、分类或标签" /></label>
      <div className="categories" aria-label="商品分类">{categories.map((item) => <button key={item} className={category === item ? "active" : ""} onClick={() => onCategory(item)}>{item}</button>)}</div>
    </div>
    {!online && <div className="inline-tip">需要角色在线才能购买和接收物品；你仍可浏览商城。</div>}
    <div className="product-grid">
      {products.map((product) => <article className={product.featured ? "product featured" : "product"} key={product.productId}>
        {product.featured && <span className="featured-label">推荐战备</span>}
        <div className="product-art" aria-hidden="true">{product.category.slice(0, 1) || "物"}</div>
        <div className="product-copy"><div className="product-meta"><span>{product.category}</span>{product.stockRemaining !== null && <span>库存 {product.stockRemaining}</span>}</div><h3>{product.name}</h3><p>{product.description}</p><small>{product.deliverySummary}</small></div>
        <div className="product-buy"><strong>{currencyIcon(product.price.currency)} {product.price.amount.toLocaleString("zh-CN")}</strong><button className="primary" disabled={!online || !product.enabled || maxQuantity(product) < 1} onClick={() => onPurchase(product)}>{product.enabled ? "购买" : "暂不可用"}</button></div>
      </article>)}
    </div>
    {products.length === 0 && <Empty title="没有匹配的商品" detail="换一个关键词或分类试试。" />}
  </>;
}

function Orders({ orders }: { orders: Order[] }) {
  if (!orders.length) return <Empty title="还没有商城订单" detail="购买的战备会在这里显示发货进度。" />;
  return <div className="data-list">{orders.map((order) => <article key={order.orderId}><div className={`state ${order.state}`}>{orderState(order.state)}</div><div className="data-main"><strong>{order.productName} × {order.quantity}</strong><span>{order.statusMessage || `订单号 ${order.orderId.slice(0, 8)}`}</span></div><div className="data-value"><strong>− {currencyIcon(order.currency)} {order.totalAmount.toLocaleString("zh-CN")}</strong><time>{formatDateTime(order.createdAt)}</time></div></article>)}</div>;
}

function Ledger({ entries }: { entries: LedgerEntry[] }) {
  if (!entries.length) return <Empty title="暂无资金流水" detail="购买、撤离和活动奖励都会形成可追溯记录。" />;
  return <div className="data-list ledger-list">{entries.map((entry) => <article key={entry.entryId}><div className={entry.amount >= 0 ? "amount-icon income" : "amount-icon expense"}>{entry.amount >= 0 ? "+" : "−"}</div><div className="data-main"><strong>{entry.reason}</strong><span>{entry.referenceId ? `关联 ${entry.referenceId.slice(0, 12)}` : "系统账务"}</span></div><div className="data-value"><strong className={entry.amount >= 0 ? "positive" : "negative"}>{entry.amount >= 0 ? "+" : ""}{entry.amount.toLocaleString("zh-CN")} {currencyName(entry.currency)}</strong><time>余额 {entry.balanceAfter.toLocaleString("zh-CN")} · {formatDateTime(entry.createdAt)}</time></div></article>)}</div>;
}

function Runs({ runs, busy, online, onQuote }: { runs: RunList | null; busy: boolean; online: boolean; onQuote: () => void }) {
  return <>
    <section className="extraction-action"><div><span className="eyebrow">LIVE SETTLEMENT</span><h3>已到达撤离点？</h3><p>扫描当前掉落栏中的可结算物品，确认报价后再扣除并入账。</p></div><button className="primary large" disabled={busy || !online || !runs?.settlementEnabled} onClick={onQuote}>{busy ? "扫描中…" : "扫描撤离物品"}</button></section>
    {!runs?.settlementEnabled && <div className="inline-tip warning">{runs?.reason || "撤离结算当前不可用"}</div>}
    {!runs?.items.length ? <Empty title="本周还没有撤离记录" detail="到达配置的撤离区域后，就能扫描并结算战利品。" /> : <div className="data-list">{runs.items.map((run) => <article key={run.runId}><div className={`state ${run.state}`}>{runState(run.state)}</div><div className="data-main"><strong>{run.extractedItemCount} 件战利品</strong><span>{run.statusMessage || `行动 ${run.runId.slice(0, 8)}`}</span></div><div className="data-value"><strong>{currencyIcon(run.rewardCurrency)} {run.rewardAmount.toLocaleString("zh-CN")}</strong><time>{formatDateTime(run.startedAt)}</time></div></article>)}</div>}
  </>;
}

function Loading() { return <div className="loading" role="status"><span /><p>正在同步你的商域数据…</p></div>; }
function Empty({ title, detail }: { title: string; detail: string }) { return <div className="empty"><span aria-hidden="true">◇</span><strong>{title}</strong><p>{detail}</p></div>; }
function maxQuantity(product: Product) { const stock = product.stockRemaining ?? 99; const limit = product.purchaseLimit === null ? 99 : Math.max(0, product.purchaseLimit - product.purchased); return Math.min(99, stock, limit); }
function clamp(value: number, min: number, max: number) { return Math.min(max, Math.max(min, value)); }
function currencyIcon(currency: Currency) { return currency === "merchantCoin" ? "币" : "券"; }
function currencyName(currency: Currency) { return currency === "merchantCoin" ? "商域币" : "周券"; }
function orderState(state: Order["state"]) { return ({ accepted: "已受理", pending: "待发货", delivering: "发货中", succeeded: "已送达", failed: "失败", uncertain: "待核对", cancelled: "已取消", refunded: "已退款" } as const)[state]; }
function runState(state: string) { return ({ preparing: "准备中", deployed: "行动中", extracted: "待结算", settled: "已结算", failed: "失败", uncertain: "待核对", cancelled: "已取消" } as Record<string, string>)[state] ?? state; }
function tabEyebrow(tab: Tab) { return ({ shop: "SUPPLY MARKET", map: "EXTRACTION ROUTES", orders: "DELIVERY TRACKING", ledger: "WALLET HISTORY", runs: "EXTRACTION LOG" })[tab]; }
function formatDate(value: string) { return new Intl.DateTimeFormat("zh-CN", { month: "long", day: "numeric", weekday: "short", hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
function formatDateTime(value: string) { return new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
function formatTime(value: string) { return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date(value)); }
function greeting() { const hour = new Date().getHours(); return hour < 6 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好"; }
