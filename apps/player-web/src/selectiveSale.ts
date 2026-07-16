import type { ExtractionQuote } from "./api";

export type QuoteSelectionDraft = Record<string, {
  selected: boolean;
  quantity: number;
}>;

export type QuoteSelectionLine = { itemId: string; quantity: number };

export type SelectiveSaleJournal = {
  version: 1;
  userId: string;
  quote: ExtractionQuote;
  selection: QuoteSelectionDraft;
  selectionKey: string;
  settlementKey: string;
};

const journalKey = "pal-control-selective-sale-v1";

export function createDefaultQuoteSelection(quote: ExtractionQuote): QuoteSelectionDraft {
  return Object.fromEntries(quote.items.map((item) => [item.itemId, {
    selected: true,
    quantity: item.quantity
  }]));
}

export function selectedQuoteLines(
  quote: ExtractionQuote,
  selection: QuoteSelectionDraft
): QuoteSelectionLine[] {
  return quote.items.flatMap((item) => {
    const draft = selection[item.itemId];
    if (!draft?.selected) return [];
    const quantity = Math.trunc(draft.quantity);
    if (quantity < 1 || quantity > item.quantity) return [];
    return [{ itemId: item.itemId, quantity }];
  });
}

export function summarizeQuoteSelection(
  quote: ExtractionQuote,
  selection: QuoteSelectionDraft
) {
  return selectedQuoteLines(quote, selection).reduce((summary, line) => {
    const quoted = quote.items.find((item) => item.itemId === line.itemId)!;
    summary.lineCount += 1;
    summary.itemCount += line.quantity;
    summary.totalValue += line.quantity * quoted.unitValue;
    return summary;
  }, { lineCount: 0, itemCount: 0, totalValue: 0 });
}

export function saveSelectiveSaleJournal(
  journal: SelectiveSaleJournal,
  storage: Storage | undefined = globalThis.sessionStorage
) {
  storage?.setItem(journalKey, JSON.stringify(journal));
}

export function loadSelectiveSaleJournal(
  userId: string,
  nowMs = Date.now(),
  storage: Storage | undefined = globalThis.sessionStorage
): SelectiveSaleJournal | null {
  const raw = storage?.getItem(journalKey);
  if (!raw) return null;
  try {
    const value = JSON.parse(raw) as Partial<SelectiveSaleJournal>;
    if (value.version !== 1 || value.userId !== userId ||
        !value.quote || !value.selection ||
        typeof value.selectionKey !== "string" || value.selectionKey.length < 8 ||
        typeof value.settlementKey !== "string" || value.settlementKey.length < 8 ||
        !Number.isFinite(Date.parse(value.quote.expiresAt)) ||
        Date.parse(value.quote.expiresAt) <= nowMs) {
      storage?.removeItem(journalKey);
      return null;
    }
    const validItems = value.quote.items.every((item) => {
      const draft = value.selection?.[item.itemId];
      return draft && typeof draft.selected === "boolean" &&
        Number.isInteger(draft.quantity) &&
        draft.quantity >= 1 && draft.quantity <= item.quantity;
    });
    if (!validItems) {
      storage?.removeItem(journalKey);
      return null;
    }
    return value as SelectiveSaleJournal;
  } catch {
    storage?.removeItem(journalKey);
    return null;
  }
}

export function clearSelectiveSaleJournal(
  storage: Storage | undefined = globalThis.sessionStorage
) {
  storage?.removeItem(journalKey);
}
