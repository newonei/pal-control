import assert from "node:assert/strict";
import test from "node:test";

import type { ExtractionRun, Order } from "../src/api.ts";
import {
  describePlayerError,
  formatQuoteCountdown,
  hasPendingEconomyActivity,
  orderStateGuidance,
  quoteSecondsRemaining,
  runStateGuidance
} from "../src/liveStatus.ts";

function order(state: Order["state"]): Order {
  return {
    orderId: "order-1",
    productId: "product-1",
    productName: "test",
    quantity: 1,
    currency: "weeklyTicket",
    totalAmount: 1,
    state,
    statusMessage: null,
    createdAt: "2026-07-15T00:00:00Z",
    updatedAt: "2026-07-15T00:00:00Z"
  };
}

function run(state: ExtractionRun["state"]): ExtractionRun {
  return {
    runId: "run-1",
    state,
    extractedItemCount: 0,
    extractedValue: 0,
    rewardCurrency: "weeklyTicket",
    rewardAmount: 0,
    startedAt: "2026-07-15T00:00:00Z",
    endedAt: null
  };
}

test("polling continues only while an automatic terminal transition is expected", () => {
  for (const state of ["accepted", "pending", "delivering", "failed"] as const) {
    assert.equal(hasPendingEconomyActivity([order(state)], []), true, state);
  }
  for (const state of ["preparing", "deployed"] as const) {
    assert.equal(hasPendingEconomyActivity([], [run(state)]), true, state);
  }
  assert.equal(hasPendingEconomyActivity([order("succeeded")], [run("settled")]), false);
  assert.equal(hasPendingEconomyActivity([order("uncertain")], [run("uncertain")]), false);
});

test("quote countdown rounds up and fails closed for expired or invalid timestamps", () => {
  const now = Date.parse("2026-07-15T00:00:00.000Z");
  assert.equal(quoteSecondsRemaining("2026-07-15T00:00:30.000Z", now), 30);
  assert.equal(quoteSecondsRemaining("2026-07-15T00:00:00.001Z", now), 1);
  assert.equal(quoteSecondsRemaining("2026-07-14T23:59:59.999Z", now), 0);
  assert.equal(quoteSecondsRemaining("not-a-date", now), 0);
  assert.equal(formatQuoteCountdown(61), "1 分 01 秒");
  assert.equal(formatQuoteCountdown(9.1), "10 秒");
  assert.equal(formatQuoteCountdown(0), "已过期");
});

test("operational errors include a concrete and safe next step", () => {
  assert.match(describePlayerError({ message: "offline", status: 409, code: "PLAYER_NOT_ONLINE" }).nextStep, /进入本周游戏世界/);
  assert.match(describePlayerError({ message: "busy", status: 503, code: "SHOP_DELIVERY_QUEUE_FULL" }).nextStep, /不要连续点击/);
  assert.match(describePlayerError({ message: "maintenance", status: 423, code: "EXTRACTION_MAINTENANCE" }).nextStep, /请勿重复提交/);
  assert.match(describePlayerError({ message: "version", status: 503, code: "PALDEFENDER_VERSION_NOT_APPROVED" }).nextStep, /安全检查/);
  assert.match(describePlayerError({ message: "expired", status: 409, code: "QUOTE_EXPIRED" }).nextStep, /重新扫描/);
  assert.match(describePlayerError({ message: "rotated", status: 409, code: "QUOTE_CONTENT_CHANGED" }).nextStep, /重新扫描/);
  assert.match(describePlayerError({ message: "rotated", status: 409, code: "OFFER_NOT_AVAILABLE" }).nextStep, /刷新商城/);
  assert.match(describePlayerError({ message: "sold out", status: 409, code: "GLOBAL_STOCK_EXCEEDED" }).nextStep, /服务器库存/);
  assert.match(describePlayerError({ message: "closed", status: 409, code: "EXTRACTION_ZONE_CLOSED" }).nextStep, /下次开放时间/);
});

test("exceptional transaction states warn against unsafe repeats", () => {
  assert.match(orderStateGuidance("uncertain") ?? "", /请勿重复购买/);
  assert.match(orderStateGuidance("partial") ?? "", /部分物品确认到账/);
  assert.match(orderStateGuidance("cancelled") ?? "", /订单已取消/);
  assert.match(orderStateGuidance("refunded") ?? "", /资金流水/);
  assert.match(runStateGuidance("uncertain") ?? "", /请勿再次提交/);
  assert.match(runStateGuidance("cancelled") ?? "", /不会继续执行/);
});
