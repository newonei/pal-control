import assert from "node:assert/strict";
import test from "node:test";
import {
  assertRequiredDynamicEntries,
  classifyManifestAssets,
  evaluateBundleBudget
} from "./check-bundle-budget.mjs";

const budgets = {
  initialJavaScriptKiB: 250,
  maxAsyncJavaScriptKiB: 80,
  totalJavaScriptKiB: 600,
  initialCssKiB: 170,
  maxAsyncCssKiB: 20,
  totalCssKiB: 220
};

const groups = {
  initialJavaScript: [{ file: "assets/index.js", bytes: 220 * 1024 }],
  asyncJavaScript: [{ file: "assets/economy.js", bytes: 50 * 1024 }],
  initialCss: [{ file: "assets/index.css", bytes: 150 * 1024 }],
  asyncCss: [{ file: "assets/economy.css", bytes: 10 * 1024 }]
};

test("accepts a bundle at or below every explicit limit", () => {
  const result = evaluateBundleBudget(groups, budgets);
  assert.equal(result.passed, true);
  assert.equal(result.metrics.initialJavaScriptKiB, 220);
  assert.equal(result.metrics.totalJavaScriptKiB, 270);
});

test("fails when an individual async chunk exceeds its limit", () => {
  const result = evaluateBundleBudget({
    ...groups,
    asyncJavaScript: [{ file: "assets/economy.js", bytes: 81 * 1024 }]
  }, budgets);
  assert.equal(result.passed, false);
  assert.deepEqual(result.checks.filter((check) => !check.passed).map((check) => check.key), ["maxAsyncJavaScriptKiB"]);
});

test("fails when aggregate JavaScript grows beyond its limit", () => {
  const result = evaluateBundleBudget({
    ...groups,
    asyncJavaScript: Array.from({ length: 8 }, (_, index) => ({
      file: `assets/feature-${index}.js`,
      bytes: 50 * 1024
    }))
  }, budgets);
  assert.equal(result.passed, false);
  assert.equal(result.checks.find((check) => check.key === "totalJavaScriptKiB")?.passed, false);
});

test("classifies recursively imported entry assets as initial and feature assets as async", () => {
  const manifest = {
    "index.html": {
      file: "assets/index.js",
      isEntry: true,
      imports: ["_vendor.js"],
      css: ["assets/index.css"]
    },
    "_vendor.js": { file: "assets/vendor.js" },
    "src/economy.tsx": {
      file: "assets/economy.js",
      isDynamicEntry: true,
      imports: ["index.html"],
      css: ["assets/economy.css"]
    }
  };
  const assets = [
    { file: "assets/index.js", bytes: 1 },
    { file: "assets/vendor.js", bytes: 1 },
    { file: "assets/economy.js", bytes: 1 },
    { file: "assets/index.css", bytes: 1 },
    { file: "assets/economy.css", bytes: 1 }
  ];
  const classified = classifyManifestAssets(manifest, assets);
  assert.deepEqual(classified.initialJavaScript.map((asset) => asset.file), ["assets/index.js", "assets/vendor.js"]);
  assert.deepEqual(classified.asyncJavaScript.map((asset) => asset.file), ["assets/economy.js"]);
  assert.deepEqual(classified.initialCss.map((asset) => asset.file), ["assets/index.css"]);
  assert.deepEqual(classified.asyncCss.map((asset) => asset.file), ["assets/economy.css"]);
});

test("refuses to pass an unclassifiable manifest", () => {
  assert.throws(() => classifyManifestAssets({}, []), /does not contain an entry chunk/);
  assert.throws(() => evaluateBundleBudget({ ...groups, initialJavaScript: [] }, budgets), /No initial JavaScript/);
});

test("requires named feature pages to remain dynamic entries", () => {
  const manifest = {
    "src/economy.tsx": { file: "assets/economy.js", isDynamicEntry: true },
    "src/analytics.tsx": { file: "assets/analytics.js" }
  };
  assert.doesNotThrow(() => assertRequiredDynamicEntries(manifest, ["src/economy.tsx"]));
  assert.throws(
    () => assertRequiredDynamicEntries(manifest, ["src/economy.tsx", "src/analytics.tsx"]),
    /src\/analytics\.tsx/
  );
  assert.throws(() => assertRequiredDynamicEntries(manifest, []), /at least one/);
});
