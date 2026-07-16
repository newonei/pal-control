export type ExtractionMapLayers = {
  openZones: boolean;
  closedZones: boolean;
  hotspots: boolean;
  risk: boolean;
  route: boolean;
  ownPosition: boolean;
};

export const DEFAULT_EXTRACTION_MAP_LAYERS: ExtractionMapLayers = {
  openZones: true,
  closedZones: true,
  hotspots: true,
  risk: true,
  route: true,
  ownPosition: true
};

export function isZoneLayerVisible(open: boolean | undefined, layers: ExtractionMapLayers) {
  return open === false ? layers.closedZones : layers.openZones;
}
