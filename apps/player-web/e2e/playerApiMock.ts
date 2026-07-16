import type { Page, Route } from "@playwright/test";

type MockOptions = {
  authenticated?: boolean;
  orderFailure?: boolean;
  selectionResponseLoss?: boolean;
  positionUnavailable?: boolean;
  closedZoneInRange?: boolean;
  openZoneInRange?: boolean;
  teamMode?: "none" | "owner" | "member";
};

const session = {
  authenticated: true,
  userId: "演示会话（已脱敏）",
  displayName: "键盘玩家",
  csrfToken: "csrf-e2e",
  expiresAt: "2099-07-17T00:00:00Z"
};

const product = {
  productId: "22222222-2222-2222-2222-222222222222",
  sku: "STARTER",
  name: "新手补给",
  description: "用于真实键盘与移动端验收的基础物资。",
  category: "补给",
  tags: ["新手"],
  price: { currency: "merchantCoin", amount: 10 },
  deliverySummary: "10 个帕鲁球",
  stockRemaining: 5,
  personalLimitRemaining: 5,
  serverStockRemaining: null,
  purchaseLimit: 5,
  globalStock: null,
  purchased: 0,
  enabled: true,
  featured: true,
  featuredRank: 1,
  contentVersionId: "11111111-1111-1111-1111-111111111111",
  contentHash: "a".repeat(64),
  iconKey: "supply",
  rarity: "Uncommon",
  usage: "用于新世界早期探索、捕捉与基础恢复。",
  presentationSource: "content"
};

