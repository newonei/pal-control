import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import {
  createSaveBackup,
  flushWorldSave,
  getSaveBackup,
  getSaveCommand,
  getSaveStatus,
  listSaveBackups,
  verifySaveBackup,
  type BackupKind,
  type SaveBackup,
  type SaveCommand,
  type SaveStatus
} from "../../lib/api/client";

type SaveManagementProps = {
  serverId: string;
  onlinePlayers: number;
};

type OperationMode = "flush" | "backup";

const TERMINAL_COMMAND_STATES = new Set([
  "completed",
  "succeeded",
  "failed",
  "uncertain",
  "cancelled"
]);

const FULL_BACKUP_STAGES = [
  ["queued", "进入命令队列"],
  ["saving-world", "通知游戏保存"],
  ["waiting-snapshot", "等待稳定快照"],
  ["copying", "复制独立备份"],
  ["verifying", "校验文件清单"],
  ["completed", "备份完成"]
] as const;

const FLUSH_STAGES = [
  ["queued", "进入命令队列"],
  ["saving-world", "保存世界"],
  ["completed", "保存完成"]
] as const;

const VERIFY_STAGES = [
  ["queued", "进入命令队列"],
  ["verifying", "重新计算校验值"],
  ["completed", "校验完成"]
] as const;

