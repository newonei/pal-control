import assert from "node:assert/strict";
import test from "node:test";

import {
  claimNewPlayerActivity,
  getNewPlayerActivities,
  type NewPlayerActivityAvailability,
  type NewPlayerActivityClaim
} from "../src/api.ts";

const availability: NewPlayerActivityAvailability = {
  activity: {
    activityId: "activity-id",
    activityKey: "welcome-week",
    version: 2,
    state: "published",
    title: "新玩家补给",
    description: "进入本周世界后可领取。",
    rewards: { merchantCoin: 120, weeklyTicket: 30 },
    revision: 3,
    createdBy: "economy-admin:test",
    createdAt: "2026-07-15T00:00:00Z",
    publishedSeasonId: "season-id",
    publishedWorldId: "world-id",
    publishedBy: "economy-admin:test",
    publishedAt: "2026-07-15T00:01:00Z",
    closedBy: null,
    closedAt: null
  },
  claimed: false,
  grant: null
};

test("new-player activities are read from the current authenticated player projection", async () => {
  await withFetch(async (input, init) => {
    assert.equal(String(input), "/api/v1/player/me/new-player-activities");
    assert.equal(init?.method, "GET");
    assert.equal(init?.credentials, "include");
    assert.equal(init?.cache, "no-store");
    return jsonResponse({ items: [availability] });
  }, async () => {
    const result = await getNewPlayerActivities();
    assert.equal(result.items.length, 1);
    assert.equal(result.items[0].activity.rewards.merchantCoin, 120);
    assert.equal(result.items[0].claimed, false);
  });
});

test("claim uses the exact version plus CSRF and an explicit idempotency key", async () => {
  const claim: NewPlayerActivityClaim = {
    activity: availability.activity,
    grant: {
      grantId: "grant-id",
      activityId: availability.activity.activityId,
      activityKey: availability.activity.activityKey,
      activityVersion: availability.activity.version,
      seasonId: "season-id",
      worldId: "world-id",
      rewards: availability.activity.rewards,
      balancesAfter: { merchantCoin: 120, weeklyTicket: 30 },
      claimedAt: "2026-07-15T00:02:00Z"
    },
    balances: { merchantCoin: 120, weeklyTicket: 30 },
    created: true,
    idempotentReplay: false
  };

  await withFetch(async (input, init) => {
    assert.equal(
      String(input),
      "/api/v1/player/me/new-player-activities/welcome%2F%E5%A4%8F%E5%AD%A3/versions/3/claim"
    );
    assert.equal(init?.method, "POST");
    const headers = new Headers(init?.headers);
    assert.equal(headers.get("X-CSRF-Token"), "csrf-token");
    assert.equal(headers.get("Idempotency-Key"), "claim-key-001");
    assert.equal(init?.body, undefined);
    return jsonResponse(claim);
  }, async () => {
    const result = await claimNewPlayerActivity(
      "welcome/夏季",
      3,
      "claim-key-001",
      "csrf-token"
    );
    assert.equal(result.created, true);
    assert.deepEqual(result.balances, { merchantCoin: 120, weeklyTicket: 30 });
  });
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
