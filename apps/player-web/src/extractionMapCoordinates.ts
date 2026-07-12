import type { ExtractionPlayerPosition, ExtractionZone } from "./api";

export const EXTRACTION_MAP_BACKGROUND = "/palworld-map/full-map-z4.png";

const WORLD_BOUNDS = {
  minX: -999_940,
  maxX: 447_900,
  minY: -738_920,
  maxY: 708_920
} as const;

const MAP_SCALE = 459;
const WORLD_X_OFFSET = -123_888;
const WORLD_Y_OFFSET = 158_000;

export type ProjectedPoint = {
  u: number;
  v: number;
  inBounds: boolean;
};

export type WorldPoint = {
  x: number;
  y: number;
};

/**
 * PalDefender exposes the coordinates shown by the in-game map. The calibrated
 * background uses Unreal world coordinates, so map coordinates must be
 * converted before applying the same projection used by the admin live map.
 */
export function mapToWorld(mapX: number, mapY: number): WorldPoint | null {
  if (!Number.isFinite(mapX) || !Number.isFinite(mapY)) return null;
  return {
    x: mapY * MAP_SCALE + WORLD_X_OFFSET,
    y: mapX * MAP_SCALE + WORLD_Y_OFFSET
  };
}

export function resolveZoneWorldPoint(zone: ExtractionZone): WorldPoint | null {
  if (isFiniteNumber(zone.worldX) && isFiniteNumber(zone.worldY)) {
    return { x: zone.worldX, y: zone.worldY };
  }
  if (isFiniteNumber(zone.mapX) && isFiniteNumber(zone.mapY)) {
    return mapToWorld(zone.mapX, zone.mapY);
  }
  return null;
}

export function resolvePlayerWorldPoint(
  position?: ExtractionPlayerPosition | null
): WorldPoint | null {
  if (!position) return null;
  if (isFiniteNumber(position.worldX) && isFiniteNumber(position.worldY)) {
    return { x: position.worldX, y: position.worldY };
  }
  if (isFiniteNumber(position.mapX) && isFiniteNumber(position.mapY)) {
    return mapToWorld(position.mapX, position.mapY);
  }
  return null;
}

export function resolveZoneWorldRadius(zone: ExtractionZone): number | null {
  if (isFiniteNumber(zone.worldRadius) && zone.worldRadius > 0) return zone.worldRadius;
  if (isFiniteNumber(zone.radius) && zone.radius > 0) return zone.radius * MAP_SCALE;
  return null;
}

export function projectWorldPoint(point: WorldPoint): ProjectedPoint {
  // Equivalent to axisSwap=true + invertX=true in the admin live-map projection.
  const normalizedX = normalize(point.x, WORLD_BOUNDS.minX, WORLD_BOUNDS.maxX);
  const normalizedY = normalize(point.y, WORLD_BOUNDS.minY, WORLD_BOUNDS.maxY);
  const u = normalizedY;
  const v = 1 - normalizedX;
  return {
    u,
    v,
    inBounds: Number.isFinite(u) && Number.isFinite(v) && u >= 0 && u <= 1 && v >= 0 && v <= 1
  };
}

export function worldRadiusToMapPercent(radius: number): number {
  const span = WORLD_BOUNDS.maxX - WORLD_BOUNDS.minX;
  return span > 0 && Number.isFinite(radius) ? radius / span * 200 : 0;
}

export function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

function normalize(value: number, min: number, max: number) {
  return max > min ? (value - min) / (max - min) : Number.NaN;
}
