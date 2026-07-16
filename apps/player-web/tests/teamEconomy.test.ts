import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import test from "node:test";

const sourceRoot = join(import.meta.dirname, "..");
const api = readFileSync(join(sourceRoot, "src", "api.ts"), "utf8");
const panel = readFileSync(join(sourceRoot, "src", "TeamEconomyPanel.tsx"), "utf8");

test("every team mutation carries CSRF and an explicit idempotency key", () => {
  for (const fragment of [
    '"/me/team-economy/teams"',
    '"/me/team-economy/invite/rotate"',
    '"/me/team-economy/join"',
    '"/me/team-economy/leave"',
    '"/me/team-economy/owner/transfer"',
    '"/me/team-economy/dissolve"'
  ]) {
    const start = api.indexOf(fragment);
    assert.notEqual(start, -1, `missing team route ${fragment}`);
    const block = api.slice(start, start + 320);
    assert.match(block, /idempotencyKey/);
    assert.match(block, /csrfToken/);
  }
});

test("invitation bearer stays in memory and is never persisted by the player UI", () => {
  assert.match(panel, /useState<TeamEconomyInvitation \| null>/);
  assert.match(panel, /setInvitation\(null\)/);
  assert.doesNotMatch(panel, /(?:localStorage|sessionStorage).*invitation/i);
  assert.doesNotMatch(panel, /(?:localStorage|sessionStorage).*token/i);
  assert.match(panel, /离开本页后无法再次查看/);
});

test("team UI explains authoritative facts, no currency reward, and safe identity boundaries", () => {
  assert.match(panel, /服务端权威经济事实/);
  assert.match(panel, /不会因为里程碑自动发币/);
  assert.match(panel, /不透明编号/);
  assert.match(panel, /不会伪造 0/);
  assert.match(panel, /globalThis\.confirm/);
});
