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

export type ContentRarity = "Common" | "Uncommon" | "Rare" | "Epic" | "Legendary";
export type PresentationSource = "content" | "legacy-fallback";

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
  iconKey: string;
  rarity: ContentRarity;
  usage: string;
  presentationSource: PresentationSource;
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

export type SeasonSettlementBoard = {
  board: "resource-value" | "task-points";
  eligible: boolean;
  rank: number | null;
  reasonCode: string;
  settledExchanges: number;
  resourceQuantity: number;
  resourceValue: number;
  taskPoints: number;
};

export type SeasonSettlement = {
  seasonId: string;
  seasonCode: string;
  cutoffAt: string;
  frozenAt: string;
  rewardState: "not-prepared" | "prepared" | "completed";
  rules: {
    rulesVersion: string;
    lateSettlementGraceMinutes: number;
    minimumSettledExchanges: number;
    minimumResourceValue: number;
    minimumTaskPoints: number;
    resourceTieBreakRule: string;
    taskTieBreakRule: string;
  };
  participation: {
    participating: boolean;
    reasonCode: string;
    resource: SeasonSettlementBoard;
    task: SeasonSettlementBoard;
    items: Array<{ itemId: string; category: string; quantity: number; value: number }>;
    categories: Array<{ category: string; quantity: number; value: number }>;
  };
  voucherExpiry: {
    jobState: "not-prepared" | "prepared" | "running" | "completed";
    itemState: "not-prepared" | "not-applicable" | "pending" | "expired";
    scheduledAmount: number;
    expiredAmount: number;
    ledgerRecorded: boolean;
    completedAt: string | null;
  };
  permanentRewards: Array<{
    source: "standard" | "supplement";
    board: "resource-value" | "task-points" | "manual";
    rank: number | null;
    marketCoin: number;
    rewardKey: string;
    decisionState: "granted" | "cancelled";
    deliveryState: "pending" | "paid" | "cancelled";
    reasonCode: string | null;
    ledgerRecorded: boolean;
    completedAt: string | null;
  }>;
};

export type SeasonSettlementResponse = {
  available: boolean;
  status: "not-frozen" | "frozen";
  settlement: SeasonSettlement | null;
};

export type PlayerNotification = {
  notificationId: string;
  schemaVersion: "1";
  seasonId: string;
  sourceType: "order-delivery" | "resource-settlement" | "season-end" | "reconciliation";
  sourceState: "delivered" | "failed" | "partial" | "uncertain" | "refunded" |
    "settled" | "cancelled" | "expired" | "frozen" | "reward-completed" |
    "voucher-expired" | "completed" | "reconciliation-required";
  severity: "success" | "info" | "warning" | "error";
  title: string;
  message: string;
  occurredAt: string;
  updatedAt: string;
  readAt: string | null;
  gameState: "pending" | "queued" | "sent" | "blocked" | "failed" | "uncertain" | "not-requested";
  safetyAction: "none" | "do-not-repeat-contact-support";
};

export type PlayerNotificationFeed = {
  schemaVersion: "1";
  unreadCount: number;
  hasActiveDelivery: boolean;
  items: PlayerNotification[];
};

export type PlayerNotificationReadResult = {
  notificationId: string;
  readAt: string;
  unreadCount: number;
};

export type PlayerNotificationReadAllResult = {
  markedRead: number;
  unreadCount: number;
};

export type TeamEconomyGoal = {
  kind: "ResourceItems" | "ResourceValue" | "TaskPoints" | "DeliveredOrders";
  displayName: string;
  progress: number;
  target: number;
  unit: string;
  achieved: boolean;
  reachedAt: string | null;
};

export type TeamEconomyContribution = {
  resourceItems: number;
  resourceValue: number;
  taskPoints: number;
  deliveredOrders: number;
  actualCurrencySpent: number;
};

export type TeamEconomyProjectionHealth = {
  ready: boolean;
  stale: boolean;
  cutoffAt: string | null;
  updatedAt: string | null;
  sourceHash: string | null;
  snapshotHash: string | null;
  lastErrorCode: string | null;
};

export type TeamEconomyDashboard = {
  enabled: boolean;
  hasTeam: boolean;
  teamId: string | null;
  name: string | null;
  status: "Active" | "Dissolved" | null;
  isOwner: boolean;
  memberCount: number;
  joinedAt: string | null;
  goals: TeamEconomyGoal[];
  teamContribution: TeamEconomyContribution | null;
  myContribution: TeamEconomyContribution | null;
  transferCandidates: Array<{
    memberHandle: string;
    label: string;
    joinedAt: string;
  }>;
  projection: TeamEconomyProjectionHealth;
  policyNotice: string;
};

