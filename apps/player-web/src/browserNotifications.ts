import type { PlayerNotification } from "./api";

const preferenceKey = "pal-player-browser-notifications-v1";
const seenKey = "pal-player-browser-notifications-seen-v1";
const sessionSeen = new Set<string>();

export type BrowserNotificationState = "unsupported" | "denied" | "available" | "enabled";

export type NotificationStorage = {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
};

export type BrowserNotificationEnvironment = {
  supported: boolean;
  permission: () => NotificationPermission;
  requestPermission: () => Promise<NotificationPermission>;
  show: (title: string, body: string, tag: string) => void;
  storage: NotificationStorage | null;
};

export function createBrowserNotificationEnvironment(): BrowserNotificationEnvironment {
  const supported = typeof Notification !== "undefined";
  return {
    supported,
    permission: () => supported ? Notification.permission : "denied",
    requestPermission: () => supported
      ? Notification.requestPermission()
      : Promise.resolve("denied"),
    show: (title, body, tag) => {
      if (!supported) return;
      new Notification(title, { body, tag });
    },
    storage: safeStorage()
  };
}

export function browserNotificationState(
  environment: BrowserNotificationEnvironment
): BrowserNotificationState {
  if (!environment.supported) return "unsupported";
  if (environment.permission() === "denied") return "denied";
  return environment.permission() === "granted" && readPreference(environment.storage) === "enabled"
    ? "enabled"
    : "available";
}

export async function enableBrowserNotifications(
  environment: BrowserNotificationEnvironment
): Promise<BrowserNotificationState> {
  if (!environment.supported) return "unsupported";
  const permission = environment.permission() === "default"
    ? await environment.requestPermission()
    : environment.permission();
  if (permission !== "granted") return permission === "denied" ? "denied" : "available";
  writePreference(environment.storage, "enabled");
  return "enabled";
}

export function disableBrowserNotifications(environment: BrowserNotificationEnvironment) {
  writePreference(environment.storage, "disabled");
  return browserNotificationState(environment);
}

export function publishUnreadBrowserNotifications(
  items: PlayerNotification[],
  environment: BrowserNotificationEnvironment
): number {
  if (browserNotificationState(environment) !== "enabled" ||
      environment.permission() !== "granted") return 0;
  const seen = readSeen(environment.storage);
  const pending = items
    .filter((item) => item.readAt === null && !seen.has(item.notificationId))
    .slice(0, 3);
  let published = 0;
  for (const item of pending) {
    try {
      environment.show(item.title, item.message, item.notificationId);
      seen.add(item.notificationId);
      sessionSeen.add(item.notificationId);
      published += 1;
    } catch {
      // The in-app feed remains authoritative when the browser OS channel fails.
    }
  }
  writeSeen(environment.storage, seen);
  return published;
}

function safeStorage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

function readPreference(storage: NotificationStorage | null) {
  try {
    return storage?.getItem(preferenceKey) ?? "disabled";
  } catch {
    return "disabled";
  }
}

function writePreference(storage: NotificationStorage | null, value: "enabled" | "disabled") {
  try {
    storage?.setItem(preferenceKey, value);
  } catch {
    // Storage denial must not affect the in-app notification center.
  }
}

function readSeen(storage: NotificationStorage | null) {
  const seen = new Set(sessionSeen);
  try {
    const parsed = JSON.parse(storage?.getItem(seenKey) ?? "[]") as unknown;
    if (Array.isArray(parsed)) {
      for (const value of parsed.slice(-100)) {
        if (typeof value === "string" && /^[0-9a-f-]{36}$/i.test(value)) seen.add(value);
      }
    }
  } catch {
    // Corrupt or unavailable local state is ignored; no notification content is stored.
  }
  return seen;
}

function writeSeen(storage: NotificationStorage | null, seen: Set<string>) {
  try {
    storage?.setItem(seenKey, JSON.stringify(Array.from(seen).slice(-100)));
  } catch {
    // Session memory still prevents a tight duplicate loop.
  }
}
