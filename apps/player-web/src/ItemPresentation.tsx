import type { ContentRarity } from "./api";
import { normalizeItemIconKey, rarityLabel } from "./presentationModel";

export function ItemIcon({ iconKey, label, size = "normal" }: {
  iconKey?: string | null;
  label: string;
  size?: "small" | "normal" | "large";
}) {
  const key = normalizeItemIconKey(iconKey);
  return (
    <span className={`item-icon ${size} icon-${key}`} role="img" aria-label={`${label}图标`}>
      <svg aria-hidden="true" focusable="false" viewBox="0 0 48 48">
        {iconShape(key)}
      </svg>
    </span>
  );
}

export function RarityBadge({ rarity }: { rarity?: ContentRarity | null }) {
  const safe = rarity ?? "Common";
  return <span className={`rarity-badge rarity-${safe.toLowerCase()}`}>{rarityLabel(safe)}</span>;
}

function iconShape(key: string) {
  switch (key) {
    case "capture":
      return <><circle cx="24" cy="24" r="16" /><path d="M8 24h32M19 24a5 5 0 0 0 10 0 5 5 0 0 0-10 0Z" /></>;
    case "medical":
      return <><rect x="9" y="13" width="30" height="25" rx="5" /><path d="M18 13v-3h12v3M24 19v13M17.5 25.5h13" /></>;
    case "ammo":
      return <><path d="M13 35V18l4-7 4 7v17ZM27 35V15l4-6 4 6v20Z" /><path d="M10 38h28" /></>;
    case "weapon":
      return <><path d="M10 36 36 10M28 9h10v10M8 39l8-1-7-7-1 8Z" /><path d="m23 23 8 8" /></>;
    case "building":
      return <><path d="M8 39h32M12 39V18l12-8 12 8v21" /><path d="M19 39V27h10v12M16 21h3M29 21h3" /></>;
    case "farming":
      return <><path d="M24 39V20M24 25c-8 0-12-5-12-11 8 0 12 4 12 11ZM24 20c7 0 11-4 11-10-7 0-11 4-11 10Z" /><path d="M10 39h28" /></>;
    case "material-basic":
      return <><path d="M8 31h32M11 31l7-14h12l7 14M17 17l-4-6M31 17l4-6" /><circle cx="18" cy="24" r="2" /><circle cx="30" cy="24" r="2" /></>;
    case "mineral":
      return <><path d="m24 7 14 14-8 19H17L9 21 24 7Z" /><path d="m9 21 15 5 14-5M24 7v19M17 40l7-14 6 14" /></>;
    case "biological":
      return <><path d="M24 9c9 5 14 12 12 20-2 8-10 12-18 9-8-3-10-11-6-18 3-6 8-9 12-11Z" /><path d="M17 31c5-7 10-11 17-14M22 25l-4-6M27 21l5 5" /></>;
    case "processed":
      return <><path d="m9 27 9-11h20l-8 11H9ZM9 27h21v10H9Z" /><path d="M18 16v11M30 27v10" /></>;
    case "ancient":
      return <><circle cx="24" cy="24" r="8" /><path d="M24 7v5M24 36v5M7 24h5M36 24h5M12 12l4 4M32 32l4 4M36 12l-4 4M16 32l-4 4" /><circle cx="24" cy="24" r="2" /></>;
    case "industrial":
      return <><path d="M8 39V23l10 5V18l10 6V11h7v28Z" /><path d="M13 34h4M22 34h4M31 34h4" /></>;
    case "valuable":
      return <><path d="m24 7 14 11-14 23L10 18 24 7Z" /><path d="m10 18 9 2 5-13 5 13 9-2M19 20l5 21 5-21" /></>;
    case "special":
      return <><path d="m24 7 4.5 10.5L40 19l-8.5 8 2.5 11-10-5.5L14 38l2.5-11L8 19l11.5-1.5L24 7Z" /><circle cx="24" cy="24" r="3" /></>;
    case "advanced":
      return <><path d="m24 6 15 9v18l-15 9-15-9V15l15-9Z" /><circle cx="24" cy="24" r="8" /><path d="M24 12v4M24 32v4M12 24h4M32 24h4" /></>;
    default:
      return <><path d="M8 16 24 8l16 8-16 8L8 16Z" /><path d="M8 16v20l16 6 16-6V16M24 24v18" /><path d="m16 12 16 8" /></>;
  }
}
