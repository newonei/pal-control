import assert from "node:assert/strict";
import test from "node:test";

import { getReliableTasks, type ReliableTaskSnapshot } from "../src/api.ts";

const snapshot: ReliableTaskSnapshot = {
  accountId: "51a92f0c-e21a-421d-a9cf-e9f2502dcb52",
  seasonId: "5313f603-9e1a-45f4-aabb-861d92226b7c",
  serverId: "pal-weekly-01",
  rankingPoints: 35,
  items: [
    {
      instanceId: "5db58337-cecc-4b54-9e94-b2d266355bea",
      cadence: "Daily",
      periodKey: "2026-07-15",
      taskKey: "daily-resource-value",
      displayName: "资源变现",
      description: "完成本日白名单资源兑换。",
      target: 500,
      progress: 320,
      completed: false,
      completedAt: null,
      reward: {
        currency: "SeasonVoucher",
        currencyAmount: 25,
        rankingPoints: 5
      },
      currencyRewardLedgerEntryId: null,
      rankingRewardEntryId: null,
      contentVersionId: "2bea36d4-9e77-4779-9795-0d91f62ac137",
      contentHash: "1".repeat(64),
      rulesVersion: "scheme-a-v1",
      rotationSeed: "2".repeat(64)
    }
  ]
};

test("reliable tasks are read only from the authenticated server projection", async () => {
  await withFetch(async (input, init) => {
    assert.equal(String(input), "/api/v1/player/me/tasks");
    assert.equal(init?.method, "GET");
    assert.equal(init?.credentials, "include");
    assert.equal(init?.cache, "no-store");
    assert.equal(init?.body, undefined);
    return jsonResponse(snapshot);
  }, async () => {
    const result = await getReliableTasks();
    assert.equal(result.rankingPoints, 35);
    assert.equal(result.items[0].progress, 320);
    assert.equal(result.items[0].contentHash, "1".repeat(64));
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
