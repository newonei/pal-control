import assert from "node:assert/strict";
import test from "node:test";
import { normalizeItemIconKey, rarityLabel } from "../src/presentationModel.ts";

test("presentation icons stay on the finite local allowlist", () => {
  assert.equal(normalizeItemIconKey("mineral"), "mineral");
  assert.equal(normalizeItemIconKey("https://evil.example/icon.svg"), "supply");
  assert.equal(normalizeItemIconKey("../mineral"), "supply");
  assert.equal(normalizeItemIconKey("<script>"), "supply");
  assert.equal(normalizeItemIconKey("unknown"), "supply");
});

test("every rarity has a stable Chinese label", () => {
  assert.deepEqual(
    ["Common", "Uncommon", "Rare", "Epic", "Legendary"].map((value) =>
      rarityLabel(value as Parameters<typeof rarityLabel>[0])),
    ["普通", "优良", "稀有", "史诗", "传说"]
  );
});
