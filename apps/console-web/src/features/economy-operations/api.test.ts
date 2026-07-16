import { afterEach, describe, expect, it, vi } from "vitest";
import {
  EconomyOperationsApiError,
  adjustWallet,
  getEconomyOperationsOverview,
  reconcileRun,
  setEconomyCircuit
} from "./api";
import {
  actionDescriptor,
  expectedConfirmation,
  isPendingTargetSatisfied,
  requiredAuthorization,
  type RiskAction
} from "./EconomyOperationsWorkbench";
import type { EconomyOperationsOverview } from "./api";

describe("economy operations workbench API", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("requests one authoritative global overview with an explicit refresh flag", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ schemaVersion: 1 }));
    vi.stubGlobal("fetch", fetchMock);

    await getEconomyOperationsOverview(true);

    expect(fetchMock).toHaveBeenCalledOnce();
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/extraction/admin/operations/overview?limit=100&refresh=true");
    expect(init.cache).toBe("no-store");
  });

  it("sends high-risk proof and a stable idempotency key when toggling a circuit", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ writesEnabled: false }));
    vi.stubGlobal("fetch", fetchMock);

    await setEconomyCircuit(
      "resource-exchange",
      false,
      { reason: "freeze after settlement alert", totp: "123456" },
      "circuit-resource-0001"
    );

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(url).toBe("/api/v1/extraction/admin/safety-gate/resource-exchange");
    expect(init.method).toBe("PUT");
    expect(headers.get("Idempotency-Key")).toBe("circuit-resource-0001");
    expect(headers.get("X-Pal-Admin-Totp")).toBe("123456");
    expect(headers.get("X-Pal-Admin-Reason")).toBe("freeze after settlement alert");
    expect(JSON.parse(String(init.body))).toEqual({
      writesEnabled: false,
      reason: "freeze after settlement alert"
    });
  });

  it("keeps reconciliation confirmation and idempotency bound to one run outcome", async () => {
    const runId = "5f319920-a676-4b30-8883-54f821b673e7";
    const confirmation = "RUN-5f319920a6764b30888354f821b673e7-SETTLED";
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ runId, state: "Settled" }));
    vi.stubGlobal("fetch", fetchMock);

    await reconcileRun(
      runId,
      "settled",
      confirmation,
      { reason: "native receipt independently verified", totp: "654321" },
      "run-settlement-0001"
    );

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(headers.get("Idempotency-Key")).toBe("run-settlement-0001");
    expect(JSON.parse(String(init.body))).toEqual({
      resolution: "settled",
      reason: "native receipt independently verified",
      confirmation
    });
  });

  it("adjusts a wallet by opaque account id without exposing a platform subject to the browser", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ created: true }));
    vi.stubGlobal("fetch", fetchMock);

    await adjustWallet({
      accountId: "572cfe70-64f4-4ef0-a89d-fb7c8cfbe5b7",
      currency: "merchantCoin",
      delta: 25
    }, { reason: "support ticket 42", totp: "123456" }, "wallet-adjustment-0001");

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(String(init.body))).toEqual({
      accountId: "572cfe70-64f4-4ef0-a89d-fb7c8cfbe5b7",
      currency: "merchantCoin",
      delta: 25,
      reason: "support ticket 42"
    });
    expect(String(init.body)).not.toContain("userId");
  });

  it("preserves stable API error codes", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      code: "RECONCILIATION_MAINTENANCE_REQUIRED",
      message: "maintenance required"
    }, 409)));

    await expect(reconcileRun(
      "5f319920-a676-4b30-8883-54f821b673e7",
      "failed",
      "RUN-5f319920a6764b30888354f821b673e7-FAILED",
      { reason: "verified no mutation", totp: "123456" },
      "run-failed-0001"
    )).rejects.toMatchObject({
      status: 409,
      code: "RECONCILIATION_MAINTENANCE_REQUIRED"
    } satisfies Partial<EconomyOperationsApiError>);
  });

  it("derives exact confirmation phrases and payload-bound operation keys", () => {
    const action: RiskAction = {
      kind: "run",
      resolution: "settled",
      run: {
        runId: "5f319920-a676-4b30-8883-54f821b673e7",
        accountId: "account",
        seasonId: "season",
        account: null,
        zoneId: "zone",
        zoneName: "Zone",
        state: "Uncertain",
        itemCount: 3,
        totalValue: 40,
        revision: 4,
        attemptCount: 1,
        errorCode: "NATIVE_RESULT_UNCERTAIN",
        errorMessage: null,
        contentVersionId: null,
        contentHash: null,
        quoteSnapshotHash: "hash",
        settlementRequestHash: "request",
        quotedAt: "2026-07-16T00:00:00Z",
        expiresAt: "2026-07-16T00:00:30Z",
        updatedAt: "2026-07-16T00:00:20Z",
        settledAt: null,
        requiresReconciliation: true
      }
    };

    expect(expectedConfirmation(action)).toBe(
      "RUN-5f319920a6764b30888354f821b673e7-SETTLED"
    );
    expect(actionDescriptor(action).key).toBe(
      "run:5f319920-a676-4b30-8883-54f821b673e7:settled"
    );
    expect(requiredAuthorization(action)).toEqual({
      role: "EconomyAdmin / Owner",
      reasonRequired: true,
      exactConfirmationRequired: true,
      totpRequired: true
    });
  });

  it("reconciles a response-loss journal only against authoritative target state", () => {
    const overview = {
      gate: {
        maintenance: true,
        circuits: {
          purchase: { writesEnabled: false },
          resourceExchange: { writesEnabled: true }
        }
      },
      orders: [],
      runs: []
    } as unknown as EconomyOperationsOverview;

    expect(isPendingTargetSatisfied(
      { kind: "maintenance", maintenance: true },
      overview
    )).toBe(true);
    expect(isPendingTargetSatisfied(
      { kind: "circuit", feature: "purchase", writesEnabled: false },
      overview
    )).toBe(true);
    expect(isPendingTargetSatisfied(
      { kind: "circuit", feature: "resource-exchange", writesEnabled: false },
      overview
    )).toBe(false);
  });
});

function jsonResponse(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}
