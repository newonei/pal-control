import type { ContentRarity } from "./api.ts";

const iconKeys = new Set([
  "supply", "capture", "medical", "ammo", "weapon", "building", "farming",
  "material-basic", "mineral", "biological", "processed", "ancient",
  "industrial", "valuable", "special", "advanced"
]);

export function normalizeItemIconKey(value?: string | null) {
  return value && iconKeys.has(value) ? value : "supply";
}

export function rarityLabel(rarity?: ContentRarity | null) {
  return ({
    Common: "普通",
    Uncommon: "优良",
    Rare: "稀有",
    Epic: "史诗",
    Legendary: "传说"
  } as const)[rarity ?? "Common"] ?? "普通";
}
