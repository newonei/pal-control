import { useEffect, useMemo, useState } from "react";
import type { ExtractionZone, ExtractionZoneList } from "./api";
import {
  EXTRACTION_MAP_BACKGROUND,
  isFiniteNumber,
  projectWorldPoint,
  resolvePlayerWorldPoint,
  resolveZoneWorldPoint,
  resolveZoneWorldRadius,
  worldRadiusToMapPercent
} from "./extractionMapCoordinates";

type Props = {
  data: ExtractionZoneList | null;
  error: string | null;
  loading: boolean;
  online: boolean;
};

type MappedZone = {
  zone: ExtractionZone;
  projected: ReturnType<typeof projectWorldPoint> | null;
  radiusPercent: number | null;
};

export function ExtractionMap({ data, error, loading, online }: Props) {
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [backgroundFailed, setBackgroundFailed] = useState(false);
  const zones = data?.items ?? [];

  const mappedZones = useMemo<MappedZone[]>(() => zones.map((zone) => {
    const point = resolveZoneWorldPoint(zone);
    const radius = resolveZoneWorldRadius(zone);
    return {
      zone,
      projected: point ? projectWorldPoint(point) : null,
      radiusPercent: radius === null ? null : worldRadiusToMapPercent(radius)
    };
  }), [zones]);

  useEffect(() => {
    if (selectedId && !zones.some((zone) => zone.id === selectedId)) setSelectedId(null);
  }, [selectedId, zones]);

  const playerWorld = resolvePlayerWorldPoint(data?.playerPosition);
  const playerProjected = playerWorld ? projectWorldPoint(playerWorld) : null;
  const enteredZone = zones.find((zone) => zone.inRange === true) ?? null;
  const nearestZone = [...zones]
    .filter((zone) => isFiniteNumber(zone.distance))
    .sort((left, right) => (left.distance as number) - (right.distance as number))[0] ?? null;
  const selected = zones.find((zone) => zone.id === selectedId) ?? enteredZone ?? nearestZone ?? zones[0] ?? null;
  const effectiveOnline = data?.playerOnline ?? online;
  const positionAvailable = data?.positionAvailable ?? playerWorld !== null;
  const status = getRangeStatus(
    effectiveOnline,
    positionAvailable,
    loading,
    error,
    zones,
    enteredZone,
    data?.statusMessage
  );

  return (
    <section className="extraction-map-page">
      <div className={`range-status ${status.tone}`} role="status" aria-live="polite">
        <span className="range-status-icon" aria-hidden="true">{status.icon}</span>
        <div>
          <span className="eyebrow">LIVE EXCHANGE STATUS</span>
          <strong>{status.title}</strong>
          <small>{status.detail}</small>
        </div>
        <span className="range-live"><i />每 5 秒更新</span>
      </div>

      {error ? <div className="inline-tip warning">资源兑换位置暂时无法更新：{error}</div> : null}

      <div className="extraction-map-layout">
        <div className="extraction-map-card">
          <header>
            <div>
              <span className="eyebrow">TACTICAL MAP</span>
              <h3>主地图资源兑换点</h3>
            </div>
            <span>{zones.length} 个开放区域</span>
          </header>

          <div className="extraction-map-canvas" aria-label="资源兑换点地图">
            {!backgroundFailed ? (
              <img
                alt="Palworld 主地图与资源兑换点"
                draggable={false}
                onError={() => setBackgroundFailed(true)}
                src={EXTRACTION_MAP_BACKGROUND}
              />
            ) : null}
            <div className={`extraction-map-grid${backgroundFailed ? " fallback" : ""}`} aria-hidden="true" />

            {mappedZones.map(({ zone, projected, radiusPercent }) => projected?.inBounds ? (
              <button
                aria-label={`查看资源兑换点 ${zone.displayName || zone.id}`}
                aria-pressed={selected?.id === zone.id}
                className={`extraction-zone-marker${zone.inRange === true ? " in-range" : ""}${selected?.id === zone.id ? " selected" : ""}`}
                key={zone.id}
                onClick={() => setSelectedId(zone.id)}
                style={{
                  left: `${projected.u * 100}%`,
                  top: `${projected.v * 100}%`,
                  width: radiusPercent !== null && radiusPercent > 0 ? `${radiusPercent}%` : "1px"
                }}
                type="button"
              >
                {radiusPercent !== null ? (
                  <span className="extraction-zone-radius" />
                ) : null}
                <span className="extraction-zone-pin"><i />换</span>
                <span className="extraction-zone-label">{zone.displayName || zone.id}</span>
              </button>
            ) : null)}

            {playerProjected?.inBounds ? (
              <div
                className={`extraction-player-marker${enteredZone ? " in-range" : ""}`}
                style={{ left: `${playerProjected.u * 100}%`, top: `${playerProjected.v * 100}%` }}
                aria-label="我的当前位置"
              >
                <i /><span>我的位置</span>
              </div>
            ) : null}

            {loading && !data ? <div className="map-data-state"><i /><strong>正在读取资源兑换点</strong></div> : null}
            {!loading && zones.length === 0 ? <div className="map-data-state"><strong>当前没有开放的资源兑换点</strong><small>请等待管理员配置兑换区域</small></div> : null}
            {!loading && zones.length > 0 && !mappedZones.some((item) => item.projected?.inBounds) ? (
              <div className="map-data-state"><strong>资源兑换点坐标暂不可绘制</strong><small>仍可在右侧查看服务器返回的信息</small></div>
            ) : null}

            <div className="extraction-map-legend" aria-label="地图图例">
              <span><i className="zone" />资源兑换区域</span>
              <span><i className="player" />我的位置</span>
            </div>
          </div>

          <footer>
            <span>{backgroundFailed ? "底图不可用，已显示校准网格" : "服务器校准主地图"}</span>
            <span>位置更新时间：{formatUpdatedAt(data?.updatedAt)}</span>
          </footer>
        </div>

        <aside className="extraction-zone-panel">
          <div className="zone-panel-heading">
            <div><span className="eyebrow">EXCHANGE LOCATIONS</span><h3>资源兑换点信息</h3></div>
            <span>{selected ? "已选择" : "等待数据"}</span>
          </div>

          {selected ? (
            <>
              <div className="selected-zone-name">
                <span aria-hidden="true">换</span>
                <div><strong>{selected.displayName || selected.id}</strong><small>{rangeLabel(selected.inRange)}</small></div>
              </div>
              <dl className="zone-facts">
                <div><dt>地图坐标</dt><dd>{formatMapPair(selected.mapX, selected.mapY)}</dd></div>
                <div><dt>有效半径</dt><dd>{formatMapValue(selected.radius, "地图单位")}</dd></div>
                <div><dt>距兑换点中心</dt><dd>{formatMapValue(selected.distance, "地图单位")}</dd></div>
              </dl>
              <div className="route-hint">
                <span className="eyebrow">ROUTE GUIDE</span>
                <strong>路线说明</strong>
                <p>{selected.routeHint?.trim() || "路线说明暂未配置，请按地图标记和坐标前往目标区域。"}</p>
              </div>
              <div className="zone-list" aria-label="全部资源兑换点">
                {zones.map((zone) => (
                  <button className={selected.id === zone.id ? "active" : ""} key={zone.id} onClick={() => setSelectedId(zone.id)}>
                    <span><strong>{zone.displayName || zone.id}</strong><small>{formatMapPair(zone.mapX, zone.mapY)}</small></span>
                    <i className={zone.inRange === true ? "inside" : zone.inRange === false ? "outside" : "unknown"}>{rangeShortLabel(zone.inRange)}</i>
                  </button>
                ))}
              </div>
            </>
          ) : <div className="zone-panel-empty">资源兑换点数据尚未就绪</div>}

          <div className="command-hint">
            <span aria-hidden="true">/</span>
            <div><strong>游戏内快速查询</strong><small>聊天框输入兼容命令 <code>!撤离</code> 查看最近资源兑换点与距离。不要使用游戏保留的 <code>/</code> 管理员命令前缀。</small></div>
          </div>
        </aside>
      </div>
    </section>
  );
}

