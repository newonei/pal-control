import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PointerEvent as ReactPointerEvent
} from "react";
import {
  getLiveMapEventsUrl,
  getLiveMapSnapshot,
  parseLiveMapSnapshot,
  type LiveMapPlayer,
  type LiveMapSnapshot,
  type LiveMapStatus
} from "../../lib/api/client";
import {
  formatWorldCoordinate,
  projectWorldPosition,
  resolveCoordinateSpace,
  type ProjectedPosition
} from "./worldCoordinates";

type ConnectionMode = "connecting" | "streaming" | "polling" | "offline";
type PlayerHealth = "fresh" | "stale" | "unavailable";

type ViewState = {
  zoom: number;
  panX: number;
  panY: number;
};

type SizedPlayer = {
  player: LiveMapPlayer;
  projected: ProjectedPosition;
  health: PlayerHealth;
};

const MIN_ZOOM = 0.65;
const MAX_ZOOM = 6;

export function LivePlayerMap({ serverId = "local" }: { serverId?: string }) {
  const [snapshot, setSnapshot] = useState<LiveMapSnapshot | null>(null);
  const [connection, setConnection] = useState<ConnectionMode>("connecting");
  const [lastError, setLastError] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [selectedPlayerId, setSelectedPlayerId] = useState<string | null>(null);
  const [followPlayerId, setFollowPlayerId] = useState<string | null>(null);
  const [backgroundFailed, setBackgroundFailed] = useState(false);
  const [view, setView] = useState<ViewState>({ zoom: 1, panX: 0, panY: 0 });
  const [viewportSize, setViewportSize] = useState({ width: 0, height: 0 });
  const [dragging, setDragging] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const viewportRef = useRef<HTMLDivElement>(null);
  const dragRef = useRef<{ pointerId: number; x: number; y: number } | null>(null);

  useEffect(() => {
    const timer = globalThis.setInterval(() => setNow(Date.now()), 1_000);
    return () => globalThis.clearInterval(timer);
  }, []);

  useEffect(() => {
    const viewport = viewportRef.current;
    if (!viewport) {
      return;
    }

    const updateSize = () => {
      const rectangle = viewport.getBoundingClientRect();
      setViewportSize({ width: rectangle.width, height: rectangle.height });
    };
    updateSize();

    const observer = new ResizeObserver(updateSize);
    observer.observe(viewport);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    let disposed = false;
    let streamOpen = false;
    let polling = false;
    let pollTimer: number | undefined;
    let activeStreamId: string | null = null;
    let lastSequence = -1;
    let lastGeneratedAt = 0;
    let hasSnapshot = false;
    const controller = new AbortController();
    const events = new EventSource(getLiveMapEventsUrl(serverId));

    const commit = (next: LiveMapSnapshot) => {
      const generatedAt = Date.parse(next.generatedAt) || Date.now();
      if (activeStreamId !== next.streamId) {
        activeStreamId = next.streamId;
        lastSequence = -1;
        lastGeneratedAt = 0;
      }
      if (next.sequence < lastSequence ||
        (next.sequence === lastSequence && generatedAt < lastGeneratedAt)) {
        return;
      }
      lastSequence = next.sequence;
      lastGeneratedAt = generatedAt;
      hasSnapshot = true;
      if (!disposed) {
        setSnapshot(next);
        setLastError(null);
      }
    };

    const pullSnapshot = async () => {
      try {
        commit(await getLiveMapSnapshot(serverId, controller.signal));
        if (!streamOpen && !disposed) {
          setConnection("polling");
        }
      } catch (error) {
        if (controller.signal.aborted || disposed) {
          return;
        }
        setLastError(error instanceof Error ? error.message : "实时地图暂时不可用");
        if (!hasSnapshot) {
          setConnection("offline");
        }
      }
    };

    const poll = async () => {
      if (!polling || disposed) {
        return;
      }
      await pullSnapshot();
      if (polling && !disposed) {
        pollTimer = globalThis.setTimeout(
          () => void poll(),
          Math.max(1_000, Math.min(5_000, snapshot?.sampleIntervalMs ?? 1_000))
        );
      }
    };

    const startPolling = () => {
      if (polling || disposed) {
        return;
      }
      polling = true;
      void poll();
    };

    const stopPolling = () => {
      polling = false;
      if (pollTimer !== undefined) {
        globalThis.clearTimeout(pollTimer);
        pollTimer = undefined;
      }
    };

    const acceptEvent = (event: MessageEvent<string>) => {
      try {
        const next = parseLiveMapSnapshot(JSON.parse(event.data));
        if (next) {
          commit(next);
        }
      } catch {
        // Heartbeats and forward-compatible event types are intentionally ignored.
      }
    };

    events.addEventListener("snapshot", acceptEvent as EventListener);
    events.onmessage = acceptEvent;
    events.onopen = () => {
      streamOpen = true;
      stopPolling();
      if (!disposed) {
        setConnection("streaming");
        setLastError(null);
      }
    };
    events.onerror = () => {
      streamOpen = false;
      if (!disposed) {
        setConnection("polling");
        startPolling();
      }
    };

    void pullSnapshot();

    return () => {
      disposed = true;
      controller.abort();
      stopPolling();
      events.close();
    };
  // Each subscription owns all timers/controllers so React StrictMode can safely remount it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverId]);

  const coordinateSpace = useMemo(
    () => resolveCoordinateSpace(snapshot?.coordinateSpace),
    [snapshot?.coordinateSpace]
  );
  const backgroundUrl = coordinateSpace.backgroundUrl ?? "/palworld-map/full-map-z4.png";

  useEffect(() => setBackgroundFailed(false), [backgroundUrl]);

  const sizedPlayers = useMemo<SizedPlayer[]>(() => {
    const globalHealth = statusRank(snapshot?.status ?? "unavailable");
    return (snapshot?.items ?? []).map((player) => {
      const localHealth = getPlayerHealth(player, snapshot, now);
      return {
        player,
        projected: projectWorldPosition(player.position, coordinateSpace),
        health: healthFromRank(Math.max(globalHealth, statusRank(localHealth)))
      };
    });
  }, [coordinateSpace, now, snapshot]);

  const normalizedQuery = query.trim().toLocaleLowerCase();
  const visiblePlayers = useMemo(
    () => sizedPlayers.filter(({ player }) =>
      `${player.name} ${player.uid ?? ""} ${player.playerId}`
        .toLocaleLowerCase()
        .includes(normalizedQuery)
    ),
    [normalizedQuery, sizedPlayers]
  );
  const mappedPlayers = visiblePlayers.filter(({ projected }) => projected.inBounds);
  const otherPlayers = visiblePlayers.filter(({ projected }) => !projected.inBounds);
  const selected = sizedPlayers.find(({ player }) => player.playerId === selectedPlayerId);
  const mapSize = Math.max(1, Math.min(viewportSize.width, viewportSize.height));
  const originX = (viewportSize.width - mapSize) / 2;
  const originY = (viewportSize.height - mapSize) / 2;

  useEffect(() => {
    if (!followPlayerId || viewportSize.width <= 0 || viewportSize.height <= 0) {
      return;
    }
    const followed = sizedPlayers.find(({ player }) => player.playerId === followPlayerId);
    if (!followed?.projected.inBounds) {
      setFollowPlayerId(null);
      return;
    }
    setView((current) => ({
      ...current,
      panX: viewportSize.width / 2 - originX - followed.projected.u * mapSize * current.zoom,
      panY: viewportSize.height / 2 - originY - followed.projected.v * mapSize * current.zoom
    }));
  }, [followPlayerId, mapSize, originX, originY, sizedPlayers, viewportSize.height, viewportSize.width]);

  const zoomAt = useCallback((nextZoom: number, clientX?: number, clientY?: number) => {
    const viewport = viewportRef.current;
    if (!viewport) {
      return;
    }
    const rectangle = viewport.getBoundingClientRect();
    const focusX = clientX === undefined ? rectangle.width / 2 : clientX - rectangle.left;
    const focusY = clientY === undefined ? rectangle.height / 2 : clientY - rectangle.top;
    setFollowPlayerId(null);
    setView((current) => {
      const zoom = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, nextZoom));
      const ratio = zoom / current.zoom;
      return {
        zoom,
        panX: focusX - originX - (focusX - originX - current.panX) * ratio,
        panY: focusY - originY - (focusY - originY - current.panY) * ratio
      };
    });
  }, [originX, originY]);

  useEffect(() => {
    const viewport = viewportRef.current;
    if (!viewport) {
      return;
    }
    const handleWheel = (event: WheelEvent) => {
      event.preventDefault();
      const factor = event.deltaY < 0 ? 1.18 : 1 / 1.18;
      zoomAt(view.zoom * factor, event.clientX, event.clientY);
    };
    viewport.addEventListener("wheel", handleWheel, { passive: false });
    return () => viewport.removeEventListener("wheel", handleWheel);
  }, [view.zoom, zoomAt]);

  const fitMap = useCallback(() => {
    setFollowPlayerId(null);
    setView({ zoom: 1, panX: 0, panY: 0 });
  }, []);

  function followPlayer(playerId: string) {
    setSelectedPlayerId(playerId);
    setFollowPlayerId(playerId);
    setView((current) => ({ ...current, zoom: Math.max(2, current.zoom) }));
  }

  function handlePointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    if (event.button !== 0) {
      return;
    }
    event.currentTarget.setPointerCapture(event.pointerId);
    dragRef.current = { pointerId: event.pointerId, x: event.clientX, y: event.clientY };
    setDragging(true);
  }

  function handlePointerMove(event: ReactPointerEvent<HTMLDivElement>) {
    const drag = dragRef.current;
    if (!drag || drag.pointerId !== event.pointerId) {
      return;
    }
    const deltaX = event.clientX - drag.x;
    const deltaY = event.clientY - drag.y;
    drag.x = event.clientX;
    drag.y = event.clientY;
    if (deltaX || deltaY) {
      setFollowPlayerId(null);
      setView((current) => ({
        ...current,
        panX: current.panX + deltaX,
        panY: current.panY + deltaY
      }));
    }
  }

  function endPointer(event: ReactPointerEvent<HTMLDivElement>) {
    if (dragRef.current?.pointerId !== event.pointerId) {
      return;
    }
    dragRef.current = null;
    setDragging(false);
    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  }

  const observedAt = snapshot?.observedAt ?? snapshot?.generatedAt;
  const bounds = coordinateSpace.bounds;

  return (
    <section className="live-map-page">
      <header className="live-map-heading">
        <div>
          <p className="eyebrow">LIVE PLAYER POSITIONS</p>
          <h2>在线玩家实时地图</h2>
          <p>位置只读采集；点击玩家即可定位并持续跟随。</p>
        </div>
        <div className="live-map-summary">
          <ConnectionBadge mode={connection} />
          <span><strong>{snapshot?.items.length ?? 0}</strong> 名玩家</span>
          <span>更新 {formatObservedAt(observedAt, now)}</span>
        </div>
      </header>

      <div className="live-map-layout">
        <div className="live-map-main">
          <div className="live-map-toolbar">
            <div className="map-source-labels">
              <span>来源 <strong>{snapshot?.source ?? "official-rest"}</strong></span>
              <span>边界 <strong>{snapshot ? "服务器校准值" : "主地图默认校准值"}</strong></span>
              <span>地图 <strong>{coordinateSpace.mapId}</strong></span>
            </div>
            <div className="map-view-actions" aria-label="地图缩放工具">
              <button onClick={() => zoomAt(view.zoom / 1.3)} aria-label="缩小地图">−</button>
              <span>{Math.round(view.zoom * 100)}%</span>
              <button onClick={() => zoomAt(view.zoom * 1.3)} aria-label="放大地图">＋</button>
              <button className="fit" onClick={fitMap}>适应地图</button>
            </div>
          </div>

          <div
            className={`live-map-viewport${dragging ? " dragging" : ""}`}
            ref={viewportRef}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={endPointer}
            onPointerCancel={endPointer}
          >
            <div
              className="live-map-plane"
              style={{
                width: mapSize,
                height: mapSize,
                transform: `translate3d(${originX + view.panX}px, ${originY + view.panY}px, 0) scale(${view.zoom})`
              }}
            >
              {!backgroundFailed ? (
                <img
                  alt="Palworld 主地图"
                  className="live-map-background"
                  draggable={false}
                  onError={() => setBackgroundFailed(true)}
                  src={backgroundUrl}
                />
              ) : null}
              <div className={`live-map-grid${backgroundFailed ? " fallback" : ""}`} aria-hidden="true" />
              <span className="map-bound map-bound-top">X {formatWorldCoordinate(bounds.maxX)}</span>
              <span className="map-bound map-bound-bottom">X {formatWorldCoordinate(bounds.minX)}</span>
              <span className="map-bound map-bound-left">Y {formatWorldCoordinate(bounds.minY)}</span>
              <span className="map-bound map-bound-right">Y {formatWorldCoordinate(bounds.maxY)}</span>

              {mappedPlayers.map(({ player, projected, health }) => (
                <button
                  aria-label={`跟随玩家 ${player.name}`}
                  className={`live-player-marker ${health}${selectedPlayerId === player.playerId ? " selected" : ""}`}
                  key={player.playerId}
                  onClick={(event) => {
                    event.stopPropagation();
                    followPlayer(player.playerId);
                  }}
                  onPointerDown={(event) => event.stopPropagation()}
                  style={{
                    left: `${projected.u * 100}%`,
                    top: `${projected.v * 100}%`,
                    transform: `translate(-50%, -50%) scale(${1 / view.zoom})`
                  }}
                  type="button"
                >
                  <span className="marker-pulse" />
                  <span className="marker-pin">{player.name.slice(0, 1).toLocaleUpperCase()}</span>
                  <span className="marker-label">
                    <strong>{player.name}</strong>
                    <small>Lv.{player.level ?? "--"}</small>
                  </span>
                </button>
              ))}
            </div>

            {!snapshot ? (
              <div className="live-map-empty-state">
                <span className="map-loader" />
                <strong>{connection === "offline" ? "地图数据暂不可用" : "正在连接实时位置流"}</strong>
                <small>{lastError ?? "等待第一份玩家位置快照"}</small>
              </div>
            ) : snapshot.items.length === 0 ? (
              <div className="live-map-empty-state compact">
                <strong>当前没有在线玩家</strong>
                <small>地图会在玩家加入后自动更新</small>
              </div>
            ) : null}

            <div className="live-map-legend">
              <span><i className="fresh" />实时</span>
              <span><i className="stale" />延迟</span>
              <span><i className="unavailable" />不可用</span>
            </div>
            {followPlayerId ? (
              <button className="follow-indicator" onClick={() => setFollowPlayerId(null)}>
                跟随中 · {sizedPlayers.find(({ player }) => player.playerId === followPlayerId)?.player.name}
                <span>停止</span>
              </button>
            ) : null}
          </div>

          <footer className="live-map-footer">
            <span>
              X {formatWorldCoordinate(bounds.minX)} → {formatWorldCoordinate(bounds.maxX)} · Y {formatWorldCoordinate(bounds.minY)} → {formatWorldCoordinate(bounds.maxY)}
            </span>
            <span>{backgroundFailed ? "底图不可用，已切换校准网格" : "本地 Palworld 地图底图"}</span>
          </footer>
        </div>

        <aside className="live-map-sidebar">
          <div className="live-player-list-heading">
            <div>
              <p className="eyebrow">PLAYERS</p>
              <h3>玩家位置</h3>
            </div>
            <span>{visiblePlayers.length}/{sizedPlayers.length}</span>
          </div>

          <label className="live-map-search">
            <span>⌕</span>
            <input
              onChange={(event) => setQuery(event.target.value)}
              placeholder="搜索名称、UID 或 Player ID"
              value={query}
            />
            {query ? <button onClick={() => setQuery("")} type="button">清除</button> : null}
          </label>

          <div className="live-player-scroll">
            <PlayerGroup
              followPlayerId={followPlayerId}
              label="主地图区域"
              onChoose={followPlayer}
              players={mappedPlayers}
              selectedPlayerId={selectedPlayerId}
            />
            {otherPlayers.length > 0 ? (
              <PlayerGroup
                followPlayerId={followPlayerId}
                label="其他区域 / 超出校准边界"
                onChoose={(playerId) => {
                  setSelectedPlayerId(playerId);
                  setFollowPlayerId(null);
                }}
                players={otherPlayers}
                selectedPlayerId={selectedPlayerId}
              />
            ) : null}
            {visiblePlayers.length === 0 ? (
              <div className="live-player-list-empty">
                <strong>{sizedPlayers.length === 0 ? "当前没有在线玩家" : "没有匹配的玩家"}</strong>
                <small>
                  {sizedPlayers.length === 0
                    ? "玩家加入后会自动出现在列表中"
                    : "清除搜索词以查看全部在线玩家"}
                </small>
              </div>
            ) : null}
          </div>

          <div className="live-player-detail">
            {selected ? (
              <>
                <div>
                  <span className={`player-health-dot ${selected.health}`} />
                  <strong>{selected.player.name}</strong>
                  <em>{healthLabel(selected.health)}</em>
                </div>
                <dl>
                  <div><dt>等级</dt><dd>Lv.{selected.player.level ?? "--"}</dd></div>
                  <div><dt>区域</dt><dd>{selected.projected.inBounds ? "主地图" : "其他区域"}</dd></div>
                  <div><dt>世界 X</dt><dd>{formatWorldCoordinate(selected.player.position.x)}</dd></div>
                  <div><dt>世界 Y</dt><dd>{formatWorldCoordinate(selected.player.position.y)}</dd></div>
                </dl>
                <code>{selected.player.uid ?? selected.player.playerId}</code>
              </>
            ) : (
              <p>选择一名玩家查看精确坐标。</p>
            )}
          </div>
        </aside>
      </div>
    </section>
  );
}

