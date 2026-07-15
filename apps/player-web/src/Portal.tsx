import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  ApiClientError,
  Catalog,
  claimNewPlayerActivity,
  createOrder,
  Currency,
  ExtractionZoneList,
  ExtractionQuote,
  getCatalog,
  getExtractionZones,
  getLedger,
  getNewPlayerActivities,
  getReliableTasks,
  getOrders,
  getOverview,
  getRuns,
  LedgerEntry,
  NewPlayerActivity,
  NewPlayerActivityAvailability,
  Order,
  Overview,
  PlayerSession,
  Product,
  quoteRun,
  ReliableTaskSnapshot,
  RunList,
  settleRun
} from "./api";
import { ExtractionMap } from "./ExtractionMap";
import {
  describePlayerError,
  ECONOMY_POLL_INTERVAL_MS,
  formatQuoteCountdown,
  hasPendingEconomyActivity,
  orderStateGuidance,
  PlayerErrorPresentation,
  quoteSecondsRemaining,
  runStateGuidance
} from "./liveStatus";
import { resourceExchangeStateLabel } from "./runState";

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

type OnboardingStep = {
  key: "login" | "supply" | "zone" | "exchange" | "ledger";
  title: string;
  detail: string;
  complete: boolean;
  destination: Tab;
  action: string;
};

const tabs: Array<{ key: Tab; label: string; shortLabel: string; icon: string }> = [
  { key: "shop", label: "战备商城", shortLabel: "商城", icon: "商" },
  { key: "map", label: "兑换点地图", shortLabel: "地图", icon: "图" },
  { key: "orders", label: "我的订单", shortLabel: "订单", icon: "单" },
  { key: "ledger", label: "资金流水", shortLabel: "资金", icon: "账" },
  { key: "runs", label: "兑换记录", shortLabel: "记录", icon: "换" }
];

