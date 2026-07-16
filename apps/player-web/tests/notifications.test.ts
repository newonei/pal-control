import assert from "node:assert/strict";
import test from "node:test";

import {
  getPlayerNotifications,
  markAllPlayerNotificationsRead,
  markPlayerNotificationRead,
  type PlayerNotification
} from "../src/api.ts";
import {
  browserNotificationState,
  enableBrowserNotifications,
  publishUnreadBrowserNotifications,
  type BrowserNotificationEnvironment,
  type NotificationStorage
} from "../src/browserNotifications.ts";
import {
  hasNotificationPollingActivity,
  shouldPollNotifications
} from "../src/notificationPolling.ts";

const unread: PlayerNotification = {
  notificationId: "11111111-1111-1111-1111-111111111111",
  schemaVersion: "1",
  seasonId: "22222222-2222-2222-2222-222222222222",
  sourceType: "reconciliation",
  sourceState: "uncertain",
  severity: "warning",
  title: "资源兑换结果待核对",
  message: "请勿重复购买或重复结算，等待管理员核对。",
  occurredAt: "2026-07-16T04:00:00Z",
  updatedAt: "2026-07-16T04:00:00Z",
  readAt: null,
  gameState: "blocked",
  safetyAction: "do-not-repeat-contact-support"
};

test("browser Notification permission is requested only after explicit opt-in", async () => {
  const storage = new MemoryStorage();
  let permission: NotificationPermission = "default";
  let requests = 0;
  const shown: Array<{ title: string; body: string; tag: string }> = [];
  const environment: BrowserNotificationEnvironment = {
    supported: true,
    permission: () => permission,
    requestPermission: async () => {
      requests += 1;
      permission = "granted";
      return permission;
    },
    show: (title, body, tag) => shown.push({ title, body, tag }),
    storage
  };

  assert.equal(browserNotificationState(environment), "available");
  assert.equal(requests, 0);
  assert.equal(publishUnreadBrowserNotifications([unread], environment), 0);
  assert.equal(requests, 0);

  assert.equal(await enableBrowserNotifications(environment), "enabled");
  assert.equal(requests, 1);
  assert.equal(publishUnreadBrowserNotifications([unread], environment), 1);
  assert.deepEqual(shown, [{
    title: unread.title,
    body: unread.message,
    tag: unread.notificationId
  }]);
  assert.equal(publishUnreadBrowserNotifications([unread], environment), 0);

  const persisted = Array.from(storage.values.values()).join("\n");
  assert.ok(persisted.includes(unread.notificationId));
  assert.ok(!persisted.includes(unread.title));
  assert.ok(!persisted.includes(unread.message));
});

test("denied and unsupported browsers degrade without requesting or showing", async () => {
  let requested = false;
  let shown = false;
  const denied: BrowserNotificationEnvironment = {
    supported: true,
    permission: () => "denied",
    requestPermission: async () => {
      requested = true;
      return "denied";
    },
    show: () => { shown = true; },
    storage: new MemoryStorage()
  };
  assert.equal(await enableBrowserNotifications(denied), "denied");
  assert.equal(publishUnreadBrowserNotifications([unread], denied), 0);
  assert.equal(requested, false);
  assert.equal(shown, false);

  const unsupported = { ...denied, supported: false };
  assert.equal(browserNotificationState(unsupported), "unsupported");
  assert.equal(await enableBrowserNotifications(unsupported), "unsupported");
});

test("notification API stays self-only and sends CSRF only on read writes", async () => {
  const calls: Array<{ path: string; method?: string; csrf: string | null }> = [];
  await withFetch(async (input, init) => {
    const headers = new Headers(init?.headers);
    calls.push({
      path: String(input),
      method: init?.method,
      csrf: headers.get("X-CSRF-Token")
    });
    const body = String(input).endsWith("/read-all")
      ? { markedRead: 1, unreadCount: 0 }
      : String(input).endsWith("/read")
        ? { notificationId: unread.notificationId, readAt: unread.updatedAt, unreadCount: 0 }
        : { schemaVersion: "1", unreadCount: 1, hasActiveDelivery: false, items: [unread] };
    return jsonResponse(body);
  }, async () => {
    await getPlayerNotifications(500);
    await markPlayerNotificationRead(unread.notificationId, "csrf-token");
    await markAllPlayerNotificationsRead("csrf-token");
  });

  assert.deepEqual(calls, [
    { path: "/api/v1/player/me/notifications?limit=100", method: "GET", csrf: null },
    {
      path: `/api/v1/player/me/notifications/${unread.notificationId}/read`,
      method: "POST",
      csrf: "csrf-token"
    },
    { path: "/api/v1/player/me/notifications/read-all", method: "POST", csrf: "csrf-token" }
  ]);
  assert.ok(calls.every((call) => !/[?&](accountId|userId|playerUid|steamId)=/i.test(call.path)));
});

test("notification polling is visibility-gated and stops without unread or active work", () => {
  const idleFeed = {
    schemaVersion: "1" as const,
    unreadCount: 0,
    hasActiveDelivery: false,
    items: []
  };
  assert.equal(hasNotificationPollingActivity(false, false, idleFeed), false);
  assert.equal(hasNotificationPollingActivity(true, false, idleFeed), true);
  assert.equal(hasNotificationPollingActivity(false, true, idleFeed), true);
  assert.equal(hasNotificationPollingActivity(false, false, { ...idleFeed, unreadCount: 1 }), true);
  assert.equal(shouldPollNotifications("hidden", true), false);
  assert.equal(shouldPollNotifications("visible", false), false);
  assert.equal(shouldPollNotifications("visible", true), true);
});

class MemoryStorage implements NotificationStorage {
  readonly values = new Map<string, string>();

  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}

async function withFetch(
  implementation: (input: URL | RequestInfo, init?: RequestInit) => Promise<Response>,
  action: () => Promise<void>
) {
  const previous = globalThis.fetch;
  globalThis.fetch = implementation as typeof fetch;
  try {
    await action();
  } finally {
    globalThis.fetch = previous;
  }
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" }
  });
}
