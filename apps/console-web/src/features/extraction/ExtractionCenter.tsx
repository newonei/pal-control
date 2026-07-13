import { useEffect, useMemo, useRef, useState } from "react";
import {
  createExtractionQuote,
  createShopOrder,
  getExtractionOverview,
  getExtractionRuns,
  getShopCatalog,
  getShopOrders,
  getWalletLedger,
  settleExtractionRun,
  type ExtractionCurrency,
  type ExtractionOverview,
  type ExtractionQuote,
  type ExtractionRun,
  type ShopCatalog,
  type ShopOrder,
  type ShopProduct,
  type WalletLedgerEntry
} from "./api";
import "./extraction.css";

type ExtractionCenterProps = {
  userId?: string;
  onSelectPlayer?: () => void;
};

const activeOrderStates = new Set<ShopOrder["state"]>(["accepted", "pending", "delivering"]);
const numberFormatter = new Intl.NumberFormat("zh-CN");
const dateFormatter = new Intl.DateTimeFormat("zh-CN", {
  month: "2-digit",
  day: "2-digit",
  hour: "2-digit",
  minute: "2-digit",
  hour12: false
});

export function ExtractionCenter({ userId = "", onSelectPlayer }: ExtractionCenterProps) {
  const [overview, setOverview] = useState<ExtractionOverview>();
  const [catalog, setCatalog] = useState<ShopCatalog>();
  const [orders, setOrders] = useState<ShopOrder[]>([]);
  const [ledger, setLedger] = useState<WalletLedgerEntry[]>([]);
  const [runs, setRuns] = useState<ExtractionRun[]>([]);
  const [settlementEnabled, setSettlementEnabled] = useState(false);
  const [settlementReason, setSettlementReason] = useState<string>();
  const [extractionQuote, setExtractionQuote] = useState<ExtractionQuote>();
  const [scanning, setScanning] = useState(false);
  const [settling, setSettling] = useState(false);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("all");
  const [selectedProduct, setSelectedProduct] = useState<ShopProduct>();
  const [quantity, setQuantity] = useState(1);
  const purchaseIdempotencyKey = useRef("");
  const [loading, setLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [clock, setClock] = useState(() => Date.now());
  const mountedUserId = useRef(userId);

  async function refresh(signal?: AbortSignal) {
    if (!userId) return;
    setLoading(true);
    setError(undefined);
    try {
      const [nextOverview, nextCatalog, nextOrders, nextLedger, nextRuns] = await Promise.all([
        getExtractionOverview(userId, signal),
        getShopCatalog(userId, signal),
        getShopOrders(userId, signal),
        getWalletLedger(userId, signal),
        getExtractionRuns(userId, signal)
      ]);
      if (signal?.aborted || mountedUserId.current !== userId) return;
      setOverview(nextOverview);
      setCatalog(nextCatalog);
      setOrders(nextOrders.items);
      setLedger(nextLedger.items);
      setRuns(nextRuns.items);
      setSettlementEnabled(nextRuns.settlementEnabled);
      setSettlementReason(nextRuns.reason ?? undefined);
    } catch (nextError) {
      if (!signal?.aborted) setError(errorMessage(nextError, "商城数据读取失败"));
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }

  useEffect(() => {
    mountedUserId.current = userId;
    setOverview(undefined);
    setOrders([]);
    setLedger([]);
    setRuns([]);
    setSettlementEnabled(false);
    setSettlementReason(undefined);
    setExtractionQuote(undefined);
    purchaseIdempotencyKey.current = "";
    setNotice(undefined);
    setError(undefined);
    if (!userId) return;
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [userId]);

  useEffect(() => {
    const timer = globalThis.setInterval(() => setClock(Date.now()), 1_000);
    return () => globalThis.clearInterval(timer);
  }, []);

  const hasActiveOrders = orders.some((order) => activeOrderStates.has(order.state));
  useEffect(() => {
    if (!userId || !hasActiveOrders) return;
    const timer = globalThis.setInterval(() => {
      void Promise.all([
        getExtractionOverview(userId),
        getShopOrders(userId),
        getWalletLedger(userId)
      ]).then(([nextOverview, nextOrders, nextLedger]) => {
        if (mountedUserId.current !== userId) return;
        setOverview(nextOverview);
        setOrders(nextOrders.items);
        setLedger(nextLedger.items);
      }).catch((nextError: unknown) => setError(errorMessage(nextError, "订单状态同步失败")));
    }, 4_000);
    return () => globalThis.clearInterval(timer);
  }, [hasActiveOrders, userId]);

  const products = catalog?.items ?? [];
  const categories = useMemo(
    () => Array.from(new Set(products.map((product) => product.category))).sort((left, right) => left.localeCompare(right, "zh-CN")),
    [products]
  );
  const filteredProducts = useMemo(() => {
    const needle = normalize(query);
    return products.filter((product) => {
      if (category !== "all" && product.category !== category) return false;
      if (!needle) return true;
      return normalize([product.name, product.description, product.category, product.deliverySummary, ...product.tags].join(" ")).includes(needle);
    });
  }, [category, products, query]);
  const pendingOrders = orders.filter((order) => activeOrderStates.has(order.state));
  const visibleOrders = orders.slice(0, 6);
  const visibleLedger = ledger.slice(0, 8);
  const visibleRuns = runs.slice(0, 8);
  const selectedBalance = selectedProduct && overview
    ? overview.balances[selectedProduct.price.currency]
    : 0;
  const orderTotal = selectedProduct ? selectedProduct.price.amount * quantity : 0;
  const maxQuantity = selectedProduct ? productMaxQuantity(selectedProduct) : 1;

  function openPurchase(product: ShopProduct) {
    setSelectedProduct(product);
    setQuantity(1);
    purchaseIdempotencyKey.current = uniqueKey();
    setError(undefined);
    setNotice(undefined);
  }

  function closePurchase() {
    setSelectedProduct(undefined);
    purchaseIdempotencyKey.current = "";
  }

  function changePurchaseQuantity(nextQuantity: number) {
    if (nextQuantity === quantity) return;
    setQuantity(nextQuantity);
    purchaseIdempotencyKey.current = uniqueKey();
  }

  async function confirmPurchase() {
    if (!selectedProduct || !userId || submitting) return;
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > maxQuantity) {
      setError(`购买数量必须在 1 至 ${maxQuantity} 之间。`);
      return;
    }
    if (orderTotal > selectedBalance) {
      setError(`${currencyName(selectedProduct.price.currency)}余额不足。`);
      return;
    }
    setSubmitting(true);
    setError(undefined);
    try {
      if (!purchaseIdempotencyKey.current) {
        purchaseIdempotencyKey.current = uniqueKey();
      }
      const order = await createShopOrder({
        userId,
        productId: selectedProduct.productId,
        quantity,
        idempotencyKey: purchaseIdempotencyKey.current
      });
      closePurchase();
      setNotice(`订单 ${shortId(order.orderId)} 已提交，正在等待游戏内发货。`);
      const [nextOverview, nextOrders, nextLedger] = await Promise.all([
        getExtractionOverview(userId),
        getShopOrders(userId),
        getWalletLedger(userId)
      ]);
      setOverview(nextOverview);
      setOrders(nextOrders.items);
      setLedger(nextLedger.items);
    } catch (nextError) {
      setError(errorMessage(nextError, "购买失败"));
    } finally {
      setSubmitting(false);
    }
  }

  async function scanExtractionLoot() {
    if (!userId || scanning || !settlementEnabled) return;
    setScanning(true);
    setError(undefined);
    setNotice(undefined);
    try {
      setExtractionQuote(await createExtractionQuote(userId));
    } catch (nextError) {
      setError(errorMessage(nextError, "扫描可售资源失败"));
    } finally {
      setScanning(false);
    }
  }

  async function confirmExtraction() {
    if (!userId || !extractionQuote || settling) return;
    setSettling(true);
    setError(undefined);
    try {
      const run = await settleExtractionRun({
        runId: extractionQuote.runId,
        userId,
        idempotencyKey: uniqueKey("extract")
      });
      setExtractionQuote(undefined);
      setNotice(run.state === "extracted"
        ? `资源兑换成功，已获得 ${formatNumber(run.rewardAmount)} 战备券。`
        : run.statusMessage || "资源兑换进入待核验状态，请勿重复提交。");
      const [nextOverview, nextLedger, nextRuns] = await Promise.all([
        getExtractionOverview(userId),
        getWalletLedger(userId),
        getExtractionRuns(userId)
      ]);
      setOverview(nextOverview);
      setLedger(nextLedger.items);
      setRuns(nextRuns.items);
      setSettlementEnabled(nextRuns.settlementEnabled);
      setSettlementReason(nextRuns.reason ?? undefined);
    } catch (nextError) {
      setError(errorMessage(nextError, "资源兑换结算失败"));
    } finally {
      setSettling(false);
    }
  }

  if (!userId) {
    return (
      <section className="extraction-center extraction-empty-account">
        <span className="extraction-empty-icon">域</span>
        <p className="extraction-eyebrow">WORLD RESOURCE ECONOMY</p>
        <h2>请先选择玩家账户</h2>
        <p>商城余额和订单使用平台 UserId 跨周存档保存，不能使用随世界重置的 PlayerUID。</p>
        {onSelectPlayer ? <button className="extraction-primary" onClick={onSelectPlayer} type="button">选择在线玩家</button> : null}
      </section>
    );
  }

  return (
    <section className="extraction-center">
      <header className="extraction-hero">
        <div>
          <p className="extraction-eyebrow">幻兽商域 · 周世界资源经济</p>
          <h2>{overview?.season.name ?? "赛季战备中心"}</h2>
          <p>永久货币跨档保存；战备券、角色成长和游戏内物资随周档重置。</p>
        </div>
        <div className="extraction-countdown" aria-live="polite">
          <span>{seasonStateName(overview?.season.state)}</span>
          <strong>{formatCountdown(overview?.season.endsAt, clock)}</strong>
          <small>{overview?.season.endsAt ? `${formatDate(overview.season.endsAt)} 结档` : "等待赛季排期"}</small>
        </div>
        <button className="extraction-refresh" disabled={loading} onClick={() => void refresh()} type="button">
          {loading ? "同步中…" : "刷新"}
        </button>
      </header>

      {error ? <div className="extraction-feedback error" role="alert"><span>{error}</span><button onClick={() => setError(undefined)} type="button">关闭</button></div> : null}
      {notice ? <div className="extraction-feedback success" role="status"><span>{notice}</span><button onClick={() => setNotice(undefined)} type="button">关闭</button></div> : null}

      <div className="extraction-wallet-grid">
        <WalletCard
          accent="gold"
          label="永久商域币"
          value={overview?.balances.merchantCoin}
          note="运营、活动与赛季奖励获得 · 跨周保留"
          glyph="商"
        />
        <WalletCard
          accent="cyan"
          label="本周战备券"
          value={overview?.balances.weeklyTicket}
          note="资源兑换获得 · 周档采购 · 结档清零"
          glyph="券"
        />
        <article className="extraction-stat-card">
          <span>本周已结算兑换</span>
          <strong>{overview ? overview.seasonStats.settledExchanges : "--"}</strong>
          <small>
            失败结算 {overview?.seasonStats.failedSettlements ?? "--"} · 待核验 {overview?.seasonStats.uncertainSettlements ?? "--"} ·
            已兑换价值 {formatNumber(overview?.seasonStats.exchangedValue)}
          </small>
        </article>
        <article className="extraction-stat-card">
          <span>待发货订单</span>
          <strong className={pendingOrders.length ? "warning" : ""}>{pendingOrders.length}</strong>
          <small>{pendingOrders.length ? "发货完成前请勿重复购买" : "当前订单队列正常"}</small>
        </article>
      </div>

      <section className="extraction-settlement-panel">
        <div>
          <p className="extraction-eyebrow">RESOURCE SETTLEMENT</p>
          <h3>白名单资源兑换</h3>
          <p>进入资源回收区后扫描 Items、Food 与 DropSlot。系统先用 RCON 扣除白名单资源，REST 回读证明后才发放战备券。</p>
          {!settlementEnabled && settlementReason ? <small>{settlementReason}</small> : null}
        </div>
        <span className={settlementEnabled ? "ready" : "locked"}>{settlementEnabled ? "结算链路已就绪" : "结算链路未启用"}</span>
        <button className="extraction-primary" disabled={!settlementEnabled || scanning} onClick={() => void scanExtractionLoot()} type="button">
          {scanning ? "位置与背包核验中…" : "扫描可售资源"}
        </button>
      </section>

      <div className="extraction-main-layout">
        <section className="extraction-market">
          <header className="extraction-section-heading">
            <div><p className="extraction-eyebrow">ONLINE MARKET</p><h3>在线战备商城</h3></div>
            <strong>{filteredProducts.length} 件商品</strong>
          </header>
          <div className="extraction-market-tools">
            <label className="extraction-search">
              <span>搜索商品</span>
              <input onChange={(event) => setQuery(event.target.value)} placeholder="名称、物品、标签…" value={query} />
            </label>
            <div className="extraction-categories" aria-label="商品分类">
              <button className={category === "all" ? "active" : ""} onClick={() => setCategory("all")} type="button">全部</button>
              {categories.map((item) => (
                <button className={category === item ? "active" : ""} key={item} onClick={() => setCategory(item)} type="button">
                  {categoryName(item)}
                </button>
              ))}
            </div>
          </div>

          <div className="extraction-product-grid">
            {filteredProducts.map((product) => {
              const unavailableReason = productUnavailableReason(product, overview);
              return (
                <article className={product.featured ? "extraction-product featured" : "extraction-product"} key={product.productId}>
                  <div className="extraction-product-mark"><span>{product.name.slice(0, 1)}</span>{product.featured ? <em>推荐</em> : null}</div>
                  <div className="extraction-product-copy">
                    <div><span>{categoryName(product.category)}</span><code>{product.productId}</code></div>
                    <h4>{product.name}</h4>
                    <p>{product.description}</p>
                    <small>{product.deliverySummary}</small>
                  </div>
                  <div className="extraction-product-tags">
                    {product.tags.slice(0, 3).map((tag) => <span key={tag}>{tag}</span>)}
                    {product.purchaseLimit !== null ? <span>限购 {product.purchased}/{product.purchaseLimit}</span> : null}
                  </div>
                  <footer>
                    <div><strong>{formatNumber(product.price.amount)}</strong><span>{currencyName(product.price.currency)}</span></div>
                    <button disabled={Boolean(unavailableReason)} onClick={() => openPurchase(product)} title={unavailableReason} type="button">
                      {unavailableReason || "购买"}
                    </button>
                  </footer>
                </article>
              );
            })}
            {!loading && filteredProducts.length === 0 ? (
              <div className="extraction-no-results"><strong>没有匹配的商品</strong><span>尝试清除搜索词或切换分类。</span></div>
            ) : null}
          </div>
        </section>

        <aside className="extraction-orders">
          <header className="extraction-section-heading"><div><p className="extraction-eyebrow">DELIVERY QUEUE</p><h3>订单与发货</h3></div></header>
          <div className="extraction-order-list">
            {visibleOrders.map((order) => <OrderRow key={order.orderId} order={order} />)}
            {!loading && visibleOrders.length === 0 ? <div className="extraction-mini-empty">还没有商城订单</div> : null}
          </div>
          <div className="extraction-delivery-note">
            <strong>可靠发货规则</strong>
            <p>订单已受理或发货中时不要重复提交。状态为“不确定”时，应先检查游戏背包和账本。</p>
          </div>
        </aside>
      </div>

      <div className="extraction-history-grid">
        <HistoryCard title="最近货币账本" eyebrow="WALLET LEDGER">
          {visibleLedger.map((entry) => <LedgerRow entry={entry} key={entry.entryId} />)}
          {!loading && visibleLedger.length === 0 ? <div className="extraction-mini-empty">暂无货币变动</div> : null}
        </HistoryCard>
        <HistoryCard title="最近资源兑换记录" eyebrow="SETTLEMENT LOG">
          {visibleRuns.map((run) => <RunRow key={run.runId} run={run} />)}
          {!loading && visibleRuns.length === 0 ? <div className="extraction-mini-empty">暂无资源兑换记录</div> : null}
        </HistoryCard>
      </div>

      {selectedProduct ? (
        <div className="extraction-dialog-backdrop" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !submitting) closePurchase();
        }}>
          <section aria-labelledby="extraction-purchase-title" aria-modal="true" className="extraction-dialog" role="dialog">
            <header>
              <div><p className="extraction-eyebrow">PURCHASE CONFIRMATION</p><h3 id="extraction-purchase-title">确认采购</h3></div>
              <button aria-label="关闭" disabled={submitting} onClick={closePurchase} type="button">×</button>
            </header>
            <div className="extraction-dialog-product">
              <span>{selectedProduct.name.slice(0, 1)}</span>
              <div><strong>{selectedProduct.name}</strong><small>{selectedProduct.deliverySummary}</small></div>
            </div>
            <label className="extraction-quantity">
              <span>购买数量</span>
              <div>
                <button disabled={quantity <= 1 || submitting} onClick={() => changePurchaseQuantity(Math.max(1, quantity - 1))} type="button">−</button>
                <input max={maxQuantity} min={1} onChange={(event) => changePurchaseQuantity(clampInteger(event.target.value, 1, maxQuantity))} type="number" value={quantity} />
                <button disabled={quantity >= maxQuantity || submitting} onClick={() => changePurchaseQuantity(Math.min(maxQuantity, quantity + 1))} type="button">＋</button>
              </div>
            </label>
            <dl className="extraction-order-summary">
              <div><dt>单价</dt><dd>{formatNumber(selectedProduct.price.amount)} {currencyName(selectedProduct.price.currency)}</dd></div>
              <div><dt>当前余额</dt><dd>{formatNumber(selectedBalance)} {currencyName(selectedProduct.price.currency)}</dd></div>
              <div className="total"><dt>应付合计</dt><dd>{formatNumber(orderTotal)} {currencyName(selectedProduct.price.currency)}</dd></div>
            </dl>
            <p className="extraction-dialog-warning">确认后将先创建扣款账本，再由服务端投递物品。发货完成前请勿重复提交。</p>
            <footer>
              <button disabled={submitting} onClick={closePurchase} type="button">取消</button>
              <button className="extraction-primary" disabled={submitting || orderTotal > selectedBalance} onClick={() => void confirmPurchase()} type="button">
                {submitting ? "提交中…" : "确认支付并发货"}
              </button>
            </footer>
          </section>
        </div>
      ) : null}

      {extractionQuote ? (
        <div className="extraction-dialog-backdrop" onMouseDown={(event) => {
          if (event.target === event.currentTarget && !settling) setExtractionQuote(undefined);
        }}>
          <section aria-labelledby="extraction-settle-title" aria-modal="true" className="extraction-dialog extraction-settle-dialog" role="dialog">
            <header>
              <div><p className="extraction-eyebrow">RESOURCE QUOTE</p><h3 id="extraction-settle-title">确认出售资源</h3></div>
              <button aria-label="关闭" disabled={settling} onClick={() => setExtractionQuote(undefined)} type="button">×</button>
            </header>
            <p className="extraction-zone-label">资源回收点：<strong>{extractionQuote.zoneName}</strong> · 报价剩余 {formatShortCountdown(extractionQuote.expiresAt, clock)}</p>
            <div className="extraction-quote-lines">
              {extractionQuote.items.map((item) => (
                <article key={item.itemId}>
                  <div><strong>{item.name}</strong><code>{item.itemId}</code></div>
                  <span>{item.quantity} × {formatNumber(item.unitValue)}</span>
                  <em>{formatNumber(item.totalValue)}</em>
                </article>
              ))}
            </div>
            <dl className="extraction-order-summary">
              <div><dt>可售资源数量</dt><dd>{formatNumber(extractionQuote.itemCount)} 件</dd></div>
              <div className="total"><dt>预计获得</dt><dd>{formatNumber(extractionQuote.totalValue)} 战备券</dd></div>
            </dl>
            <p className="extraction-dialog-warning">确认后会永久移除上列游戏物品。扣物结果未经 REST 回读证明时不会入账，也不会自动重试。</p>
            <footer>
              <button disabled={settling} onClick={() => setExtractionQuote(undefined)} type="button">取消</button>
              <button className="extraction-primary" disabled={settling || new Date(extractionQuote.expiresAt).getTime() <= clock} onClick={() => void confirmExtraction()} type="button">
                {settling ? "扣物与回读中…" : "确认扣物并结算"}
              </button>
            </footer>
          </section>
        </div>
      ) : null}
    </section>
  );
}

