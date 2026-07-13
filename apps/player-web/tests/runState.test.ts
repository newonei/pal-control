import assert from "node:assert/strict";
import test from "node:test";

import { resourceExchangeStateLabel } from "../src/runState.ts";

test("successful compatibility states are displayed as completed exchanges", () => {
  assert.equal(resourceExchangeStateLabel("extracted"), "已兑换");
  assert.equal(resourceExchangeStateLabel("settled"), "已兑换");
});

test("in-flight and exceptional states use resource exchange terminology", () => {
  assert.equal(resourceExchangeStateLabel("preparing"), "待确认");
  assert.equal(resourceExchangeStateLabel("deployed"), "结算中");
  assert.equal(resourceExchangeStateLabel("failed"), "兑换失败");
  assert.equal(resourceExchangeStateLabel("uncertain"), "待人工核对");
  assert.equal(resourceExchangeStateLabel("cancelled"), "已取消/过期");
});