function getRangeStatus(
  online: boolean,
  positionAvailable: boolean,
  loading: boolean,
  error: string | null,
  zones: ExtractionZone[],
  enteredZone: ExtractionZone | null,
  statusMessage?: string | null
) {
  if (enteredZone) return {
    tone: "inside",
    icon: "✓",
    title: "已进入资源兑换区域",
    detail: `当前位置位于「${enteredZone.displayName || enteredZone.id}」，可扫描并出售白名单资源。`
  };
  if (!online) return { tone: "unknown", icon: "?", title: "当前位置未知", detail: "角色当前离线，上线后将自动更新与资源兑换点的距离。" };
  if (loading && zones.length === 0) return { tone: "unknown", icon: "…", title: "正在确认兑换位置", detail: "正在读取角色位置和服务器资源兑换点配置。" };
  if (error) return { tone: "unknown", icon: "!", title: "当前位置暂不可用", detail: "位置读取失败，不会猜测你是否已进入资源兑换区域。" };
  if (!positionAvailable) return { tone: "unknown", icon: "?", title: "当前位置暂不可用", detail: statusMessage?.trim() || "服务器尚未返回可靠的位置坐标，不会显示推测结果。" };
  if (zones.some((zone) => zone.inRange === false)) return { tone: "outside", icon: "→", title: "尚未进入资源兑换区域", detail: "按地图标记前往资源兑换点；进入有效半径后状态会自动变化。" };
  return { tone: "unknown", icon: "?", title: "当前位置暂不可用", detail: "服务器尚未返回可靠的位置判定，不会显示推测结果。" };
}

function rangeLabel(value?: boolean | null) {
  return value === true ? "已进入有效范围" : value === false ? "尚未进入有效范围" : "范围状态未知";
}

function rangeShortLabel(value?: boolean | null) {
  return value === true ? "范围内" : value === false ? "范围外" : "未知";
}

function formatMapPair(x?: number | null, y?: number | null) {
  return isFiniteNumber(x) && isFiniteNumber(y)
    ? `X ${formatNumber(x)} · Y ${formatNumber(y)}`
    : "坐标未知";
}

function formatMapValue(value?: number | null, unit = "") {
  return isFiniteNumber(value) ? `${formatNumber(value)}${unit ? ` ${unit}` : ""}` : "未知";
}

function formatNumber(value: number) {
  return new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 1 }).format(value);
}

function formatUpdatedAt(value?: string | null) {
  if (!value) return "未知";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "未知";
  return new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit" }).format(date);
}
