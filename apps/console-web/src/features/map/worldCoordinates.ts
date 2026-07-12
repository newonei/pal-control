import type {
  LiveMapBounds,
  LiveMapCoordinateSpace,
  LiveMapPosition
} from "../../lib/api/client";

export const DEFAULT_WORLD_BOUNDS: LiveMapBounds = {
  minX: -999_940,
  maxX: 447_900,
  minY: -738_920,
  maxY: 708_920
};

export const DEFAULT_COORDINATE_SPACE: LiveMapCoordinateSpace = {
  mapId: "main-world",
  units: "unreal-centimeters",
  bounds: DEFAULT_WORLD_BOUNDS,
  projection: {
    axisSwap: true,
    invertX: true,
    invertY: false
  },
  backgroundUrl: "/palworld-map/full-map-z4.png"
};

export type ProjectedPosition = {
  u: number;
  v: number;
  inBounds: boolean;
};

export function resolveCoordinateSpace(
  coordinateSpace?: LiveMapCoordinateSpace | null
): LiveMapCoordinateSpace {
  if (!coordinateSpace || !isUsableBounds(coordinateSpace.bounds)) {
    return DEFAULT_COORDINATE_SPACE;
  }

  return {
    ...DEFAULT_COORDINATE_SPACE,
    ...coordinateSpace,
    bounds: coordinateSpace.bounds,
    projection: {
      ...DEFAULT_COORDINATE_SPACE.projection,
      ...coordinateSpace.projection
    },
    backgroundUrl: coordinateSpace.backgroundUrl || DEFAULT_COORDINATE_SPACE.backgroundUrl
  };
}

export function projectWorldPosition(
  position: LiveMapPosition,
  coordinateSpace: LiveMapCoordinateSpace
): ProjectedPosition {
  const { bounds, projection } = resolveCoordinateSpace(coordinateSpace);
  let x = normalize(position.x, bounds.minX, bounds.maxX);
  let y = normalize(position.y, bounds.minY, bounds.maxY);

  if (projection.invertX) {
    x = 1 - x;
  }
  if (projection.invertY) {
    y = 1 - y;
  }

  const u = projection.axisSwap ? y : x;
  const v = projection.axisSwap ? x : y;

  return {
    u,
    v,
    inBounds: Number.isFinite(u) && Number.isFinite(v) &&
      u >= 0 && u <= 1 && v >= 0 && v <= 1
  };
}

export function formatWorldCoordinate(value: number): string {
  if (!Number.isFinite(value)) {
    return "--";
  }
  return Math.round(value).toLocaleString("zh-CN");
}

function normalize(value: number, min: number, max: number): number {
  if (!Number.isFinite(value) || max <= min) {
    return Number.NaN;
  }
  return (value - min) / (max - min);
}

function isUsableBounds(bounds: LiveMapBounds): boolean {
  return Number.isFinite(bounds.minX) && Number.isFinite(bounds.maxX) &&
    Number.isFinite(bounds.minY) && Number.isFinite(bounds.maxY) &&
    bounds.maxX > bounds.minX && bounds.maxY > bounds.minY;
}