export async function mockPlayerApi(page: Page, options: MockOptions = {}) {
  const authenticated = options.authenticated ?? true;
  const selectionRequests: Array<{
    idempotencyKey: string | null;
    body: { sourceRevision: number; items: Array<{ itemId: string; quantity: number }> };
  }> = [];
  const settlementRequests: Array<{ runId: string; idempotencyKey: string | null }> = [];
  const teamRequests: Array<{
    path: string;
    method: string;
    csrfToken: string | null;
    idempotencyKey: string | null;
    body: Record<string, unknown>;
  }> = [];
  let teamMode = options.teamMode ?? "owner";
  let extractionZonesUnavailable = false;
  let selectedQuote: Record<string, unknown> | null = null;
  let selectionResponseLost = false;
  let notifications = [{
    notificationId: "99999999-9999-9999-9999-999999999999",
    schemaVersion: "1",
    seasonId: "88888888-8888-8888-8888-888888888888",
    sourceType: "reconciliation",
    sourceState: "uncertain",
    severity: "warning",
    title: "资源兑换结果待核对",
    message: "物品扣除或入账结果无法安全确认。请勿再次兑换或重复提交，等待管理员核对。",
    occurredAt: "2026-07-16T04:00:00Z",
    updatedAt: "2026-07-16T04:00:00Z",
    readAt: null as string | null,
    gameState: "blocked",
    safetyAction: "do-not-repeat-contact-support"
  }];
  await page.route("**/api/v1/player/**", async (route) => {
    const request = route.request();
    const path = new URL(request.url()).pathname.replace("/api/v1/player", "");
    const method = request.method();

    if (path === "/auth/session" && method === "GET") {
      return json(route, authenticated
        ? session
        : { authenticated: false, userId: null, displayName: null, csrfToken: null, expiresAt: null });
    }
    if (path === "/auth/mode" && method === "GET") {
      return json(route, {
        authenticationMode: "trustedGameCode",
        steamOpenIdRequired: false,
        pendingPlatformIdentity: false,
        trustedGameCodeFallback: true
      });
    }
    if (path === "/auth/request-code" && method === "POST") {
      return json(route, {
        challengeId: "challenge-e2e",
        expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(),
        retryAfterSeconds: 30
      });
    }
    if (path === "/auth/verify" && method === "POST") {
      return json(route, { code: "INVALID_AUTH_CODE", message: "验证码不正确，请重新查看游戏内通知。" }, 401);
    }
    if (path === "/auth/logout" && method === "POST") {
      return route.fulfill({ status: 204 });
    }
    if (path === "/me/overview" && method === "GET") {
      return json(route, {
        userId: session.userId,
        displayName: session.displayName,
        online: true,
        gameplayMode: "weekly-resource-economy",
        season: {
          seasonId: "33333333-3333-3333-3333-333333333333",
          name: "第 1 周资源世界",
          state: "active",
          startsAt: "2026-07-14T00:00:00Z",
          endsAt: "2026-07-21T00:00:00Z",
          nextShopRefreshAt: null
        },
        balances: { merchantCoin: 500, weeklyTicket: 300 },
        seasonStats: {
          settledExchanges: 0,
          failedSettlements: 0,
          uncertainSettlements: 0,
          exchangedValue: 0
        }
      });
    }
    if (path === "/me/federation" && method === "GET") {
      const matrixSha256 = "3edd0fe96d70a8438362afaa0d6a8b8638797988275abaa02f00538055f68342";
      const experimentalCompatibility = {
        combinationId: "pal-1.0.0.100427-native-dev36",
        matrixVersion: "1.0.0",
        matrixSha256,
        status: "experimental",
        gameVersion: "v1.0.0.100427",
        steamBuild: "unknown",
        palDefenderVersion: "1.8.1.3933",
        ue4ssCommit: "c2ac246447a8bcd92541070cb474044e7a2bbbe6",
        nativeProtocolVersion: "1.0",
        nativeModVersion: "0.3.0-dev.36",
        bridgeAvailability: "available",
        capabilities: ["bridge.handshake", "inventory.probe"],
        verifiedAt: "2026-07-15T03:07:00Z"
      };
      return json(route, {
        localServerId: "alpha",
        matrixVersion: "1.0.0",
        matrixSha256,
        observedAt: "2026-07-16T08:00:00Z",
        servers: [
          {
            serverId: "alpha",
            displayName: "晨曦周世界",
            portalUrl: "https://alpha.demo.invalid/player/",
            local: true,
            availability: "available",
            accountExists: true,
            accountDisplayName: "演示调查员",
            season: {
              code: "WEEK-ALPHA-01",
              displayName: "晨曦第 1 周",
              startsAt: "2026-07-14T00:00:00Z",
              endsAt: "2026-07-21T00:00:00Z",
              state: "active"
            },
            balances: { marketCoin: 1_240, seasonVoucher: 360 },
            balancesAvailable: true,
            compatibility: experimentalCompatibility,
            switchAvailable: false,
            errorCode: null,
            observedAt: "2026-07-16T08:00:00Z"
          },
          {
            serverId: "beta",
            displayName: "远山周世界",
            portalUrl: "https://beta.demo.invalid/player/",
            local: false,
            availability: "available",
            accountExists: true,
            accountDisplayName: "远山行商",
            season: {
              code: "WEEK-BETA-03",
              displayName: "远山第 3 周",
              startsAt: "2026-07-15T00:00:00Z",
              endsAt: "2026-07-22T00:00:00Z",
              state: "active"
            },
            balances: { marketCoin: 860, seasonVoucher: 140 },
            balancesAvailable: true,
            compatibility: experimentalCompatibility,
            switchAvailable: true,
            errorCode: null,
            observedAt: "2026-07-16T08:00:00Z"
          },
          {
            serverId: "gamma",
            displayName: "群岛周世界",
            portalUrl: "https://gamma.demo.invalid/player/",
            local: false,
            availability: "incompatible",
            accountExists: null,
            accountDisplayName: null,
            season: null,
            balances: null,
            balancesAvailable: false,
            compatibility: {
              combinationId: "pal-1.0.1.100619-build-24181105-quarantine",
              matrixVersion: "1.0.0",
              matrixSha256,
              status: "quarantined",
              gameVersion: "v1.0.1.100619",
              steamBuild: "24181105",
              palDefenderVersion: "unknown",
              ue4ssCommit: "c2ac246447a8bcd92541070cb474044e7a2bbbe6",
              nativeProtocolVersion: "1.0",
              nativeModVersion: "0.3.0-dev.36",
              bridgeAvailability: "unavailable",
              capabilities: ["official-rest.read"],
              verifiedAt: "2026-07-15T06:30:00Z"
            },
            switchAvailable: false,
            errorCode: "FEDERATION_COMPATIBILITY_QUARANTINED",
            observedAt: "2026-07-16T08:00:00Z"
          }
        ]
      });
    }
    if (path === "/me/catalog" && method === "GET") {
      return json(route, {
        revision: "r1",
        contentVersionId: product.contentVersionId,
        contentHash: product.contentHash,
        businessDate: "2026-07-16",
        rulesVersion: "weekly-economy-v1",
        items: [product]
      });
    }
    if (path === "/me/orders" && method === "GET") return json(route, { items: [] });
    if (path === "/me/orders" && method === "POST") {
      if (options.orderFailure) {
        return json(route, {
          code: "DELIVERY_QUEUE_CAPACITY",
          message: "发货队列暂时拥堵。"
        }, 429);
      }
      return json(route, {
        orderId: "44444444-4444-4444-4444-444444444444",
        productId: product.productId,
        productName: product.name,
        quantity: 1,
        currency: "merchantCoin",
        totalAmount: 10,
        state: "accepted",
        statusMessage: "订单已受理",
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString()
      });
    }
    if (path === "/me/ledger" && method === "GET") return json(route, { items: [] });
    if (path === "/me/runs" && method === "GET") {
      return json(route, { items: [], settlementEnabled: true, reason: null });
    }
    if (path === "/me/runs/quote" && method === "POST") {
      return json(route, {
        runId: "55555555-5555-5555-5555-555555555555",
        revision: 1,
        state: "quoted",
        zoneName: "测试兑换点",
        items: [
          { itemId: "Leather", name: "皮革", quantity: 5, unitValue: 2, totalValue: 10, iconKey: "biological", rarity: "Uncommon", usage: "用于装备与加工配方。", presentationSource: "content" },
          { itemId: "Bone", name: "骨头", quantity: 4, unitValue: 3, totalValue: 12, iconKey: "ancient", rarity: "Rare", usage: "用于古代科技与稀有制造。", presentationSource: "content" }
        ],
        itemCount: 9,
        totalValue: 22,
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        sourceQuoteRunId: null,
        selectionDerived: false
      });
    }
    const selectMatch = path.match(/^\/me\/runs\/([0-9a-f-]+)\/select$/i);
    if (selectMatch && method === "POST") {
      const body = request.postDataJSON() as {
        sourceRevision: number;
        items: Array<{ itemId: string; quantity: number }>;
      };
      const idempotencyKey = request.headers()["idempotency-key"] ?? null;
      selectionRequests.push({ idempotencyKey, body });
      const allItems = [
        { itemId: "Leather", name: "皮革", quantity: 5, unitValue: 2, totalValue: 10, iconKey: "biological", rarity: "Uncommon", usage: "用于装备与加工配方。", presentationSource: "content" },
        { itemId: "Bone", name: "骨头", quantity: 4, unitValue: 3, totalValue: 12, iconKey: "ancient", rarity: "Rare", usage: "用于古代科技与稀有制造。", presentationSource: "content" }
      ];
      const items = body.items.map((line) => {
        const source = allItems.find((item) => item.itemId === line.itemId)!;
        return {
          ...source,
          quantity: line.quantity,
          totalValue: line.quantity * source.unitValue
        };
      });
      selectedQuote ??= {
        runId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        revision: 1,
        state: "quoted",
        zoneName: "测试兑换点",
        items,
        itemCount: items.reduce((total, item) => total + item.quantity, 0),
        totalValue: items.reduce((total, item) => total + item.totalValue, 0),
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        sourceQuoteRunId: selectMatch[1],
        selectionDerived: true
      };
      if (options.selectionResponseLoss && !selectionResponseLost) {
        selectionResponseLost = true;
        return route.abort("connectionfailed");
      }
      return json(route, selectedQuote);
    }
    const settleMatch = path.match(/^\/me\/runs\/([0-9a-f-]+)\/settle$/i);
    if (settleMatch && method === "POST") {
      settlementRequests.push({
        runId: settleMatch[1],
        idempotencyKey: request.headers()["idempotency-key"] ?? null
      });
      const rewardAmount = Number(selectedQuote?.totalValue ?? 0);
      const itemCount = Number(selectedQuote?.itemCount ?? 0);
      return json(route, {
        runId: settleMatch[1],
        state: "extracted",
        extractedItemCount: itemCount,
        extractedValue: rewardAmount,
        rewardCurrency: "weeklyTicket",
        rewardAmount,
        startedAt: new Date().toISOString(),
        endedAt: new Date().toISOString(),
        statusMessage: null
      });
    }
    if (path === "/me/new-player-activities" && method === "GET") {
      return json(route, { items: [] });
    }
    if (path === "/me/tasks" && method === "GET") {
      return json(route, {
        accountId: "66666666-6666-6666-6666-666666666666",
        seasonId: "33333333-3333-3333-3333-333333333333",
        serverId: "local",
        rankingPoints: 5,
        items: [{
          instanceId: "77777777-7777-7777-7777-777777777777",
          cadence: "Daily",
          periodKey: "2026-07-16",
          taskKey: "daily-exchange",
          displayName: "完成一次资源兑换",
          description: "只按服务端确认的兑换推进。",
          eventKind: "ResourceExchangeSettled",
          targetAmount: 1,
          progress: 0,
          completed: false,
          completedAt: null,
          rewardGranted: false,
          reward: { currency: "MarketCoin", amount: 10, rankingPoints: 5 },
          contentVersionId: product.contentVersionId,
          contentHash: product.contentHash,
          rulesVersion: "weekly-economy-v1",
          rotationSeed: "b".repeat(64)
        }]
      });
    }
    if (path === "/me/season-leaderboards/latest" && method === "GET") {
      return json(route, {
        available: true,
        status: "frozen",
        settlement: {
          seasonId: "88888888-8888-8888-8888-888888888888",
          seasonCode: "WEEK-E2E-01",
          cutoffAt: "2026-07-14T00:15:00Z",
          frozenAt: "2026-07-14T00:20:00Z",
          rewardState: "completed",
          rules: {
            rulesVersion: "weekly-resource-ranking-v1",
            lateSettlementGraceMinutes: 15,
            minimumSettledExchanges: 1,
            minimumResourceValue: 100,
            minimumTaskPoints: 10,
            resourceTieBreakRule: "resourceValue desc, resourceQuantity desc, firstSettledAt asc",
            taskTieBreakRule: "taskPoints desc, firstTaskPointAt asc"
          },
          participation: {
            participating: true,
            reasonCode: "frozen-contribution-recorded",
            resource: {
              board: "resource-value",
              eligible: true,
              rank: 2,
              reasonCode: "eligible",
              settledExchanges: 3,
              resourceQuantity: 40,
              resourceValue: 800,
              taskPoints: 18
            },
            task: {
              board: "task-points",
              eligible: true,
              rank: 3,
              reasonCode: "eligible",
              settledExchanges: 3,
              resourceQuantity: 40,
              resourceValue: 800,
              taskPoints: 18
            },
            items: [{ itemId: "Wood", category: "基础资源", quantity: 40, value: 800 }],
            categories: [{ category: "基础资源", quantity: 40, value: 800 }]
          },
          voucherExpiry: {
            jobState: "completed",
            itemState: "expired",
            scheduledAmount: 300,
            expiredAmount: 300,
            ledgerRecorded: true,
            completedAt: "2026-07-14T00:30:00Z"
          },
          permanentRewards: [{
            source: "standard",
            board: "resource-value",
            rank: 2,
            marketCoin: 300,
            rewardKey: "leaderboard:e2e:resource-rank-2",
            decisionState: "granted",
            deliveryState: "paid",
            reasonCode: null,
            ledgerRecorded: true,
            completedAt: "2026-07-14T00:31:00Z"
          }]
        }
      });
    }
    if (path === "/me/team-economy" && method === "GET") {
      return json(route, teamDashboard(teamMode));
    }
    const teamLeaderboard = path.match(
      /^\/me\/team-economy\/leaderboards\/(resourceValue|taskPoints|deliveredOrders)$/
    );
    if (teamLeaderboard && method === "GET") {
      return json(route, teamLeaderboardFixture(teamLeaderboard[1]));
    }
    if (path.startsWith("/me/team-economy/") && method === "POST") {
      const body = (request.postDataJSON() ?? {}) as Record<string, unknown>;
      teamRequests.push({
        path,
        method,
        csrfToken: request.headers()["x-csrf-token"] ?? null,
        idempotencyKey: request.headers()["idempotency-key"] ?? null,
        body
      });
      if (path === "/me/team-economy/invite/rotate") {
        return json(route, {
          teamId: "a1000000-0000-0000-0000-000000000001",
          inviteId: "a2000000-0000-0000-0000-000000000002",
          token: "tm1.a2000000000000000000000000000002.safe-e2e-invite-material-0123456789abcd",
          tokenShown: true,
          expiresAt: "2026-07-17T08:00:00Z",
          maximumUses: 10,
          remainingUses: 10,
          replayed: false
        });
      }
      if (path === "/me/team-economy/teams") teamMode = "owner";
      if (path === "/me/team-economy/join") teamMode = "member";
      if (path === "/me/team-economy/leave" || path === "/me/team-economy/dissolve") {
        teamMode = "none";
      }
      if (path === "/me/team-economy/owner/transfer") teamMode = "member";
      return json(route, {
        teamId: "a1000000-0000-0000-0000-000000000001",
        name: "苍穹搬运社",
        status: path === "/me/team-economy/dissolve" ? "Dissolved" : "Active",
        memberCount: teamMode === "none" ? 0 : 4,
        isOwner: teamMode === "owner",
        replayed: false,
        updatedAt: "2026-07-16T08:00:00Z"
      });
    }
    if (path === "/me/notifications" && method === "GET") {
      return json(route, {
        schemaVersion: "1",
        unreadCount: notifications.filter((item) => item.readAt === null).length,
        hasActiveDelivery: false,
        items: notifications
      });
    }
    if (path === "/me/notifications/read-all" && method === "POST") {
      const markedRead = notifications.filter((item) => item.readAt === null).length;
      notifications = notifications.map((item) => ({
        ...item,
        readAt: item.readAt ?? new Date().toISOString()
      }));
      return json(route, { markedRead, unreadCount: 0 });
    }
    const notificationRead = path.match(/^\/me\/notifications\/([0-9a-f-]+)\/read$/i);
    if (notificationRead && method === "POST") {
      const readAt = new Date().toISOString();
      notifications = notifications.map((item) => item.notificationId === notificationRead[1]
        ? { ...item, readAt }
        : item);
      return json(route, {
        notificationId: notificationRead[1],
        readAt,
        unreadCount: notifications.filter((item) => item.readAt === null).length
      });
    }
    if (path === "/me/extraction-zones" && method === "GET") {
      if (extractionZonesUnavailable) {
        return json(route, {
          code: "POSITION_POLL_FAILED",
          message: "位置轮询暂时失败"
        }, 503);
      }
      const eventWindow = {
        startsAt: "2026-07-16T08:00:00Z",
        endsAt: "2026-07-16T11:00:00Z",
        graceEndsAt: "2026-07-16T11:01:00Z"
      };
      const worldEvents = [{
        eventId: "d".repeat(32),
        eventKey: "resource-surge",
        displayName: "资源潮汐",
        kind: "ResourceSurge",
        seed: "e".repeat(64),
        window: eventWindow,
        zoneYieldMultiplierBasisPoints: 11_500,
        productPriceMultiplierBasisPoints: 10_000
      }];
      return json(route, {
        items: [
          {
            id: "zone-a",
            displayName: "河湾开放兑换点",
            mapX: 50,
            mapY: 50,
            radius: 100,
            routeHint: "沿河流向北，经过石桥后进入标记半径。",
            riskHint: "可控风险，仍需留意野生帕鲁。",
            inRange: options.openZoneInRange ? true : false,
            distance: options.openZoneInRange ? 1 : 25,
            open: true,
            hotspot: true,
            yieldMultiplierBasisPoints: 13_225,
            riskLevel: "Guarded",
            dynamicSelectedOpen: true,
            dynamicOpenWindow: eventWindow,
            hotspotWindow: eventWindow,
            dynamicPolicyVersion: "scheme-a-dynamic-v1",
            dynamicSeed: "c".repeat(64),
            worldEvents
          },
          {
            id: "zone-b",
            displayName: "火山关闭兑换点",
            mapX: -250,
            mapY: 310,
            radius: 120,
            routeHint: "从火山传送点向东，沿岩壁前往。",
            riskHint: "严峻风险，兑换点关闭时不要等待在区域内。",
            inRange: options.closedZoneInRange ? true : false,
            distance: options.closedZoneInRange ? 5 : 420,
            open: false,
            hotspot: false,
            yieldMultiplierBasisPoints: 10_000,
            nextOpensAt: "2026-07-17T04:00:00Z",
            riskLevel: "Severe",
            dynamicSelectedOpen: false,
            dynamicOpenWindow: eventWindow,
            hotspotWindow: null,
            dynamicPolicyVersion: "scheme-a-dynamic-v1",
            dynamicSeed: "c".repeat(64),
            worldEvents
          }
        ],
        playerPosition: options.positionUnavailable ? null : { mapX: 48, mapY: 49 },
        updatedAt: new Date().toISOString(),
        playerOnline: true,
        positionAvailable: !options.positionUnavailable,
        status: options.positionUnavailable ? "position-unavailable" : options.openZoneInRange ? "inside" : "outside",
        statusMessage: options.positionUnavailable
          ? "服务器没有返回可靠位置"
          : options.openZoneInRange
            ? "角色位于开放兑换点内"
            : "角色位于兑换点外",
        dynamicPolicyVersion: "scheme-a-dynamic-v1",
        dynamicSeed: "c".repeat(64),
        worldEvents
      });
    }

    return json(route, { code: "E2E_ROUTE_NOT_FOUND", message: `${method} ${path}` }, 404);
  });
  return {
    selectionRequests,
    settlementRequests,
    teamRequests,
    setExtractionZonesUnavailable(value = true) {
      extractionZonesUnavailable = value;
    }
  };
}