export function SaveManagement({ serverId, onlinePlayers }: SaveManagementProps) {
  const storageKey = `pal-control.pending-save-command.${serverId}`;
  const [status, setStatus] = useState<SaveStatus | null>(null);
  const [managedBackups, setManagedBackups] = useState<SaveBackup[]>([]);
  const [nativeBackups, setNativeBackups] = useState<SaveBackup[]>([]);
  const [activeTab, setActiveTab] = useState<BackupKind>("managed");
  const [selectedBackupId, setSelectedBackupId] = useState<string | null>(null);
  const [selectedBackup, setSelectedBackup] = useState<SaveBackup | null>(null);
  const [operationMode, setOperationMode] = useState<OperationMode>("backup");
  const [label, setLabel] = useState("");
  const [reason, setReason] = useState("");
  const [verifyReason, setVerifyReason] = useState("");
  const [command, setCommand] = useState<SaveCommand | null>(null);
  const [commandLocator, setCommandLocator] = useState<string | null>(() => readPendingCommand(storageKey));
  const [commandConnectionError, setCommandConnectionError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    const results = await Promise.allSettled([
      getSaveStatus(serverId, signal),
      listSaveBackups(serverId, "managed", signal),
      listSaveBackups(serverId, "native", signal)
    ]);
    if (signal?.aborted) {
      return;
    }
    const errors: string[] = [];
    const [statusResult, managedResult, nativeResult] = results;
    if (statusResult.status === "fulfilled") {
      setStatus(statusResult.value);
    } else {
      errors.push(errorMessage(statusResult.reason, "存档状态读取失败"));
    }
    if (managedResult.status === "fulfilled") {
      setManagedBackups(sortBackups(managedResult.value));
    } else {
      errors.push(errorMessage(managedResult.reason, "Pal Control 备份列表读取失败"));
    }
    if (nativeResult.status === "fulfilled") {
      setNativeBackups(sortBackups(nativeResult.value));
    } else {
      errors.push(errorMessage(nativeResult.reason, "游戏自动备份列表读取失败"));
    }
    setPageError(errors.length > 0 ? errors.join("；") : null);
    setLoading(false);
  }, [serverId]);

  useEffect(() => {
    const controller = new AbortController();
    let disposed = false;
    let timer: number | undefined;
    const update = async () => {
      await refresh(controller.signal);
      if (!disposed) {
        timer = globalThis.setTimeout(() => void update(), 15_000);
      }
    };
    void update();
    return () => {
      disposed = true;
      controller.abort();
      if (timer !== undefined) {
        globalThis.clearTimeout(timer);
      }
    };
  }, [refresh]);

  useEffect(() => {
    if (!commandLocator) {
      return;
    }
    const controller = new AbortController();
    let disposed = false;
    let timer: number | undefined;
    const poll = async () => {
      try {
        const nextCommand = await getSaveCommand(commandLocator, controller.signal);
        if (disposed) {
          return;
        }
        setCommand(nextCommand);
        setCommandConnectionError(null);
        persistPendingCommand(storageKey, nextCommand);
        if (isTerminal(nextCommand)) {
          clearPendingCommand(storageKey);
          setCommandLocator(null);
          setSubmitting(false);
          void refresh();
          return;
        }
        timer = globalThis.setTimeout(() => void poll(), 1_000);
      } catch (error) {
        if (disposed || controller.signal.aborted) {
          return;
        }
        setCommandConnectionError(errorMessage(error, "命令状态暂时无法读取"));
        timer = globalThis.setTimeout(() => void poll(), 2_500);
      }
    };
    void poll();
    return () => {
      disposed = true;
      controller.abort();
      if (timer !== undefined) {
        globalThis.clearTimeout(timer);
      }
    };
  }, [commandLocator, refresh, storageKey]);

  useEffect(() => {
    if (!selectedBackupId) {
      setSelectedBackup(null);
      return;
    }
    const controller = new AbortController();
    let disposed = false;
    void getSaveBackup(serverId, selectedBackupId, controller.signal)
      .then((backup) => {
        if (!disposed) {
          setSelectedBackup(backup);
        }
      })
      .catch((error) => {
        if (!disposed && !controller.signal.aborted) {
          const fallback = [...managedBackups, ...nativeBackups]
            .find((item) => item.backupId === selectedBackupId) ?? null;
          setSelectedBackup(fallback);
          setPageError(errorMessage(error, "备份详情读取失败"));
        }
      });
    return () => {
      disposed = true;
      controller.abort();
    };
  }, [managedBackups, nativeBackups, selectedBackupId, serverId]);

  const validationPassed = status
    ? Object.values(status.validation).every(Boolean)
    : false;
  const diskUsedRatio = status?.disk.totalBytes
    ? 1 - status.disk.availableBytes / status.disk.totalBytes
    : null;
  const visibleBackups = activeTab === "managed" ? managedBackups : nativeBackups;
  const selectedFromList = useMemo(
    () => visibleBackups.find((backup) => backup.backupId === selectedBackupId) ?? null,
    [selectedBackupId, visibleBackups]
  );
  const detail = selectedBackup?.backupId === selectedBackupId ? selectedBackup : selectedFromList;
  const commandBusy = submitting || Boolean(commandLocator && (!command || !isTerminal(command)));

  function beginCommand(nextCommand: SaveCommand) {
    setCommand(nextCommand);
    setCommandConnectionError(null);
    persistPendingCommand(storageKey, nextCommand);
    setCommandLocator(nextCommand.statusUrl || nextCommand.commandId);
  }

  async function submitOperation(event: FormEvent) {
    event.preventDefault();
    if (commandBusy) {
      return;
    }
    const cleanReason = reason.trim();
    const cleanLabel = label.trim();
    if (cleanReason.length < 3) {
      setFormError("操作原因至少需要 3 个字符，并会进入审计记录。");
      return;
    }
    if (operationMode === "backup" && !cleanLabel) {
      setFormError("创建备份必须填写名称。");
      return;
    }
    setFormError(null);
    setSubmitting(true);
    try {
      const idempotencyKey = createIdempotencyKey(operationMode);
      const nextCommand = operationMode === "backup"
        ? await createSaveBackup(serverId, { label: cleanLabel, reason: cleanReason }, idempotencyKey)
        : await flushWorldSave(serverId, cleanReason, idempotencyKey);
      beginCommand(nextCommand);
      setReason("");
      if (operationMode === "backup") {
        setLabel("");
      }
    } catch (error) {
      setFormError(errorMessage(error, "命令提交失败"));
      setSubmitting(false);
    }
  }

  async function reverifyBackup() {
    if (!detail || detail.kind !== "managed" || commandBusy) {
      return;
    }
    const cleanReason = verifyReason.trim();
    if (cleanReason.length < 3) {
      setPageError("重新校验原因至少需要 3 个字符。");
      return;
    }
    setSubmitting(true);
    setPageError(null);
    try {
      const nextCommand = await verifySaveBackup(
        serverId,
        detail.backupId,
        cleanReason,
        createIdempotencyKey("verify")
      );
      beginCommand(nextCommand);
      setVerifyReason("");
    } catch (error) {
      setPageError(errorMessage(error, "校验命令提交失败"));
      setSubmitting(false);
    }
  }

  function switchTab(kind: BackupKind) {
    setActiveTab(kind);
    setSelectedBackupId(null);
    setSelectedBackup(null);
    setVerifyReason("");
  }

  return (
    <section className="save-management-page">
      <header className="save-management-heading">
        <div>
          <p className="eyebrow">SAVE CENTER / GUARDED OPERATIONS</p>
          <h2>存档中心</h2>
          <p>保存命令、独立备份与完整性校验均进入幂等队列，并留下审计记录。</p>
        </div>
        <div className="save-heading-state">
          <span className={status?.ready ? "save-ready-badge ready" : "save-ready-badge"}>
            <i />{status?.ready ? "存档链路就绪" : loading ? "正在检查" : "存档链路不可用"}
          </span>
          <button className="ghost-button" disabled={loading} onClick={() => void refresh()} type="button">
            刷新状态
          </button>
        </div>
      </header>

      {pageError ? <div className="save-alert error" role="alert">{pageError}</div> : null}
      {status?.error ? <div className="save-alert warning">{status.error.message}</div> : null}

      <div className="save-status-grid">
        <SaveMetric label="当前世界" value={status?.worldName ?? "--"} detail={shortGuid(status?.worldGuid)} />
        <SaveMetric label="游戏版本" value={status?.gameVersion ?? "--"} detail="存档版本标记" />
        <SaveMetric
          tone={status?.ready ? "good" : "warning"}
          label="服务运行"
          value={status?.ready ? "可保存" : "未就绪"}
          detail={validationPassed ? "身份核验通过" : "等待身份核验"}
        />
        <SaveMetric
          label="在线人数"
          value={String(status?.onlinePlayerCount ?? onlinePlayers)}
          detail={status?.onlinePlayerCount === null || status?.onlinePlayerCount === undefined ? "当前 REST 快照" : "存档状态快照"}
        />
        <SaveMetric
          label="存档大小"
          value={status ? formatBytes(status.save.totalBytes) : "--"}
          detail={status ? `${status.save.fileCount} 个文件 · ${status.save.playerFileCount} 个玩家档` : "等待扫描"}
        />
        <SaveMetric
          label="最后保存"
          value={formatRelativeDate(status?.save.lastModifiedAt)}
          detail={formatDate(status?.save.lastModifiedAt)}
        />
        <SaveMetric
          tone={diskUsedRatio !== null && diskUsedRatio > 0.9 ? "warning" : "good"}
          label="磁盘空间"
          value={status ? formatBytes(status.disk.availableBytes) : "--"}
          detail={status ? `可用 / ${formatBytes(status.disk.totalBytes)}` : "等待读取"}
        />
      </div>

      <div className="save-summary-row">
        <div>
          <span>Pal Control 备份</span>
          <strong>{status?.managedBackups.count ?? managedBackups.length}</strong>
          <small>
            {formatBytes(status?.managedBackups.totalBytes ?? sumBytes(managedBackups))} · 已校验 {status?.managedBackups.verifiedCount ?? verifiedCount(managedBackups)}
          </small>
        </div>
        <div>
          <span>游戏自动备份</span>
          <strong>{status?.nativeBackups.count ?? nativeBackups.length}</strong>
          <small>{formatBytes(status?.nativeBackups.totalBytes ?? sumBytes(nativeBackups))} · 由游戏轮转维护</small>
        </div>
        <div className="save-validation-summary">
          <span>目标核验</span>
          <ValidationItem label="进程路径" passed={status?.validation.processPathMatched} />
          <ValidationItem label="服务器名称" passed={status?.validation.serverNameMatched} />
          <ValidationItem label="世界 GUID" passed={status?.validation.worldGuidMatched} />
        </div>
      </div>

      <div className="save-workbench">
        <form className="save-operation-card" onSubmit={submitOperation}>
          <div className="save-card-heading">
            <div>
              <p className="eyebrow">CREATE COMMAND</p>
              <h3>创建存档命令</h3>
            </div>
            <span>幂等提交</span>
          </div>

          <div className="save-operation-choice" role="tablist" aria-label="存档命令类型">
            <button
              className={operationMode === "flush" ? "active" : ""}
              onClick={() => setOperationMode("flush")}
              type="button"
            >仅保存世界</button>
            <button
              className={operationMode === "backup" ? "active" : ""}
              onClick={() => setOperationMode("backup")}
              type="button"
            >保存并创建备份</button>
          </div>

          {operationMode === "backup" ? (
            <label className="save-field">
              <span>备份名称 <em>必填</em></span>
              <input
                maxLength={80}
                onChange={(event) => setLabel(event.target.value)}
                placeholder="例如：更新模组前"
                value={label}
              />
            </label>
          ) : null}
          <label className="save-field">
            <span>操作原因 <em>必填 · 写入审计</em></span>
            <textarea
              maxLength={240}
              onChange={(event) => setReason(event.target.value)}
              placeholder={operationMode === "backup" ? "说明为什么需要这份备份" : "说明为什么立即保存世界"}
              rows={3}
              value={reason}
            />
          </label>

          <div className="save-operation-note">
            {operationMode === "backup"
              ? "先请求游戏保存，等待快照稳定后复制，并生成 SHA-256 文件清单。"
              : "只请求游戏立即落盘，不创建 Pal Control 独立备份。"}
          </div>
          {formError ? <p className="save-form-error" role="alert">{formError}</p> : null}
          <button
            className="primary-button save-submit"
            disabled={commandBusy || !status?.ready}
            type="submit"
          >
            {commandBusy ? "命令执行中…" : operationMode === "backup" ? "保存并创建备份" : "仅保存世界"}
          </button>
        </form>

        <CommandProgress command={command} reconnectError={commandConnectionError} />
      </div>

      <section className="save-backup-card">
        <header className="save-backup-heading">
          <div>
            <p className="eyebrow">BACKUP CATALOG</p>
            <h3>备份目录</h3>
          </div>
          <div className="save-backup-tabs" role="tablist">
            <button className={activeTab === "managed" ? "active" : ""} onClick={() => switchTab("managed")} type="button">
              Pal Control 备份 <span>{managedBackups.length}</span>
            </button>
            <button className={activeTab === "native" ? "active" : ""} onClick={() => switchTab("native")} type="button">
              游戏自动备份 <span>{nativeBackups.length}</span>
            </button>
          </div>
        </header>

        {activeTab === "native" ? (
          <div className="native-backup-notice">
            <span>只读</span>
            游戏自动备份由 Palworld 按自身策略轮转；这里仅展示已发现的快照和基本元数据。
          </div>
        ) : null}

        <div className="save-backup-layout">
          <div className="save-backup-list">
            {visibleBackups.length === 0 ? (
              <div className="save-backup-empty">
                <strong>{loading ? "正在扫描备份目录" : "还没有可显示的备份"}</strong>
                <small>{activeTab === "managed" ? "创建成功的独立备份会显示在这里" : "等待游戏产生自动备份快照"}</small>
              </div>
            ) : visibleBackups.map((backup) => (
              <button
                className={selectedBackupId === backup.backupId ? "selected" : ""}
                key={backup.backupId}
                onClick={() => setSelectedBackupId(backup.backupId)}
                type="button"
              >
                <span className={`backup-kind-icon ${backup.kind}`}>{backup.kind === "managed" ? "PC" : "PW"}</span>
                <span className="backup-list-copy">
                  <strong>{backup.label || (backup.kind === "native" ? "游戏自动快照" : "未命名备份")}</strong>
                  <small>{formatDate(backup.createdAt)} · {formatBytes(backup.totalBytes)}</small>
                </span>
                <IntegrityBadge value={backup.integrity} />
              </button>
            ))}
          </div>

          <BackupDetail
            backup={detail}
            canVerify={!commandBusy}
            onVerify={() => void reverifyBackup()}
            verifyReason={verifyReason}
            onVerifyReasonChange={setVerifyReason}
          />
        </div>
      </section>
    </section>
  );
}

