import assert from "node:assert/strict";
import test from "node:test";

import { selectQuote, type ExtractionQuote } from "../src/api.ts";
import {
  clearSelectiveSaleJournal,
  createDefaultQuoteSelection,
  loadSelectiveSaleJournal,
  saveSelectiveSaleJournal,
  selectedQuoteLines,
  summarizeQuoteSelection
} from "../src/selectiveSale.ts";

const quote: ExtractionQuote = {
  runId: "11111111-1111-1111-1111-111111111111",
  revision: 7,
  state: "quoted",
  zoneName: "测试兑换点",
  items: [
    { itemId: "Leather", name: "皮革", quantity: 5, unitValue: 2, totalValue: 10 },
    { itemId: "Bone", name: "骨头", quantity: 4, unitValue: 3, totalValue: 12 }
  ],
  itemCount: 9,
  totalValue: 22,
  expiresAt: "2099-07-16T12:00:00Z",
  sourceQuoteRunId: null,
  selectionDerived: false
};

test("selection defaults to every quoted line and recomputes exact chosen value", () => {
  const draft = createDefaultQuoteSelection(quote);
  assert.deepEqual(selectedQuoteLines(quote, draft), [
    { itemId: "Leather", quantity: 5 },
    { itemId: "Bone", quantity: 4 }
  ]);
  draft.Leather.selected = false;
  draft.Bone.quantity = 2;
  assert.deepEqual(selectedQuoteLines(quote, draft), [{ itemId: "Bone", quantity: 2 }]);
  assert.deepEqual(summarizeQuoteSelection(quote, draft), {
    lineCount: 1,
    itemCount: 2,
    totalValue: 6
  });
});

test("player select request is self-only and carries revision, exact lines, CSRF and idempotency", async () => {
  const original = globalThis.fetch;
  globalThis.fetch = (async (input, init) => {
    assert.equal(
      String(input),
      "/api/v1/player/me/runs/11111111-1111-1111-1111-111111111111/select"
    );
    assert.equal(init?.method, "POST");
    const headers = new Headers(init?.headers);
    assert.equal(headers.get("X-CSRF-Token"), "csrf-selection");
    assert.equal(headers.get("Idempotency-Key"), "selection-key-0001");
    assert.deepEqual(JSON.parse(String(init?.body)), {
      sourceRevision: 7,
      items: [{ itemId: "Bone", quantity: 2 }]
    });
    assert.equal(String(init?.body).includes("userId"), false);
    return new Response(JSON.stringify({
      ...quote,
      runId: "22222222-2222-2222-2222-222222222222",
      revision: 1,
      items: [quote.items[1]],
      sourceQuoteRunId: quote.runId,
      selectionDerived: true
    }), { status: 200, headers: { "Content-Type": "application/json" } });
  }) as typeof fetch;
  try {
    const selected = await selectQuote(
      quote.runId,
      quote.revision,
      [{ itemId: "Bone", quantity: 2 }],
      "selection-key-0001",
      "csrf-selection"
    );
    assert.equal(selected.selectionDerived, true);
    assert.equal(selected.sourceQuoteRunId, quote.runId);
  } finally {
    globalThis.fetch = original;
  }
});

test("response-loss journal survives refresh for the same user and expires safely", () => {
  const storage = new MemoryStorage();
  const selection = createDefaultQuoteSelection(quote);
  saveSelectiveSaleJournal({
    version: 1,
    userId: "steam_self",
    quote,
    selection,
    selectionKey: "selection-key-refresh-0001",
    settlementKey: "settlement-key-refresh-0001"
  }, storage);
  const restored = loadSelectiveSaleJournal(
    "steam_self",
    Date.parse("2026-07-16T00:00:00Z"),
    storage
  );
  assert.equal(restored?.quote.runId, quote.runId);
  assert.equal(restored?.selectionKey, "selection-key-refresh-0001");
  assert.equal(loadSelectiveSaleJournal("steam_other", Date.now(), storage), null);

  saveSelectiveSaleJournal({
    version: 1,
    userId: "steam_self",
    quote: { ...quote, expiresAt: "2026-07-15T00:00:00Z" },
    selection,
    selectionKey: "selection-key-expired-0001",
    settlementKey: "settlement-key-expired-0001"
  }, storage);
  assert.equal(loadSelectiveSaleJournal(
    "steam_self",
    Date.parse("2026-07-16T00:00:00Z"),
    storage
  ), null);
  clearSelectiveSaleJournal(storage);
  assert.equal(storage.length, 0);
});

class MemoryStorage implements Storage {
  readonly values = new Map<string, string>();
  get length() { return this.values.size; }
  clear() { this.values.clear(); }
  getItem(key: string) { return this.values.get(key) ?? null; }
  key(index: number) { return Array.from(this.values.keys())[index] ?? null; }
  removeItem(key: string) { this.values.delete(key); }
  setItem(key: string, value: string) { this.values.set(key, value); }
}