function WalletCard({ label, value, note, glyph, accent }: { label: string; value?: number; note: string; glyph: string; accent: string }) {
  return <article className={`extraction-wallet-card ${accent}`}><span>{glyph}</span><div><small>{label}</small><strong>{formatNumber(value)}</strong><p>{note}</p></div></article>;
}

function OrderRow({ order }: { order: ShopOrder }) {
  return <article className="extraction-order-row"><span className={`extraction-order-state ${order.state}`}>{orderStateName(order.state)}</span><div><strong>{order.productName} × {order.quantity}</strong><small>{formatDate(order.createdAt)} · {shortId(order.orderId)}</small><p>{order.statusMessage || orderStateHelp(order.state)}</p></div><em>{formatNumber(order.totalAmount)} {currencyName(order.currency)}</em></article>;
}

function LedgerRow({ entry }: { entry: WalletLedgerEntry }) {
  return <article className="extraction-history-row"><span className={entry.amount >= 0 ? "positive" : "negative"}>{entry.amount >= 0 ? "+" : "−"}</span><div><strong>{entry.reason}</strong><small>{formatDate(entry.createdAt)} · 余额 {formatNumber(entry.balanceAfter)} {currencyName(entry.currency)}</small></div><em className={entry.amount >= 0 ? "positive" : "negative"}>{entry.amount >= 0 ? "+" : ""}{formatNumber(entry.amount)}</em></article>;
}