function SaveMetric({
  label,
  value,
  detail,
  tone = "neutral"
}: {
  label: string;
  value: string;
  detail: string;
  tone?: "neutral" | "good" | "warning";
}) {
  return (
    <div className={`save-metric ${tone}`}>
      <span>{label}</span>
      <strong title={value}>{value}</strong>
      <small title={detail}>{detail}</small>
    </div>
  );
}

function ValidationItem({ label, passed }: { label: string; passed?: boolean }) {
  return <small className={passed ? "passed" : "pending"}><i>{passed ? "✓" : "·"}</i>{label}</small>;
}

function CommandProgress({
  command,
  reconnectError
}: {
  command: SaveCommand | null;
  reconnectError: string | null;
}) {
  const stages = commandStages(command);
  const stage = normalizeStage(command?.stage ?? "queued");
  const rawIndex = stages.findIndex(([key]) => key === stage);
  const completed = command ? isTerminalSuccess(command) : false;
  const currentIndex = completed ? stages.length - 1 : Math.max(0, rawIndex);
  const failed = command ? isTerminal(command) && !isTerminalSuccess(command) : false;

  return (
    <section className="save-command-card">
      <div className="save-card-heading">
        <div>
          <p className="eyebrow">COMMAND PROGRESS</p>
          <h3>命令状态</h3>
        </div>
        {command ? <CommandStateBadge command={command} /> : <span>等待提交</span>}
      </div>

      {!command ? (
        <div className="save-command-empty">
          <span>◇</span>
          <strong>尚未执行存档命令</strong>
          <small>命令提交后，会在这里显示每个阶段和最终结果。</small>
        </div>
      ) : (
        <>
          <ol className="save-command-stages">
            {stages.map(([key, label], index) => {
              const state = index < currentIndex || (completed && index === currentIndex)
                ? "done"
                : index === currentIndex
                  ? failed ? "failed" : "active"
                  : "pending";
              return (
                <li className={state} key={key}>
                  <i>{state === "done" ? "✓" : state === "failed" ? "!" : index + 1}</i>
                  <span><strong>{label}</strong><small>{stageHint(key)}</small></span>
                </li>
              );
            })}
          </ol>
          <div className="save-command-meta">
            <span>命令 <code>{shortId(command.commandId)}</code></span>
            <span>提交 {formatDate(command.createdAt)}</span>
          </div>
          {reconnectError ? (
            <div className="save-command-reconnect"><i />状态连接中断，正在自动重连：{reconnectError}</div>
          ) : null}
          {command.error ? (
            <div className="save-alert error"><strong>{command.error.code}</strong>{command.error.message}</div>
          ) : null}
          {command.backupId && isTerminalSuccess(command) ? (
            <div className="save-alert success">备份已登记：<code>{command.backupId}</code></div>
          ) : null}
        </>
      )}
    </section>
  );
}

