import { useEffect, useMemo, useState } from "react";
import type { PlayerNotification, PlayerNotificationFeed } from "./api";
import {
  browserNotificationState,
  createBrowserNotificationEnvironment,
  disableBrowserNotifications,
  enableBrowserNotifications,
  publishUnreadBrowserNotifications,
  type BrowserNotificationEnvironment,
  type BrowserNotificationState
} from "./browserNotifications";

type Props = {
  feed: PlayerNotificationFeed | null;
  loading: boolean;
  error: string | null;
  busy: boolean;
  onRetry: () => void;
  onRead: (notificationId: string) => void;
  onReadAll: () => void;
};

export function NotificationCenter({
  feed,
  loading,
  error,
  busy,
  onRetry,
  onRead,
  onReadAll
}: Props) {
  const environment = useMemo<BrowserNotificationEnvironment>(
    () => createBrowserNotificationEnvironment(),
    []
  );
  const [browserState, setBrowserState] = useState<BrowserNotificationState>(
    () => browserNotificationState(environment)
  );

  useEffect(() => {
    if (!feed) return;
    publishUnreadBrowserNotifications(feed.items, environment);
    setBrowserState(browserNotificationState(environment));
  }, [environment, feed]);

  async function toggleBrowserNotifications() {
    if (browserState === "enabled") {
      setBrowserState(disableBrowserNotifications(environment));
      return;
    }
    setBrowserState(await enableBrowserNotifications(environment));
  }

  return (
    <section className="notification-center" aria-labelledby="notification-center-title">
      <header className="notification-controls">
        <div>
          <span className="eyebrow">SELF-ONLY ACTIVITY FEED</span>
          <h3 id="notification-center-title">站内消息与投递状态</h3>
          <p>站内消息始终保留；浏览器系统提醒需要你主动授权。</p>
        </div>
        <div className="notification-actions">
          <span className={`browser-notification-state ${browserState}`} role="status">
            {browserStateLabel(browserState)}
          </span>
          <button
            type="button"
            className="secondary"
            disabled={browserState === "unsupported" || browserState === "denied"}
            onClick={() => void toggleBrowserNotifications()}
          >
            {browserState === "enabled" ? "关闭浏览器提醒" : "启用浏览器提醒"}
          </button>
          <button
            type="button"
            className="secondary"
            disabled={busy || !feed?.unreadCount}
            onClick={onReadAll}
          >
            全部标为已读
          </button>
        </div>
      </header>

      {loading && <div className="notification-message" role="status">正在读取消息…</div>}
      {!loading && error && (
        <div className="notification-message error" role="alert">
          <span><strong>消息暂时无法读取</strong><small>{error}</small></span>
          <button className="secondary compact" onClick={onRetry}>重新加载</button>
        </div>
      )}
      {!loading && !error && !feed?.items.length && (
        <div className="notification-empty">
          <span aria-hidden="true">铃</span>
          <strong>暂无消息</strong>
          <p>订单送达、资源结算、周档结束或需要核对时会出现在这里。</p>
        </div>
      )}
      {!loading && !error && feed && feed.items.length > 0 && (
        <>
          <div className="notification-summary" role="status" aria-live="polite">
            <strong>{feed.unreadCount > 0 ? `${feed.unreadCount} 条未读` : "消息均已读"}</strong>
            <span>{feed.hasActiveDelivery ? "游戏内提醒正在排队" : "没有正在处理的提醒"}</span>
          </div>
          <ol className="notification-list">
            {feed.items.map((item) => (
              <NotificationItem
                key={item.notificationId}
                item={item}
                busy={busy}
                onRead={onRead}
              />
            ))}
          </ol>
        </>
      )}
    </section>
  );
}

function NotificationItem({
  item,
  busy,
  onRead
}: {
  item: PlayerNotification;
  busy: boolean;
  onRead: (notificationId: string) => void;
}) {
  return (
    <li className={`${item.severity} ${item.readAt ? "read" : "unread"}`}>
      <div className="notification-icon" aria-hidden="true">{sourceIcon(item.sourceType)}</div>
      <div className="notification-copy">
        <div className="notification-meta">
          <span>{sourceTypeLabel(item.sourceType)}</span>
          <time dateTime={item.occurredAt}>{formatNotificationTime(item.occurredAt)}</time>
        </div>
        <h3>{item.title}</h3>
        <p>{item.message}</p>
        {item.safetyAction === "do-not-repeat-contact-support" && (
          <strong className="notification-safety">请勿重复购买或重复结算，等待管理员核对。</strong>
        )}
        <small className={`game-delivery ${item.gameState}`}>
          {gameStateLabel(item.gameState)}
        </small>
      </div>
      {!item.readAt && (
        <button
          type="button"
          className="secondary compact"
          disabled={busy}
          onClick={() => onRead(item.notificationId)}
        >
          标为已读
        </button>
      )}
    </li>
  );
}

function browserStateLabel(state: BrowserNotificationState) {
  return ({
    unsupported: "当前浏览器不支持系统提醒，站内消息仍可用",
    denied: "系统提醒权限已被拒绝，站内消息仍可用",
    available: "浏览器提醒未启用",
    enabled: "浏览器提醒已启用"
  } as const)[state];
}

function sourceTypeLabel(type: PlayerNotification["sourceType"]) {
  return ({
    "order-delivery": "战备发货",
    "resource-settlement": "资源兑换",
    "season-end": "周档结算",
    reconciliation: "异常核对"
  } as const)[type];
}

function sourceIcon(type: PlayerNotification["sourceType"]) {
  return ({
    "order-delivery": "箱",
    "resource-settlement": "券",
    "season-end": "档",
    reconciliation: "核"
  } as const)[type];
}

function gameStateLabel(state: PlayerNotification["gameState"]) {
  return ({
    pending: "游戏内提醒等待投递",
    queued: "游戏内提醒已排队，尚未确认送达",
    sent: "游戏内提醒已提交游戏客户端",
    blocked: "游戏内提醒能力不可用，已保留站内消息",
    failed: "游戏内提醒失败，已保留站内消息",
    uncertain: "游戏内提醒结果不确定，不会自动重发",
    "not-requested": "仅站内消息"
  } as const)[state];
}

function formatNotificationTime(value: string) {
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  }).format(new Date(value));
}