export type TeamEconomyMutation = {
  teamId: string;
  name: string;
  status: "Active" | "Dissolved";
  memberCount: number;
  isOwner: boolean;
  replayed: boolean;
  updatedAt: string;
};

export type TeamEconomyInvitation = {
  teamId: string;
  inviteId: string;
  token: string | null;
  tokenShown: boolean;
  expiresAt: string;
  maximumUses: number;
  remainingUses: number;
  replayed: boolean;
};

export type TeamEconomyLeaderboardMetric =
  "resourceValue" | "taskPoints" | "deliveredOrders";

export type TeamEconomyLeaderboard = {
  metric: "ResourceValue" | "TaskPoints" | "DeliveredOrders";
  cutoffAt: string;
  offset: number;
  limit: number;
  total: number;
  nextCursor: string | null;
  items: Array<{
    rank: number;
    teamId: string;
    teamName: string;
    memberCount: number;
    value: number;
    reachedAt: string;
    isMyTeam: boolean;
  }>;
  tieBreakPolicy: string;
  eligibilityPolicy: string;
  projection: TeamEconomyProjectionHealth;
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
  riskHint?: string | null;
  inRange?: boolean | null;
  distance?: number | null;
  open?: boolean;
  hotspot?: boolean;
  yieldMultiplierBasisPoints?: number;
  nextOpensAt?: string | null;
  openWindows?: Array<{
    dayOfWeek: number;
    opensAt: string;
    closesAt: string;
    graceSeconds: number;
  }> | null;
  riskLevel?: ExtractionZoneRiskLevel | null;
  dynamicSelectedOpen?: boolean;
  dynamicOpenWindow?: EconomyEventWindow | null;
  hotspotWindow?: EconomyEventWindow | null;
  dynamicPolicyVersion?: string | null;
  dynamicSeed?: string | null;
  worldEvents?: EconomyWorldEvent[];
};

export type ExtractionZoneRiskLevel = "Guarded" | "Elevated" | "Severe";

export type EconomyEventWindow = {
  startsAt: string;
  endsAt: string;
  graceEndsAt: string;
};

export type EconomyWorldEvent = {
  eventId: string;
  eventKey: string;
  displayName: string;
  kind: "ResourceSurge" | "SupplyRelief";
  seed: string;
  window: EconomyEventWindow;
  zoneYieldMultiplierBasisPoints: number;
  productPriceMultiplierBasisPoints: number;
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
  nextOpensAt?: string | null;
  dynamicPolicyVersion?: string | null;
  dynamicSeed?: string | null;
  worldEvents?: EconomyWorldEvent[];
};

type ExtractionMapPoint = { x: number; y: number };

type ExtractionZoneMapResponse = {
  status: string;
  statusMessage: string;
  sampledAt: string;
  nextOpensAt: string | null;
  dynamicPolicyVersion: string | null;
  dynamicSeed: string | null;
  worldEvents: EconomyWorldEvent[];
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
    riskHint: string | null;
    mapPosition: ExtractionMapPoint;
    worldPosition: ExtractionMapPoint;
    radius: number;
    worldRadius: number;
    distanceToCenter: number | null;
    distanceToBoundary: number | null;
    inside: boolean | null;
    open: boolean;
    hotspot: boolean;
    yieldMultiplierBasisPoints: number;
    nextOpensAt: string | null;
    openWindows: Array<{
      dayOfWeek: number;
      opensAt: string;
      closesAt: string;
      graceSeconds: number;
    }> | null;
    riskLevel: ExtractionZoneRiskLevel | null;
    dynamicSelectedOpen: boolean;
    dynamicOpenWindow: EconomyEventWindow | null;
    hotspotWindow: EconomyEventWindow | null;
    dynamicPolicyVersion: string | null;
    dynamicSeed: string | null;
    worldEvents: EconomyWorldEvent[] | null;
  }>;
};