function CommandStateBadge({ command }: { command: SaveCommand }) {
  const state = command.state.toLocaleLowerCase();
  const label = isTerminalSuccess(command)
    ? "已完成"
    : state === "failed"
      ? "失败"
      : state === "uncertain"
        ? "结果待确认"
        : state === "cancelled"
          ? "已取消"
        : "执行中";
  const tone = isTerminalSuccess(command) ? "success" : isTerminal(command) ? "error" : "running";
  return <span className={`save-command-state ${tone}`}><i />{label}</span>;
}

function IntegrityBadge({ value }: { value: string }) {
  const normalized = value.toLocaleLowerCase();
  const tone = ["verified", "valid", "passed", "ok"].includes(normalized) || normalized.includes("verified")
    ? "verified"
    : ["failed", "invalid", "corrupt"].includes(normalized)
      ? "failed"
      : "unknown";
  const label = tone === "verified" ? "已校验" : tone === "failed" ? "校验失败" : "待校验";
  return <span className={`backup-integrity ${tone}`}><i />{label}</span>;
}

function BackupDetail({
  backup,
  canVerify,
  verifyReason,
  onVerifyReasonChange,
  onVerify
}: {
  backup: SaveBackup | null;
  canVerify: boolean;
  verifyReason: string;
  onVerifyReasonChange: (value: string) => void;
  onVerify: () => void;
}) {
  if (!backup) {
    return (
      <aside className="save-backup-detail empty">
        <span>⌁</span>
        <strong>选择一份备份查看详情</strong>
        <small>可查看版本、大小、完整性和文件清单摘要。</small>
      </aside>
    );
  }
  return (
    <aside className="save-backup-detail">
      <div className="backup-detail-title">
        <div>
          <p className="eyebrow">BACKUP DETAIL</p>
          <h3>{backup.label || (backup.kind === "native" ? "游戏自动快照" : "未命名备份")}</h3>
        </div>
        <IntegrityBadge value={backup.integrity} />
      </div>
      <dl>
        <div><dt>创建时间</dt><dd>{formatDate(backup.createdAt)}</dd></div>
        <div><dt>游戏版本</dt><dd>{backup.gameVersion ?? "--"}</dd></div>
        <div><dt>存档大小</dt><dd>{formatBytes(backup.totalBytes)}</dd></div>
        <div><dt>文件数量</dt><dd>{backup.fileCount}</dd></div>
        <div><dt>一致性</dt><dd>{consistencyLabel(backup.consistency)}</dd></div>
        <div><dt>操作人</dt><dd>{backup.actor ?? (backup.kind === "native" ? "Palworld" : "--")}</dd></div>
      </dl>
      <div className="backup-detail-reason">
        <span>创建原因</span>
        <p>{backup.reason ?? (backup.kind === "native" ? "游戏自动轮转产生" : "未记录")}</p>
      </div>
      <div className="backup-manifest-summary">
        <span>Manifest SHA-256 摘要</span>
        <code title={backup.manifestSha256 ?? ""}>{backup.manifestSha256 ?? "游戏自动备份不提供独立清单"}</code>
      </div>
      <div className="backup-world-guid">
        <span>世界 GUID</span>
        <code>{backup.worldGuid ?? "--"}</code>
      </div>
      {backup.kind === "managed" ? (
        <div className="backup-verify-box">
          <label>
            <span>重新校验原因 <em>写入审计</em></span>
            <input
              maxLength={240}
              onChange={(event) => onVerifyReasonChange(event.target.value)}
              placeholder="例如：定期完整性检查"
              value={verifyReason}
            />
          </label>
          <button className="ghost-button" disabled={!canVerify || verifyReason.trim().length < 3} onClick={onVerify} type="button">
            重新校验
          </button>
        </div>
      ) : (
        <div className="native-detail-lock"><i />该目录保持只读展示</div>
      )}
    </aside>
  );
}