function PlayerGroup({
  label,
  players,
  selectedPlayerId,
  followPlayerId,
  onChoose
}: {
  label: string;
  players: SizedPlayer[];
  selectedPlayerId: string | null;
  followPlayerId: string | null;
  onChoose: (playerId: string) => void;
}) {
  if (players.length === 0) {
    return null;
  }

  return (
    <section className="live-player-group">
      <header><span>{label}</span><em>{players.length}</em></header>
      {players.map(({ player, health }) => (
        <button
          className={`${selectedPlayerId === player.playerId ? "selected" : ""}${followPlayerId === player.playerId ? " following" : ""}`}
          key={player.playerId}
          onClick={() => onChoose(player.playerId)}
          type="button"
        >
          <span className={`player-list-avatar ${health}`}>{player.name.slice(0, 1).toLocaleUpperCase()}</span>
          <span>
            <strong>{player.name}</strong>
            <small>{formatWorldCoordinate(player.position.x)}, {formatWorldCoordinate(player.position.y)}</small>
          </span>
          <em>{followPlayerId === player.playerId ? "跟随" : `Lv.${player.level ?? "--"}`}</em>
        </button>
      ))}
    </section>
  );
}

function ConnectionBadge({ mode }: { mode: ConnectionMode }) {
  const labels: Record<ConnectionMode, string> = {
    connecting: "正在连接",
    streaming: "实时推送",
    polling: "轮询回退",
    offline: "暂不可用"
  };
  return <span className={`live-map-connection ${mode}`}><i />{labels[mode]}</span>;
}