function RunRow({ run }: { run: ExtractionRun }) {
  return <article className="extraction-history-row"><span className={run.state}>{run.state === "extracted" ? "成" : run.state === "failed" ? "败" : run.state === "uncertain" ? "核" : "结"}</span><div><strong>{runStateName(run.state)} · 资源 {run.extractedItemCount} 件</strong><small>{formatDate(run.endedAt ?? run.startedAt)} · 兑换价值 {formatNumber(run.extractedValue)}</small>{run.statusMessage ? <p>{run.statusMessage}</p> : null}</div><em className={run.rewardAmount > 0 ? "positive" : ""}>+{formatNumber(run.rewardAmount)} {currencyName(run.rewardCurrency)}</em></article>;
}

function HistoryCard({ title, eyebrow, children }: { title: string; eyebrow: string; children: React.ReactNode }) {
  return <section className="extraction-history-card"><header className="extraction-section-heading"><div><p className="extraction-eyebrow">{eyebrow}</p><h3>{title}</h3></div></header><div>{children}</div></section>;
}

function productMaxQuantity(product: ShopProduct) {
  const remainingLimit = product.purchaseLimit === null ? 99 : Math.max(0, product.purchaseLimit - product.purchased);
  const remainingStock = product.stockRemaining === null ? 99 : Math.max(0, product.stockRemaining);
  return Math.max(1, Math.min(99, remainingLimit, remainingStock));
}

