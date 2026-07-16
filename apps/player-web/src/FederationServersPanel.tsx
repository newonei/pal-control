import { useEffect, useState } from "react";
import {
  FederationServersError,
  federationAvailabilityLabel,
  federationBalanceView,
  federationCompatibilityLabel,
  federationPortalHref,
  loadFederationServers
} from "./federationServers";
import type { FederationOverview, FederationServer } from "./federationServers";
import "./federationServers.css";

type Props = {
  onSessionExpired: () => void;
  refreshSignal?: number;
};

export function FederationServersPanel({
  onSessionExpired,
  refreshSignal = 0
}: Props) {
  const [overview, setOverview] = useState<FederationOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [disabled, setDisabled] = useState(false);
  const [refreshVersion, setRefreshVersion] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    let active = true;
    setLoading(true);
    setError(null);
    setDisabled(false);
    void loadFederationServers(controller.signal)
      .then(next => {
        if (active) setOverview(next);
      })
      .catch((reason: unknown) => {
        if (!active || isAbort(reason)) return;
        if (reason instanceof FederationServersError && reason.status === 401) {
          onSessionExpired();
          return;
        }
        if (reason instanceof FederationServersError &&
            reason.status === 404 && reason.code === "FEDERATION_DISABLED") {
          setOverview(null);
          setDisabled(true);
          return;
        }
        setOverview(null);
        setError(reason instanceof Error ? reason.message : "服务器注册读取失败。请稍后重试。");
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
      controller.abort();
    };
  }, [onSessionExpired, refreshSignal, refreshVersion]);

  return (
    <section className="federation-panel" aria-labelledby="federation-panel-title">
      <header className="federation-panel-heading">
        <div>
          <span className="eyebrow">SERVER REGISTRY</span>
          <h3 id="federation-panel-title">我的服务器</h3>
          <p>
            每台 Palworld 服务器独立保存账户、周档和钱包。本页只读汇总，
            不提供跨服转币、库存、交易或拍卖。
          </p>
        </div>
        <button
          type="button"
          className="secondary"
          disabled={loading}
          onClick={() => setRefreshVersion(value => value + 1)}
        >
          {loading ? "读取中…" : "重新读取"}
        </button>
      </header>

      {loading && overview === null && (
        <div className="loading-state federation-state" role="status">正在读取各服务器状态…</div>
      )}
      {disabled && (
        <div className="empty-state federation-state" role="status">
          <strong>当前未启用多服注册</strong>
          <p>单服玩法不受影响；启用后这里只展示经过兼容门禁的只读摘要。</p>
        </div>
      )}
      {error && (
        <div className="alert error federation-state" role="alert">
          <span><strong>{error}</strong><small>余额未展示，也不会以 0 代替失败结果。</small></span>
          <button type="button" onClick={() => setRefreshVersion(value => value + 1)}>重试</button>
        </div>
      )}
      {overview && (
        <>
          <div className="federation-summary" aria-label="服务器注册摘要">
            <span>{overview.servers.length} 个注册节点</span>
            <span>兼容矩阵 {overview.matrixVersion}</span>
            <span>读取于 {formatObservedAt(overview.observedAt)}</span>
          </div>
          <div className="federation-server-grid">
            {overview.servers.map(server => (
              <FederationServerCard key={server.serverId} server={server} />
            ))}
          </div>
        </>
      )}
    </section>
  );
}

function FederationServerCard({ server }: { server: FederationServer }) {
  const balances = federationBalanceView(server);
  const portalHref = federationPortalHref(server);
  const compatibility = server.compatibility;
  return (
    <article className={`federation-server-card ${server.availability}`}>
      <header>
        <div>
          <h4>{server.displayName}</h4>
          <small>{server.local ? "当前服务器" : "远程服务器"}</small>
        </div>
        <span className={`federation-status ${server.availability}`}>
          {federationAvailabilityLabel(server.availability)}
        </span>
      </header>

      <dl>
        <div>
          <dt>账户</dt>
          <dd>{accountLabel(server)}</dd>
        </div>
        <div>
          <dt>本服周档</dt>
          <dd>{server.season?.displayName ?? "暂不可读"}</dd>
        </div>
        <div>
          <dt>兼容状态</dt>
          <dd>
            {federationCompatibilityLabel(compatibility)}
            {compatibility && <small>{compatibility.gameVersion} · {compatibility.steamBuild}</small>}
          </dd>
        </div>
      </dl>

      <div className={`federation-balance ${balances.available ? "available" : "unavailable"}`}>
        <strong>{balances.primary}</strong>
        <span>{balances.secondary}</span>
      </div>

      {server.errorCode && server.availability !== "available" && (
        <p className="federation-error-code">状态码：<code>{server.errorCode}</code></p>
      )}
      {portalHref ? (
        <a
          className="primary federation-switch"
          href={portalHref}
          target="_blank"
          rel="noopener noreferrer"
          referrerPolicy="no-referrer"
        >
          打开该服玩家门户
        </a>
      ) : (
        <span className="federation-switch-note">
          {server.local ? "你正在此服务器门户" : "当前不可安全切换"}
        </span>
      )}
    </article>
  );
}

function accountLabel(server: FederationServer): string {
  if (server.availability !== "available" || server.accountExists === null) return "未读取";
  if (!server.accountExists) return "尚未建立";
  return server.accountDisplayName ?? "已建立";
}

function formatObservedAt(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? "--"
    : new Intl.DateTimeFormat("zh-CN", {
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      hour12: false
    }).format(parsed);
}

function isAbort(reason: unknown): boolean {
  return reason instanceof DOMException && reason.name === "AbortError";
}
