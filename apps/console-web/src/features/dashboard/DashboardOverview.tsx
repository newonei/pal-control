import {
  type PalDefenderStatus,
  type PlayerSummary,
  type ServerCapabilities
} from "../../lib/api/client";

export type DashboardOverviewProps = {
  players: PlayerSummary[];
  capabilities?: ServerCapabilities;
  palDefenderStatus?: PalDefenderStatus;
  onNavigate: (key: string) => void;
};

const quickActions = [
  {
    key: "players",
    eyebrow: "PLAYERS",
    title: "玩家中心",
    description: "查看在线状态、等级与原生玩家详情。"
  },
  {
    key: "player-rewards",
    eyebrow: "REWARDS",
    title: "奖励发放",
    description: "发放经验、点数、物品、帕鲁和帕鲁蛋。"
  },
  {
    key: "announcements",
    eyebrow: "OPERATIONS",
    title: "公告中心",
    description: "发布聊天公告、顶部横幅和客户端浮层。"
  },
  {
    key: "map",
    eyebrow: "LIVE MAP",
    title: "实时地图",
    description: "查看在线玩家位置并跟随地图标记。"
  },
  {
    key: "saves",
    eyebrow: "SAVE CENTER",
    title: "存档与备份",
    description: "保存世界、创建备份并校验完整性。"
  },
  {
    key: "paldefender",
    eyebrow: "SYSTEM",
    title: "PalDefender",
    description: "发送广播或警报，并检查 API 与命令审计。"
  }
] as const;

export function DashboardOverview({
  players,
  capabilities,
  palDefenderStatus,
  onNavigate
}: DashboardOverviewProps) {
  const onlinePlayers = players.filter((player) => player.online);
  const connectedServices = [
    capabilities?.officialRestConnected,
    capabilities?.bridgeConnected,
    palDefenderStatus?.connected
  ].filter(Boolean).length;
  const commandPipelineReady = Boolean(capabilities?.commandQueueReady && capabilities?.auditReady);
  const palDefenderVersion = palDefenderStatus?.version?.Version.VersionLong ??
    palDefenderStatus?.version?.Version.Version;

  return (
    <section className="dashboard-overview" aria-label="服务器总览">
      <header className="dashboard-hero">
        <div>
          <p className="eyebrow">PAL CONTROL / LOCAL SERVER</p>
          <h2>服务器运行总览</h2>
          <p>集中查看玩家、控制链路和安全命令队列，并快速进入常用操作。</p>
        </div>
        <div className="dashboard-hero-actions">
          <span className={commandPipelineReady ? "safe-mode write-enabled" : "safe-mode"}>
            {commandPipelineReady ? "命令与审计已就绪" : "写入链路待就绪"}
          </span>
          <button className="primary-button" onClick={() => onNavigate("player-rewards")} type="button">
            发放玩家奖励
          </button>
        </div>
      </header>

      <div className="metric-grid dashboard-metrics">
        <DashboardMetric
          detail={`玩家总数 ${players.length}`}
          label="在线玩家"
          tone={onlinePlayers.length > 0 ? "positive" : "neutral"}
          value={String(onlinePlayers.length)}
        />
        <DashboardMetric
          detail="官方 REST / Native Bridge / PalDefender"
          label="已连接服务"
          tone={connectedServices === 3 ? "positive" : "warning"}
          value={`${connectedServices} / 3`}
        />
        <DashboardMetric
          detail={capabilities?.auditReady ? "追加式审计在线" : "等待审计存储"}
          label="安全命令队列"
          tone={commandPipelineReady ? "positive" : "warning"}
          value={commandPipelineReady ? "可用" : "受限"}
        />
        <DashboardMetric
          detail={palDefenderVersion ? `版本 ${palDefenderVersion}` : "等待版本探针"}
          label="PalDefender"
          tone={palDefenderStatus?.connected ? "positive" : "warning"}
          value={palDefenderStatus?.connected ? "在线" : palDefenderStatus?.enabled ? "未连接" : "未启用"}
        />
      </div>

      <div className="dashboard-content-grid">
        <section className="dashboard-panel dashboard-online-panel">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">ONLINE PLAYERS</p>
              <h3>当前在线</h3>
            </div>
            <span className="count-pill">{onlinePlayers.length}</span>
          </div>

          <div className="dashboard-player-list">
            {onlinePlayers.length > 0 ? onlinePlayers.slice(0, 6).map((player) => (
              <button
                className="dashboard-player-row"
                key={player.playerId}
                onClick={() => onNavigate("players")}
                type="button"
              >
                <span className="avatar">{player.name.slice(0, 1).toLocaleUpperCase()}</span>
                <span>
                  <strong>{player.name}</strong>
                  <small>{player.uid ?? player.playerId}</small>
                </span>
                <em>Lv.{player.level ?? "--"}</em>
              </button>
            )) : (
              <div className="dashboard-empty-state">
                <strong>当前没有在线玩家</strong>
                <small>玩家进入服务器后会显示在这里。</small>
              </div>
            )}
          </div>

          <button className="ghost-button dashboard-panel-action" onClick={() => onNavigate("players")} type="button">
            查看全部玩家
          </button>
        </section>

        <section className="dashboard-panel dashboard-health-panel">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">SERVICE HEALTH</p>
              <h3>服务连接</h3>
            </div>
          </div>
          <div className="capability-grid dashboard-health-grid">
            <HealthItem
              enabled={capabilities?.officialRestConnected}
              label="官方 REST"
              note="玩家与服务器基础数据"
            />
            <HealthItem
              enabled={capabilities?.bridgeConnected}
              label="Native Bridge"
              note="原生对象、背包与帕鲁"
            />
            <HealthItem
              enabled={palDefenderStatus?.connected}
              label="PalDefender REST"
              note={palDefenderStatus?.error?.message ?? "商城发货与管理命令"}
            />
            <HealthItem
              enabled={commandPipelineReady}
              label="命令与审计"
              note="幂等派发与最终状态"
            />
          </div>
        </section>
      </div>

      <section className="dashboard-quick-section">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">QUICK ACTIONS</p>
            <h3>快捷入口</h3>
          </div>
        </div>
        <div className="dashboard-quick-grid">
          {quickActions.map((action) => (
            <button
              className="dashboard-quick-card"
              key={action.key}
              onClick={() => onNavigate(action.key)}
              type="button"
            >
              <span>{action.eyebrow}</span>
              <strong>{action.title}</strong>
              <small>{action.description}</small>
              <em aria-hidden="true">→</em>
            </button>
          ))}
        </div>
      </section>
    </section>
  );
}

function DashboardMetric({
  detail,
  label,
  tone,
  value
}: {
  detail: string;
  label: string;
  tone: "positive" | "warning" | "neutral";
  value: string;
}) {
  return (
    <article className={`metric-card dashboard-metric ${tone}`}>
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </article>
  );
}

function HealthItem({ enabled, label, note }: { enabled?: boolean; label: string; note: string }) {
  return (
    <article className="capability-card dashboard-health-item">
      <span className={enabled ? "capability-icon enabled" : "capability-icon"}>
        {enabled ? "✓" : "!"}
      </span>
      <div>
        <strong>{label}</strong>
        <small>{enabled ? note : `未就绪 · ${note}`}</small>
      </div>
    </article>
  );
}