function commandStages(command: SaveCommand | null) {
  const type = command?.type.toLocaleLowerCase() ?? "backup";
  if (type.includes("verify")) {
    return VERIFY_STAGES;
  }
  if (type.includes("flush") || type.includes("save-world") || type.includes("save-only") || type === "save") {
    return FLUSH_STAGES;
  }
  return FULL_BACKUP_STAGES;
}

function normalizeStage(stage: string): string {
  const value = stage.toLocaleLowerCase().replaceAll("_", "-");
  if (["accepted", "pending"].includes(value)) return "queued";
  if (["saving", "save", "flushing", "flush"].includes(value)) return "saving-world";
  if (["waiting", "stabilizing", "snapshot"].includes(value)) return "waiting-snapshot";
  if (["copy", "publishing"].includes(value)) return "copying";
  if (["verify", "hashing"].includes(value)) return "verifying";
  if (["succeeded", "success", "done"].includes(value)) return "completed";
  return value;
}

function stageHint(stage: string): string {
  const hints: Record<string, string> = {
    queued: "等待同一服务器上的前序命令",
    "saving-world": "调用官方 REST 保存接口",
    "waiting-snapshot": "确认世界与玩家文件已稳定",
    copying: "写入临时目录后原子发布",
    verifying: "逐文件生成并复核 SHA-256",
    completed: "结果和审计记录已经发布"
  };
  return hints[stage] ?? "处理命令";
}

