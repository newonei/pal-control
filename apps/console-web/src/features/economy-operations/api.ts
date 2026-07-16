import {
  adminFetch,
  adminHighRiskFetch,
  type AdminHighRiskProof
} from "../../lib/api/adminFetch";

export type EconomyAccount = {
  accountId: string;
  displayName: string;
  platform: string;
  platformSubjectHash: string;
  createdAt: string;
  updatedAt: string;
};

export type EconomyOperationsOrder = {
  orderId: string;
  accountId: string;
  seasonId: string;
  serverId: string;
  account: EconomyAccount | null;
  deliveryId: string;
  deliveryAttempt: number;
  productId: string;
  sku: string;
  productName: string;
  quantity: number;
  currency: "merchantCoin" | "weeklyTicket";
  totalAmount: number;
  state: string;
  outcome: string | null;
  receiptResultId: string | null;
  contentVersionId: string | null;
  contentHash: string | null;
  worldId: string | null;
  createdAt: string;
  updatedAt: string;
  requiresReconciliation: boolean;
};

export type EconomyOperationsRun = {
  runId: string;
  accountId: string;
  seasonId: string;
  account: EconomyAccount | null;
  zoneId: string;
  zoneName: string;
  state: string;
  itemCount: number;
  totalValue: number;
  revision: number;
  attemptCount: number;
  errorCode: string | null;
  errorMessage: string | null;
  contentVersionId: string | null;
  contentHash: string | null;
  quoteSnapshotHash: string;
  settlementRequestHash: string | null;
  quotedAt: string;
  expiresAt: string;
  updatedAt: string;
  settledAt: string | null;
  requiresReconciliation: boolean;
};

export type EconomyOperationsAudit = {
  auditId: string;
  correlationId: string;
  phase: string;
  subject: string;
  roles: string[];
  method: string;
  path: string;
  requestHash: string;
  reason: string | null;
  beforeJson: string | null;
  afterJson: string | null;
  serviceVersion: string;
  resultStatus: number | null;
  occurredAt: string;
};

export type QueueState = {
  ready: boolean;
  pending: number;
  capacity: number;
  utilizationPercent: number;
  oldestAgeSeconds: number | null;
  states?: Record<string, number> | null;
};

export type BackupAge = {
  available: boolean;
  latestCreatedAt: string | null;
  ageSeconds: number | null;
  maximumAgeSeconds: number;
  requiredForWrites: boolean;
  fresh: boolean;
};

export type EconomyOperationsOverview = {
  schemaVersion: number;
  generatedAt: string;
  gameplayMode: string;
  world: {
    serverId: string;
    season: {
      seasonId: string;
      code: string;
      displayName: string;
      state: string;
      worldId: string | null;
      startsAt: string;
      endsAt: string;
      revision: number;
    } | null;
    save: Record<string, unknown>;
  };
  content: {
    versionId: string;
    versionNumber: number;
    businessDate: string;
    rulesVersion: string;
    contentHash: string;
    rotationSeed: string;
    hotspotZoneIds: string[];
  } | null;
  gate: {
    maintenance: boolean;
    reason: string;
    changedBy: string;
    changedAt: string;
    activeOperations: number;
    circuits: {
      purchase: { writesEnabled: boolean; updatedAt: string; source: string };
      resourceExchange: { writesEnabled: boolean; updatedAt: string; source: string };
    };
    blockers: { purchase: string[]; resourceExchange: string[] };
  };
  queues: {
    delivery: QueueState;
    settlement: QueueState;
    outbox: QueueState;
    uncertain: Record<string, number>;
  };
  backups: { game: BackupAge; economy: BackupAge };
  alerts: Array<{
    code: string;
    severity: string;
    active: boolean;
    autoCircuit: boolean;
    affects: string[];
    value: number | null;
    threshold: number | null;
    message: string;
  }>;
  accounts: EconomyAccount[];
  orders: EconomyOperationsOrder[];
  runs: EconomyOperationsRun[];
  rollover: {
    operationId: string;
    serverId: string;
    fromSeasonId: string;
    fromWorldId: string;
    targetWorldId: string;
    rulesVersion: string;
    currentStep: string;
    revision: number;
    newSeasonCommitted: boolean;
    createdAt: string;
    updatedAt: string;
    completedSteps: Array<Record<string, unknown>>;
  } | null;
  audit: EconomyOperationsAudit[];
};

