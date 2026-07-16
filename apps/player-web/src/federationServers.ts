export type FederationAvailability = "available" | "unavailable" | "incompatible";

export type FederationSeason = {
  code: string;
  displayName: string;
  startsAt: string;
  endsAt: string;
  state: "draft" | "scheduled" | "active" | "closed" | "archived";
};

export type FederationBalances = {
  marketCoin: number;
  seasonVoucher: number;
};

export type FederationCompatibility = {
  combinationId: string;
  matrixVersion: string;
  matrixSha256: string;
  status: "stable" | "experimental" | "quarantined";
  gameVersion: string;
  steamBuild: string;
  palDefenderVersion: string;
  ue4ssCommit: string;
  nativeProtocolVersion: string;
  nativeModVersion: string;
  bridgeAvailability: "available" | "unavailable" | "unknown";
  capabilities: string[];
  verifiedAt: string;
};

export type FederationServer = {
  serverId: string;
  displayName: string;
  portalUrl: string;
  local: boolean;
  availability: FederationAvailability;
  accountExists: boolean | null;
  accountDisplayName: string | null;
  season: FederationSeason | null;
  balances: FederationBalances | null;
  balancesAvailable: boolean;
  compatibility: FederationCompatibility | null;
  switchAvailable: boolean;
  errorCode: string | null;
  observedAt: string;
};

export type FederationOverview = {
  localServerId: string;
  matrixVersion: string;
  matrixSha256: string;
  servers: FederationServer[];
  observedAt: string;
};

export type FederationBalanceView = {
  primary: string;
  secondary: string;
  available: boolean;
};

export class FederationServersError extends Error {
  readonly status: number;
  readonly code?: string;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = "FederationServersError";
    this.status = status;
    this.code = code;
  }
}

const endpoint = "/api/v1/player/me/federation";
const forbiddenIdentityKeys = new Set([
  "accountid",
  "externaluserid",
  "nodekey",
  "playeruid",
  "steamid",
  "subjecttoken",
  "userid"
]);

export async function loadFederationServers(
  signal?: AbortSignal
): Promise<FederationOverview> {
  const response = await fetch(endpoint, {
    method: "GET",
    headers: new Headers({ Accept: "application/json" }),
    credentials: "include",
    cache: "no-store",
    signal
  });
  const body = await response.json().catch(() => null) as unknown;
  if (!response.ok) {
    const error = isObject(body) ? body : null;
    throw new FederationServersError(
      readString(error, "message") ?? `服务器注册读取失败（HTTP ${response.status}）`,
      response.status,
      readString(error, "code") ?? undefined
    );
  }
  if (!isFederationOverview(body) || containsForbiddenIdentityField(body)) {
    throw new FederationServersError(
      "服务器注册响应格式无效，已停止展示。",
      502,
      "FEDERATION_RESPONSE_INVALID"
    );
  }
  return body;
}

export function federationAvailabilityLabel(
  availability: FederationAvailability
): string {
  switch (availability) {
    case "available": return "可读取";
    case "incompatible": return "版本不兼容";
    case "unavailable": return "节点不可用";
  }
}

export function federationCompatibilityLabel(
  compatibility: FederationCompatibility | null
): string {
  if (compatibility === null) return "兼容状态未知";
  switch (compatibility.status) {
    case "stable": return "稳定兼容";
    case "experimental": return "实验兼容";
    case "quarantined": return "已隔离";
  }
}

export function federationBalanceView(server: FederationServer): FederationBalanceView {
  if (server.availability !== "available") {
    return {
      primary: "余额不可用",
      secondary: "节点失败或不兼容，未将失败结果解释为 0。",
      available: false
    };
  }
  if (server.accountExists === false) {
    return {
      primary: "本服尚无账户",
      secondary: "进入该服务器并完成本服登录后才会建立本地账户。",
      available: false
    };
  }
  if (!server.balancesAvailable || server.balances === null) {
    return {
      primary: "余额暂不可读",
      secondary: "账户存在，但本服当前周档或钱包不可用。",
      available: false
    };
  }
  const number = new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 0 });
  return {
    primary: `${number.format(server.balances.marketCoin)} 商域币`,
    secondary: `${number.format(server.balances.seasonVoucher)} 周战备券`,
    available: true
  };
}

export function federationPortalHref(server: FederationServer): string | null {
  if (server.local || !server.switchAvailable || server.availability !== "available" ||
      server.portalUrl !== server.portalUrl.trim()) {
    return null;
  }
  try {
    const url = new URL(server.portalUrl);
    if ((url.protocol !== "https:" && url.protocol !== "http:") ||
        url.username !== "" || url.password !== "" ||
        url.search !== "" || url.hash !== "") {
      return null;
    }
    // Return the allowlisted server value exactly. Never append a player id,
    // session, federation subject, or any other query parameter.
    return server.portalUrl;
  } catch {
    return null;
  }
}

