import type { PlayerNotificationFeed } from "./api";

export function hasNotificationPollingActivity(
  notificationsTabActive: boolean,
  pendingEconomyActivity: boolean,
  feed: PlayerNotificationFeed | null
) {
  return notificationsTabActive || pendingEconomyActivity ||
    (feed?.unreadCount ?? 0) > 0 || feed?.hasActiveDelivery === true;
}

export function shouldPollNotifications(
  visibility: DocumentVisibilityState,
  activity: boolean
) {
  return visibility === "visible" && activity;
}