function isTerminal(command: SaveCommand): boolean {
  return TERMINAL_COMMAND_STATES.has(command.state.toLocaleLowerCase()) ||
    normalizeStage(command.stage) === "completed";
}

function isTerminalSuccess(command: SaveCommand): boolean {
  const state = command.state.toLocaleLowerCase();
  if (["failed", "uncertain", "cancelled"].includes(state)) {
    return false;
  }
  return ["completed", "succeeded"].includes(state) ||
    (normalizeStage(command.stage) === "completed" && !command.error);
}

function createIdempotencyKey(scope: string): string {
  const id = typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  return `save-${scope}-${id}`;
}

function readPendingCommand(storageKey: string): string | null {
  try {
    const raw = globalThis.localStorage?.getItem(storageKey);
    if (!raw) return null;
    const value = JSON.parse(raw) as { statusUrl?: unknown; commandId?: unknown };
    if (typeof value.statusUrl === "string" && value.statusUrl) return value.statusUrl;
    if (typeof value.commandId === "string" && value.commandId) return value.commandId;
  } catch {
    // A stale or malformed browser entry must not block the save center.
  }
  return null;
}

function persistPendingCommand(storageKey: string, command: SaveCommand) {
  if (isTerminal(command)) {
    clearPendingCommand(storageKey);
    return;
  }
  try {
    globalThis.localStorage?.setItem(storageKey, JSON.stringify({
      commandId: command.commandId,
      statusUrl: command.statusUrl
    }));
  } catch {
    // The status remains visible even when browser storage is unavailable.
  }
}