export function Portal({ session, csrfToken, onLogout, onSessionExpired }: Props) {
  const [tab, setTab] = useState<Tab>("shop");
  const [overview, setOverview] = useState<Overview | null>(null);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [orders, setOrders] = useState<Order[]>([]);
  const [ledger, setLedger] = useState<LedgerEntry[]>([]);
  const [runs, setRuns] = useState<RunList | null>(null);
  const [newPlayerActivities, setNewPlayerActivities] = useState<NewPlayerActivityAvailability[]>([]);
  const [newPlayerActivitiesLoading, setNewPlayerActivitiesLoading] = useState(true);
  const [newPlayerActivitiesError, setNewPlayerActivitiesError] = useState<string | null>(null);
  const [reliableTasks, setReliableTasks] = useState<ReliableTaskSnapshot | null>(null);
  const [reliableTasksLoading, setReliableTasksLoading] = useState(true);
  const [reliableTasksError, setReliableTasksError] = useState<string | null>(null);
  const [claimingActivity, setClaimingActivity] = useState<string | null>(null);
  const [extractionZones, setExtractionZones] = useState<ExtractionZoneList | null>(null);
  const [extractionZonesLoading, setExtractionZonesLoading] = useState(true);
  const [extractionZonesError, setExtractionZonesError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<PlayerErrorPresentation | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("全部");
  const [purchase, setPurchase] = useState<PurchaseDraft | null>(null);
  const [purchaseBusy, setPurchaseBusy] = useState(false);
  const [quote, setQuote] = useState<ExtractionQuote | null>(null);
  const [quoteBusy, setQuoteBusy] = useState(false);
  const [settleKey, setSettleKey] = useState<string | null>(null);
  const [clock, setClock] = useState(() => Date.now());
  const activityRefreshInFlight = useRef(false);
  const activityClaimKeys = useRef(new Map<string, string>());

  const handleError = useCallback((reason: unknown) => {
    if (reason instanceof ApiClientError && reason.status === 401) {
      onSessionExpired();
      return;
    }
    setError(describePlayerError(reason));
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
      setExtractionZonesError(reason instanceof Error ? reason.message : "资源兑换点数据加载失败");
    } finally {
      setExtractionZonesLoading(false);
    }
  }, [onSessionExpired]);

  const reloadNewPlayerActivities = useCallback(async (quiet = false) => {
    if (!quiet) setNewPlayerActivitiesLoading(true);
    setNewPlayerActivitiesError(null);
    try {
      const response = await getNewPlayerActivities();
      setNewPlayerActivities(response.items);
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setNewPlayerActivitiesError(
        reason instanceof Error ? reason.message : "新玩家活动加载失败"
      );
    } finally {
      setNewPlayerActivitiesLoading(false);
    }
  }, [onSessionExpired]);

  const reloadReliableTasks = useCallback(async (quiet = false) => {
    if (!quiet) setReliableTasksLoading(true);
    setReliableTasksError(null);
    try {
      setReliableTasks(await getReliableTasks());
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setReliableTasksError(reason instanceof Error ? reason.message : "任务进度加载失败");
    } finally {
      setReliableTasksLoading(false);
    }
  }, [onSessionExpired]);

  useEffect(() => {
    void reload();
    void reloadExtractionZones();
    void reloadNewPlayerActivities();
    void reloadReliableTasks();
  }, [reload, reloadExtractionZones, reloadNewPlayerActivities, reloadReliableTasks]);

  useEffect(() => {
    if (tab !== "map") return;
    void reloadExtractionZones(true);
    const timer = globalThis.setInterval(() => {
      if (document.visibilityState === "visible") void reloadExtractionZones(true);
    }, 5_000);
    return () => globalThis.clearInterval(timer);
  }, [reloadExtractionZones, tab]);

  const pendingEconomyActivity = hasPendingEconomyActivity(orders, runs?.items ?? []);

  const refreshPendingEconomyActivity = useCallback(async () => {
    if (activityRefreshInFlight.current) return;
    activityRefreshInFlight.current = true;
    try {
      const [nextOverview, nextOrders, nextLedger, nextRuns] = await Promise.all([
        getOverview(), getOrders(), getLedger(), getRuns()
      ]);
      setOverview(nextOverview);
      setOrders(nextOrders.items);
      setLedger(nextLedger.items);
      setRuns(nextRuns);
    } catch (reason) {
      handleError(reason);
    } finally {
      activityRefreshInFlight.current = false;
    }
  }, [handleError]);

  useEffect(() => {
    if (!pendingEconomyActivity) return;

    const refreshWhenVisible = () => {
      if (document.visibilityState === "visible") void refreshPendingEconomyActivity();
    };
    const timer = globalThis.setInterval(refreshWhenVisible, ECONOMY_POLL_INTERVAL_MS);
    document.addEventListener("visibilitychange", refreshWhenVisible);
    return () => {
      globalThis.clearInterval(timer);
      document.removeEventListener("visibilitychange", refreshWhenVisible);
    };
  }, [pendingEconomyActivity, refreshPendingEconomyActivity]);

  useEffect(() => {
    if (!quote) return;
    setClock(Date.now());
    const timer = globalThis.setInterval(() => setClock(Date.now()), 1_000);
    return () => globalThis.clearInterval(timer);
  }, [quote]);

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
        purchase.product,
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
      setClock(Date.now());
    } catch (reason) {
      handleError(reason);
    } finally {
      setQuoteBusy(false);
    }
  }

  async function confirmSettlement() {
    if (!quote || !settleKey) return;
    if (quoteSecondsRemaining(quote.expiresAt) === 0) {
      setClock(Date.now());
      setError({
        message: "资源兑换报价已过期。",
        nextStep: "旧报价不会扣除物品。关闭窗口后重新扫描背包即可取得新报价。"
      });
      return;
    }
    setQuoteBusy(true);
    setError(null);
    try {
      const result = await settleRun(quote.runId, settleKey, csrfToken);
      setNotice(result.state === "extracted" || result.state === "settled"
        ? `资源兑换成功，已获得 ${result.rewardAmount.toLocaleString("zh-CN")} ${currencyName(result.rewardCurrency)}。`
        : result.statusMessage || "资源兑换已提交，请等待结果核验。");
      setQuote(null);
      setSettleKey(null);
      await Promise.all([reload(true), reloadExtractionZones(true)]);
    } catch (reason) {
      handleError(reason);
    } finally {
      setQuoteBusy(false);
    }
  }

  async function claimActivity(activity: NewPlayerActivity) {
    const claimIdentity = `${activity.activityKey}:${activity.version}`;
    let idempotencyKey = activityClaimKeys.current.get(claimIdentity);
    if (!idempotencyKey) {
      idempotencyKey = crypto.randomUUID();
      activityClaimKeys.current.set(claimIdentity, idempotencyKey);
    }

    setClaimingActivity(claimIdentity);
    setError(null);
    setNotice(null);
    try {
      const result = await claimNewPlayerActivity(
        activity.activityKey,
        activity.version,
        idempotencyKey,
        csrfToken
      );
      setOverview((current) => current ? {
        ...current,
        balances: result.balances
      } : current);
      setNotice(result.idempotentReplay
        ? `“${activity.title}”奖励已领取，余额已重新同步。`
        : `已领取“${activity.title}”：${formatActivityRewards(activity)}。`);
      await Promise.all([reload(true), reloadNewPlayerActivities(true)]);
    } catch (reason) {
      handleError(reason);
    } finally {
      setClaimingActivity(null);
    }
  }

  const balance = overview?.balances;
  const displayName = overview?.displayName ?? session.displayName ?? "调查员";
  const quoteRemainingSeconds = quote ? quoteSecondsRemaining(quote.expiresAt, clock) : 0;
  const quoteExpired = quote !== null && quoteRemainingSeconds === 0;
  const hasSettledExchange = runs?.items.some((run) =>
    run.state === "settled" || run.state === "extracted") ?? false;
  const onboardingSteps: OnboardingStep[] = [
    {
      key: "login",
      title: "登录并绑定本周角色",
      detail: "当前会话已经通过平台身份与本周 PlayerUID 校验。",
      complete: true,
      destination: "shop",
      action: "查看商城"
    },
    {
      key: "supply",
      title: "购买一份战备补给",
      detail: "从战备商城选择商品；订单创建后会显示真实发货状态。",
      complete: orders.length > 0,
      destination: "shop",
      action: "浏览商城"
    },
    {
      key: "zone",
      title: "进入资源兑换点",
      detail: "打开地图查看路线，并让游戏角色进入兑换范围。",
      complete: quote !== null || hasSettledExchange ||
        (extractionZones?.items.some((zone) => zone.inRange === true) ?? false),
      destination: "map",
      action: "查看兑换点"
    },
    {
      key: "exchange",
      title: "确认整单出售",
      detail: "扫描允许容器中的全部白名单资源，核对报价后整单确认；首发不支持选择性出售。",
      complete: hasSettledExchange,
      destination: "runs",
      action: "扫描并兑换"
    },
    {
      key: "ledger",
      title: "核对资金账本",
      detail: "购买、退款、资源兑换和活动奖励都会留下可追溯的资金流水。",
      complete: ledger.length > 0,
      destination: "ledger",
      action: "查看资金流水"
    }
  ];

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
            <strong>本周世界结束</strong>
            <span>{overview ? formatDate(overview.season.endsAt) : "--"}</span>
            <small>商域币永久保留，周券随新档更新。</small>
          </div>
        </aside>

        <main id="main-content" className="content" tabIndex={-1}>
          <section className="balance-row" aria-label="我的资产">
            <div className="welcome">
              <span className="eyebrow">WELCOME BACK</span>
              <h1>{greeting()}，{displayName}</h1>
              <p>采购本周战备，或到兑换点出售白名单资源。</p>
            </div>
            <BalanceCard icon="币" name="永久商域币" value={balance?.merchantCoin} hint="跨周永久保留" />
            <BalanceCard icon="券" name="周战备券" value={balance?.weeklyTicket} hint="仅本周有效" tone="amber" />
          </section>

          {error && <div className="alert error" role="alert"><span><strong>{error.message}</strong><small>{error.nextStep}</small></span><button onClick={() => setError(null)}>关闭</button></div>}
          {notice && <div className="alert success" role="status"><span>{notice}</span><button onClick={() => setNotice(null)}>关闭</button></div>}

          {tab === "shop" && (
            <>
              <NewPlayerWelcome
                steps={onboardingSteps}
                activities={newPlayerActivities}
                activitiesLoading={newPlayerActivitiesLoading}
                activitiesError={newPlayerActivitiesError}
                claimingActivity={claimingActivity}
                online={overview?.online ?? false}
                onNavigate={setTab}
                onClaim={(activity) => void claimActivity(activity)}
                onRetry={() => void reloadNewPlayerActivities()}
              />
              <ReliableTasks
                snapshot={reliableTasks}
                loading={reliableTasksLoading}
                error={reliableTasksError}
                onRetry={() => void reloadReliableTasks()}
              />
            </>
          )}

          <div className="section-heading">
            <div><span className="eyebrow">{tabEyebrow(tab)}</span><h2>{tabs.find((item) => item.key === tab)?.label}</h2></div>
            <button className="secondary" disabled={refreshing} onClick={() => {
              void reload(true);
              if (tab === "map") void reloadExtractionZones(true);
              if (tab === "shop") {
                void reloadNewPlayerActivities(true);
                void reloadReliableTasks(true);
              }
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
              {tab === "orders" && <Orders orders={orders} polling={pendingEconomyActivity} />}
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
                  polling={pendingEconomyActivity}
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
            <span className="eyebrow">RESOURCE QUOTE</span>
            <h2 id="quote-title">确认出售资源</h2>
            <p>资源兑换点：<strong>{quote.zoneName}</strong> · 报价有效至 {formatTime(quote.expiresAt)}</p>
            <div className={quoteExpired ? "quote-countdown expired" : "quote-countdown"} role="timer" aria-label={quoteExpired ? "报价已过期" : `报价剩余 ${formatQuoteCountdown(quoteRemainingSeconds)}`}>
              <span>{quoteExpired ? "报价已过期" : "剩余时间"}</span>
              <strong>{formatQuoteCountdown(quoteRemainingSeconds)}</strong>
            </div>
            <div className="quote-items">
              {quote.items.map((item) => <div key={item.itemId}><span>{item.name} × {item.quantity}</span><strong>券 {item.totalValue.toLocaleString("zh-CN")}</strong></div>)}
            </div>
            <div className="purchase-total"><span>{quote.itemCount} 件资源</span><strong>券 {quote.totalValue.toLocaleString("zh-CN")}</strong></div>
            <p className="modal-note warning">{quoteExpired
              ? "此报价已失效，不会扣除物品。关闭窗口后重新扫描背包获取新报价。"
              : "确认后会出售报价中的全部物品并从背包扣除。结果不确定时不会重复入账，请等待管理员核对。"}</p>
            <div className="modal-actions">
              <button className="secondary" disabled={quoteBusy} onClick={() => { setQuote(null); setSettleKey(null); }}>{quoteExpired ? "关闭并重新扫描" : "暂不出售"}</button>
              <button className="primary" disabled={quoteBusy || quoteExpired} onClick={() => void confirmSettlement()}>{quoteBusy ? "兑换中…" : quoteExpired ? "报价已过期" : "确认出售全部资源"}</button>
            </div>
          </section>
        </div>
      )}
    </div>
  );
}

