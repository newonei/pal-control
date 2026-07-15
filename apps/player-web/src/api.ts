const root = "/api/v1/player";

export type PlayerSession = {
  authenticated: boolean;
  userId: string | null;
  displayName: string | null;
  csrfToken: string | null;
  expiresAt: string | null;
};

export type CodeChallenge = {
  challengeId: string;
  expiresAt: string;
  retryAfterSeconds: number;
};

export type PlayerAuthenticationMode = {
  authenticationMode: "trustedGameCode" | "openIdThenGameCode";
  steamOpenIdRequired: boolean;
  pendingPlatformIdentity: boolean;
  trustedGameCodeFallback: boolean;
};

export type Currency = "merchantCoin" | "weeklyTicket";

export type Overview = {
  userId: string;
  displayName: string | null;
  online: boolean;
  gameplayMode: "weekly-resource-economy";
  season: {
    seasonId: string;
    name: string;
    state: "scheduled" | "active" | "settling" | "closed";
    startsAt: string;
    endsAt: string;
    nextShopRefreshAt: string | null;
  };
  balances: { merchantCoin: number; weeklyTicket: number };
  seasonStats: {
    settledExchanges: number;
    failedSettlements: number;
    uncertainSettlements: number;
    exchangedValue: number;
    successfulRuns?: number;
    failedRuns?: number;
    uncertainRuns?: number;
    extractedValue?: number;
  };
};

export type Product = {
  productId: string;
  sku: string;
  name: string;
  description: string;
  category: string;
  tags: string[];
  price: { currency: Currency; amount: number };
  deliverySummary: string;
  stockRemaining: number | null;
  personalLimitRemaining: number | null;
  serverStockRemaining: number | null;
  purchaseLimit: number | null;
  globalStock: number | null;
  purchased: number;
  enabled: boolean;
  featured: boolean;
  featuredRank: number | null;
  contentVersionId: string;
  contentHash: string;
};

export type Catalog = {
  revision: string;
  contentVersionId: string;
  contentHash: string;
  businessDate: string;
  rulesVersion: string;
  items: Product[];
};

export type Order = {
  orderId: string;
  productId: string;
  productName: string;
  quantity: number;
  currency: Currency;
  totalAmount: number;
  state: "accepted" | "pending" | "delivering" | "succeeded" | "failed" | "partial" | "uncertain" | "cancelled" | "refunded";
  statusMessage: string | null;
  createdAt: string;
  updatedAt: string;
};

export type LedgerEntry = {
  entryId: string;
  currency: Currency;
  amount: number;
  balanceAfter: number;
  reason: string;
  referenceId: string | null;
  createdAt: string;
};

export type ExtractionRun = {
  runId: string;
  state: "preparing" | "deployed" | "extracted" | "settled" | "failed" | "uncertain" | "cancelled";
  extractedItemCount: number;
  extractedValue: number;
  rewardCurrency: Currency;
  rewardAmount: number;
  startedAt: string;
  endedAt: string | null;
  statusMessage?: string | null;
};

export type RunList = {
  items: ExtractionRun[];
  settlementEnabled: boolean;
  reason: string | null;
};

export type NewPlayerActivity = {
  activityId: string;
  activityKey: string;
  version: number;
  state: "draft" | "published" | "closed";
  title: string;
  description: string;
  rewards: { merchantCoin: number; weeklyTicket: number };
  revision: number;
  createdBy: string;
  createdAt: string;
  publishedSeasonId: string | null;
  publishedWorldId: string | null;
  publishedBy: string | null;
  publishedAt: string | null;
  closedBy: string | null;
  closedAt: string | null;
};

export type NewPlayerActivityGrant = {
  grantId: string;
  activityId: string;
  activityKey: string;
  activityVersion: number;
  seasonId: string;
  worldId: string;
  rewards: { merchantCoin: number; weeklyTicket: number };
  balancesAfter: { merchantCoin: number; weeklyTicket: number };
  claimedAt: string;
};

export type NewPlayerActivityAvailability = {
  activity: NewPlayerActivity;
  claimed: boolean;
  grant: NewPlayerActivityGrant | null;
};

