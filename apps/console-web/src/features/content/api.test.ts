import { afterEach, describe, expect, it, vi } from "vitest";
import {
  ContentApiError,
  PUBLISH_CONFIRMATION,
  ROLLBACK_CONFIRMATION,
  contentErrorMessage,
  getCurrentContent,
  publishContentDraft,
  rollbackContentVersion,
  updateContentDraft
} from "./api";

describe("economy content management API", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("sends the optimistic draft revision in If-Match", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      draftId: "draft-1",
      revision: 8,
      definition: { schemaVersion: 1 }
    }));
    vi.stubGlobal("fetch", fetchMock);

    await updateContentDraft("local", "draft-1", 7, { schemaVersion: 1 });

    expect(fetchMock).toHaveBeenCalledOnce();
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(url).toBe("/api/v1/servers/local/economy-content/drafts/draft-1");
    expect(init.method).toBe("PUT");
    expect(headers.get("If-Match")).toBe("7");
    expect(JSON.parse(String(init.body))).toEqual({ definition: { schemaVersion: 1 } });
  });

  it("sends publication confirmation, idempotency, revision and high-risk proof", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      version: { versionNumber: 4 },
      pointer: { versionNumber: 4 },
      versionCreated: true,
      pointerChanged: true,
      replayed: false
    }));
    vi.stubGlobal("fetch", fetchMock);

    await publishContentDraft("weekly", {
      draftId: "3b7c6fe2-98d8-4939-9b6f-a1f5fe4ad51e",
      revision: 12,
      businessDate: "2026-07-15",
      reason: "publish weekly allowlist revision",
      confirmation: PUBLISH_CONFIRMATION,
      totp: "123456",
      idempotencyKey: "publish-request-001"
    });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(url).toContain("/servers/weekly/economy-content/drafts/3b7c6fe2-98d8-4939-9b6f-a1f5fe4ad51e/publish");
    expect(headers.get("If-Match")).toBe("12");
    expect(headers.get("Idempotency-Key")).toBe("publish-request-001");
    expect(headers.get("X-Pal-Admin-Totp")).toBe("123456");
    expect(headers.get("X-Pal-Admin-Reason")).toBe("publish weekly allowlist revision");
    expect(JSON.parse(String(init.body))).toEqual({
      businessDate: "2026-07-15",
      reason: "publish weekly allowlist revision",
      confirmation: PUBLISH_CONFIRMATION
    });
  });

  it("treats an explicit not-published response as an empty current pointer", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      code: "CONTENT_NOT_PUBLISHED",
      message: "No version"
    }, 404)));

    await expect(getCurrentContent("local")).resolves.toBeNull();
  });

  it("pins rollback to the expected current version and keeps its idempotency key", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({
      pointer: { versionId: "version-2" },
      previousVersionId: "version-3",
      pointerChanged: true,
      replayed: false
    }));
    vi.stubGlobal("fetch", fetchMock);

    await rollbackContentVersion("local", {
      targetVersionId: "version-2",
      expectedCurrentVersionId: "version-3",
      reason: "rollback broken weekly content",
      confirmation: ROLLBACK_CONFIRMATION,
      totp: "654321",
      idempotencyKey: "rollback-request-001"
    });

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = new Headers(init.headers);
    expect(headers.get("Idempotency-Key")).toBe("rollback-request-001");
    expect(headers.get("X-Pal-Admin-Totp")).toBe("654321");
    expect(JSON.parse(String(init.body))).toEqual({
      targetVersionId: "version-2",
      expectedCurrentVersionId: "version-3",
      reason: "rollback broken weekly content",
      confirmation: ROLLBACK_CONFIRMATION
    });
  });

  it("preserves the API error code and provides revision recovery guidance", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse({
      code: "CONTENT_DRAFT_REVISION_CONFLICT",
      message: "Changed concurrently"
    }, 409)));

    let observed: unknown;
    try {
      await updateContentDraft("local", "draft-1", 2, { schemaVersion: 1 });
    } catch (error) {
      observed = error;
    }

    expect(observed).toBeInstanceOf(ContentApiError);
    expect(observed).toMatchObject({ status: 409, code: "CONTENT_DRAFT_REVISION_CONFLICT" });
    expect(contentErrorMessage(observed, "fallback")).toContain("保留当前本地 JSON");
  });
});

function jsonResponse(payload: unknown, status = 200) {
  return new Response(JSON.stringify(payload), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}
