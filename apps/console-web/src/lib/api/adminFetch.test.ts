import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ADMIN_API_KEY_HEADER,
  adminFetch,
  getAdminApiKey,
  getAdminAuthState,
  setAdminApiKey,
  signOutAdmin,
  subscribeAdminAuth,
  type AdminAuthState
} from "./adminFetch";

const API_KEY = "test-admin-key-12345678901234567890";

class MemorySessionStorage implements Storage {
  private readonly values = new Map<string, string>();

  get length() { return this.values.size; }
  clear() { this.values.clear(); }
  getItem(key: string) { return this.values.get(key) ?? null; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  removeItem(key: string) { this.values.delete(key); }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

describe("adminFetch credential boundary", () => {
  beforeEach(() => {
    vi.stubGlobal("sessionStorage", new MemorySessionStorage());
    vi.stubGlobal("location", { origin: "http://console.local" });
    signOutAdmin("test reset");
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("injects the current-tab API key into same-origin management requests", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);
    setAdminApiKey(API_KEY);

    await adminFetch("/api/v1/servers/local/capabilities", {
      headers: { Accept: "application/json" }
    });
    await adminFetch("http://console.local/api/v1/admin/session");

    expect(fetchMock).toHaveBeenCalledTimes(2);
    for (const [, init] of fetchMock.mock.calls) {
      const headers = new Headers((init as RequestInit | undefined)?.headers);
      expect(headers.get(ADMIN_API_KEY_HEADER)).toBe(API_KEY);
    }
  });

  it("does not leak the API key to player-portal or cross-origin requests", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 401 }));
    vi.stubGlobal("fetch", fetchMock);
    setAdminApiKey(API_KEY);

    await adminFetch("/api/v1/player/session");
    await adminFetch("https://telemetry.example/api/v1/events", {
      headers: { "X-Trace": "allowed" }
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
    for (const [, init] of fetchMock.mock.calls) {
      const headers = new Headers((init as RequestInit | undefined)?.headers);
      expect(headers.has(ADMIN_API_KEY_HEADER)).toBe(false);
    }
    expect(getAdminApiKey()).toBe(API_KEY);
  });

  it("clears the credential and notifies the UI when an admin request returns 401", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 401 }));
    vi.stubGlobal("fetch", fetchMock);
    setAdminApiKey(API_KEY);
    const observed: AdminAuthState[] = [];
    const unsubscribe = subscribeAdminAuth((state) => observed.push(state));

    await adminFetch("/api/v1/admin/session");
    unsubscribe();

    expect(getAdminApiKey()).toBeNull();
    expect(getAdminAuthState()).toMatchObject({
      status: "signed-out",
      session: null
    });
    expect(getAdminAuthState().message).toContain("重新输入 API Key");
    expect(observed.at(-1)?.status).toBe("signed-out");
  });
});