function productUnavailableReason(product: ShopProduct, overview?: ExtractionOverview) {
  if (!product.enabled) return "已下架";
  if (product.stockRemaining !== null && product.stockRemaining <= 0) return "已售罄";
  if (product.purchaseLimit !== null && product.purchased >= product.purchaseLimit) return "已达限购";
  if (overview?.season.state !== "active") return "非销售期";
  if (overview.balances[product.price.currency] < product.price.amount) return "余额不足";
  return "";
}

function currencyName(currency: ExtractionCurrency) {
  return currency === "merchantCoin" ? "商域币" : "战备券";
}

function categoryName(category: string) {
  const names: Record<string, string> = {
    starter: "起步战备",
    weapon: "武器",
    armor: "护甲",
    ammo: "弹药",
    medical: "医疗",
    sphere: "帕鲁球",
    food: "补给",
    utility: "工具",
    insurance: "保险",
    bundle: "组合包"
  };
  return names[category.toLocaleLowerCase()] ?? category;
}

function seasonStateName(state?: ExtractionOverview["season"]["state"]) {
  if (state === "active") return "本周商域运营中";
  if (state === "settling") return "赛季结算中";
  if (state === "scheduled") return "赛季未开始";
  if (state === "closed") return "赛季已结束";
  return "等待赛季状态";
}

