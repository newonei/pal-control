import assert from "node:assert/strict";
import test from "node:test";

import {
  getLatestSeasonSettlement,
  getSeasonSettlement,
  type SeasonSettlement,
  type SeasonSettlementResponse
} from "../src/api.ts";
import {
  boardReasonLabel,
  participationReasonLabel,
  permanentRewardPresentation,
  voucherExpiryPresentation
} from "../src/seasonSettlementView.ts";

const frozen: SeasonSettlement = {
  seasonId: "11111111-1111-1111-1111-111111111111",
  seasonCode: "WEEK-01",
  cutoffAt: "2026-07-14T00:15:00Z",
  frozenAt: "2026-07-14T00:20:00Z",
  rewardState: "completed",
  rules: {
    rulesVersion: "weekly-resource-ranking-v1",
    lateSettlementGraceMinutes: 15,
    minimumSettledExchanges: 1,
    minimumResourceValue: 100,
    minimumTaskPoints: 10,
    resourceTieBreakRule: "resourceValue desc",
    taskTieBreakRule: "taskPoints desc"
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
      taskPoints: 8
    },
    task: {
      board: "task-points",
      eligible: false,
      rank: null,
      reasonCode: "below-minimum-task-points",
      settledExchanges: 3,
      resourceQuantity: 40,
      resourceValue: 800,
      taskPoints: 8
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
    rewardKey: "leaderboard:snapshot:resource-rank-2",
    decisionState: "granted",
    deliveryState: "paid",
    reasonCode: null,
    ledgerRecorded: true,
    completedAt: "2026-07-14T00:31:00Z"
  }]
};

test("latest and season-specific settlement requests remain authenticated self-only reads", async () => {
  const calls: string[] = [];
  await withFetch(async (input, init) => {
    const path = String(input);
    calls.push(path);
    assert.equal(init?.method, "GET");
    assert.equal(init?.credentials, "include");
    assert.equal(init?.cache, "no-store");
    assert.equal(init?.body, undefined);
    assert.equal(new URL(path, "https://player.invalid").search, "");
    return jsonResponse({ available: true, status: "frozen", settlement: frozen });
  }, async () => {
    const latest = await getLatestSeasonSettlement();
    const bySeason = await getSeasonSettlement(frozen.seasonId);
    assert.equal(latest.settlement?.participation.resource.rank, 2);
    assert.equal(bySeason.settlement?.seasonId, frozen.seasonId);
  });
  assert.deepEqual(calls, [
    "/api/v1/player/me/season-leaderboards/latest",
    `/api/v1/player/me/season-leaderboards/${frozen.seasonId}`
  ]);
});

test("unfrozen, excluded and below-minimum participation states have explicit copy", () => {
  const unavailable: SeasonSettlementResponse = {
    available: false,
    status: "not-frozen",
    settlement: null
  };
  assert.equal(unavailable.settlement, null);
  assert.match(participationReasonLabel("identity-banned-at-freeze"), /封禁/);
  assert.match(participationReasonLabel("manual-exclusion-at-freeze"), /审核排除/);
  assert.match(boardReasonLabel(frozen.participation.task), /未达到最低要求/);
  assert.match(boardReasonLabel({
    ...frozen.participation.resource,
    eligible: false,
    rank: null,
    reasonCode: "below-minimum-resource-value"
  }), /价值未达到/);
});

test("voucher expiry and every reward delivery state distinguish pending, cancelled and ledger-paid", () => {
  assert.deepEqual(voucherExpiryPresentation(frozen.voucherExpiry), {
    label: "周券已过期",
    detail: "账本已确认清除 300 张周战备券。",
    tone: "success"
  });
  assert.equal(voucherExpiryPresentation({
    ...frozen.voucherExpiry,
    jobState: "prepared",
    itemState: "pending",
    expiredAmount: 0,
    ledgerRecorded: false,
    completedAt: null
  }).label, "周券待过期");
  assert.equal(voucherExpiryPresentation({
    ...frozen.voucherExpiry,
    jobState: "completed",
    itemState: "not-applicable",
    scheduledAmount: 0,
    expiredAmount: 0,
    ledgerRecorded: false
  }).label, "没有待过期周券");

  const paid = permanentRewardPresentation(frozen.permanentRewards[0]);
  assert.equal(paid.label, "永久币已发放");
  assert.equal(permanentRewardPresentation({
    ...frozen.permanentRewards[0],
    deliveryState: "pending",
    ledgerRecorded: false,
    completedAt: null
  }).label, "永久币待发放");
  assert.match(permanentRewardPresentation({
    ...frozen.permanentRewards[0],
    decisionState: "cancelled",
    deliveryState: "cancelled",
    reasonCode: "identity-banned-before-reward",
    ledgerRecorded: false,
    completedAt: null
  }).detail, /名次不变/);
});

async function withFetch(
  implementation: typeof fetch,
  action: () => Promise<void>
) {
  const original = globalThis.fetch;
  globalThis.fetch = implementation;
  try {
    await action();
  } finally {
    globalThis.fetch = original;
  }
}

function jsonResponse(value: unknown) {
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: { "Content-Type": "application/json" }
  });
}