export class EconomyOperationsApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string
  ) {
    super(message);
    this.name = "EconomyOperationsApiError";
  }
}

export function getEconomyOperationsOverview(
  refresh = false,
  signal?: AbortSignal
): Promise<EconomyOperationsOverview> {
  return requestJson<EconomyOperationsOverview>(
    `/api/v1/extraction/admin/operations/overview?limit=100&refresh=${refresh}`,
    { signal, cache: "no-store" },
    "经济运营状态读取失败"
  );
}

export function getOrderEvidence(orderId: string, signal?: AbortSignal): Promise<Record<string, unknown>> {
  return requestJson(
    `/api/v1/extraction/admin/operations/orders/${encodeURIComponent(orderId)}/evidence`,
    { signal, cache: "no-store" },
    "订单证据读取失败"
  );
}

export function getRunEvidence(runId: string, signal?: AbortSignal): Promise<Record<string, unknown>> {
  return requestJson(
    `/api/v1/extraction/admin/operations/runs/${encodeURIComponent(runId)}/evidence`,
    { signal, cache: "no-store" },
    "资源兑换证据读取失败"
  );
}

export function setEconomyCircuit(
  feature: "purchase" | "resource-exchange",
  writesEnabled: boolean,
  proof: AdminHighRiskProof,
  idempotencyKey: string
): Promise<Record<string, unknown>> {
  return highRiskJson(
    `/api/v1/extraction/admin/safety-gate/${feature}`,
    "PUT",
    { writesEnabled, reason: proof.reason },
    proof,
    idempotencyKey,
    "经济熔断器更新失败"
  );
}

export function setEconomyMaintenance(
  maintenance: boolean,
  proof: AdminHighRiskProof,
  idempotencyKey: string
): Promise<Record<string, unknown>> {
  return highRiskJson(
    "/api/v1/extraction/admin/rollover/maintenance",
    "POST",
    { maintenance, reason: proof.reason },
    proof,
    idempotencyKey,
    "维护状态更新失败"
  );
}

export function reconcileOrder(
  orderId: string,
  resolution: "delivered" | "refund",
  confirmation: string,
  proof: AdminHighRiskProof,
  idempotencyKey: string
): Promise<Record<string, unknown>> {
  return highRiskJson(
    `/api/v1/extraction/admin/orders/${encodeURIComponent(orderId)}/reconcile`,
    "POST",
    { resolution, reason: proof.reason, confirmation },
    proof,
    idempotencyKey,
    "订单对账失败"
  );
}

export function reconcileRun(
  runId: string,
  resolution: "settled" | "failed",
  confirmation: string,
  proof: AdminHighRiskProof,
  idempotencyKey: string
): Promise<Record<string, unknown>> {
  return highRiskJson(
    `/api/v1/extraction/admin/runs/${encodeURIComponent(runId)}/reconcile`,
    "POST",
    { resolution, reason: proof.reason, confirmation },
    proof,
    idempotencyKey,
    "资源兑换对账失败"
  );
}

export function adjustWallet(
  input: {
    accountId: string;
    currency: "merchantCoin" | "weeklyTicket";
    delta: number;
  },
  proof: AdminHighRiskProof,
  idempotencyKey: string
): Promise<Record<string, unknown>> {
  return highRiskJson(
    "/api/v1/extraction/admin/wallet-adjustments",
    "POST",
    { ...input, reason: proof.reason },
    proof,
    idempotencyKey,
    "钱包调账失败"
  );
}

async function highRiskJson<T>(
  url: string,
  method: string,
  body: unknown,
  proof: AdminHighRiskProof,
  idempotencyKey: string,
  fallback: string
): Promise<T> {
  const response = await adminHighRiskFetch(url, {
    method,
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": idempotencyKey
    },
    body: JSON.stringify(body)
  }, proof);
  return readJson<T>(response, fallback);
}

async function requestJson<T>(
  url: string,
  init: RequestInit,
  fallback: string
): Promise<T> {
  return readJson<T>(await adminFetch(url, init), fallback);
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  const payload = await response.json().catch(() => null) as unknown;
  if (!response.ok) {
    const problem = isRecord(payload) ? payload : {};
    throw new EconomyOperationsApiError(
      typeof problem.message === "string" ? problem.message : fallback,
      response.status,
      typeof problem.code === "string" ? problem.code : "ECONOMY_OPERATIONS_REQUEST_FAILED"
    );
  }
  return payload as T;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
