import { adminFetch } from "../../lib/api/adminFetch";

export type AnalyticsCount = { value: number | null; suppressed: boolean };
export type AnalyticsRate = {
  numerator: number | null;
  denominator: number | null;
  basisPoints: number | null;
  suppressed: boolean;
  denominatorComplete: boolean;
};

export type EconomyAnalyticsReport = {
  schemaVersion: number;
  serverId: string;
  seasonId: string | null;
  contentVersionId: string | null;
  window: {
    from: string;
    to: string;
    dateBasis: "utc" | "business";
    stable: boolean;
    stableThrough: string;
    timeZoneId: string;
  };
  privacy: {
    minimumCohortSize: number;
    suppressionRule: string;
    containsPlayerIdentifiers: false;
  };
  source: {
    kind: string;
    schemaVersion: number;
    asOf: string;
    recomputationHash: string;
    tables: string[];
    rowsRead: number;
    complete: boolean;
  };
  funnel: Array<{
    key: string;
    label: string;
    accounts: AnalyticsCount;
    facts: number | null;
    successOnly: boolean;
    source: string;
  }>;
  products: Array<{
    sku: string;
    catalogViewers: AnalyticsCount;
    deliveredBuyers: AnalyticsCount;
    deliveredQuantity: number | null;
    purchaseRate: AnalyticsRate;
  }>;
  resourceExchange: {
    quotingAccounts: AnalyticsCount;
    settledAccounts: AnalyticsCount;
    quotedRuns: number | null;
    settledRuns: number | null;
    uncertainRuns: number | null;
    settledValue: number | null;
    conversionRate: AnalyticsRate;
  };
  zones: Array<{
    zoneId: string;
    accounts: AnalyticsCount;
    quotedRuns: number | null;
    settledRuns: number | null;
    uncertainRuns: number | null;
    settledValue: number | null;
    suppressed: boolean;
  }>;
  currencies: Array<{
    currency: "merchantCoin" | "weeklyTicket";
    accounts: AnalyticsCount;
    inflow: number | null;
    outflow: number | null;
    net: number | null;
    balanceP50: number | null;
    balanceP95: number | null;
    minimumBalance: number | null;
    maximumBalance: number | null;
    suppressed: boolean;
  }>;
  uncertain: {
    orders: number | null;
    deliveries: number | null;
    resourceSettlements: number | null;
    suppressed: boolean;
  };
  alerts: Array<{ code: string; severity: string; message: string }>;
  page: {
    limit: number;
    offset: number;
    totalProducts: number;
    totalZones: number;
    nextCursor: string | null;
  };
};

export type EconomyAnalyticsFilters = {
  serverId: string;
  from: string;
  to: string;
  dateBasis: "business" | "utc";
  seasonId?: string;
  contentVersionId?: string;
  limit?: number;
  cursor?: string;
};

export class EconomyAnalyticsApiError extends Error {
  constructor(message: string, readonly status: number, readonly code: string) {
    super(message);
    this.name = "EconomyAnalyticsApiError";
  }
}

export function analyticsUrl(filters: EconomyAnalyticsFilters): string {
  const parameters = new URLSearchParams({
    serverId: filters.serverId.trim(),
    from: filters.from,
    to: filters.to,
    dateBasis: filters.dateBasis,
    limit: String(filters.limit ?? 50)
  });
  if (filters.seasonId?.trim()) parameters.set("seasonId", filters.seasonId.trim());
  if (filters.contentVersionId?.trim()) parameters.set("contentVersionId", filters.contentVersionId.trim());
  if (filters.cursor?.trim()) parameters.set("cursor", filters.cursor.trim());
  return `/api/v1/economy/analytics?${parameters.toString()}`;
}

export async function getEconomyAnalytics(
  filters: EconomyAnalyticsFilters,
  signal?: AbortSignal
): Promise<EconomyAnalyticsReport> {
  const response = await adminFetch(analyticsUrl(filters), {
    signal,
    cache: "no-store"
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { code?: string; message?: string } | null;
    throw new EconomyAnalyticsApiError(
      body?.message ?? `运营分析读取失败（HTTP ${response.status}）`,
      response.status,
      body?.code ?? "ANALYTICS_REQUEST_FAILED"
    );
  }
  return response.json() as Promise<EconomyAnalyticsReport>;
}
