import { useEffect, useState } from "react";
import { AnnouncementBoard } from "../features/announcements/AnnouncementBoard";
import { ServerConfigurationPanel } from "../features/configuration/ServerConfigurationPanel";
import { DashboardOverview } from "../features/dashboard/DashboardOverview";
import { ExtractionCenter } from "../features/extraction";
import { LivePlayerMap } from "../features/map/LivePlayerMap";
import { PalDefenderDirectory } from "../features/paldefender/PalDefenderDirectory";
import { PalDefenderOperations } from "../features/paldefender/PalDefenderOperations";
import { PalDefenderSystemPanel } from "../features/paldefender/PalDefenderSystemPanel";
import { PlayerCenter as PlayerCenterView } from "../features/players/PlayerCenter";
import { SaveManagement } from "../features/saves/SaveManagement";
import {
  getCapabilities,
  getGameResourceCatalog,
  getNativeInventoryProbe,
  getNativePalProbe,
  getPalDefenderStatus,
  getPalSkillCatalog,
  getPlayers,
  type GameResourceCatalog,
  type NativeInventoryProbe,
  type NativePalProbe,
  type PalDefenderStatus,
  type PalSkillCatalog,
  type PlayerSummary,
  type ServerCapabilities
} from "../lib/api/client";

type NavKey =
  | "dashboard"
  | "players"
  | "extraction"
  | "operations"
  | "directory"
  | "announcements"
  | "map"
  | "saves"
  | "configuration"
  | "system";

const navigation: Array<{ key: NavKey; label: string; glyph: string; group: string }> = [
  { key: "dashboard", label: "仪表盘", glyph: "总", group: "总览" },
  { key: "players", label: "玩家中心", glyph: "玩", group: "玩家运营" },
  { key: "extraction", label: "摸金商城", glyph: "撤", group: "玩家运营" },
  { key: "operations", label: "奖励与管理", glyph: "奖", group: "玩家运营" },
  { key: "directory", label: "公会与封禁", glyph: "会", group: "玩家运营" },
  { key: "announcements", label: "公告中心", glyph: "公", group: "内容运营" },
  { key: "map", label: "实时地图", glyph: "图", group: "内容运营" },
  { key: "saves", label: "存档管理", glyph: "档", group: "服务器" },
  { key: "configuration", label: "服务器配置", glyph: "配", group: "服务器" },
  { key: "system", label: "接口与审计", glyph: "审", group: "系统" }
];