function isFederationOverview(value: unknown): value is FederationOverview {
  if (!isObject(value) ||
      !isSafeText(value.localServerId, 64) ||
      !isSafeText(value.matrixVersion, 32) ||
      typeof value.matrixSha256 !== "string" ||
      !/^[a-f0-9]{64}$/.test(value.matrixSha256) ||
      !isTimestamp(value.observedAt) ||
      !Array.isArray(value.servers) ||
      value.servers.length < 1 || value.servers.length > 16) {
    return false;
  }
  return value.servers.every(isFederationServer);
}

function isFederationServer(value: unknown): value is FederationServer {
  if (!isObject(value) ||
      !isSafeText(value.serverId, 64) ||
      !isSafeText(value.displayName, 80) ||
      !isSafeText(value.portalUrl, 2_048) ||
      typeof value.local !== "boolean" ||
      !["available", "unavailable", "incompatible"].includes(String(value.availability)) ||
      ![true, false, null].includes(value.accountExists as boolean | null) ||
      !(value.accountDisplayName === null || isSafeText(value.accountDisplayName, 160)) ||
      typeof value.balancesAvailable !== "boolean" ||
      typeof value.switchAvailable !== "boolean" ||
      !(value.errorCode === null || isSafeText(value.errorCode, 128)) ||
      !isTimestamp(value.observedAt)) {
    return false;
  }
  if (!(value.season === null || isFederationSeason(value.season)) ||
      !(value.balances === null || isFederationBalances(value.balances)) ||
      !(value.compatibility === null || isFederationCompatibility(value.compatibility))) {
    return false;
  }
  return value.balancesAvailable === (value.balances !== null) &&
    !(value.balancesAvailable && value.accountExists !== true);
}

function isFederationSeason(value: unknown): value is FederationSeason {
  return isObject(value) &&
    isSafeText(value.code, 128) &&
    isSafeText(value.displayName, 160) &&
    isTimestamp(value.startsAt) &&
    isTimestamp(value.endsAt) &&
    ["draft", "scheduled", "active", "closed", "archived"].includes(String(value.state));
}

function isFederationBalances(value: unknown): value is FederationBalances {
  return isObject(value) &&
    isSafeBalance(value.marketCoin) &&
    isSafeBalance(value.seasonVoucher);
}

function isFederationCompatibility(value: unknown): value is FederationCompatibility {
  return isObject(value) &&
    isSafeText(value.combinationId, 128) &&
    isSafeText(value.matrixVersion, 32) &&
    typeof value.matrixSha256 === "string" && /^[a-f0-9]{64}$/.test(value.matrixSha256) &&
    ["stable", "experimental", "quarantined"].includes(String(value.status)) &&
    isSafeText(value.gameVersion, 64) &&
    isSafeText(value.steamBuild, 32) &&
    isSafeText(value.palDefenderVersion, 64) &&
    isSafeText(value.ue4ssCommit, 64) &&
    isSafeText(value.nativeProtocolVersion, 32) &&
    isSafeText(value.nativeModVersion, 64) &&
    ["available", "unavailable", "unknown"].includes(String(value.bridgeAvailability)) &&
    Array.isArray(value.capabilities) && value.capabilities.length <= 64 &&
    value.capabilities.every(item => isSafeText(item, 96)) &&
    isTimestamp(value.verifiedAt);
}

function containsForbiddenIdentityField(value: unknown): boolean {
  if (Array.isArray(value)) return value.some(containsForbiddenIdentityField);
  if (!isObject(value)) return false;
  return Object.entries(value).some(([key, item]) =>
    forbiddenIdentityKeys.has(normalizeIdentityKey(key)) || containsForbiddenIdentityField(item));
}

function normalizeIdentityKey(key: string): string {
  return key.replace(/[-_\s]/g, "").toLowerCase();
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isSafeText(value: unknown, maximumLength: number): value is string {
  return typeof value === "string" && value.trim().length > 0 &&
    value.length <= maximumLength && !/[\u0000-\u001f\u007f]/.test(value);
}

function isTimestamp(value: unknown): value is string {
  return typeof value === "string" && value.length <= 64 &&
    Number.isFinite(Date.parse(value));
}

function isSafeBalance(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function readString(value: Record<string, unknown> | null, key: string): string | null {
  return value !== null && typeof value[key] === "string" ? value[key] : null;
}
