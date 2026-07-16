import assert from "node:assert/strict";
import test from "node:test";

import {
  FederationServersError,
  federationBalanceView,
  federationPortalHref,
  loadFederationServers
} from "../src/federationServers.ts";
import type {
  FederationOverview,
  FederationServer
} from "../src/federationServers.ts";

const matrixSha256 = "3edd0fe96d70a8438362afaa0d6a8b8638797988275abaa02f00538055f68342";
const compatibility = {
  combinationId: "pal-1.0.0.100427-native-dev36",
  matrixVersion: "1.0.0",
  matrixSha256,
  status: "experimental" as const,
  gameVersion: "v1.0.0.100427",
  steamBuild: "unknown",
  palDefenderVersion: "1.8.1.3933",
  ue4ssCommit: "c2ac246447a8bcd92541070cb474044e7a2bbbe6",
  nativeProtocolVersion: "1.0",
  nativeModVersion: "0.3.0-dev.36",
  bridgeAvailability: "available" as const,
  capabilities: ["bridge.handshake", "inventory.probe"],
  verifiedAt: "2026-07-15T03:07:00Z"
};

const alpha: FederationServer = {
  serverId: "alpha",
  displayName: "晨曦周世界",
  portalUrl: "https://alpha.example.test/player/",
  local: true,
  availability: "available",
  accountExists: true,
  accountDisplayName: "调查员 A",
  season: {
    code: "WEEK-01",
    displayName: "第 1 周",
    startsAt: "2026-07-14T00:00:00Z",
    endsAt: "2026-07-21T00:00:00Z",
    state: "active"
  },
  balances: { marketCoin: 1_240, seasonVoucher: 360 },
  balancesAvailable: true,
  compatibility,
  switchAvailable: false,
  errorCode: null,
  observedAt: "2026-07-16T08:00:00Z"
};

const beta: FederationServer = {
  serverId: "beta",
  displayName: "远山周世界",
  portalUrl: "https://beta.example.test/player/",
  local: false,
  availability: "unavailable",
  accountExists: null,
  accountDisplayName: null,
  season: null,
  balances: null,
  balancesAvailable: false,
  compatibility: null,
  switchAvailable: false,
  errorCode: "FEDERATION_NODE_TIMEOUT",
  observedAt: "2026-07-16T08:00:00Z"
};

const gamma: FederationServer = {
  ...beta,
  serverId: "gamma",
  displayName: "群岛周世界",
  portalUrl: "https://gamma.example.test/player/",
  availability: "incompatible",
  compatibility: { ...compatibility, matrixSha256: "0".repeat(64) },
  errorCode: "FEDERATION_COMPATIBILITY_MISMATCH"
};

const overview: FederationOverview = {
  localServerId: "alpha",
  matrixVersion: "1.0.0",
  matrixSha256,
  servers: [alpha, beta, gamma],
  observedAt: "2026-07-16T08:00:00Z"
};

test("federation read is session-derived, bodyless and uses the fixed self endpoint", async () => {
  await withFetch(async (input, init) => {
    assert.equal(String(input), "/api/v1/player/me/federation");
    assert.equal(init?.method, "GET");
    assert.equal(init?.credentials, "include");
    assert.equal(init?.cache, "no-store");
    assert.equal(init?.body, undefined);
    const headers = new Headers(init?.headers);
    assert.equal(headers.get("Accept"), "application/json");
    for (const forbidden of [
      "X-Account-Id",
      "X-Federation-Subject",
      "X-Pal-Control-Node-Key",
      "X-Player-UserId",
      "X-Steam-Id"
    ]) {
      assert.equal(headers.has(forbidden), false);
    }
    assert.equal(new URL(String(input), "https://player.invalid").search, "");
    return jsonResponse(overview);
  }, async () => {
    const result = await loadFederationServers();
    assert.equal(result.servers.length, 3);
    assert.equal(result.servers[0].balances?.marketCoin, 1_240);
  });
});

test("unavailable and incompatible nodes never render a synthetic zero balance", () => {
  for (const server of [beta, gamma]) {
    const view = federationBalanceView(server);
    assert.equal(view.available, false);
    assert.match(view.primary, /不可用/);
    assert.doesNotMatch(view.primary, /[0-9]/);
    assert.match(view.secondary, /未将失败结果解释为 0/);
  }
  assert.deepEqual(federationBalanceView(alpha), {
    primary: "1,240 商域币",
    secondary: "360 周战备券",
    available: true
  });
});

test("server switching returns only the exact allowlisted portal URL", () => {
  const remote = {
    ...alpha,
    serverId: "delta",
    local: false,
    switchAvailable: true,
    portalUrl: "https://delta.example.test/player/"
  };
  assert.equal(federationPortalHref(remote), remote.portalUrl);
  assert.equal(federationPortalHref({
    ...remote,
    portalUrl: "https://delta.example.test/player/?subject=fed1_secret"
  }), null);
  assert.equal(federationPortalHref({
    ...remote,
    portalUrl: "https://user:secret@delta.example.test/player/"
  }), null);
  assert.equal(federationPortalHref({ ...remote, availability: "unavailable" }), null);
  assert.equal(federationPortalHref(alpha), null);
});

test("raw identity-bearing response fields fail closed instead of entering UI state", async () => {
  await withFetch(async () => jsonResponse({
    ...overview,
    servers: [{ ...alpha, userId: "steam_raw_identity" }, beta, gamma]
  }), async () => {
    await assert.rejects(
      loadFederationServers(),
      (reason: unknown) => reason instanceof FederationServersError &&
        reason.status === 502 && reason.code === "FEDERATION_RESPONSE_INVALID"
    );
  });
});

test("normalized identity-bearing response fields fail closed at any nesting depth", async () => {
  for (const forbiddenKey of [
    "account_id",
    "EXTERNAL_USER_ID",
    "Node-Key",
    "player uid",
    "STEAM_id",
    "subject-token",
    "UsEr-Id"
  ]) {
    await withFetch(async () => jsonResponse({
      ...overview,
      servers: [
        alpha,
        {
          ...beta,
          nested: [{ metadata: { [forbiddenKey]: "raw_identity" } }]
        },
        gamma
      ]
    }), async () => {
      await assert.rejects(
        loadFederationServers(),
        (reason: unknown) => reason instanceof FederationServersError &&
          reason.status === 502 && reason.code === "FEDERATION_RESPONSE_INVALID",
        `${forbiddenKey} must be rejected`
      );
    });
  }
});

test("disabled federation remains an explicit 404 state", async () => {
  await withFetch(async () => jsonResponse({
    code: "FEDERATION_DISABLED",
    message: "Federation is disabled."
  }, 404), async () => {
    await assert.rejects(
      loadFederationServers(),
      (reason: unknown) => reason instanceof FederationServersError &&
        reason.status === 404 && reason.code === "FEDERATION_DISABLED"
    );
  });
});

async function withFetch(
  replacement: typeof fetch,
  action: () => Promise<void>
) {
  const original = globalThis.fetch;
  globalThis.fetch = replacement;
  try {
    await action();
  } finally {
    globalThis.fetch = original;
  }
}

function jsonResponse(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}
