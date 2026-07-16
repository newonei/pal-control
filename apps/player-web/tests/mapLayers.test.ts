import assert from "node:assert/strict";
import test from "node:test";
import {
  DEFAULT_EXTRACTION_MAP_LAYERS,
  isZoneLayerVisible
} from "../src/mapLayers.ts";

test("map layers default to a useful complete tactical view", () => {
  assert.equal(Object.values(DEFAULT_EXTRACTION_MAP_LAYERS).every(Boolean), true);
});

test("open and closed zone visibility is independent and display-only", () => {
  const onlyOpen = { ...DEFAULT_EXTRACTION_MAP_LAYERS, closedZones: false };
  const onlyClosed = { ...DEFAULT_EXTRACTION_MAP_LAYERS, openZones: false };
  assert.equal(isZoneLayerVisible(true, onlyOpen), true);
  assert.equal(isZoneLayerVisible(false, onlyOpen), false);
  assert.equal(isZoneLayerVisible(true, onlyClosed), false);
  assert.equal(isZoneLayerVisible(false, onlyClosed), true);
});