function clearPendingCommand(storageKey: string) {
  try {
    globalThis.localStorage?.removeItem(storageKey);
  } catch {
    // Ignore storage access errors (for example, private browsing policies).
  }
}

function sortBackups(backups: SaveBackup[]): SaveBackup[] {
  return [...backups].sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
}

function sumBytes(backups: SaveBackup[]): number {
  return backups.reduce((sum, backup) => sum + backup.totalBytes, 0);
}

function verifiedCount(backups: SaveBackup[]): number {
  return backups.filter((backup) => {
    const integrity = backup.integrity.toLocaleLowerCase();
    return ["verified", "valid", "passed", "ok"].includes(integrity) || integrity.includes("verified");
  }).length;
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return "--";
  if (bytes < 1_024) return `${Math.round(bytes)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1_024;
  let unit = units[0];
  for (let index = 1; index < units.length && value >= 1_024; index += 1) {
    value /= 1_024;
    unit = units[index];
  }
  return `${value >= 10 ? value.toFixed(1) : value.toFixed(2)} ${unit}`;
}

function formatDate(value?: string | null): string {
  if (!value) return "--";
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "--";
  return date.toLocaleString("zh-CN", {
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false
  });
}

function formatRelativeDate(value?: string | null): string {
  if (!value) return "--";
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return "--";
  const seconds = Math.max(0, Math.floor((Date.now() - timestamp) / 1_000));
  if (seconds < 10) return "刚刚";
  if (seconds < 60) return `${seconds} 秒前`;
  if (seconds < 3_600) return `${Math.floor(seconds / 60)} 分钟前`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3_600)} 小时前`;
  return `${Math.floor(seconds / 86_400)} 天前`;
}

function shortGuid(value?: string | null): string {
  if (!value) return "等待识别世界 GUID";
  return value.length > 18 ? `${value.slice(0, 8)}…${value.slice(-8)}` : value;
}

function shortId(value: string): string {
  return value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value;
}

function consistencyLabel(value: string): string {
  const normalized = value.toLocaleLowerCase();
  if (["native", "game-native"].includes(normalized)) return "游戏原生快照";
  if (["server-generated", "online-verified"].includes(normalized)) return "在线校验快照";
  if (["stable", "consistent", "verified"].includes(normalized)) return "稳定快照";
  if (["uncertain", "unknown"].includes(normalized)) return "待确认";
  return value || "--";
}

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.name !== "AbortError" && error.message) return error.message;
  return fallback;
}