function teamDashboard(mode: "none" | "owner" | "member") {
  const projection = {
    ready: true,
    stale: false,
    cutoffAt: "2026-07-16T08:00:00Z",
    updatedAt: "2026-07-16T08:00:01Z",
    sourceHash: "c".repeat(64),
    snapshotHash: "d".repeat(64),
    lastErrorCode: null
  };
  if (mode === "none") {
    return {
      enabled: true,
      hasTeam: false,
      teamId: null,
      name: null,
      status: null,
      isOwner: false,
      memberCount: 0,
      joinedAt: null,
      goals: [],
      teamContribution: null,
      myContribution: null,
      transferCandidates: [],
      projection,
      policyNotice: "仅统计同一服务器、同一周世界内，成员有效期中的服务端权威经济事实。"
    };
  }
  return {
    enabled: true,
    hasTeam: true,
    teamId: "a1000000-0000-0000-0000-000000000001",
    name: "苍穹搬运社",
    status: "Active",
    isOwner: mode === "owner",
    memberCount: 4,
    joinedAt: "2026-07-14T00:00:00Z",
    goals: [
      { kind: "ResourceItems", displayName: "出售白名单资源", progress: 438, target: 500, unit: "件", achieved: false, reachedAt: null },
      { kind: "ResourceValue", displayName: "累计资源价值", progress: 2860, target: 2500, unit: "价值", achieved: true, reachedAt: "2026-07-16T06:00:00Z" },
      { kind: "TaskPoints", displayName: "可靠任务积分", progress: 72, target: 100, unit: "分", achieved: false, reachedAt: null },
      { kind: "DeliveredOrders", displayName: "成功送达订单", progress: 8, target: 10, unit: "单", achieved: false, reachedAt: null }
    ],
    teamContribution: {
      resourceItems: 438,
      resourceValue: 2860,
      taskPoints: 72,
      deliveredOrders: 8,
      actualCurrencySpent: 640
    },
    myContribution: {
      resourceItems: 126,
      resourceValue: 830,
      taskPoints: 22,
      deliveredOrders: 3,
      actualCurrencySpent: 210
    },
    transferCandidates: mode === "owner" ? [
      { memberHandle: "0a1b2c3d4e5f60718293a4b5", label: "成员 A1F4", joinedAt: "2026-07-14T02:00:00Z" },
      { memberHandle: "1a2b3c4d5e6f708192a3b4c5", label: "成员 C82D", joinedAt: "2026-07-14T04:00:00Z" }
    ] : [],
    projection,
    policyNotice: "仅统计同一服务器、同一周世界内，成员有效期中的服务端权威经济事实；里程碑不会自动发币。"
  };
}