function orderStateName(state: ShopOrder["state"]) {
  const names: Record<ShopOrder["state"], string> = {
    accepted: "已受理",
    pending: "待发货",
    delivering: "发货中",
    succeeded: "已到账",
    failed: "失败",
    uncertain: "待核验",
    cancelled: "已取消"
  };
  return names[state];
}

function orderStateHelp(state: ShopOrder["state"]) {
  if (state === "succeeded") return "游戏内物品已回读确认";
  if (state === "uncertain") return "请核对背包，不要重复购买";
  if (state === "failed") return "未发货，等待系统退款或人工复核";
  if (state === "cancelled") return "订单已取消";
  return "正在排队投递至当前周档角色";
}

function runStateName(state: ExtractionRun["state"]) {
  const names: Record<ExtractionRun["state"], string> = {
    preparing: "报价中",
    deployed: "结算处理中",
    extracted: "已结算",
    failed: "结算失败",
    uncertain: "待核验（请勿重试）",
    cancelled: "报价已取消"
  };
  return names[state];
}

function formatCountdown(value: string | undefined, now: number) {
  if (!value) return "--天 --:--:--";
  const remaining = Math.max(0, new Date(value).getTime() - now);
  const totalSeconds = Math.floor(remaining / 1_000);
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor(totalSeconds % 86_400 / 3_600);
  const minutes = Math.floor(totalSeconds % 3_600 / 60);
  const seconds = totalSeconds % 60;
  return `${days}天 ${pad(hours)}:${pad(minutes)}:${pad(seconds)}`;
}

function formatNumber(value: number | undefined) {
  return value === undefined ? "--" : numberFormatter.format(value);
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? "时间未知" : dateFormatter.format(date);
}

function normalize(value: string) {
  return value.trim().toLocaleLowerCase().replace(/[\s_-]+/g, "");
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

function shortId(value: string) {
  return value.length > 12 ? `${value.slice(0, 8)}…` : value;
}

function clampInteger(value: string, minimum: number, maximum: number) {
  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? Math.max(minimum, Math.min(maximum, parsed)) : minimum;
}

function uniqueKey(prefix = "shop") {
  return globalThis.crypto?.randomUUID?.() ?? `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function formatShortCountdown(value: string, now: number) {
  const seconds = Math.max(0, Math.ceil((new Date(value).getTime() - now) / 1_000));
  return `${seconds} 秒`;
}

function errorMessage(error: unknown, fallback: string) {
  return error instanceof Error ? error.message : fallback;
}