function getPlayerHealth(
  player: LiveMapPlayer,
  snapshot: LiveMapSnapshot | null,
  now: number
): PlayerHealth {
  if (!player.online || !snapshot) {
    return "unavailable";
  }
  const observedAt = Date.parse(player.observedAt ?? snapshot.observedAt ?? snapshot.generatedAt);
  if (!Number.isFinite(observedAt)) {
    return "unavailable";
  }
  const age = Math.max(0, now - observedAt);
  if (age > snapshot.unavailableAfterMs) {
    return "unavailable";
  }
  if (age > snapshot.staleAfterMs) {
    return "stale";
  }
  return "fresh";
}

function statusRank(status: LiveMapStatus | PlayerHealth): number {
  return status === "unavailable" ? 2 : status === "stale" ? 1 : 0;
}

function healthFromRank(rank: number): PlayerHealth {
  return rank >= 2 ? "unavailable" : rank >= 1 ? "stale" : "fresh";
}

function healthLabel(health: PlayerHealth): string {
  return health === "fresh" ? "实时" : health === "stale" ? "位置延迟" : "位置不可用";
}

function formatObservedAt(value: string | null | undefined, now: number): string {
  if (!value) {
    return "--";
  }
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "--";
  }
  const seconds = Math.max(0, Math.round((now - timestamp) / 1_000));
  if (seconds < 2) {
    return "刚刚";
  }
  if (seconds < 60) {
    return `${seconds} 秒前`;
  }
  return new Date(timestamp).toLocaleTimeString("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit"
  });
}
