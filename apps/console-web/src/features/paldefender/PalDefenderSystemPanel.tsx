import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent
} from "react";
import {
  getPalDefenderAudit,
  getPalDefenderCatalog,
  getPalDefenderCommand,
  getPalDefenderStatus,
  submitPalDefenderCommand,
  type PalDefenderAuditEvent,
  type PalDefenderCatalog,
  type PalDefenderCommand,
  type PalDefenderCommandState,
  type PalDefenderStatus
} from "../../lib/api/client";

type CommandAction = "Alert" | "Broadcast" | "ReloadConfig";

type PalDefenderSystemPanelProps = {
  status?: PalDefenderStatus;
  serverId?: string;
};

const actions: Array<{
  key: CommandAction;
  title: string;
  description: string;
  permission: string;
}> = [
  {
    key: "Alert",
    title: "发送警报",
    description: "通过 PalDefender 向游戏内发送高可见度警报。",
    permission: "REST.Messages.Alert"
  },
  {
    key: "Broadcast",
    title: "广播消息",
    description: "向当前服务器的聊天频道广播一条全服消息。",
    permission: "REST.Messages.Broadcast"
  },
  {
    key: "ReloadConfig",
    title: "热重载配置",
    description: "让 PalDefender 重新读取配置，不重启 PalServer。",
    permission: "REST.Reload.Config"
  }
];