export function App() {
  const [capabilities, setCapabilities] = useState<ServerCapabilities>();
  const [palDefenderStatus, setPalDefenderStatus] = useState<PalDefenderStatus>();
  const [gameCatalog, setGameCatalog] = useState<GameResourceCatalog>();
  const [nativeInventoryProbe, setNativeInventoryProbe] = useState<NativeInventoryProbe | null>();
  const [nativePalProbe, setNativePalProbe] = useState<NativePalProbe | null>();
  const [palSkillCatalog, setPalSkillCatalog] = useState<PalSkillCatalog | null>();
  const [players, setPlayers] = useState<PlayerSummary[]>([]);
  const [activeNav, setActiveNav] = useState<NavKey>("dashboard");
  const [inventoryLoading, setInventoryLoading] = useState(true);
  const [palLoading, setPalLoading] = useState(true);
  const [selectedPlayerId, setSelectedPlayerId] = useState<string>();

  useEffect(() => {
    const controller = new AbortController();
    void Promise.all([
      getCapabilities(controller.signal),
      getPalDefenderStatus("local", controller.signal).catch(() => undefined),
      getGameResourceCatalog("local", controller.signal).catch(() => undefined),
      getNativeInventoryProbe(controller.signal),
      getNativePalProbe(controller.signal),
      getPalSkillCatalog(controller.signal),
      getPlayers(controller.signal)
    ]).then(([nextCapabilities, nextPalDefender, nextGameCatalog, nextInventoryProbe, nextPalProbe, nextSkillCatalog, nextPlayers]) => {
      if (controller.signal.aborted) return;
      setCapabilities(nextCapabilities);
      setPalDefenderStatus(nextPalDefender);
      setGameCatalog(nextGameCatalog);
      setNativeInventoryProbe(nextInventoryProbe);
      setNativePalProbe(nextPalProbe);
      setPalSkillCatalog(nextSkillCatalog);
      setPlayers(nextPlayers);
      setSelectedPlayerId((current) => reconcileSelectedPlayer(current, nextPlayers));
      setInventoryLoading(false);
      setPalLoading(false);
    });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    const timer = globalThis.setInterval(() => {
      void Promise.all([
        getCapabilities(),
        getPalDefenderStatus().catch(() => undefined),
        getPlayers()
      ]).then(([nextCapabilities, nextPalDefender, nextPlayers]) => {
        setCapabilities(nextCapabilities);
        setPalDefenderStatus(nextPalDefender);
        setPlayers(nextPlayers);
        setSelectedPlayerId((current) => reconcileSelectedPlayer(current, nextPlayers));
      });
    }, 10_000);
    return () => globalThis.clearInterval(timer);
  }, []);

  const activeLabel = navigation.find((entry) => entry.key === activeNav)?.label ?? "仪表盘";
  const bridgeConnected = capabilities?.bridgeConnected ?? false;
  const apiOnline = capabilities?.mode !== "api-offline";
  const pdConnected = palDefenderStatus?.connected ?? false;
  const publishChatAnnouncements = capabilities?.publishChatAnnouncements ?? capabilities?.publishAnnouncements ?? false;
  const publishClientOverlay = capabilities?.publishClientOverlay ?? false;
  const publishTopBanner = capabilities?.publishTopBanner ?? false;
  const guardedWriteEnabled = Boolean(
    pdConnected || capabilities?.writePals || capabilities?.writeInventory || capabilities?.writePlayerProgression ||
    publishChatAnnouncements || publishTopBanner || publishClientOverlay
  );
  const selectedPlayer = players.find((player) => player.playerId === selectedPlayerId);

  async function refreshInventory() {
    setInventoryLoading(true);
    try {
      setNativeInventoryProbe(await getNativeInventoryProbe());
    } finally {
      setInventoryLoading(false);
    }
  }

  async function refreshPals() {
    setPalLoading(true);
    try {
      setNativePalProbe(await getNativePalProbe());
    } finally {
      setPalLoading(false);
    }
  }

  function navigate(key: string) {
    const aliases: Record<string, NavKey> = {
      "player-rewards": "operations",
      paldefender: "system"
    };
    const resolved = aliases[key] ?? key;
    if (navigation.some((entry) => entry.key === resolved)) setActiveNav(resolved as NavKey);
  }

  function renderPage() {
    if (activeNav === "dashboard") {
      return <DashboardOverview players={players} capabilities={capabilities} palDefenderStatus={palDefenderStatus} onNavigate={navigate} />;
    }
    if (activeNav === "operations") {
      return <PalDefenderOperations catalog={gameCatalog} players={players} selectedPlayerId={selectedPlayerId} onSelectPlayer={setSelectedPlayerId} connected={pdConnected} />;
    }
    if (activeNav === "extraction") {
      return <ExtractionCenter
        userId={selectedPlayer?.uid ?? undefined}
        onSelectPlayer={() => setActiveNav("players")}
      />;
    }
    if (activeNav === "directory") return <PalDefenderDirectory connected={pdConnected} />;
    if (activeNav === "announcements") {
      return <AnnouncementBoard
        restConnected={capabilities?.officialRestConnected ?? false}
        bridgeConnected={bridgeConnected}
        publishChatAnnouncements={publishChatAnnouncements}
        publishClientOverlay={publishClientOverlay}
        publishTopBanner={publishTopBanner}
        commandQueueReady={capabilities?.commandQueueReady ?? false}
        auditReady={capabilities?.auditReady ?? false}
      />;
    }
    if (activeNav === "map") return <LivePlayerMap serverId={capabilities?.serverId ?? "local"} />;
    if (activeNav === "saves") {
      return <SaveManagement onlinePlayers={players.filter((player) => player.online).length} serverId={capabilities?.serverId ?? "local"} />;
    }
    if (activeNav === "configuration") return <ServerConfigurationPanel />;
    if (activeNav === "system") return <PalDefenderSystemPanel status={palDefenderStatus} />;
    return <PlayerCenterView
      catalog={gameCatalog}
      capabilities={capabilities}
      connected={pdConnected}
      inventoryLoading={inventoryLoading}
      nativeInventoryProbe={nativeInventoryProbe}
      nativePalProbe={nativePalProbe}
      onNavigate={navigate}
      onRefreshInventory={refreshInventory}
      onRefreshPals={refreshPals}
      onSelectPlayer={setSelectedPlayerId}
      palLoading={palLoading}
      palSkillCatalog={palSkillCatalog}
      players={players}
      selectedPlayerId={selectedPlayerId}
    />;
  }

  return (
    <main className="shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark">PC</span>
          <div><strong>Pal Control</strong><small>幻兽商域运营中心</small></div>
        </div>

        <nav aria-label="主导航">
          {navigation.map((entry, index) => {
            const showGroup = index === 0 || navigation[index - 1].group !== entry.group;
            return <div className="nav-entry" key={entry.key}>
              {showGroup ? <span className="nav-group-label">{entry.group}</span> : null}
              <button className={activeNav === entry.key ? "nav-item active" : "nav-item"} onClick={() => setActiveNav(entry.key)} type="button">
                <span className="nav-glyph">{entry.glyph}</span><span>{entry.label}</span>
              </button>
            </div>;
          })}
        </nav>

        <div className="server-card">
          <div className={apiOnline ? "status-dot online" : "status-dot"} />
          <div><strong>{apiOnline ? "本机控制 API 在线" : "本机控制 API 离线"}</strong><small>{pdConnected ? "PalDefender 已连接" : "PalDefender 未连接"}</small></div>
        </div>
      </aside>

      <section className="workspace">
        <header className="topbar">
          <div><p className="eyebrow">PALWORLD / LOCAL SERVER</p><h1>{activeLabel}</h1></div>
          <div className="top-actions">
            <span className={guardedWriteEnabled ? "safe-mode write-enabled" : "safe-mode"}>{guardedWriteEnabled ? "受控写入已启用" : "安全只读"}</span>
            <button className="ghost-button" onClick={() => setActiveNav("operations")} type="button">玩家发放</button>
            <button className="primary-button" onClick={() => setActiveNav("announcements")} type="button">新建公告</button>
          </div>
        </header>

        <div className="status-strip" aria-label="服务连接状态">
          <StatusItem label="控制 API" ready={apiOnline} readyText="在线" waitingText="离线" />
          <StatusItem label="官方 REST" ready={capabilities?.officialRestConnected ?? false} readyText="已连接" waitingText="待连接" />
          <StatusItem label="PalDefender" ready={pdConnected} readyText={String(palDefenderStatus?.version?.Version?.VersionLong ?? "已连接")} waitingText="待连接" />
          <StatusItem label="Native Bridge" ready={bridgeConnected} readyText="已连接" waitingText="待连接" />
          <StatusItem label="命令队列" ready={capabilities?.commandQueueReady ?? false} readyText="可用" waitingText="未就绪" />
        </div>

        {renderPage()}
      </section>
    </main>
  );
}

function StatusItem({ label, ready, readyText, waitingText }: { label: string; ready: boolean; readyText: string; waitingText: string }) {
  return <div className="status-item"><span>{label}</span><strong className={ready ? "write-online" : "warning"}><i />{ready ? readyText : waitingText}</strong></div>;
}

function reconcileSelectedPlayer(current: string | undefined, players: PlayerSummary[]) {
  return current && players.some((player) => player.playerId === current)
    ? current
    : players[0]?.playerId;
}
