import { readdir, readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const KIB = 1024;
const REQUIRED_BUDGETS = [
  "initialJavaScriptKiB",
  "maxAsyncJavaScriptKiB",
  "totalJavaScriptKiB",
  "initialCssKiB",
  "maxAsyncCssKiB",
  "totalCssKiB"
];

export function classifyManifestAssets(manifest, assets) {
  const entries = Object.entries(manifest).filter(([, item]) => item?.isEntry === true);
  if (entries.length === 0) {
    throw new Error("Vite manifest does not contain an entry chunk; bundle classification cannot continue.");
  }

  const initialManifestKeys = new Set();
  const visit = (key) => {
    if (initialManifestKeys.has(key)) return;
    const item = manifest[key];
    if (!item) throw new Error(`Vite manifest references missing static import: ${key}`);
    initialManifestKeys.add(key);
    for (const importedKey of item.imports ?? []) visit(importedKey);
  };
  for (const [key] of entries) visit(key);

  const initialFiles = new Set();
  for (const key of initialManifestKeys) {
    const item = manifest[key];
    if (item.file) initialFiles.add(item.file);
    for (const cssFile of item.css ?? []) initialFiles.add(cssFile);
  }

  const knownFiles = new Set(assets.map((asset) => asset.file));
  for (const file of initialFiles) {
    if (!knownFiles.has(file)) throw new Error(`Manifest asset is missing from dist: ${file}`);
  }

  const byExtension = (extension) => assets.filter((asset) => asset.file.endsWith(extension));
  const scripts = byExtension(".js");
  const styles = byExtension(".css");
  return {
    initialJavaScript: scripts.filter((asset) => initialFiles.has(asset.file)),
    asyncJavaScript: scripts.filter((asset) => !initialFiles.has(asset.file)),
    initialCss: styles.filter((asset) => initialFiles.has(asset.file)),
    asyncCss: styles.filter((asset) => !initialFiles.has(asset.file))
  };
}

export function assertRequiredDynamicEntries(manifest, requiredEntries) {
  if (!Array.isArray(requiredEntries) || requiredEntries.length === 0) {
    throw new Error("Bundle budget must declare at least one requiredDynamicEntries item.");
  }
  const missing = requiredEntries.filter((key) => manifest[key]?.isDynamicEntry !== true);
  if (missing.length > 0) {
    throw new Error(`Required feature modules are not dynamic entries: ${missing.join(", ")}`);
  }
}

export function evaluateBundleBudget(groups, budgets) {
  for (const key of REQUIRED_BUDGETS) {
    if (!Number.isFinite(budgets[key]) || budgets[key] <= 0) {
      throw new Error(`Bundle budget ${key} must be a positive number.`);
    }
  }
  if (groups.initialJavaScript.length === 0) {
    throw new Error("No initial JavaScript asset was classified; refusing to pass the budget.");
  }

  const sum = (items) => items.reduce((total, item) => total + item.bytes, 0);
  const max = (items) => items.reduce((largest, item) => Math.max(largest, item.bytes), 0);
  const metrics = {
    initialJavaScriptKiB: sum(groups.initialJavaScript) / KIB,
    maxAsyncJavaScriptKiB: max(groups.asyncJavaScript) / KIB,
    totalJavaScriptKiB: (sum(groups.initialJavaScript) + sum(groups.asyncJavaScript)) / KIB,
    initialCssKiB: sum(groups.initialCss) / KIB,
    maxAsyncCssKiB: max(groups.asyncCss) / KIB,
    totalCssKiB: (sum(groups.initialCss) + sum(groups.asyncCss)) / KIB
  };
  const checks = REQUIRED_BUDGETS.map((key) => ({
    key,
    actualKiB: metrics[key],
    limitKiB: budgets[key],
    passed: metrics[key] <= budgets[key]
  }));
  return { metrics, checks, passed: checks.every((check) => check.passed) };
}

async function listAssets(directory, root = directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const result = [];
  for (const entry of entries) {
    const absolute = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      result.push(...await listAssets(absolute, root));
      continue;
    }
    if (!entry.isFile() || (!entry.name.endsWith(".js") && !entry.name.endsWith(".css"))) continue;
    const details = await stat(absolute);
    result.push({
      file: path.relative(root, absolute).split(path.sep).join("/"),
      bytes: details.size
    });
  }
  return result;
}

function formatKiB(value) {
  return `${value.toFixed(2)} KiB`;
}

export async function checkBundleBudget(rootDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..")) {
  const distDirectory = path.join(rootDirectory, "dist");
  const [manifestText, budgetText, assets] = await Promise.all([
    readFile(path.join(distDirectory, ".vite", "manifest.json"), "utf8"),
    readFile(path.join(rootDirectory, "bundle-budget.json"), "utf8"),
    listAssets(distDirectory)
  ]);
  const manifest = JSON.parse(manifestText);
  const budgets = JSON.parse(budgetText);
  assertRequiredDynamicEntries(manifest, budgets.requiredDynamicEntries);
  const groups = classifyManifestAssets(manifest, assets);
  const result = evaluateBundleBudget(groups, budgets);

  console.log("\nConsole bundle budget (uncompressed):");
  for (const check of result.checks) {
    const marker = check.passed ? "PASS" : "FAIL";
    console.log(`  ${marker} ${check.key}: ${formatKiB(check.actualKiB)} <= ${formatKiB(check.limitKiB)}`);
  }

  const largest = [...groups.initialJavaScript, ...groups.asyncJavaScript, ...groups.initialCss, ...groups.asyncCss]
    .sort((left, right) => right.bytes - left.bytes)
    .slice(0, 6);
  console.log("  Largest assets:");
  for (const asset of largest) console.log(`    ${formatKiB(asset.bytes / KIB)}  ${asset.file}`);

  if (!result.passed) {
    const failures = result.checks.filter((check) => !check.passed).map((check) => check.key).join(", ");
    throw new Error(`Console bundle budget exceeded: ${failures}`);
  }
  return result;
}

const invokedPath = process.argv[1] ? pathToFileURL(path.resolve(process.argv[1])).href : "";
if (invokedPath === import.meta.url) {
  checkBundleBudget().catch((error) => {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  });
}