export type ExtractionQuote = {
  runId: string;
  revision: number;
  state: "quoted";
  zoneName: string;
  items: Array<{
    itemId: string;
    name: string;
    quantity: number;
    unitValue: number;
    totalValue: number;
    iconKey: string;
    rarity: ContentRarity;
    usage: string;
    presentationSource: PresentationSource;
  }>;
  itemCount: number;
  totalValue: number;
  expiresAt: string;
  sourceQuoteRunId: string | null;
  selectionDerived: boolean;
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

export function getLatestSeasonSettlement() {
  return request<SeasonSettlementResponse>("/me/season-leaderboards/latest", { method: "GET" });
}

export function getSeasonSettlement(seasonId: string) {
  return request<SeasonSettlementResponse>(
    `/me/season-leaderboards/${encodeURIComponent(seasonId)}`,
    { method: "GET" }
  );
}

export function getPlayerNotifications(limit = 50) {
  const bounded = Math.min(100, Math.max(1, Math.trunc(limit)));
  return request<PlayerNotificationFeed>(`/me/notifications?limit=${bounded}`, { method: "GET" });
}

export function markPlayerNotificationRead(notificationId: string, csrfToken: string) {
  return request<PlayerNotificationReadResult>(
    `/me/notifications/${encodeURIComponent(notificationId)}/read`,
    { method: "POST", csrfToken }
  );
}

export function markAllPlayerNotificationsRead(csrfToken: string) {
  return request<PlayerNotificationReadAllResult>(
    "/me/notifications/read-all",
    { method: "POST", csrfToken }
  );
}

export function getTeamEconomy() {
  return request<TeamEconomyDashboard>("/me/team-economy", { method: "GET" });
}

export function getTeamEconomyLeaderboard(
  metric: TeamEconomyLeaderboardMetric,
  cursor: string | null = null,
  limit = 50
) {
  const query = new URLSearchParams({ limit: String(Math.min(100, Math.max(1, limit))) });
  if (cursor) query.set("cursor", cursor);
  return request<TeamEconomyLeaderboard>(
    `/me/team-economy/leaderboards/${metric}?${query.toString()}`,
    { method: "GET" }
  );
}

export function createTeamEconomyTeam(name: string, idempotencyKey: string, csrfToken: string) {
  return request<TeamEconomyMutation>("/me/team-economy/teams", {
    method: "POST",
    json: { name },
    idempotencyKey,
    csrfToken
  });
}

export function rotateTeamEconomyInvitation(
  maximumUses: number,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<TeamEconomyInvitation>("/me/team-economy/invite/rotate", {
    method: "POST",
    json: { maximumUses },
    idempotencyKey,
    csrfToken
  });
}

export function joinTeamEconomyTeam(token: string, idempotencyKey: string, csrfToken: string) {
  return request<TeamEconomyMutation>("/me/team-economy/join", {
    method: "POST",
    json: { token },
    idempotencyKey,
    csrfToken
  });
}

export function leaveTeamEconomyTeam(idempotencyKey: string, csrfToken: string) {
  return request<TeamEconomyMutation>("/me/team-economy/leave", {
    method: "POST",
    json: {},
    idempotencyKey,
    csrfToken
  });
}

export function transferTeamEconomyOwner(
  memberHandle: string,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<TeamEconomyMutation>("/me/team-economy/owner/transfer", {
    method: "POST",
    json: { memberHandle },
    idempotencyKey,
    csrfToken
  });
}

export function dissolveTeamEconomyTeam(
  confirmation: string,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<TeamEconomyMutation>("/me/team-economy/dissolve", {
    method: "POST",
    json: { confirmation },
    idempotencyKey,
    csrfToken
  });
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
        riskHint: zone.riskHint,
        inRange: positionAvailable ? zone.inside : null,
        distance: positionAvailable ? zone.distanceToCenter : null,
        open: zone.open,
        hotspot: zone.hotspot,
        yieldMultiplierBasisPoints: zone.yieldMultiplierBasisPoints,
        nextOpensAt: zone.nextOpensAt,
        openWindows: zone.openWindows,
        riskLevel: zone.riskLevel,
        dynamicSelectedOpen: zone.dynamicSelectedOpen,
        dynamicOpenWindow: zone.dynamicOpenWindow,
        hotspotWindow: zone.hotspotWindow,
        dynamicPolicyVersion: zone.dynamicPolicyVersion,
        dynamicSeed: zone.dynamicSeed,
        worldEvents: zone.worldEvents ?? []
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
      statusMessage: response.statusMessage,
      nextOpensAt: response.nextOpensAt,
      dynamicPolicyVersion: response.dynamicPolicyVersion,
      dynamicSeed: response.dynamicSeed,
      worldEvents: response.worldEvents ?? []
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

export function selectQuote(
  runId: string,
  sourceRevision: number,
  items: Array<{ itemId: string; quantity: number }>,
  idempotencyKey: string,
  csrfToken: string
) {
  return request<ExtractionQuote>(`/me/runs/${encodeURIComponent(runId)}/select`, {
    method: "POST",
    json: { sourceRevision, items },
    idempotencyKey,
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
