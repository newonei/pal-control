import { useEffect, useMemo, useRef, useState } from "react";
import {
  getPalDefenderBanlist,
  getPalDefenderCommand,
  getPalDefenderGuild,
  getPalDefenderGuilds,
  submitPalDefenderCommand,
  type PalDefenderBanlist,
  type PalDefenderCommand,
  type PalDefenderGuild,
  type PalDefenderGuilds
} from "../../lib/api/client";

const terminalStates = new Set(["succeeded", "failed", "uncertain", "cancelled"]);

function uniqueKey() {
  return globalThis.crypto?.randomUUID?.() ?? `pd-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function stringField(value: unknown, ...keys: string[]) {
  const record = asRecord(value);
  for (const key of keys) {
    const result = record[key];
    if (typeof result === "string" && result) return result;
  }
  return "";
}

function numberField(value: unknown, ...keys: string[]) {
  const record = asRecord(value);
  for (const key of keys) {
    const result = record[key];
    if (typeof result === "number" && Number.isFinite(result)) return result;
  }
  return 0;
}

export function PalDefenderDirectory({ connected }: { connected: boolean }) {
  const [guilds, setGuilds] = useState<PalDefenderGuilds>();
  const [guild, setGuild] = useState<PalDefenderGuild>();
  const [banlist, setBanlist] = useState<PalDefenderBanlist>();
  const [selectedGuildId, setSelectedGuildId] = useState("");
  const [query, setQuery] = useState("");
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const commandInFlight = useRef(false);

  const guildEntries = useMemo(
    () => Object.entries(guilds?.Guilds ?? {}),
    [guilds]
  );
  const userEntries = useMemo(
    () => Array.isArray(banlist?.Banlist?.UserEntries) ? banlist.Banlist.UserEntries : [],
    [banlist]
  );
  const ipEntries = useMemo(
    () => Array.isArray(banlist?.Banlist?.IPEntries) ? banlist.Banlist.IPEntries : [],
    [banlist]
  );
  const normalizedQuery = query.trim().toLocaleLowerCase();
  const filteredUsers = userEntries.filter((entry) => JSON.stringify(entry).toLocaleLowerCase().includes(normalizedQuery));
  const filteredIps = ipEntries.filter((entry) => JSON.stringify(entry).toLocaleLowerCase().includes(normalizedQuery));

  async function refresh(signal?: AbortSignal) {
    if (!connected) {
      setLoading(false);
      return;
    }
    setLoading(true);
    setError(undefined);
    try {
      const [nextGuilds, nextBanlist] = await Promise.all([
        getPalDefenderGuilds(signal),
        getPalDefenderBanlist(undefined, signal)
      ]);
      setGuilds(nextGuilds);
      setBanlist(nextBanlist);
      const firstId = selectedGuildId || Object.keys(nextGuilds.Guilds ?? {})[0] || "";
      setSelectedGuildId(firstId);
      if (firstId) setGuild(await getPalDefenderGuild(firstId, signal));
    } catch (nextError) {
      if (!signal?.aborted) setError(nextError instanceof Error ? nextError.message : "公会与封禁资料读取失败");
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [connected]);

  async function selectGuild(guildId: string) {
    setSelectedGuildId(guildId);
    setError(undefined);
    try {
      setGuild(await getPalDefenderGuild(guildId));
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "公会详情读取失败");
    }
  }

  async function unban(kind: "user" | "ip", value: string) {
    if (commandInFlight.current) return;
    if (!value) {
      setError("该封禁记录缺少可识别的账号或 IP。");
      return;
    }
    if (!globalThis.confirm(`确认解除 ${kind === "user" ? "账号" : "IP"} “${value}” 的封禁？`)) return;
    commandInFlight.current = true;
    setBusy(true);
    setError(undefined);
    setNotice(undefined);
    try {
      let command: PalDefenderCommand = await submitPalDefenderCommand({
        path: `${kind === "user" ? "unban" : "unbanip"}/${encodeURIComponent(value)}`,
        payload: { Reason: "控制台解除封禁" },
        reason: "控制台解除封禁",
        idempotencyKey: uniqueKey()
      });
      for (let index = 0; index < 60 && !terminalStates.has(command.state); index += 1) {
        await new Promise((resolve) => globalThis.setTimeout(resolve, 750));
        command = await getPalDefenderCommand(command.statusUrl);
      }
      if (command.state === "succeeded") {
        setNotice(`已解除 ${value} 的封禁。`);
        await refresh();
      } else if (command.state === "uncertain") {
        setError("解除结果不确定，请刷新封禁列表人工核验，勿重复提交。");
      } else {
        setError(command.error?.message ?? `命令状态：${command.state}`);
      }
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "解除封禁失败");
    } finally {
      commandInFlight.current = false;
      setBusy(false);
    }
  }

  const detail = guild?.Guild;
  const camps = Array.isArray(detail?.camps) ? detail.camps : [];
  const members = Array.isArray(detail?.members) ? detail.members : [];

  return (
    <section className="pd-page pd-directory-page">
      <header className="pd-page-heading">
        <div>
          <p className="eyebrow">COMMUNITY DIRECTORY</p>
          <h2>公会与封禁</h2>
          <p>查看公会成员、基地和封禁名单；解除操作由持久队列审计。</p>
        </div>
        <button className="ghost-button" disabled={loading || !connected} onClick={() => void refresh()} type="button">
          {loading ? "同步中…" : "刷新目录"}
        </button>
      </header>

      {!connected ? <div className="pd-banner warning"><strong>PalDefender 未连接</strong><span>公会与封禁目录暂不可读取。</span></div> : null}
      {error ? <div className="pd-feedback error" role="alert">{error}</div> : null}
      {notice ? <div className="pd-feedback success" role="status">{notice}</div> : null}

      <div className="pd-directory-layout">
        <aside className="pd-guild-list">
          <div className="pd-section-title"><div><span>公会目录</span><strong>{guildEntries.length} 个公会</strong></div></div>
          {guildEntries.map(([guildId, summary]) => {
            const item = asRecord(summary);
            return (
              <button className={selectedGuildId === guildId ? "active" : ""} key={guildId} onClick={() => void selectGuild(guildId)} type="button">
                <span className="pd-guild-mark">会</span>
                <span><strong>{stringField(item, "name", "Name") || "未命名公会"}</strong><small>{numberField(item, "member_count", "MemberCount")} 名成员 · {numberField(item, "camp_count", "CampCount")} 个基地</small></span>
              </button>
            );
          })}
          {guildEntries.length === 0 && !loading ? <div className="pd-empty">当前没有公会资料</div> : null}
        </aside>

        <section className="pd-guild-detail">
          <header>
            <div><p className="eyebrow">GUILD PROFILE</p><h3>{stringField(detail, "name", "Name") || "选择一个公会"}</h3></div>
            <span className="pd-state info">Lv.{numberField(detail, "Level", "level") || "--"}</span>
          </header>
          <div className="pd-metric-grid compact">
            <Metric label="成员" value={numberField(detail, "member_count", "MemberCount") || members.length} />
            <Metric label="基地" value={numberField(detail, "camp_count", "CampCount") || camps.length} />
            <Metric label="管理员" value={stringField(asRecord(detail).admin, "name", "Name") || "--"} />
          </div>
          <div className="pd-detail-columns">
            <section className="pd-read-card">
              <div className="pd-section-title"><div><span>成员标识</span><strong>{members.length} 条</strong></div></div>
              <div className="pd-code-list">
                {members.map((member, index) => <article className="pd-member-row" key={`${stringField(member, "player_uid")}-${index}`}>
                  <span><strong>{stringField(member, "player_name", "name", "Name") || "未知成员"}</strong><small>{stringField(member, "status", "Status") || "状态未知"}</small></span>
                  <code>{stringField(member, "player_uid", "id", "PlayerUID", "UserId") || "--"}</code>
                </article>)}
                {members.length === 0 ? <div className="pd-empty">无成员明细</div> : null}
              </div>
            </section>
            <section className="pd-read-card">
              <div className="pd-section-title"><div><span>基地目录</span><strong>{camps.length} 个</strong></div></div>
              <div className="pd-base-list">
                {camps.map((camp, index) => <article key={`${stringField(camp, "id", "BaseCampId", "base_camp_id")}-${index}`}>
                  <strong>{stringField(camp, "name", "Name") || `基地 ${index + 1}`} · Lv.{numberField(camp, "level", "Level") || "--"}</strong>
                  <small>{stringField(camp, "id", "BaseCampId", "base_camp_id") || "未返回基地 GUID"}</small>
                  <small>{formatPosition(asRecord(camp).map_pos)}</small>
                  <button disabled title="当前令牌未授予 REST.Base.Delete 权限" type="button">删除基地已锁定</button>
                </article>)}
                {camps.length === 0 ? <div className="pd-empty">该公会暂无基地</div> : null}
              </div>
            </section>
          </div>
          {detail ? <div className="pd-guild-resources">
            <article><span>公会仓储</span><strong>{detail.items?.current ?? 0} / {detail.items?.max ?? 0}</strong><small>{detail.items?.container_id || "未返回容器 ID"}</small></article>
            <article><span>探索任务</span><strong>{detail.expeditions?.finished ?? 0} 次完成</strong><small>{Object.keys(detail.expeditions?.missions ?? {}).length} 条任务记录</small></article>
            <article><span>当前研究</span><strong>{detail.laboratory?.current_research || "暂无研究"}</strong><small>{Object.keys(detail.laboratory?.researches ?? {}).length} 项研究记录</small></article>
          </div> : null}
          {Object.keys(detail?.laboratory?.researches ?? {}).length > 0 ? <section className="pd-research-list">
            <div className="pd-section-title"><div><span>研究进度</span><strong>{Object.keys(detail?.laboratory?.researches ?? {}).length} 项</strong></div></div>
            {Object.entries(detail?.laboratory?.researches ?? {}).map(([name, research]) => <article key={name}>
              <span><strong>{name}</strong><small>{research.work_amount} / {research.required_work_amount}</small></span>
              <div><i style={{ width: `${Math.max(0, Math.min(100, research.percentage))}%` }} /></div>
              <em>{research.percentage.toFixed(1)}%</em>
            </article>)}
          </section> : null}
        </section>
      </div>

      <section className="pd-ban-panel">
        <header className="pd-ban-heading">
          <div><p className="eyebrow">BANLIST</p><h3>封禁记录</h3><p>{banlist?.Banlist?.BannedMessage || "服务器封禁名单"}</p></div>
          <label className="pd-search"><span>搜索</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="账号、IP、原因或签发人" /></label>
        </header>
        <div className="pd-ban-columns">
          <BanColumn
            busy={busy}
            entries={filteredUsers}
            kind="user"
            onUnban={(entry) => void unban("user", stringField(entry, "UserId", "UserID", "userId", "user_id", "ID", "id"))}
          />
          <BanColumn
            busy={busy}
            entries={filteredIps}
            kind="ip"
            onUnban={(entry) => void unban("ip", stringField(entry, "IP", "Ip", "ip", "Address", "address"))}
          />
        </div>
      </section>
    </section>
  );
}

function formatPosition(value: unknown) {
  const position = asRecord(value);
  const x = typeof position.x === "number" ? position.x.toFixed(1) : "--";
  const y = typeof position.y === "number" ? position.y.toFixed(1) : "--";
  return `地图坐标 X ${x} · Y ${y}`;
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return <article className="pd-metric"><span>{label}</span><strong>{value}</strong></article>;
}

function BanColumn({ entries, kind, busy, onUnban }: {
  entries: unknown[];
  kind: "user" | "ip";
  busy: boolean;
  onUnban: (entry: unknown) => void;
}) {
  return (
    <section className="pd-ban-column">
      <div className="pd-section-title"><div><span>{kind === "user" ? "账号封禁" : "IP 封禁"}</span><strong>{entries.length} 条</strong></div></div>
      {entries.map((entry, index) => {
        const primary = kind === "user"
          ? stringField(entry, "UserId", "UserID", "userId", "user_id", "ID", "id")
          : stringField(entry, "IP", "Ip", "ip", "Address", "address");
        const reason = stringField(entry, "Reason", "reason") || stringField(asRecord(entry).BannedBy, "Reason", "reason") || "未填写原因";
        const active = asRecord(entry).Active !== false;
        return <article className="pd-ban-row" key={`${primary}-${index}`}>
          <div><strong>{primary || "未知记录"}</strong><small>{active ? reason : `已解除 · ${reason}`}</small></div>
          <button className="ghost-button" disabled={busy || !primary || !active} onClick={() => onUnban(entry)} type="button">{active ? "解除" : "已解除"}</button>
        </article>;
      })}
      {entries.length === 0 ? <div className="pd-empty">没有匹配的封禁记录</div> : null}
    </section>
  );
}