function NewPlayerWelcome({
  steps,
  activities,
  activitiesLoading,
  activitiesError,
  claimingActivity,
  online,
  onNavigate,
  onClaim,
  onRetry
}: {
  steps: OnboardingStep[];
  activities: NewPlayerActivityAvailability[];
  activitiesLoading: boolean;
  activitiesError: string | null;
  claimingActivity: string | null;
  online: boolean;
  onNavigate: (tab: Tab) => void;
  onClaim: (activity: NewPlayerActivity) => void;
  onRetry: () => void;
}) {
  const completedCount = steps.filter((step) => step.complete).length;
  return (
    <section className="new-player-welcome" aria-labelledby="new-player-welcome-title">
      <header>
        <div>
          <span className="eyebrow">FIRST STEPS</span>
          <h2 id="new-player-welcome-title">本周世界上手路线</h2>
          <p>完成状态只读取当前角色的地图、报价与订单记录。</p>
        </div>
        <strong className="onboarding-progress" aria-label={`已完成 ${completedCount} 个，共 ${steps.length} 个步骤`}>
          {completedCount} / {steps.length}
        </strong>
      </header>

      <ol className="onboarding-steps">
        {steps.map((step, index) => (
          <li className={step.complete ? "complete" : ""} key={step.key}>
            <span className="step-number" aria-hidden="true">{step.complete ? "✓" : index + 1}</span>
            <div>
              <strong>{step.title}</strong>
              <p>{step.detail}</p>
              <small>{step.complete ? "已由服务器记录确认" : "尚未完成"}</small>
            </div>
            <button className="secondary compact" onClick={() => onNavigate(step.destination)}>
              {step.complete ? "查看" : step.action}
            </button>
          </li>
        ))}
      </ol>

      <div className="new-player-activities" aria-labelledby="new-player-activities-title">
        <div className="activity-heading">
          <div>
            <span className="eyebrow">PLAYER ACTIVITIES</span>
            <h3 id="new-player-activities-title">可领取的新玩家活动</h3>
          </div>
          {!online && <span className="activity-online-hint">角色上线后可领取</span>}
        </div>

        {activitiesLoading && <p className="activity-message" role="status">正在同步活动资格…</p>}
        {!activitiesLoading && activitiesError && (
          <div className="activity-message error" role="alert">
            <span>活动暂时无法读取：{activitiesError}</span>
            <button className="secondary compact" onClick={onRetry}>重新加载</button>
          </div>
        )}
        {!activitiesLoading && !activitiesError && activities.length === 0 && (
          <p className="activity-message">当前世界没有已发布、可领取的新玩家活动。</p>
        )}
        {!activitiesLoading && !activitiesError && activities.length > 0 && (
          <ul className="activity-list">
            {activities.map(({ activity, claimed, grant }) => {
              const claimIdentity = `${activity.activityKey}:${activity.version}`;
              const busy = claimingActivity === claimIdentity;
              const rewardDescription = formatActivityRewards(activity);
              const descriptionId = `activity-${activity.activityId}-description`;
              return (
                <li key={claimIdentity}>
                  <div>
                    <strong>{activity.title}</strong>
                    <p id={descriptionId}>{activity.description}</p>
                    <span>{rewardDescription}</span>
                    {claimed && grant && <small>领取于 {formatDateTime(grant.claimedAt)}</small>}
                  </div>
                  <button
                    className={claimed ? "secondary" : "primary"}
                    aria-describedby={descriptionId}
                    aria-busy={busy || undefined}
                    disabled={claimed || busy || !online}
                    onClick={() => onClaim(activity)}
                  >
                    {claimed ? "已领取" : busy ? "领取中…" : online ? "领取奖励" : "等待角色上线"}
                  </button>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </section>
  );
}

function ReliableTasks({
  snapshot,
  loading,
  error,
  onRetry
}: {
  snapshot: ReliableTaskSnapshot | null;
  loading: boolean;
  error: string | null;
  onRetry: () => void;
}) {
  return (
    <section className="reliable-tasks" aria-labelledby="reliable-tasks-title">
      <header>
        <div>
          <span className="eyebrow">SERVER-PROVEN TASKS</span>
          <h2 id="reliable-tasks-title">本周可靠任务</h2>
          <p>只按服务器确认的资源兑换与已送达订单推进；刷新或重复回执不会重复奖励。</p>
        </div>
        <strong>任务积分 {snapshot?.rankingPoints.toLocaleString("zh-CN") ?? "--"}</strong>
      </header>
      {loading && <p className="task-message" role="status">正在同步任务进度…</p>}
      {!loading && error && (
        <div className="task-message error" role="alert">
          <span>{error}</span><button className="secondary compact" onClick={onRetry}>重试</button>
        </div>
      )}
      {!loading && !error && snapshot?.items.length === 0 && (
        <p className="task-message">当前内容版本没有开放任务。</p>
      )}
      {!loading && !error && snapshot && snapshot.items.length > 0 && (
        <div className="task-grid">
          {snapshot.items.map((task) => {
            const progress = Math.min(task.progress, task.targetAmount);
            const ratio = task.targetAmount > 0 ? Math.min(100, progress * 100 / task.targetAmount) : 0;
            return (
              <article className={task.completed ? "complete" : ""} key={task.instanceId}>
                <div className="task-title">
                  <span>{task.cadence === "Daily" ? "日任务" : "周任务"}</span>
                  {task.completed && <em>{task.rewardGranted ? "奖励已入账" : "奖励入账中"}</em>}
                </div>
                <h3>{task.displayName}</h3>
                <p>{task.description}</p>
                <div
                  className="task-progress"
                  role="progressbar"
                  aria-label={`${task.displayName}进度`}
                  aria-valuemin={0}
                  aria-valuemax={task.targetAmount}
                  aria-valuenow={progress}
                >
                  <span style={{ width: `${ratio}%` }} />
                </div>
                <div className="task-meta">
                  <strong>{progress.toLocaleString("zh-CN")} / {task.targetAmount.toLocaleString("zh-CN")}</strong>
                  <span>奖励 {task.reward.amount.toLocaleString("zh-CN")} {taskRewardCurrency(task.reward.currency)} · {task.reward.rankingPoints} 积分</span>
                </div>
                <small>规则 {task.rulesVersion} · 内容 {task.contentHash.slice(0, 8)}</small>
              </article>
            );
          })}
        </div>
      )}
    </section>
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
        <div className="product-copy"><div className="product-meta"><span>{product.category}</span>{product.personalLimitRemaining !== null && <span>个人本周剩余 {product.personalLimitRemaining}</span>}{product.serverStockRemaining !== null && <span>全服剩余 {product.serverStockRemaining}</span>}</div><h3>{product.name}</h3><p>{product.description}</p><small>{product.deliverySummary}</small></div>
        <div className="product-buy"><strong>{currencyIcon(product.price.currency)} {product.price.amount.toLocaleString("zh-CN")}</strong><button className="primary" disabled={!online || !product.enabled || maxQuantity(product) < 1} onClick={() => onPurchase(product)}>{productPurchaseLabel(product)}</button></div>
      </article>)}
    </div>
    {products.length === 0 && <Empty title="没有匹配的商品" detail="换一个关键词或分类试试。" />}
  </>;
}

function Orders({ orders, polling }: { orders: Order[]; polling: boolean }) {
  if (!orders.length) return <Empty title="还没有商城订单" detail="购买的战备会在这里显示发货进度。" />;
  return <>
    {polling && <div className="live-update" role="status"><span aria-hidden="true" />正在自动更新处理中的订单，终态后停止。</div>}
    <div className="data-list">{orders.map((order) => {
      const guidance = orderStateGuidance(order.state);
      return <article key={order.orderId}><div className={`state ${order.state}`}>{orderState(order.state)}</div><div className="data-main"><strong>{order.productName} × {order.quantity}</strong><span>{order.statusMessage || `订单号 ${order.orderId.slice(0, 8)}`}</span>{guidance && <small className="state-guidance">{guidance}</small>}</div><div className="data-value"><strong>− {currencyIcon(order.currency)} {order.totalAmount.toLocaleString("zh-CN")}</strong><time>{formatDateTime(order.createdAt)}</time></div></article>;
    })}</div>
  </>;
}

function Ledger({ entries }: { entries: LedgerEntry[] }) {
  if (!entries.length) return <Empty title="暂无资金流水" detail="购买、资源兑换和活动奖励都会形成可追溯记录。" />;
  return <div className="data-list ledger-list">{entries.map((entry) => <article key={entry.entryId}><div className={entry.amount >= 0 ? "amount-icon income" : "amount-icon expense"}>{entry.amount >= 0 ? "+" : "−"}</div><div className="data-main"><strong>{entry.reason}</strong><span>{entry.referenceId ? `关联 ${entry.referenceId.slice(0, 12)}` : "系统账务"}</span></div><div className="data-value"><strong className={entry.amount >= 0 ? "positive" : "negative"}>{entry.amount >= 0 ? "+" : ""}{entry.amount.toLocaleString("zh-CN")} {currencyName(entry.currency)}</strong><time>余额 {entry.balanceAfter.toLocaleString("zh-CN")} · {formatDateTime(entry.createdAt)}</time></div></article>)}</div>;
}

function Runs({ runs, busy, online, polling, onQuote }: { runs: RunList | null; busy: boolean; online: boolean; polling: boolean; onQuote: () => void }) {
  return <>
    <section className="extraction-action"><div><span className="eyebrow">RESOURCE EXCHANGE</span><h3>已到达资源兑换点？</h3><p>扫描允许容器中的白名单资源，确认整单报价后再扣除并入账。</p></div><button className="primary large" disabled={busy || !online || !runs?.settlementEnabled} onClick={onQuote}>{busy ? "扫描中…" : "扫描可售资源"}</button></section>
    {!runs?.settlementEnabled && <div className="inline-tip warning">{runs?.reason || "资源兑换当前不可用"}</div>}
    {polling && <div className="live-update" role="status"><span aria-hidden="true" />正在自动更新处理中的兑换，终态后停止。</div>}
    {!runs?.items.length ? <Empty title="本周还没有资源兑换记录" detail="到达开放的资源兑换点后，即可扫描并出售白名单资源。" /> : <div className="data-list">{runs.items.map((run) => {
      const guidance = runStateGuidance(run.state);
      return <article key={run.runId}><div className={`state ${run.state}`}>{resourceExchangeStateLabel(run.state)}</div><div className="data-main"><strong>{run.extractedItemCount} 件资源</strong><span>{run.statusMessage || `兑换单 ${run.runId.slice(0, 8)}`}</span>{guidance && <small className="state-guidance">{guidance}</small>}</div><div className="data-value"><strong>{currencyIcon(run.rewardCurrency)} {run.rewardAmount.toLocaleString("zh-CN")}</strong><time>{formatDateTime(run.startedAt)}</time></div></article>;
    })}</div>}
  </>;
}

function Loading() { return <div className="loading" role="status"><span /><p>正在同步你的商域数据…</p></div>; }
function Empty({ title, detail }: { title: string; detail: string }) { return <div className="empty"><span aria-hidden="true">◇</span><strong>{title}</strong><p>{detail}</p></div>; }
function maxQuantity(product: Product) { const personal = product.personalLimitRemaining ?? 99; const server = product.serverStockRemaining ?? 99; return Math.max(0, Math.min(99, personal, server)); }
function productPurchaseLabel(product: Product) { if (!product.enabled) return "暂不可用"; if (product.serverStockRemaining !== null && product.serverStockRemaining <= 0) return "全服售罄"; if (product.personalLimitRemaining !== null && product.personalLimitRemaining <= 0) return "本周限购已用完"; return "购买"; }
function clamp(value: number, min: number, max: number) { return Math.min(max, Math.max(min, value)); }
function currencyIcon(currency: Currency) { return currency === "merchantCoin" ? "币" : "券"; }
function currencyName(currency: Currency) { return currency === "merchantCoin" ? "商域币" : "周券"; }
function taskRewardCurrency(currency: "MarketCoin" | "SeasonVoucher") { return currency === "MarketCoin" ? "商域币" : "周战备券"; }
function orderState(state: Order["state"]) { return ({ accepted: "已受理", pending: "待发货", delivering: "发货中", succeeded: "已送达", failed: "失败", partial: "部分到账", uncertain: "待核对", cancelled: "已取消", refunded: "已退款" } as const)[state]; }
function tabEyebrow(tab: Tab) { return ({ shop: "SUPPLY MARKET", map: "EXCHANGE LOCATIONS", orders: "DELIVERY TRACKING", ledger: "WALLET HISTORY", runs: "EXCHANGE LOG" })[tab]; }
function formatDate(value: string) { return new Intl.DateTimeFormat("zh-CN", { month: "long", day: "numeric", weekday: "short", hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
function formatDateTime(value: string) { return new Intl.DateTimeFormat("zh-CN", { month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit" }).format(new Date(value)); }
function formatTime(value: string) { return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(new Date(value)); }
function formatActivityRewards(activity: NewPlayerActivity) {
  const rewards = [];
  if (activity.rewards.merchantCoin > 0) rewards.push(`${activity.rewards.merchantCoin.toLocaleString("zh-CN")} 商域币`);
  if (activity.rewards.weeklyTicket > 0) rewards.push(`${activity.rewards.weeklyTicket.toLocaleString("zh-CN")} 周券`);
  return rewards.join(" + ") || "无货币奖励";
}
function greeting() { const hour = new Date().getHours(); return hour < 6 ? "夜深了" : hour < 11 ? "早上好" : hour < 14 ? "中午好" : hour < 18 ? "下午好" : "晚上好"; }