export type NewPlayerActivityClaim = {
  activity: NewPlayerActivity;
  grant: NewPlayerActivityGrant;
  balances: { merchantCoin: number; weeklyTicket: number };
  created: boolean;
  idempotentReplay: boolean;
};

export type ReliableTask = {
  instanceId: string;
  cadence: "Daily" | "Weekly";
  periodKey: string;
  taskKey: string;
  displayName: string;
  description: string;
  eventKind: string;
  targetAmount: number;
  progress: number;
  completed: boolean;
  completedAt: string | null;
  rewardGranted: boolean;
  reward: {
    currency: "MarketCoin" | "SeasonVoucher";
    amount: number;
    rankingPoints: number;
  };
  contentVersionId: string;
  contentHash: string;
  rulesVersion: string;
  rotationSeed: string;
};

export type ReliableTaskSnapshot = {
  accountId: string;
  seasonId: string;
  serverId: string;
  rankingPoints: number;
  items: ReliableTask[];
};

export type ExtractionZone = {
  id: string;
  displayName: string;
  mapX?: number | null;
  mapY?: number | null;
  radius?: number | null;
  worldX?: number | null;
  worldY?: number | null;
  worldRadius?: number | null;
  routeHint?: string | null;
  inRange?: boolean | null;
  distance?: number | null;
};

export type ExtractionPlayerPosition = {
  mapX?: number | null;
  mapY?: number | null;
  worldX?: number | null;
  worldY?: number | null;
};

export type ExtractionZoneList = {
  items: ExtractionZone[];
  playerPosition?: ExtractionPlayerPosition | null;
  updatedAt?: string | null;
  playerOnline?: boolean;
  positionAvailable?: boolean;
  status?: string | null;
  statusMessage?: string | null;
};

type ExtractionMapPoint = { x: number; y: number };

type ExtractionZoneMapResponse = {
  status: string;
  statusMessage: string;
  sampledAt: string;
  player: {
    online: boolean;
    positionAvailable: boolean;
    mapPosition: ExtractionMapPoint | null;
    worldPosition: ExtractionMapPoint | null;
  };
  zones: Array<{
    id: string;
    name: string;
    routeHint: string | null;
    mapPosition: ExtractionMapPoint;
    worldPosition: ExtractionMapPoint;
    radius: number;
    worldRadius: number;
    distanceToCenter: number | null;
    distanceToBoundary: number | null;
    inside: boolean | null;
  }>;
};

export type ExtractionQuote = {
  runId: string;
  state: "quoted";
  zoneName: string;
  items: Array<{
    itemId: string;
    name: string;
    quantity: number;
    unitValue: number;
    totalValue: number;
  }>;
  itemCount: number;
  totalValue: number;
  expiresAt: string;
};

export class ApiClientError extends Error {
  readonly status: number;
  readonly code?: string;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = "ApiClientError";
    this.status = status;
    this.code = code;
  }
}

export function getAuthenticationMode() {
  return request<PlayerAuthenticationMode>("/auth/mode", { method: "GET" });
}

export function steamLoginStartUrl() {
  return `${root}/auth/steam/start`;
}

export function requestCode(userId: string | null) {
  return request<CodeChallenge>("/auth/request-code", {
    method: "POST",
    json: { userId }
  });
}

export function verifyCode(challengeId: string, code: string) {
  return request<PlayerSession>("/auth/verify", {
    method: "POST",
    json: { challengeId, code }
  });
}

export function getSession() {
  return request<PlayerSession>("/auth/session", { method: "GET" });
}

export function logout(csrfToken: string) {
  return request<void>("/auth/logout", { method: "POST", csrfToken });
}

export function getOverview() {
  return request<Overview>("/me/overview", { method: "GET" });
}

export function getCatalog() {
  return request<Catalog>("/me/catalog", { method: "GET" });
}

export function getOrders() {
  return request<{ items: Order[] }>("/me/orders", { method: "GET" });
}

export function getLedger() {
  return request<{ items: LedgerEntry[] }>("/me/ledger", { method: "GET" });
}

export function getRuns() {
  return request<RunList>("/me/runs", { method: "GET" });
}

