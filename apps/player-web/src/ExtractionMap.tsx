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
import {
  DEFAULT_EXTRACTION_MAP_LAYERS,
  ExtractionMapLayers,
  isZoneLayerVisible
} from "./mapLayers";

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
  const [layers, setLayers] = useState<ExtractionMapLayers>(DEFAULT_EXTRACTION_MAP_LAYERS);
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
  const effectiveOnline = data?.playerOnline ?? online;
  const positionAvailable = data?.positionAvailable ?? playerWorld !== null;
  const positionTrusted = effectiveOnline && positionAvailable && error === null;
  const enteredOpenZone = positionTrusted
    ? zones.find((zone) => zone.inRange === true && zone.open === true) ?? null
    : null;
  const enteredClosedZone = positionTrusted
    ? zones.find((zone) => zone.inRange === true && zone.open === false) ?? null
    : null;
  const nearestOpenZone = nearestByDistance(zones.filter((zone) => zone.open === true));
  const nearestZone = nearestByDistance(zones);
  const selected = zones.find((zone) => zone.id === selectedId)
    ?? nearestOpenZone
    ?? nearestZone
    ?? null;
  const selectedMapped = selected
    ? mappedZones.find((item) => item.zone.id === selected.id) ?? null
    : null;
  const status = getRangeStatus(
    effectiveOnline,
    positionAvailable,
    loading,
    error,
    zones,
    enteredOpenZone,
    enteredClosedZone,
    data?.statusMessage,
    data?.nextOpensAt
  );
  const toggleLayer = (key: keyof ExtractionMapLayers) => setLayers((current) => ({
    ...current,
    [key]: !current[key]
  }));

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
            <span>{zones.length} 个兑换点</span>
          </header>

          <fieldset className="map-layer-controls">
            <legend>地图图层</legend>
            {([
              ["openZones", "开放区"],
              ["closedZones", "关闭区"],
              ["hotspots", "热点"],
              ["risk", "风险"],
              ["route", "前往路线"],
              ["ownPosition", "我的位置"]
            ] as Array<[keyof ExtractionMapLayers, string]>).map(([key, label]) => (
              <label key={key}>
                <input
                  checked={layers[key]}
                  onChange={() => toggleLayer(key)}
                  type="checkbox"
                />
                <span>{label}</span>
              </label>
            ))}
          </fieldset>

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

            {layers.route && playerProjected?.inBounds && selectedMapped?.projected?.inBounds ? (
              <svg
                aria-label={selected?.open === true
                  ? `从我的位置前往 ${selected.displayName || selected.id} 的示意路线`
                  : `预览路线：从我的位置前往 ${selected?.displayName || selected?.id}（当前不可兑换）`}
                className="extraction-player-route"
                preserveAspectRatio="none"
                role="img"
                viewBox="0 0 100 100"
              >
                <line
                  x1={playerProjected.u * 100}
                  y1={playerProjected.v * 100}
                  x2={selectedMapped.projected.u * 100}
                  y2={selectedMapped.projected.v * 100}
                />
              </svg>
            ) : null}

            {mappedZones.map(({ zone, projected, radiusPercent }) => projected?.inBounds && isZoneLayerVisible(zone.open, layers) ? (
              <button
                aria-label={`查看资源兑换点 ${zone.displayName || zone.id}`}
                aria-pressed={selected?.id === zone.id}
                className={`extraction-zone-marker${zone.inRange === true ? " in-range" : ""}${selected?.id === zone.id ? " selected" : ""}${zone.open === false ? " closed" : ""}${zone.hotspot && layers.hotspots ? " hotspot" : ""}${layers.risk && zone.riskLevel ? ` risk-${zone.riskLevel.toLowerCase()}` : ""}`}
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
                {zone.hotspot && layers.hotspots ? <span className="extraction-hotspot-ring" aria-hidden="true" /> : null}
                {zone.riskLevel && layers.risk ? <span className="extraction-risk-ring" aria-hidden="true" /> : null}
                <span className="extraction-zone-pin"><i />换</span>
                <span className="extraction-zone-label">{zone.displayName || zone.id}</span>
              </button>
            ) : null)}

            {layers.ownPosition && playerProjected?.inBounds ? (
              <div
                className={`extraction-player-marker${enteredOpenZone ? " in-range" : ""}`}
                style={{ left: `${playerProjected.u * 100}%`, top: `${playerProjected.v * 100}%` }}
                aria-label={enteredClosedZone
                  ? `我的当前位置，位于已关闭的${enteredClosedZone.displayName || enteredClosedZone.id}范围内`
                  : "我的当前位置"}
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
              <span><i className="zone" />开放区域</span>
              <span><i className="closed-zone" />关闭区域</span>
              <span><i className="hotspot-zone" />经济热点</span>
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
                <div>
                  <strong>{selected.displayName || selected.id}</strong>
                  <small>{zoneStatusLabel(selected)} · {rangeLabel(selected.inRange)}</small>
                </div>
              </div>
              <dl className="zone-facts">
                <div><dt>地图坐标</dt><dd>{formatMapPair(selected.mapX, selected.mapY)}</dd></div>
                <div><dt>有效半径</dt><dd>{formatMapValue(selected.radius, "地图单位")}</dd></div>
                <div><dt>距兑换点中心</dt><dd>{formatMapValue(selected.distance, "地图单位")}</dd></div>
                <div><dt>当前收益倍率</dt><dd>{formatYieldMultiplier(selected.yieldMultiplierBasisPoints)}{selected.hotspot ? "（今日热点）" : ""}</dd></div>
                <div><dt>风险等级</dt><dd>{riskLevelLabel(selected.riskLevel)}</dd></div>
                <div><dt>开放时间</dt><dd>{formatOpenWindows(selected.openWindows)}</dd></div>
                {selected.dynamicOpenWindow ? <div><dt>今日动态开放</dt><dd>{formatEventWindow(selected.dynamicOpenWindow)}</dd></div> : null}
                {selected.hotspotWindow ? <div><dt>热点时段</dt><dd>{formatEventWindow(selected.hotspotWindow)}</dd></div> : null}
                {selected.open === false ? <div><dt>下一开放</dt><dd>{formatDateTime(selected.nextOpensAt ?? data?.nextOpensAt)}</dd></div> : null}
              </dl>
              {selected.open !== true ? (
                <div className="inline-tip warning" role="note">
                  该兑换点当前不可兑换，路线仅供预览；请前往开放兑换点，或等待下次开放：{formatDateTime(selected.nextOpensAt ?? data?.nextOpensAt)}。
                </div>
              ) : null}
              <div className="route-hint">
                <span className="eyebrow">ROUTE GUIDE</span>
                <strong>{selected.open === true ? "路线说明" : "预览路线（当前不可兑换）"}</strong>
                <p>{selected.routeHint?.trim() || "路线说明暂未配置，请按地图标记和坐标前往目标区域。"}</p>
              </div>
              <div className="route-hint">
                <span className="eyebrow">RISK / REWARD</span>
                <strong>收益与风险</strong>
                <p>{selected.riskHint?.trim() || "该点尚未配置风险提示；出发前只携带准备出售的资源，并留意周边环境。"}</p>
              </div>
              <div className="route-hint world-event-facts">
                <span className="eyebrow">WORLD ECONOMY EVENTS</span>
                <strong>当前世界经济事件</strong>
                {resolveWorldEvents(selected, data).length ? (
                  <ul>
                    {resolveWorldEvents(selected, data).map((event) => (
                      <li key={event.eventId}>
                        <b>{event.displayName}</b>
                        <span>资源倍率 {formatYieldMultiplier(event.zoneYieldMultiplierBasisPoints)} · 商品价格倍率 {formatYieldMultiplier(event.productPriceMultiplierBasisPoints)}</span>
                        <small>{formatEventWindow(event.window)}</small>
                      </li>
                    ))}
                  </ul>
                ) : <p>当前没有生效的世界经济事件。</p>}
                {selected.dynamicPolicyVersion || data?.dynamicPolicyVersion ? (
                  <small className="dynamic-policy">策略 {selected.dynamicPolicyVersion || data?.dynamicPolicyVersion}</small>
                ) : null}
              </div>
              <div className="zone-list" aria-label="全部资源兑换点">
                {zones.map((zone) => (
                  <button className={selected.id === zone.id ? "active" : ""} key={zone.id} onClick={() => setSelectedId(zone.id)}>
                    <span><strong>{zone.displayName || zone.id}</strong><small>{formatMapPair(zone.mapX, zone.mapY)}</small></span>
                    <i className={zone.open === false ? "outside" : zone.hotspot ? "inside" : "unknown"}>{zoneStatusShortLabel(zone)}</i>
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
  enteredOpenZone: ExtractionZone | null,
  enteredClosedZone: ExtractionZone | null,
  statusMessage?: string | null,
  nextOpensAt?: string | null
) {
  if (!online) return {
    tone: "unknown",
    icon: "?",
    title: "当前位置未知",
    detail: "角色当前离线；地图可能保留上次成功数据，但不可据此扫描或出售资源。"
  };
  if (error) return {
    tone: "unknown",
    icon: "!",
    title: "上次位置不可确认",
    detail: "本轮位置更新失败；地图仍显示上次成功数据，但不可据此扫描或出售资源。请等待下一次更新。"
  };
  if (loading && zones.length === 0) return {
    tone: "unknown",
    icon: "…",
    title: "正在确认兑换位置",
    detail: "正在读取角色位置和服务器资源兑换点配置。"
  };
  if (!positionAvailable) return {
    tone: "unknown",
    icon: "?",
    title: "当前位置暂不可用",
    detail: `${statusMessage?.trim() || "服务器尚未返回可靠的位置坐标。"} 上次地图仅供参考，不可据此扫描或出售资源。`
  };
  if (enteredOpenZone) return {
    tone: "inside",
    icon: "✓",
    title: "已进入资源兑换区域",
    detail: `当前位置位于「${enteredOpenZone.displayName || enteredOpenZone.id}」，可扫描并出售白名单资源。`
  };
  if (enteredClosedZone) return {
    tone: "outside",
    icon: "!",
    title: "已在关闭兑换区范围内",
    detail: `「${enteredClosedZone.displayName || enteredClosedZone.id}」当前关闭，暂不可扫描或出售资源。下次开放：${formatDateTime(enteredClosedZone.nextOpensAt ?? nextOpensAt)}。`
  };
  if (zones.some((zone) => zone.inRange === false)) return { tone: "outside", icon: "→", title: "尚未进入资源兑换区域", detail: "按地图标记前往资源兑换点；进入有效半径后状态会自动变化。" };
  return { tone: "unknown", icon: "?", title: "当前位置暂不可用", detail: "服务器尚未返回可靠的位置判定，不会显示推测结果。" };
}

function nearestByDistance(zones: ExtractionZone[]) {
  if (zones.length === 0) return null;
  return [...zones].sort((left, right) => {
    const leftDistance = isFiniteNumber(left.distance) ? left.distance : Number.POSITIVE_INFINITY;
    const rightDistance = isFiniteNumber(right.distance) ? right.distance : Number.POSITIVE_INFINITY;
    return leftDistance - rightDistance;
  })[0] ?? null;
}

function rangeLabel(value?: boolean | null) {
  return value === true ? "已进入有效范围" : value === false ? "尚未进入有效范围" : "范围状态未知";
}

function rangeShortLabel(value?: boolean | null) {
  return value === true ? "范围内" : value === false ? "范围外" : "未知";
}

function zoneStatusLabel(zone: ExtractionZone) {
  if (zone.open === false) return "当前关闭";
  if (zone.open === true) return zone.hotspot ? "当前开放，今日热点" : "当前开放";
  return "开放状态未知";
}

function zoneStatusShortLabel(zone: ExtractionZone) {
  if (zone.open === false) return "关闭";
  if (zone.open === true) return zone.hotspot ? "热点" : rangeShortLabel(zone.inRange);
  return "未知";
}

function riskLevelLabel(value?: ExtractionZone["riskLevel"]) {
  if (!value) return "未公布";
  return ({ Guarded: "可控", Elevated: "较高", Severe: "严峻" } as const)[value] ?? "未公布";
}

function resolveWorldEvents(zone: ExtractionZone, data: ExtractionZoneList | null) {
  return zone.worldEvents?.length ? zone.worldEvents : data?.worldEvents ?? [];
}

function formatYieldMultiplier(value?: number | null) {
  if (!isFiniteNumber(value)) return "未公布";
  return `${new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 2 }).format(value / 100)}%`;
}

function formatOpenWindows(windows?: ExtractionZone["openWindows"]) {
  if (!windows?.length) return "未配置";
  const days = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
  return windows.map((window) => {
    const grace = window.graceSeconds > 0 ? `，已有报价宽限 ${window.graceSeconds} 秒` : "";
    return `${days[window.dayOfWeek] ?? `星期 ${window.dayOfWeek}`} ${shortTime(window.opensAt)}–${shortTime(window.closesAt)}${grace}`;
  }).join("；");
}

function formatEventWindow(window: NonNullable<ExtractionZone["dynamicOpenWindow"]>) {
  return `${formatDateTime(window.startsAt)}–${formatDateTime(window.endsAt)}（宽限至 ${formatDateTime(window.graceEndsAt)}）`;
}

function shortTime(value: string) {
  return value.split(".")[0]?.slice(0, 5) || value;
}

function formatDateTime(value?: string | null) {
  if (!value) return "未提供";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "未提供";
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  }).format(date);
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
