const ADMIN_API_KEY_STORAGE_KEY = "pal-control.admin-api-key";

export const ADMIN_API_KEY_HEADER = "X-Pal-Admin-Key";
export const ADMIN_TOTP_HEADER = "X-Pal-Admin-Totp";
export const ADMIN_REASON_HEADER = "X-Pal-Admin-Reason";

export type AdminSession = {
  subject: string;
  roles: string[];
  authenticationMethod: string | null;
};

export type AdminAuthState =
  | { status: "checking"; session: null; message: string | null }
  | { status: "authenticated"; session: AdminSession; message: null }
  | { status: "signed-out" | "unavailable"; session: null; message: string };

export type AdminHighRiskProof = {
  totp: string;
  reason: string;
};

type AuthStateListener = (state: AdminAuthState) => void;

let authState: AdminAuthState = {
  status: "checking",
  session: null,
  message: null
};
const listeners = new Set<AuthStateListener>();

export function getAdminAuthState(): AdminAuthState {
  return authState;
}

export function subscribeAdminAuth(listener: AuthStateListener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function getAdminApiKey(): string | null {
  try {
    return globalThis.sessionStorage?.getItem(ADMIN_API_KEY_STORAGE_KEY) ?? null;
  } catch {
    return null;
  }
}

export function setAdminApiKey(apiKey: string): void {
  const value = apiKey.trim();
  if (value.length < 24 || value.length > 512) {
    throw new Error("API Key 长度必须在 24 到 512 个字符之间。");
  }
  try {
    globalThis.sessionStorage.setItem(ADMIN_API_KEY_STORAGE_KEY, value);
  } catch {
    throw new Error("浏览器阻止了会话存储，无法在当前标签页保存管理员凭据。");
  }
  updateAuthState({ status: "checking", session: null, message: null });
}

export function signOutAdmin(message = "已退出管理员会话，请重新输入 API Key。"): void {
  clearStoredApiKey();
  updateAuthState({ status: "signed-out", session: null, message });
}

export async function authenticateAdmin(apiKey: string, signal?: AbortSignal): Promise<AdminAuthState> {
  setAdminApiKey(apiKey);
  return probeAdminSession(signal);
}

export async function probeAdminSession(signal?: AbortSignal): Promise<AdminAuthState> {
  updateAuthState({ status: "checking", session: null, message: null });
  try {
    const response = await adminFetch("/api/v1/admin/session", {
      signal,
      cache: "no-store"
    });
    if (!response.ok) {
      if (response.status !== 401) {
        updateAuthState({
          status: "unavailable",
          session: null,
          message: `管理员会话探测失败（HTTP ${response.status}）。`
        });
      }
      return authState;
    }

    const payload = await response.json() as unknown;
    const session = parseAdminSession(payload);
    if (!session) {
      updateAuthState({
        status: "unavailable",
        session: null,
        message: "管理员会话响应格式无效。"
      });
      return authState;
    }
    updateAuthState({ status: "authenticated", session, message: null });
  } catch (error) {
    if (signal?.aborted || (error instanceof DOMException && error.name === "AbortError")) {
      return authState;
    }
    updateAuthState({
      status: "unavailable",
      session: null,
      message: "无法连接本机控制 API，请确认服务已经启动。"
    });
  }
  return authState;
}

/**
 * Fetches a Console management endpoint. The API key is attached only to a
 * same-origin /api/v1 management route; player-portal and cross-origin targets
 * deliberately receive the original request unchanged.
 */
export async function adminFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  if (!isAdminApiRequest(input)) {
    return globalThis.fetch(input, init);
  }

  const headers = new Headers(input instanceof Request ? input.headers : undefined);
  if (init?.headers) {
    new Headers(init.headers).forEach((value, name) => headers.set(name, value));
  }
  const apiKey = getAdminApiKey();
  if (apiKey) {
    headers.set(ADMIN_API_KEY_HEADER, apiKey);
  } else {
    headers.delete(ADMIN_API_KEY_HEADER);
  }

  const response = await globalThis.fetch(input, { ...init, headers });
  if (response.status === 401) {
    signOutAdmin("管理员凭据无效或已经失效，请重新输入 API Key。");
  }
  return response;
}

/**
 * Reusable request helper for endpoints protected by a high-risk policy.
 * Feature pages should collect the one-time TOTP and a concrete audit reason,
 * then pass them here without persisting either value.
 */
export function adminHighRiskFetch(
  input: RequestInfo | URL,
  init: RequestInit,
  proof: AdminHighRiskProof
): Promise<Response> {
  const headers = createAdminHighRiskHeaders(proof, init.headers);
  return adminFetch(input, { ...init, headers });
}

export function createAdminHighRiskHeaders(
  proof: AdminHighRiskProof,
  existing?: HeadersInit
): Headers {
  const totp = proof.totp.trim();
  const reason = proof.reason.trim();
  if (!/^\d{6}$/.test(totp)) {
    throw new Error("高风险操作需要 6 位 TOTP 动态验证码。");
  }
  if (reason.length < 3 || reason.length > 512 || /[\u0000-\u001f\u007f]/.test(reason)) {
    throw new Error("操作原因必须是 3 到 512 个不含控制字符的文本。");
  }
  const headers = new Headers(existing);
  headers.set(ADMIN_TOTP_HEADER, totp);
  headers.set(ADMIN_REASON_HEADER, reason);
  return headers;
}

export function isAdminApiRequest(input: RequestInfo | URL, origin = browserOrigin()): boolean {
  const rawUrl = input instanceof Request ? input.url : input.toString();
  const isNetworkPath = rawUrl.startsWith("//");
  const isAbsolute = isNetworkPath || /^[a-zA-Z][a-zA-Z\d+.-]*:/.test(rawUrl);
  if (isAbsolute && !origin) {
    return false;
  }

  let target: URL;
  try {
    target = new URL(rawUrl, origin ?? "http://pal-control.local");
  } catch {
    return false;
  }
  if (origin && target.origin !== origin) {
    return false;
  }

  const path = target.pathname;
  const isApiV1 = path === "/api/v1" || path.startsWith("/api/v1/");
  const isPlayerPortal = path === "/api/v1/player" || path.startsWith("/api/v1/player/");
  return isApiV1 && !isPlayerPortal;
}

function browserOrigin(): string | null {
  try {
    const origin = globalThis.location?.origin;
    return origin && origin !== "null" ? origin : null;
  } catch {
    return null;
  }
}

function clearStoredApiKey(): void {
  try {
    globalThis.sessionStorage?.removeItem(ADMIN_API_KEY_STORAGE_KEY);
  } catch {
    // The visible auth state still changes even when storage is unavailable.
  }
}

function updateAuthState(next: AdminAuthState): void {
  authState = next;
  listeners.forEach((listener) => listener(authState));
}

function parseAdminSession(value: unknown): AdminSession | null {
  if (!isRecord(value) || typeof value.subject !== "string" || value.subject.length === 0 ||
      !Array.isArray(value.roles) || !value.roles.every((role) => typeof role === "string")) {
    return null;
  }
  return {
    subject: value.subject,
    roles: [...new Set(value.roles)].sort(),
    authenticationMethod: typeof value.authenticationMethod === "string"
      ? value.authenticationMethod
      : null
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
