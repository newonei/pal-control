export type ExtractionCurrency = "merchantCoin" | "weeklyTicket";

export type ExtractionSeason = {
  seasonId: string;
  name: string;
  state: "scheduled" | "active" | "settling" | "closed";
  startsAt: string;
  endsAt: string;
  nextShopRefreshAt: string | null;
};

export type ExtractionBalances = {
  merchantCoin: number;
  weeklyTicket: number;
};

export type ExtractionOverview = {
  gameplayMode: "weekly-resource-economy";
  userId: string;
  displayName: string | null;
  season: ExtractionSeason;
  balances: ExtractionBalances;
  seasonStats: {
    settledExchanges: number;
    failedSettlements: number;
    uncertainSettlements: number;
    exchangedValue: number;
    /** @deprecated compatibility alias for settledExchanges */
    successfulRuns: number;
    /** @deprecated compatibility alias for failedSettlements */
    failedRuns: number;
    /** @deprecated compatibility alias for uncertainSettlements */
    uncertainRuns: number;
    /** @deprecated compatibility alias for exchangedValue */
    extractedValue: number;
  };
};

export type ShopProduct = {
  productId: string;
  name: string;
  description: string;
  category: string;
  tags: string[];
  price: {
    currency: ExtractionCurrency;
    amount: number;
  };
  deliverySummary: string;
  stockRemaining: number | null;
  purchaseLimit: number | null;
  purchased: number;
  enabled: boolean;
  featured: boolean;
};

export type ShopCatalog = {
  revision: string;
  items: ShopProduct[];
};

export type ShopOrderState =
  | "accepted"
  | "pending"
  | "delivering"
  | "succeeded"
  | "failed"
  | "uncertain"
  | "cancelled";

export type ShopOrder = {
  orderId: string;
  productId: string;
  productName: string;
  quantity: number;
  currency: ExtractionCurrency;
  totalAmount: number;
  state: ShopOrderState;
  statusMessage: string | null;
  createdAt: string;
  updatedAt: string;
};

export type ShopOrderList = {
  items: ShopOrder[];
};

export type WalletLedgerEntry = {
  entryId: string;
  currency: ExtractionCurrency;
  amount: number;
  balanceAfter: number;
  reason: string;
  referenceId: string | null;
  createdAt: string;
};

export type WalletLedger = {
  items: WalletLedgerEntry[];
};

export type ExtractionRunState = "preparing" | "deployed" | "extracted" | "failed" | "uncertain" | "cancelled";

export type ExtractionRun = {
  runId: string;
  state: ExtractionRunState;
  extractedItemCount: number;
  extractedValue: number;
  rewardCurrency: ExtractionCurrency;
  rewardAmount: number;
  startedAt: string;
  endedAt: string | null;
  statusMessage?: string | null;
  internalState?: string;
};

export type ExtractionRunList = {
  items: ExtractionRun[];
  settlementEnabled: boolean;
  reason: string | null;
};

export type ExtractionQuoteItem = {
  itemId: string;
  name: string;
  quantity: number;
  unitValue: number;
  totalValue: number;
};

export type ExtractionQuote = {
  runId: string;
  state: "quoted";
  zoneName: string;
  items: ExtractionQuoteItem[];
  itemCount: number;
  totalValue: number;
  expiresAt: string;
};

export type CreateShopOrderInput = {
  userId: string;
  productId: string;
  quantity: number;
  idempotencyKey: string;
};

const root = "/api/v1/extraction";

export function getExtractionOverview(userId: string, signal?: AbortSignal) {
  return getJson<ExtractionOverview>(withUserId("overview", userId), signal);
}

export function getShopCatalog(userId: string, signal?: AbortSignal) {
  return getJson<ShopCatalog>(withUserId("catalog", userId), signal);
}

export function getShopOrders(userId: string, signal?: AbortSignal) {
  return getJson<ShopOrderList>(withUserId("orders", userId), signal);
}

export function getWalletLedger(userId: string, signal?: AbortSignal) {
  return getJson<WalletLedger>(withUserId("ledger", userId), signal);
}

export function getExtractionRuns(userId: string, signal?: AbortSignal) {
  return getJson<ExtractionRunList>(withUserId("runs", userId), signal);
}

export async function createExtractionQuote(userId: string): Promise<ExtractionQuote> {
  const response = await fetch(`${root}/runs/quote`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ userId })
  });
  if (!response.ok) throw new Error(await apiError(response, "扫描可售资源失败"));
  return response.json() as Promise<ExtractionQuote>;
}

export async function settleExtractionRun(input: {
  runId: string;
  userId: string;
  idempotencyKey: string;
}): Promise<ExtractionRun> {
  const response = await fetch(`${root}/runs/${encodeURIComponent(input.runId)}/settle`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": input.idempotencyKey
    },
    body: JSON.stringify({ userId: input.userId })
  });
  if (!response.ok) throw new Error(await apiError(response, "资源兑换结算失败"));
  return response.json() as Promise<ExtractionRun>;
}

export async function createShopOrder(input: CreateShopOrderInput): Promise<ShopOrder> {
  const response = await fetch(`${root}/orders`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": input.idempotencyKey
    },
    body: JSON.stringify({
      userId: input.userId,
      productId: input.productId,
      quantity: input.quantity
    })
  });
  if (!response.ok) throw new Error(await apiError(response, "提交商城订单失败"));
  return response.json() as Promise<ShopOrder>;
}

function withUserId(path: string, userId: string) {
  const query = new URLSearchParams({ userId });
  return `${root}/${path}?${query.toString()}`;
}

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) throw new Error(await apiError(response, "读取资源经济数据失败"));
  return response.json() as Promise<T>;
}

async function apiError(response: Response, fallback: string) {
  const body = await response.json().catch(() => null) as {
    message?: string;
    detail?: string;
    error?: { message?: string };
  } | null;
  return body?.error?.message ?? body?.message ?? body?.detail ?? `${fallback}（HTTP ${response.status}）`;
}