function teamLeaderboardFixture(metric: string) {
  const values: Record<string, number[]> = {
    resourceValue: [2860, 2410, 1980],
    taskPoints: [91, 72, 66],
    deliveredOrders: [12, 9, 8]
  };
  const names = ["苍穹搬运社", "绿洲后勤队", "雪原补给团"];
  return {
    metric: metric === "resourceValue" ? "ResourceValue" :
      metric === "taskPoints" ? "TaskPoints" : "DeliveredOrders",
    cutoffAt: "2026-07-16T08:00:00Z",
    offset: 0,
    limit: 50,
    total: 3,
    nextCursor: null,
    items: values[metric].map((value, index) => ({
      rank: index + 1,
      teamId: `b1000000-0000-0000-0000-00000000000${index + 1}`,
      teamName: names[index],
      memberCount: 4,
      value,
      reachedAt: `2026-07-16T0${index + 5}:00:00Z`,
      isMyTeam: index === (metric === "resourceValue" ? 0 : metric === "taskPoints" ? 1 : 2)
    })),
    tieBreakPolicy: "value desc, reachedAt asc, teamId asc",
    eligibilityPolicy: "active team, minimum members and contribution",
    projection: {
      ready: true,
      stale: false,
      cutoffAt: "2026-07-16T08:00:00Z",
      updatedAt: "2026-07-16T08:00:01Z",
      sourceHash: "c".repeat(64),
      snapshotHash: "d".repeat(64),
      lastErrorCode: null
    }
  };
}

function json(route: Route, body: unknown, status = 200) {
  return route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });
}
