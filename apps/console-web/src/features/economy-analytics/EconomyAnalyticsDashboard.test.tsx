import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { EconomyAnalyticsDashboard, formatCount, formatRate } from "./EconomyAnalyticsDashboard";
import {
  EconomyAnalyticsApiError,
  analyticsUrl,
  getEconomyAnalytics
} from "./api";

describe("economy analytics dashboard", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it("builds a bounded authoritative slice without a client event payload", () => {
    const url = analyticsUrl({
      serverId: "local",
      from: "2026-07-01",
      to: "2026-07-07",
      dateBasis: "business",
      seasonId: "10000000-0000-0000-0000-000000000001",
      contentVersionId: "20000000-0000-0000-0000-000000000002",
      limit: 25,
      cursor: "25"
    });
    expect(url).toBe(
      "/api/v1/economy/analytics?serverId=local&from=2026-07-01&to=2026-07-07&dateBasis=business&limit=25&seasonId=10000000-0000-0000-0000-000000000001&contentVersionId=20000000-0000-0000-0000-000000000002&cursor=25"
    );
    expect(url).not.toContain("accountId");
    expect(url).not.toContain("playerUid");
    expect(url).not.toContain("eventCount");
  });

  it("uses a Viewer GET with no-store and preserves stable API errors", async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ schemaVersion: 1 }), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      }))
      .mockResolvedValueOnce(new Response(JSON.stringify({
        code: "ANALYTICS_EVENT_PAYLOAD_INVALID",
        message: "source invalid"
      }), {
        status: 409,
        headers: { "Content-Type": "application/json" }
      }));
    vi.stubGlobal("fetch", fetchMock);
    const filters = {
      serverId: "local",
      from: "2026-07-01",
      to: "2026-07-07",
      dateBasis: "utc" as const
    };
    await getEconomyAnalytics(filters);
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.cache).toBe("no-store");
    expect(init.method).toBeUndefined();
    await expect(getEconomyAnalytics(filters)).rejects.toMatchObject({
      status: 409,
      code: "ANALYTICS_EVENT_PAYLOAD_INVALID"
    } satisfies Partial<EconomyAnalyticsApiError>);
  });

  it("renders keyboard-labelled filters, live status and semantic analytics sections", () => {
    const html = renderToStaticMarkup(<EconomyAnalyticsDashboard />);
    expect(html).toContain("运营分析筛选");
    expect(html).toContain("开始日期");
    expect(html).toContain("日期口径");
    expect(html).toContain("aria-live=\"polite\"");
    expect(html).toContain("应用筛选");
  });

  it("never turns suppressed or incomplete denominators into a percentage", () => {
    expect(formatCount({ value: null, suppressed: true })).toBe("少样本隐藏");
    expect(formatRate({
      numerator: 3,
      denominator: 4,
      basisPoints: null,
      suppressed: true,
      denominatorComplete: true
    })).toBe("少样本隐藏");
    expect(formatRate({
      numerator: 10,
      denominator: 8,
      basisPoints: null,
      suppressed: false,
      denominatorComplete: false
    })).toBe("分母不完整");
    expect(formatRate({
      numerator: 60,
      denominator: 120,
      basisPoints: 5_000,
      suppressed: false,
      denominatorComplete: true
    })).toBe("50.00%");
  });
});