export function PalDefenderSystemPanel({
  status: providedStatus,
  serverId = "local"
}: PalDefenderSystemPanelProps = {}) {
  const [status, setStatus] = useState<PalDefenderStatus | null>(providedStatus ?? null);
  const [catalog, setCatalog] = useState<PalDefenderCatalog | null>(null);
  const [audit, setAudit] = useState<PalDefenderAuditEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState<string>();
  const [activeAction, setActiveAction] = useState<CommandAction>("Alert");
  const [message, setMessage] = useState("");
  const [reason, setReason] = useState("网页控制台执行 PalDefender 运维命令");
  const [reloadConfirmed, setReloadConfirmed] = useState(false);
  const [command, setCommand] = useState<PalDefenderCommand>();
  const [sending, setSending] = useState(false);
  const [commandNotice, setCommandNotice] = useState<string>();
  const [commandError, setCommandError] = useState<string>();
  const pollControllerRef = useRef<AbortController | null>(null);
  const submitLockRef = useRef(false);

  useEffect(() => {
    if (providedStatus) {
      setStatus(providedStatus);
    }
  }, [providedStatus]);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setPageError(undefined);
    try {
      const [nextStatus, nextCatalog, nextAudit] = await Promise.all([
        getPalDefenderStatus(serverId, signal),
        getPalDefenderCatalog(serverId, signal),
        getPalDefenderAudit(80, signal)
      ]);
      setStatus(nextStatus);
      setCatalog(nextCatalog);
      setAudit(nextAudit);
    } catch (error) {
      if (!signal?.aborted) {
        setPageError(error instanceof Error ? error.message : "PalDefender 状态读取失败");
      }
    } finally {
      if (!signal?.aborted) {
        setLoading(false);
      }
    }
  }, [serverId]);

  useEffect(() => {
    const controller = new AbortController();
    void refresh(controller.signal);
    return () => controller.abort();
  }, [refresh]);

  useEffect(() => () => pollControllerRef.current?.abort(), []);

  const selectedAction = useMemo(
    () => actions.find((action) => action.key === activeAction) ?? actions[0],
    [activeAction]
  );
  const version = status?.version?.Version.VersionLong ?? status?.version?.Version.Version;
  const canSubmit = Boolean(
    status?.connected &&
    reason.trim().length >= 3 &&
    !sending &&
    (activeAction === "ReloadConfig" ? reloadConfirmed : message.trim().length > 0)
  );

  function chooseAction(action: CommandAction) {
    if (sending) {
      return;
    }
    setActiveAction(action);
    setCommand(undefined);
    setCommandNotice(undefined);
    setCommandError(undefined);
    setReloadConfirmed(false);
  }

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!canSubmit || submitLockRef.current) {
      return;
    }

    pollControllerRef.current?.abort();
    const controller = new AbortController();
    pollControllerRef.current = controller;
    submitLockRef.current = true;
    setSending(true);
    setCommand(undefined);
    setCommandError(undefined);
    setCommandNotice("命令正在持久化并进入幂等队列……");

    try {
      const idempotencyKey = createIdempotencyKey(activeAction);
      const payload = activeAction === "ReloadConfig" ? {} : { Message: message.trim() };
      let nextCommand = await submitPalDefenderCommand({
        serverId,
        path: activeAction,
        payload,
        reason: reason.trim(),
        idempotencyKey
      });
      setCommand(nextCommand);

      for (let attempt = 0; attempt < 40 && isPending(nextCommand.state); attempt += 1) {
        await abortableDelay(500, controller.signal);
        nextCommand = await getPalDefenderCommand(nextCommand.statusUrl, controller.signal);
        setCommand(nextCommand);
      }

      if (isPending(nextCommand.state)) {
        setCommandNotice(`命令 ${shortId(nextCommand.commandId)} 仍在处理中，可稍后从审计记录确认最终状态。`);
      } else if (nextCommand.state === "succeeded") {
        setCommandNotice(
          `${selectedAction.title}已完成，上游返回 HTTP ${nextCommand.result?.httpStatus ?? "--"}。`
        );
        if (activeAction !== "ReloadConfig") {
          setMessage("");
        }
        setReloadConfirmed(false);
      } else if (nextCommand.state === "uncertain") {
        setCommandNotice("命令结果无法确认。系统不会自动重发，请先在游戏内核实。");
      } else {
        setCommandNotice(undefined);
        setCommandError(nextCommand.error?.message ?? `${selectedAction.title}失败。`);
      }

      try {
        const [nextAudit, nextStatus] = await Promise.all([
          getPalDefenderAudit(80, controller.signal),
          getPalDefenderStatus(serverId, controller.signal)
        ]);
        setAudit(nextAudit);
        setStatus(nextStatus);
      } catch (refreshError) {
        if (!controller.signal.aborted) {
          setPageError(
            refreshError instanceof Error ? refreshError.message : "命令完成，但状态刷新失败"
          );
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        setCommandNotice(undefined);
        setCommandError(error instanceof Error ? error.message : `${selectedAction.title}失败。`);
      }
    } finally {
      submitLockRef.current = false;
      if (!controller.signal.aborted) {
        setSending(false);
      }
    }
  }

  return (
    <section className="paldefender-system-page" aria-label="PalDefender 系统中心">
      <header className="paldefender-heading">
        <div>
          <p className="eyebrow">PALDEFENDER / TRUSTED LOCAL ADAPTER</p>
          <h2>PalDefender 系统中心</h2>
          <p>查看连接、白名单能力和审计事件，并通过持久化命令队列执行运维操作。</p>
        </div>
        <div className="paldefender-heading-actions">
          <span className={status?.connected ? "native-ready" : "native-ready locked"}>
            {status?.connected ? "REST 已连接" : status?.enabled ? "连接异常" : "未启用"}
          </span>
          <button className="ghost-button" disabled={loading || sending} onClick={() => void refresh()} type="button">
            {loading ? "刷新中……" : "刷新状态"}
          </button>
        </div>
      </header>

      {pageError ? <div className="paldefender-feedback error" role="alert">{pageError}</div> : null}
      {status?.error ? (
        <div className="paldefender-feedback warning">
          <strong>{status.error.code}</strong>
          <span>{status.error.message}</span>
        </div>
      ) : null}

      <div className="metric-grid paldefender-status-grid">
        <SystemMetric
          detail={status?.baseUrl ?? "未配置上游地址"}
          label="REST 连接"
          tone={status?.connected ? "positive" : "warning"}
          value={status?.connected ? "在线" : "离线"}
        />
        <SystemMetric
          detail={status?.version?.Version.Beta ? "Beta 构建" : "稳定构建"}
          label="版本"
          tone={version ? "positive" : "neutral"}
          value={version ?? "--"}
        />
        <SystemMetric
          detail={catalog?.basePath ?? `/api/v1/servers/${serverId}/paldefender`}
          label="白名单端点"
          tone={catalog?.count ? "positive" : "neutral"}
          value={String(catalog?.count ?? 0)}
        />
        <SystemMetric
          detail="最近 80 条命令事件"
          label="审计事件"
          tone={audit.length > 0 ? "positive" : "neutral"}
          value={String(audit.length)}
        />
      </div>

      <div className="paldefender-workbench">
        <section className="paldefender-command-card">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">GUARDED COMMANDS</p>
              <h3>运维命令</h3>
            </div>
            <span className="count-pill">3</span>
          </div>

          <div className="paldefender-action-tabs" role="tablist" aria-label="PalDefender 运维命令">
            {actions.map((action) => (
              <button
                aria-selected={activeAction === action.key}
                className={activeAction === action.key ? "active" : ""}
                disabled={sending}
                key={action.key}
                onClick={() => chooseAction(action.key)}
                role="tab"
                type="button"
              >
                <strong>{action.title}</strong>
                <small>{action.permission}</small>
              </button>
            ))}
          </div>

          <form className="paldefender-command-form" onSubmit={submit}>
            <div className="paldefender-action-copy">
              <strong>{selectedAction.title}</strong>
              <p>{selectedAction.description}</p>
            </div>

            {activeAction === "ReloadConfig" ? (
              <label className="paldefender-confirmation">
                <input
                  checked={reloadConfirmed}
                  disabled={sending}
                  onChange={(event) => setReloadConfirmed(event.target.checked)}
                  type="checkbox"
                />
                <span>
                  <strong>确认热重载 PalDefender 配置</strong>
                  <small>此操作不会重启 PalServer，但会立即应用 PalDefender 配置变更。</small>
                </span>
              </label>
            ) : (
              <label className="field-label">
                {activeAction === "Alert" ? "警报内容" : "广播内容"}
                <textarea
                  disabled={sending}
                  maxLength={1000}
                  onChange={(event) => setMessage(event.target.value)}
                  placeholder={activeAction === "Alert" ? "例如：服务器将在十分钟后维护" : "输入全服聊天广播"}
                  value={message}
                />
                <small>{message.length} / 1000</small>
              </label>
            )}

            <label className="field-label">
              审计原因
              <input
                disabled={sending}
                maxLength={500}
                onChange={(event) => setReason(event.target.value)}
                value={reason}
              />
              <small>至少 3 个字符；会随命令写入追加式审计。</small>
            </label>

            {commandError ? <div className="paldefender-feedback error" role="alert">{commandError}</div> : null}
            {commandNotice ? <div className="paldefender-feedback success" aria-live="polite">{commandNotice}</div> : null}

            <div className="paldefender-command-actions">
              <span>{status?.connected ? "202 仅表示已入队，页面会继续查询终态" : "需要 PalDefender REST 在线"}</span>
              <button className="primary-button" disabled={!canSubmit} type="submit">
                {sending ? "命令处理中……" : `执行${selectedAction.title}`}
              </button>
            </div>
          </form>

          <CommandResult command={command} />
        </section>

        <section className="paldefender-catalog-card">
          <div className="panel-heading">
            <div>
              <p className="eyebrow">API CATALOG</p>
              <h3>能力目录</h3>
            </div>
            <span className="count-pill">{catalog?.count ?? 0}</span>
          </div>
          <div className="paldefender-catalog-list">
            {(catalog?.items ?? []).map((item) => {
              const locked = item.permission === "REST.Base.Delete";
              return (
              <article className={locked ? "paldefender-catalog-row locked" : "paldefender-catalog-row"} key={`${item.method}-${item.path}`}>
                <span className={`paldefender-method method-${item.method.toLocaleLowerCase()}`}>{item.method}</span>
                <div>
                  <strong>{item.description}</strong>
                  <code>{item.path}</code>
                </div>
                <small>{item.permission}{locked ? " · 当前令牌未授权" : ""}</small>
              </article>
              );
            })}
            {!loading && !catalog?.items.length ? (
              <div className="paldefender-empty">当前没有可展示的白名单端点。</div>
            ) : null}
          </div>
          <p className="paldefender-catalog-note">能力目录表示控制 API 的允许路径；实际执行仍受 PalDefender 令牌权限约束。删除基地权限已锁定。</p>
        </section>
      </div>

      <section className="paldefender-audit-card">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">COMMAND AUDIT</p>
            <h3>命令审计</h3>
          </div>
          <span className="count-pill">{audit.length}</span>
        </div>
        <div className="paldefender-audit-table-wrap">
          <table className="paldefender-audit-table">
            <thead>
              <tr>
                <th>时间</th>
                <th>命令</th>
                <th>状态</th>
                <th>操作</th>
                <th>原因</th>
                <th>HTTP</th>
              </tr>
            </thead>
            <tbody>
              {audit.map((event) => (
                <tr key={event.eventId}>
                  <td>{formatDate(event.at)}</td>
                  <td><code>{shortId(event.commandId)}</code></td>
                  <td><CommandState state={event.state} /></td>
                  <td><code>{event.upstreamPath}</code></td>
                  <td title={event.reason}>{event.reason}</td>
                  <td>{event.httpStatus ?? "--"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {!loading && audit.length === 0 ? (
            <div className="paldefender-empty">尚无 PalDefender 命令审计事件。</div>
          ) : null}
        </div>
      </section>
    </section>
  );
}

function SystemMetric({
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
    <article className={`metric-card paldefender-metric ${tone}`}>
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{detail}</small>
    </article>
  );
}

function CommandResult({ command }: { command?: PalDefenderCommand }) {
  if (!command) {
    return (
      <div className="paldefender-command-result empty">
        <strong>尚未提交命令</strong>
        <small>命令提交后会显示队列状态、命令号和上游结果。</small>
      </div>
    );
  }
  return (
    <div className="paldefender-command-result">
      <div>
        <span>命令状态</span>
        <CommandState state={command.state} />
      </div>
      <div>
        <span>命令 ID</span>
        <code title={command.commandId}>{command.commandId}</code>
      </div>
      <div>
        <span>上游接口</span>
        <code>{command.result?.upstreamPath ?? "等待派发"}</code>
      </div>
      <div>
        <span>完成时间</span>
        <strong>{command.completedAt ? formatDate(command.completedAt) : "处理中"}</strong>
      </div>
      {command.error ? (
        <div className="paldefender-command-error">
          <strong>{command.error.code}</strong>
          <span>{command.error.message}</span>
        </div>
      ) : null}
    </div>
  );
}

function CommandState({ state }: { state: PalDefenderCommandState }) {
  const labels: Record<PalDefenderCommandState, string> = {
    accepted: "已入队",
    dispatched: "已派发",
    succeeded: "已完成",
    failed: "失败",
    uncertain: "待确认"
  };
  return <span className={`paldefender-command-state state-${state}`}>{labels[state]}</span>;
}

function isPending(state: PalDefenderCommandState) {
  return state === "accepted" || state === "dispatched";
}

function createIdempotencyKey(action: CommandAction) {
  const suffix = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  return `console-${action.toLocaleLowerCase()}-${suffix}`;
}

function abortableDelay(milliseconds: number, signal: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    if (signal.aborted) {
      reject(new DOMException("Aborted", "AbortError"));
      return;
    }
    const onAbort = () => {
      globalThis.clearTimeout(timer);
      reject(new DOMException("Aborted", "AbortError"));
    };
    const timer = globalThis.setTimeout(() => {
      signal.removeEventListener("abort", onAbort);
      resolve();
    }, milliseconds);
    signal.addEventListener("abort", onAbort, { once: true });
  });
}

function shortId(value: string) {
  return value.length > 12 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value;
}

function formatDate(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString("zh-CN", { hour12: false });
}