export function getNewPlayerActivities() {
  return request<{ items: NewPlayerActivityAvailability[] }>(
    "/me/new-player-activities",
    { method: "GET" }
  );
}

export function getReliableTasks() {
  return request<ReliableTaskSnapshot>("/me/tasks", { method: "GET" });
}

export function claimNewPlayerActivity(
  activityKey: string,
  version: number,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<NewPlayerActivityClaim>(
    `/me/new-player-activities/${encodeURIComponent(activityKey)}/versions/${version}/claim`,
    {
      method: "POST",
      idempotencyKey,
      csrfToken
    }
  );
}

export async function getExtractionZones(): Promise<ExtractionZoneList> {
  const response = await request<ExtractionZoneList | ExtractionZoneMapResponse>(
    "/me/extraction-zones",
    { method: "GET" }
  );

  // Keep the player UI compatible with the initial flat contract while using
  // the richer calibrated response now returned by the control API.
  if ("zones" in response && Array.isArray(response.zones)) {
    const positionAvailable = response.player?.positionAvailable === true;
    return {
      items: response.zones.map((zone) => ({
        id: zone.id,
        displayName: zone.name,
        mapX: zone.mapPosition?.x ?? null,
        mapY: zone.mapPosition?.y ?? null,
        radius: zone.radius,
        worldX: zone.worldPosition?.x ?? null,
        worldY: zone.worldPosition?.y ?? null,
        worldRadius: zone.worldRadius,
        routeHint: zone.routeHint,
        inRange: positionAvailable ? zone.inside : null,
        distance: positionAvailable ? zone.distanceToCenter : null
      })),
      playerPosition: positionAvailable ? {
        mapX: response.player.mapPosition?.x ?? null,
        mapY: response.player.mapPosition?.y ?? null,
        worldX: response.player.worldPosition?.x ?? null,
        worldY: response.player.worldPosition?.y ?? null
      } : null,
      updatedAt: response.sampledAt,
      playerOnline: response.player.online,
      positionAvailable,
      status: response.status,
      statusMessage: response.statusMessage
    };
  }

  if ("items" in response && Array.isArray(response.items)) return response;
  throw new ApiClientError("资源兑换点响应格式无效", 502, "INVALID_EXTRACTION_ZONE_RESPONSE");
}

export function createOrder(
  product: Product,
  quantity: number,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<Order>("/me/orders", {
    method: "POST",
    json: {
      productId: product.productId,
      quantity,
      contentVersionId: product.contentVersionId,
      contentHash: product.contentHash,
      sku: product.sku
    },
    idempotencyKey,
    csrfToken
  });
}

export function quoteRun(csrfToken: string) {
  return request<ExtractionQuote>("/me/runs/quote", {
    method: "POST",
    csrfToken
  });
}

export function settleRun(runId: string, idempotencyKey: string, csrfToken: string) {
  return request<ExtractionRun>(`/me/runs/${encodeURIComponent(runId)}/settle`, {
    method: "POST",
    idempotencyKey,
    csrfToken
  });
}

type RequestOptions = {
  method: "GET" | "POST";
  json?: unknown;
  csrfToken?: string;
  idempotencyKey?: string;
};

async function request<T>(path: string, options: RequestOptions): Promise<T> {
  const headers = new Headers({ Accept: "application/json" });
  if (options.json !== undefined) headers.set("Content-Type", "application/json");
  if (options.csrfToken) headers.set("X-CSRF-Token", options.csrfToken);
  if (options.idempotencyKey) headers.set("Idempotency-Key", options.idempotencyKey);

  const response = await fetch(`${root}${path}`, {
    method: options.method,
    headers,
    body: options.json === undefined ? undefined : JSON.stringify(options.json),
    credentials: "include",
    cache: "no-store"
  });

  if (!response.ok) {
    const body = await response.json().catch(() => null) as {
      code?: string;
      message?: string;
      detail?: string;
      error?: { code?: string; message?: string };
    } | null;
    throw new ApiClientError(
      body?.error?.message ?? body?.message ?? body?.detail ?? `请求失败（HTTP ${response.status}）`,
      response.status,
      body?.error?.code ?? body?.code
    );
  }

  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}
