import { useCallback, useEffect, useMemo, useState } from "react";
import {
  ApiClientError,
  createTeamEconomyTeam,
  dissolveTeamEconomyTeam,
  getTeamEconomy,
  getTeamEconomyLeaderboard,
  joinTeamEconomyTeam,
  leaveTeamEconomyTeam,
  rotateTeamEconomyInvitation,
  TeamEconomyContribution,
  TeamEconomyDashboard,
  TeamEconomyInvitation,
  TeamEconomyLeaderboard,
  TeamEconomyLeaderboardMetric,
  transferTeamEconomyOwner
} from "./api";

type Props = {
  csrfToken: string;
  onSessionExpired: () => void;
  refreshSignal: number;
};

const metrics: Array<{ key: TeamEconomyLeaderboardMetric; title: string; unit: string }> = [
  { key: "resourceValue", title: "资源价值榜", unit: "价值" },
  { key: "taskPoints", title: "可靠任务积分榜", unit: "分" },
  { key: "deliveredOrders", title: "成功送达榜", unit: "单" }
];

export function TeamEconomyPanel({ csrfToken, onSessionExpired, refreshSignal }: Props) {
  const [dashboard, setDashboard] = useState<TeamEconomyDashboard | null>(null);
  const [leaderboards, setLeaderboards] = useState<Partial<Record<TeamEconomyLeaderboardMetric, TeamEconomyLeaderboard>>>({});
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [teamName, setTeamName] = useState("");
  const [joinToken, setJoinToken] = useState("");
  const [invitation, setInvitation] = useState<TeamEconomyInvitation | null>(null);
  const [transferHandle, setTransferHandle] = useState("");
  const [dissolveConfirmation, setDissolveConfirmation] = useState("");

  const load = useCallback(async (quiet = false) => {
    if (!quiet) setLoading(true);
    setError(null);
    try {
      const next = await getTeamEconomy();
      setDashboard(next);
      if (next.hasTeam && next.projection.ready) {
        const boards = await Promise.all(metrics.map(async ({ key }) => [
          key,
          await getTeamEconomyLeaderboard(key)
        ] as const));
        setLeaderboards(Object.fromEntries(boards));
      } else {
        setLeaderboards({});
      }
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setError(reason instanceof Error ? reason.message : "团队协作数据暂时无法读取。");
    } finally {
      setLoading(false);
    }
  }, [onSessionExpired]);

  useEffect(() => {
    void load();
  }, [load, refreshSignal]);

  const runMutation = useCallback(async (
    action: () => Promise<unknown>,
    success: string
  ) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      setNotice(success);
      await load(true);
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setError(reason instanceof Error ? reason.message : "团队操作失败，请稍后重试。");
    } finally {
      setBusy(false);
    }
  }, [load, onSessionExpired]);

  const goalSummary = useMemo(() => {
    const achieved = dashboard?.goals.filter((goal) => goal.achieved).length ?? 0;
    return `${achieved} / ${dashboard?.goals.length ?? 0}`;
  }, [dashboard]);

  async function rotateInvitation() {
    setBusy(true);
    setError(null);
    setNotice(null);
    // A rotated token intentionally lives only in React memory. Navigating
    // away, refreshing, or closing the page destroys it.
    setInvitation(null);
    try {
      const result = await rotateTeamEconomyInvitation(10, crypto.randomUUID(), csrfToken);
      setInvitation(result);
      setNotice(result.tokenShown
        ? "新邀请已生成，请现在复制。离开本页后无法再次查看。"
        : "该请求已安全重放；一次性邀请不会再次显示，请重新轮换。"
      );
    } catch (reason) {
      if (reason instanceof ApiClientError && reason.status === 401) {
        onSessionExpired();
        return;
      }
      setError(reason instanceof Error ? reason.message : "邀请轮换失败。");
    } finally {
      setBusy(false);
    }
  }

  if (loading) {
    return <div className="team-message" role="status">正在同步团队目标与排行榜…</div>;
  }
  if (error && !dashboard) {
    return <div className="team-message error" role="alert"><span>{error}</span><button className="secondary compact" onClick={() => void load()}>重试</button></div>;
  }
  if (dashboard?.enabled === false) {
    return (
      <div className="team-economy">
        <section className="team-onboarding" aria-labelledby="team-disabled-title">
          <div>
            <span className="eyebrow">WEEKLY TEAM</span>
            <h3 id="team-disabled-title">本服务器尚未启用团队协作</h3>
            <p>{dashboard.policyNotice}</p>
          </div>
        </section>
      </div>
    );
  }
  if (!dashboard?.hasTeam) {
    return (
      <div className="team-economy">
        {error && <div className="team-message error" role="alert">{error}</div>}
        {notice && <div className="team-message success" role="status">{notice}</div>}
        <section className="team-onboarding" aria-labelledby="team-onboarding-title">
          <div>
            <span className="eyebrow">WEEKLY TEAM</span>
            <h3 id="team-onboarding-title">组建本周资源协作小队</h3>
            <p>团队只汇总加入之后、离队之前的服务端权威经济事实；不会追溯旧贡献，也不会因为里程碑自动发币。</p>
          </div>
          <div className="team-entry-grid">
            <form onSubmit={(event) => {
              event.preventDefault();
              void runMutation(
                () => createTeamEconomyTeam(teamName, crypto.randomUUID(), csrfToken),
                "小队已创建。你可以轮换一次性邀请，让队友加入。"
              );
            }}>
              <h4>创建小队</h4>
              <label htmlFor="team-name">小队名称（2–32 字符）</label>
              <input id="team-name" value={teamName} minLength={2} maxLength={32} required autoComplete="off" onChange={(event) => setTeamName(event.target.value)} />
              <button className="primary" disabled={busy || teamName.trim().length < 2}>创建</button>
            </form>
            <form onSubmit={(event) => {
              event.preventDefault();
              void runMutation(
                () => joinTeamEconomyTeam(joinToken, crypto.randomUUID(), csrfToken),
                "已加入小队；加入前的历史贡献不会倒灌。"
              );
            }}>
              <h4>使用邀请加入</h4>
              <label htmlFor="team-invite-token">一次性邀请 token</label>
              <input id="team-invite-token" type="password" value={joinToken} required autoComplete="off" spellCheck={false} onChange={(event) => setJoinToken(event.target.value)} />
              <small>token 仅用于本次提交，不会写入浏览器存储。</small>
              <button className="primary" disabled={busy || !joinToken.trim()}>加入</button>
            </form>
          </div>
          <p className="team-policy">{dashboard?.policyNotice}</p>
        </section>
      </div>
    );
  }

  return (
    <div className="team-economy">
      {error && <div className="team-message error" role="alert">{error}</div>}
      {notice && <div className="team-message success" role="status">{notice}</div>}
      <section className="team-hero" aria-labelledby="team-title">
        <div>
          <span className="eyebrow">AUTHORITATIVE TEAM PROJECTION</span>
          <h3 id="team-title">{dashboard.name}</h3>
          <p>{dashboard.memberCount} 名成员 · {dashboard.isOwner ? "你是队长" : "本周小队成员"} · 目标 {goalSummary}</p>
        </div>
        <div className={dashboard.projection.stale ? "team-health stale" : "team-health"} role="status">
          <strong>{dashboard.projection.ready ? (dashboard.projection.stale ? "使用上次安全快照" : "权威投影正常") : "等待权威投影"}</strong>
          <small>{dashboard.projection.cutoffAt ? `统计至 ${formatDateTime(dashboard.projection.cutoffAt)}` : "不会伪造 0 进度"}</small>
        </div>
      </section>

      {!dashboard.projection.ready ? (
        <div className="team-message warning" role="status">服务端尚未完成首次权威投影。目标与排行榜暂不显示，避免把未知状态伪装成 0。</div>
      ) : (
        <>
          <section className="team-goals" aria-labelledby="team-goals-title">
            <header><div><span className="eyebrow">COOPERATIVE GOALS</span><h3 id="team-goals-title">本周合作目标</h3></div><span>达标只记录里程碑，不新增货币奖励</span></header>
            <div className="team-goal-grid">
              {dashboard.goals.map((goal) => {
                const progress = Math.min(goal.progress, goal.target);
                const ratio = goal.target > 0 ? Math.min(100, progress * 100 / goal.target) : 0;
                return <article className={goal.achieved ? "achieved" : ""} key={goal.kind}>
                  <div><span>{goal.displayName}</span>{goal.achieved && <em>已达标</em>}</div>
                  <strong>{goal.progress.toLocaleString("zh-CN")} <small>/ {goal.target.toLocaleString("zh-CN")} {goal.unit}</small></strong>
                  <div className="team-progress" role="progressbar" aria-label={goal.displayName} aria-valuemin={0} aria-valuemax={goal.target} aria-valuenow={progress}><span style={{ width: `${ratio}%` }} /></div>
                  <small>{goal.reachedAt ? `达标于 ${formatDateTime(goal.reachedAt)}` : "继续完成服务端确认的兑换、任务或送达"}</small>
                </article>;
              })}
            </div>
          </section>

          <section className="team-contributions" aria-label="团队与本人贡献">
            <ContributionCard title="团队累计" contribution={dashboard.teamContribution} />
            <ContributionCard title="我的贡献" contribution={dashboard.myContribution} />
          </section>

          <section className="team-leaderboards" aria-labelledby="team-rankings-title">
            <header><div><span className="eyebrow">TEAM RANKINGS</span><h3 id="team-rankings-title">本周团队三榜</h3></div><span>同值按先达到时间、再按 teamId 排序</span></header>
            <div className="team-board-grid">
              {metrics.map((metric) => {
                const board = leaderboards[metric.key];
                return <article key={metric.key}>
                  <h4>{metric.title}</h4>
                  {!board ? <p>排行榜暂不可用。</p> : board.items.length === 0 ? <p>还没有满足最低贡献的队伍。</p> : (
                    <ol>
                      {board.items.slice(0, 10).map((item) => <li className={item.isMyTeam ? "mine" : ""} key={item.teamId}>
                        <strong>#{item.rank}</strong><span>{item.teamName}<small>{item.memberCount} 人</small></span><b>{item.value.toLocaleString("zh-CN")} {metric.unit}</b>
                      </li>)}
                    </ol>
                  )}
                  {board && <small>{board.total} 支合格队伍 · 截止 {formatDateTime(board.cutoffAt)}</small>}
                </article>;
              })}
            </div>
          </section>
        </>
      )}

      <section className="team-management" aria-labelledby="team-management-title">
        <header><div><span className="eyebrow">TEAM MANAGEMENT</span><h3 id="team-management-title">成员与邀请</h3></div></header>
        {dashboard.isOwner ? (
          <div className="team-management-grid">
            <div>
              <h4>轮换邀请</h4>
              <p>旧邀请会立即失效；新 token 只在本次响应中显示一次。</p>
              <button className="secondary" disabled={busy} onClick={() => void rotateInvitation()}>生成并轮换邀请</button>
              {invitation?.tokenShown && invitation.token && (
                <div className="team-token" role="status">
                  <strong>立即复制，离开后不可找回</strong>
                  <code>{invitation.token}</code>
                  <button type="button" className="secondary compact" onClick={() => void navigator.clipboard.writeText(invitation.token!)}>复制</button>
                  <small>有效至 {formatDateTime(invitation.expiresAt)} · 最多 {invitation.maximumUses} 次</small>
                </div>
              )}
            </div>
            <div>
              <h4>转让队长</h4>
              <p>成员只以本队临时不透明编号显示，不暴露平台或游戏身份。</p>
              <label htmlFor="team-transfer">选择成员</label>
              <select id="team-transfer" value={transferHandle} onChange={(event) => setTransferHandle(event.target.value)}>
                <option value="">请选择</option>
                {dashboard.transferCandidates.map((candidate) => <option key={candidate.memberHandle} value={candidate.memberHandle}>{candidate.label} · 加入于 {formatDateTime(candidate.joinedAt)}</option>)}
              </select>
              <button className="secondary" disabled={busy || !transferHandle} onClick={() => {
                if (!globalThis.confirm("确认把队长权限转让给所选成员？转让后你将失去邀请轮换与解散权限。")) return;
                void runMutation(
                  () => transferTeamEconomyOwner(transferHandle, crypto.randomUUID(), csrfToken),
                  "队长权限已转让。"
                );
              }}>确认转让</button>
            </div>
            <div className="danger-zone">
              <h4>解散小队</h4>
              <p>解散后历史贡献保留用于审计，但队伍从公开排行榜隐藏。请输入完整队名确认。</p>
              <label htmlFor="team-dissolve">输入“{dashboard.name}”</label>
              <input id="team-dissolve" value={dissolveConfirmation} autoComplete="off" onChange={(event) => setDissolveConfirmation(event.target.value)} />
              <button className="danger" disabled={busy || dissolveConfirmation !== dashboard.name} onClick={() => {
                if (!globalThis.confirm("确认永久解散本周小队？此操作不能撤销。")) return;
                void runMutation(
                  () => dissolveTeamEconomyTeam(dissolveConfirmation, crypto.randomUUID(), csrfToken),
                  "小队已解散。"
                );
              }}>永久解散</button>
            </div>
          </div>
        ) : (
          <div className="team-leave">
            <div><h4>离开小队</h4><p>离队后新贡献不再计入；已经产生的团队贡献不会转移到下一支队伍。</p></div>
            <button className="danger" disabled={busy} onClick={() => {
              if (!globalThis.confirm("确认离开当前小队？加入前和离队后的贡献都不会计入该小队。")) return;
              void runMutation(
                () => leaveTeamEconomyTeam(crypto.randomUUID(), csrfToken),
                "已离开小队。"
              );
            }}>确认离队</button>
          </div>
        )}
        <p className="team-policy">{dashboard.policyNotice}</p>
      </section>
    </div>
  );
}

function ContributionCard({ title, contribution }: { title: string; contribution: TeamEconomyContribution | null }) {
  return (
    <article>
      <h3>{title}</h3>
      <dl>
        <div><dt>资源件数</dt><dd>{formatNumber(contribution?.resourceItems)}</dd></div>
        <div><dt>兑换价值</dt><dd>{formatNumber(contribution?.resourceValue)}</dd></div>
        <div><dt>任务积分</dt><dd>{formatNumber(contribution?.taskPoints)}</dd></div>
        <div><dt>成功送达</dt><dd>{formatNumber(contribution?.deliveredOrders)}</dd></div>
        <div><dt>实际货币消费</dt><dd>{formatNumber(contribution?.actualCurrencySpent)}</dd></div>
      </dl>
    </article>
  );
}

function formatNumber(value: number | undefined) {
  return value === undefined ? "--" : value.toLocaleString("zh-CN");
}

function formatDateTime(value: string) {
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  }).format(new Date(value));
}
