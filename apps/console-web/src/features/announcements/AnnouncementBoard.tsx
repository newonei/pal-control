import { useEffect, useMemo, useRef, useState } from "react";
import {
  createAnnouncement,
  getCommandStatus,
  publishAnnouncement,
  type AnnouncementChannel,
  type ChannelDeliveryResult,
  type CommandStatus
} from "../../lib/api/client";

type Audience = "global" | "online" | "guild" | "players";
type ChannelOptionValue = "web" | AnnouncementChannel;

const defaultTitle = "今晚 22:00 服务器维护";
const defaultBody = "预计维护 20 分钟。维护前 5 分钟请返回据点，系统会自动保存世界。";
const publishAttemptStorageKey = "pal-control.announcement-publish-attempt.v1";

const audienceOptions: Array<{ value: Audience; label: string; hint: string; supported: boolean }> = [
  { value: "global", label: "全服", hint: "官方 REST", supported: true },
  { value: "online", label: "当前在线", hint: "暂未适配", supported: false },
  { value: "guild", label: "指定公会", hint: "暂未适配", supported: false },
  { value: "players", label: "指定玩家", hint: "暂未适配", supported: false }
];

const announcementChannelOrder: AnnouncementChannel[] = ["chat", "top-banner", "client-overlay"];
const announcementChannelLabels: Record<AnnouncementChannel, string> = {
  chat: "游戏聊天",
  "top-banner": "顶部横幅",
  "client-overlay": "客户端浮层"
};
const announcementChannelBadges: Record<AnnouncementChannel, string> = {
  chat: "GAME CHAT",
  "top-banner": "TOP BANNER",
  "client-overlay": "CLIENT OVERLAY"
};

type PublishAttempt = {
  fingerprint: string;
  createKey: string;
  publishKey: string;
  announcementId?: string;
  statusUrl?: string;
  result?: CommandStatus["result"];
  draft: {
    title: string;
    body: string;
    schedule: string;
    channels: AnnouncementChannel[];
  };
};

type PublishPhase = "idle" | "submitting" | CommandStatus["state"];

export function AnnouncementBoard({
  restConnected,
  bridgeConnected,
  publishChatAnnouncements,
  publishClientOverlay,
  publishTopBanner,
  commandQueueReady,
  auditReady
}: {
  restConnected: boolean;
  bridgeConnected: boolean;
  publishChatAnnouncements: boolean;
  publishClientOverlay: boolean;
  publishTopBanner: boolean;
  commandQueueReady: boolean;
  auditReady: boolean;
}) {
  const [restoredAttempt] = useState<PublishAttempt | undefined>(() => loadPublishAttempt());
  const attemptRef = useRef<PublishAttempt | undefined>(restoredAttempt);
  const inFlightRef = useRef(false);
  const [title, setTitle] = useState(restoredAttempt?.draft.title ?? defaultTitle);
  const [body, setBody] = useState(restoredAttempt?.draft.body ?? defaultBody);
  const [audience, setAudience] = useState<Audience>("global");
  const [channels, setChannels] = useState<AnnouncementChannel[]>(
    restoredAttempt?.draft.channels ?? defaultChannels(
      publishChatAnnouncements,
      publishTopBanner,
      publishClientOverlay
    )
  );
  const [schedule, setSchedule] = useState(restoredAttempt?.draft.schedule ?? "");
  const [savedAt, setSavedAt] = useState<string>();
  const [publishPhase, setPublishPhase] = useState<PublishPhase>(
    restoredAttempt?.statusUrl ? "accepted" : "idle"
  );
  const [publishNotice, setPublishNotice] = useState<string | undefined>(
    restoredAttempt?.statusUrl ? "正在恢复上一次发布命令…" : undefined
  );
  const [publishError, setPublishError] = useState<string>();
  const [channelResults, setChannelResults] = useState<ChannelDeliveryResult[]>(
    restoredAttempt?.result?.channels ?? []
  );

  const selectedAudience = useMemo(
    () => audienceOptions.find((option) => option.value === audience)?.label ?? "全服",
    [audience]
  );

  const sharedPublishReady = commandQueueReady && auditReady;
  const channelAvailability: Record<AnnouncementChannel, boolean> = {
    chat: publishChatAnnouncements,
    "top-banner": publishTopBanner,
    "client-overlay": publishClientOverlay
  };
  const channelOptions: Array<{
    value: ChannelOptionValue;
    label: string;
    available: boolean;
    hint: string;
  }> = [
    { value: "web", label: "网页公告栏", available: false, hint: "尚未接入" },
    {
      value: "chat",
      label: announcementChannelLabels.chat,
      available: publishChatAnnouncements,
      hint: getChatChannelHint(publishChatAnnouncements, restConnected, sharedPublishReady)
    },
    {
      value: "top-banner",
      label: announcementChannelLabels["top-banner"],
      available: publishTopBanner,
      hint: getTopBannerChannelHint(publishTopBanner, bridgeConnected, sharedPublishReady)
    },
    {
      value: "client-overlay",
      label: announcementChannelLabels["client-overlay"],
      available: publishClientOverlay,
      hint: getOverlayChannelHint(publishClientOverlay, bridgeConnected, sharedPublishReady)
    }
  ];

  function toggleChannel(channel: AnnouncementChannel) {
    markEdited();
    setChannels((current) =>
      normalizeChannels(current.includes(channel)
        ? current.filter((item) => item !== channel)
        : [...current, channel])
    );
  }

  const selectedChannelsReady = channels.length > 0 &&
    channels.every((channel) => channelAvailability[channel]);
  const deliveryReady = sharedPublishReady && selectedChannelsReady;
  const hasIrreversibleChannelResult = channelResults.some((result) =>
    result.state === "succeeded" || result.state === "uncertain"
  );
  const editorLocked = publishPhase === "submitting" ||
    publishPhase === "accepted" ||
    publishPhase === "dispatched" ||
    publishPhase === "succeeded" ||
    publishPhase === "uncertain" ||
    hasIrreversibleChannelResult;
  const canPublish =
    deliveryReady &&
    title.trim().length > 0 &&
    body.trim().length > 0 &&
    audience === "global" &&
    !editorLocked;

  useEffect(() => {
    const attempt = attemptRef.current;
    if (!attempt?.statusUrl) {
      return;
    }
    const controller = new AbortController();
    void (async () => {
      try {
        let command = await getCommandStatus(attempt.statusUrl!, controller.signal);
        const scheduledFor = attempt.draft.schedule
          ? new Date(attempt.draft.schedule).getTime()
          : 0;
        if (!scheduledFor || scheduledFor <= Date.now()) {
          for (let poll = 0; poll < 30 && isPending(command.state); poll += 1) {
            await delay(400);
            command = await getCommandStatus(command.statusUrl, controller.signal);
          }
        }
        applyCommandResult(command, attempt);
      } catch (error) {
        if (!controller.signal.aborted) {
          setPublishPhase("idle");
          setPublishNotice(undefined);
          setPublishError(error instanceof Error ? error.message : "上一次发布状态恢复失败。");
        }
      }
    })();
    return () => controller.abort();
  }, []);

  function markEdited() {
    if (editorLocked) {
      return;
    }
    setPublishPhase("idle");
    setPublishNotice(undefined);
    setPublishError(undefined);
    setSavedAt(undefined);
    setChannelResults([]);
  }

  function applyCommandResult(command: CommandStatus, attempt: PublishAttempt) {
    attempt.statusUrl = command.statusUrl;
    attempt.result = command.result;
    savePublishAttempt(attempt);
    setPublishPhase(command.state);
    const deliveries = command.result?.channels ?? [];
    setChannelResults(deliveries);
    const succeeded = deliveries.filter((delivery) => delivery.state === "succeeded");
    const failed = deliveries.filter((delivery) => delivery.state === "failed");
    const uncertain = deliveries.filter((delivery) => delivery.state === "uncertain");

    if (uncertain.length > 0 || failed.length > 0) {
      setPublishNotice(undefined);
      setPublishError(buildChannelAttentionMessage(succeeded, failed, uncertain));
      if (!hasDeliveredOrUncertain(deliveries) && command.state === "failed") {
        clearPublishAttempt();
        attemptRef.current = undefined;
      }
    } else if (command.state === "succeeded") {
      setPublishError(undefined);
      setPublishNotice(
        `${formatChannelList(attempt.draft.channels)}已完成派发，命令 ${shortId(command.commandId)} 已完成并写入审计。`
      );
    } else if (command.state === "uncertain") {
      setPublishNotice(undefined);
      setPublishError(
        `${command.error?.message ?? "发送结果无法确认。"} 系统不会自动重发，请先在游戏内确认。`
      );
    } else if (command.state === "failed" || command.state === "cancelled") {
      setPublishNotice(undefined);
      setPublishError(command.error?.message ?? "公告发布失败。");
      if (!hasDeliveredOrUncertain(deliveries)) {
        clearPublishAttempt();
        attemptRef.current = undefined;
      }
    } else if (attempt.draft.schedule) {
      setPublishError(undefined);
      setPublishNotice(`公告已排期，命令 ${shortId(command.commandId)} 将在设定时间发送。`);
    } else {
      setPublishError(undefined);
      setPublishNotice(`公告仍在队列中，命令号 ${shortId(command.commandId)}。`);
    }
  }

  function startNewAnnouncement() {
    if ((isPendingPhase(publishPhase) || publishPhase === "uncertain" || hasIrreversibleChannelResult) &&
        !globalThis.confirm("当前命令仍可能发送。确认保留该命令并新建另一条公告吗？")) {
      return;
    }
    clearPublishAttempt();
    attemptRef.current = undefined;
    inFlightRef.current = false;
    setTitle("");
    setBody("");
    setAudience("global");
    setChannels(defaultChannels(
      publishChatAnnouncements,
      publishTopBanner,
      publishClientOverlay
    ));
    setSchedule("");
    setSavedAt(undefined);
    setPublishPhase("idle");
    setPublishNotice(undefined);
    setPublishError(undefined);
    setChannelResults([]);
  }

  async function handlePublish() {
    if (!canPublish || inFlightRef.current) {
      return;
    }

    inFlightRef.current = true;
    setPublishPhase("submitting");
    setPublishError(undefined);
    setPublishNotice("正在持久化公告并提交发布命令…");
    try {
      const publishAt = schedule ? new Date(schedule) : null;
      if (publishAt && Number.isNaN(publishAt.getTime())) {
        throw new Error("发布时间格式无效，请重新选择。");
      }
      if (publishAt && publishAt.getTime() <= Date.now() + 30_000) {
        throw new Error("排期时间必须晚于当前时间；如需立即发布，请留空。");
      }

      const publishChannels = normalizeChannels(channels);
      const fingerprint = JSON.stringify({
        title: title.trim(),
        body: body.trim(),
        schedule: publishAt?.toISOString() ?? null,
        audience: "global",
        channels: publishChannels
      });
      if (!attemptRef.current || attemptRef.current.fingerprint !== fingerprint) {
        const attemptId = createAttemptId();
        attemptRef.current = {
          fingerprint,
          createKey: `announcement-create-${attemptId}`,
          publishKey: `announcement-publish-${attemptId}`,
          draft: {
            title: title.trim(),
            body: body.trim(),
            schedule,
            channels: publishChannels
          }
        };
        savePublishAttempt(attemptRef.current);
      }

      const attempt = attemptRef.current;
      if (!attempt.announcementId) {
        const draft = await createAnnouncement(
          {
            title: attempt.draft.title,
            body: attempt.draft.body,
            audience: { type: "global", ids: null },
            channels: attempt.draft.channels,
            publishAt: publishAt?.toISOString() ?? null,
            expiresAt: null
          },
          attempt.createKey
        );
        attempt.announcementId = draft.announcementId;
        savePublishAttempt(attempt);
      }

      let command = await publishAnnouncement(attempt.announcementId, attempt.publishKey);
      attempt.statusUrl = command.statusUrl;
      savePublishAttempt(attempt);
      if (isPending(command.state) && publishAt && publishAt.getTime() > Date.now() + 1000) {
        applyCommandResult(command, attempt);
        return;
      }

      for (let poll = 0; poll < 30 && isPending(command.state); poll += 1) {
        await delay(400);
        command = await getCommandStatus(command.statusUrl);
        setPublishPhase(command.state);
        setChannelResults(command.result?.channels ?? []);
      }

      applyCommandResult(command, attempt);
    } catch (error) {
      setPublishPhase("idle");
      setPublishNotice(undefined);
      setPublishError(error instanceof Error ? error.message : "公告发布失败。");
    } finally {
      inFlightRef.current = false;
    }
  }

  const hasSucceededChannel = channelResults.some((result) => result.state === "succeeded");
  const hasFailedChannel = channelResults.some((result) => result.state === "failed");
  const hasUncertainChannel = channelResults.some((result) => result.state === "uncertain");
  const currentRecordState = hasSucceededChannel && (hasFailedChannel || hasUncertainChannel)
    ? "部分送达"
    : publishPhase === "succeeded"
      ? "已发布"
    : publishPhase === "accepted" || publishPhase === "dispatched"
      ? schedule ? "已排期" : "发布中"
      : publishPhase === "uncertain"
        ? "待确认"
        : publishPhase === "failed"
          ? "发布失败"
          : publishPhase === "cancelled"
            ? "已取消"
            : "草稿";
  const scheduleMinimum = formatDateTimeLocal(new Date(Date.now() + 60_000));
  const publishButtonLabel = publishPhase === "submitting"
    ? "正在提交…"
    : publishPhase === "accepted" || publishPhase === "dispatched"
      ? "已加入发布队列"
      : publishPhase === "succeeded"
        ? "已发布"
        : publishPhase === "uncertain"
          ? "等待人工确认"
          : hasIrreversibleChannelResult
            ? "部分渠道需处理"
            : channels.length === 0
              ? "请选择发布渠道"
              : !selectedChannelsReady
                ? "所选渠道不可用"
                : !sharedPublishReady
                  ? "等待安全发布链路"
                  : schedule ? "排期发布" : "发布公告";
  const selectedChannelNames = channels.map((channel) => announcementChannelLabels[channel]);
  const unavailableSelectedChannels = channels.filter((channel) => !channelAvailability[channel]);
  const publishPanelReady = deliveryReady || editorLocked;
  const publishPanelTitle = publishError
    ? "发布需要处理"
    : publishPhase === "succeeded"
      ? "发布完成"
      : deliveryReady
        ? "所选渠道可安全发布"
        : "发布功能已锁定";
  const publishPanelMessage = publishError ?? publishNotice ?? (
    channels.length === 0
      ? "请至少选择一个已经就绪的发布渠道。"
      : unavailableSelectedChannels.length > 0
        ? `${formatChannelList(unavailableSelectedChannels)}当前不可用；可取消这些渠道后继续。`
        : !sharedPublishReady
          ? "幂等命令队列或追加式审计尚未就绪。"
          : `命令会先持久化并写入审计，再通过${formatChannelList(channels)}发送；无法确认的渠道不会自动重发。`
  );

  return (
    <div className="announcement-layout">
      <aside className="announcement-list-panel">
        <div className="panel-heading">
          <div>
            <p className="eyebrow">ANNOUNCEMENTS</p>
            <h2>公告记录</h2>
          </div>
          <span className="count-pill">1</span>
        </div>

        <div className="announcement-filters">
          <button className="active">全部</button>
          <button>草稿</button>
          <button>排期</button>
          <button>历史</button>
        </div>

        <div className="announcement-list">
          <button className="announcement-row selected">
            <span className={`announcement-state state-${currentRecordState}`}>{currentRecordState}</span>
            <strong>{title || "未命名公告"}</strong>
            <small>{schedule ? schedule.replace("T", " ") : "当前编辑"}</small>
          </button>
        </div>
      </aside>

      <section className="announcement-editor">
        <div className="editor-heading">
          <div>
            <p className="eyebrow">DRAFT / NEW</p>
            <h2>编辑公告</h2>
          </div>
          <span className={savedAt ? "draft-status saved" : "draft-status"}>
            {savedAt ? `已在本页暂存 ${savedAt}` : editorLocked ? currentRecordState : "有未保存修改"}
          </span>
        </div>

        <div className="announcement-workspace">
          <form
            className="announcement-form"
            onSubmit={(event) => {
              event.preventDefault();
              void handlePublish();
            }}
          >
            <label className="field-label">
              公告标题
              <input
                disabled={editorLocked}
                maxLength={120}
                onChange={(event) => {
                  markEdited();
                  setTitle(event.target.value);
                }}
                value={title}
              />
              <small>{title.length} / 120</small>
            </label>

            <label className="field-label">
              公告正文
              <textarea
                disabled={editorLocked}
                maxLength={1000}
                onChange={(event) => {
                  markEdited();
                  setBody(event.target.value);
                }}
                value={body}
              />
              <small>{body.length} / 1000</small>
            </label>

            <fieldset disabled={editorLocked}>
              <legend>发送对象</legend>
              <div className="choice-grid audience-choices">
                {audienceOptions.map((option) => (
                  <label className={audience === option.value ? "choice-card checked" : "choice-card"} key={option.value}>
                    <input
                      checked={audience === option.value}
                      disabled={!option.supported}
                      name="audience"
                      onChange={() => {
                        markEdited();
                        setAudience(option.value);
                      }}
                      type="radio"
                    />
                    <strong>{option.label}</strong>
                    <small>{option.hint}</small>
                  </label>
                ))}
              </div>
            </fieldset>

            <fieldset disabled={editorLocked}>
              <legend>发布渠道</legend>
              <div className="choice-grid channel-choices">
                {channelOptions.map((option) => {
                  const channel = isAnnouncementChannel(option.value) ? option.value : undefined;
                  const checked = channel ? channels.includes(channel) : false;
                  const disabled = !channel || (!option.available && !checked);
                  return (
                    <label
                      className={`choice-card${checked ? " checked" : ""}${!option.available ? " unavailable" : ""}`}
                      key={option.value}
                    >
                      <input
                        checked={checked}
                        disabled={disabled}
                        onChange={() => channel && toggleChannel(channel)}
                        type="checkbox"
                      />
                      <strong>{option.label}</strong>
                      <small>{option.hint}{checked && !option.available ? " · 可取消" : ""}</small>
                    </label>
                  );
                })}
              </div>
            </fieldset>

            <label className="field-label schedule-field">
              发布时间
              <input
                disabled={editorLocked}
                min={scheduleMinimum}
                onChange={(event) => {
                  markEdited();
                  setSchedule(event.target.value);
                }}
                type="datetime-local"
                value={schedule}
              />
              <small>留空表示审核后立即发送</small>
            </label>

            <div className="editor-actions">
              {editorLocked ? (
                <button className="ghost-button" onClick={startNewAnnouncement} type="button">
                  新建公告
                </button>
              ) : (
                <button className="ghost-button" onClick={() => setSavedAt(new Date().toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" }))} type="button">
                  本页暂存
                </button>
              )}
              <button className="primary-button" disabled={!canPublish} type="submit">
                {publishButtonLabel}
              </button>
            </div>
          </form>

          <aside className="announcement-preview-panel">
            <div className="preview-heading">
              <span>实时预览</span>
              <em>{selectedAudience}</em>
            </div>
            <div className="announcement-preview">
              <span className="preview-badge">
                {channels.length > 1
                  ? "MULTI-CHANNEL NOTICE"
                  : channels.length === 1
                    ? announcementChannelBadges[channels[0]]
                    : "NO CHANNEL SELECTED"}
              </span>
              <h3>{title || "未命名公告"}</h3>
              <p>{body || "在这里输入公告正文。"}</p>
              <div className="preview-channels" aria-label="已选发布渠道">
                {selectedChannelNames.length > 0
                  ? selectedChannelNames.map((name) => <span key={name}>{name}</span>)
                  : <span className="empty">未选择渠道</span>}
              </div>
              <div className="preview-meta">
                <span>{schedule ? schedule.replace("T", " ") : "立即发送"}</span>
                <span>{channels.length} 个渠道</span>
              </div>
            </div>

            {channelResults.length > 0 ? (
              <div className="channel-delivery-results" aria-live="polite">
                <p className="eyebrow">CHANNEL DELIVERY</p>
                <div>
                  {channelResults.map((result) => (
                    <article className="channel-delivery-row" key={result.channel}>
                      <span className={`channel-delivery-state state-${deliveryStateClass(result.state)}`}>
                        {deliveryStateLabel(result.state)}
                      </span>
                      <strong>{announcementChannelLabels[result.channel]}</strong>
                      <small>{formatDeliveryMeta(result)}</small>
                      {result.error ? <p>{result.error.message}</p> : null}
                    </article>
                  ))}
                </div>
              </div>
            ) : null}

            <div className="delivery-checklist">
              <p className="eyebrow">DELIVERY CHECK</p>
              <ul>
                <li className={title.trim() && body.trim() ? "passed" : ""}>标题与正文已填写</li>
                <li className={audience === "global" ? "passed" : "blocked"}>全服发送对象</li>
                <li className={channels.length > 0 ? "passed" : "blocked"}>至少选择一个发布渠道</li>
                {channels.map((channel) => (
                  <li className={channelAvailability[channel] ? "passed" : "blocked"} key={channel}>
                    {announcementChannelLabels[channel]}有效能力
                  </li>
                ))}
                {channels.includes("chat") ? (
                  <li className={restConnected ? "passed" : "blocked"}>官方 REST 已连接</li>
                ) : null}
                {channels.includes("top-banner") ? (
                  <li className={bridgeConnected ? "passed" : "blocked"}>Native Bridge 顶部横幅链路</li>
                ) : null}
                {channels.includes("client-overlay") ? (
                  <li className={bridgeConnected ? "passed" : "blocked"}>Native Bridge 浮层链路</li>
                ) : null}
                <li className={commandQueueReady ? "passed" : "blocked"}>幂等发布命令队列</li>
                <li className={auditReady ? "passed" : "blocked"}>追加式发布审计</li>
              </ul>
            </div>

            <div className={`publish-lock${publishPanelReady ? " ready" : ""}${publishError ? " error" : ""}`}>
              <strong>{publishPanelTitle}</strong>
              <p>{publishPanelMessage}</p>
            </div>
          </aside>
        </div>
      </section>
    </div>
  );
}

function defaultChannels(
  publishChatAnnouncements: boolean,
  publishTopBanner: boolean,
  publishClientOverlay: boolean
): AnnouncementChannel[] {
  if (publishChatAnnouncements) {
    return ["chat"];
  }
  if (publishTopBanner) {
    return ["top-banner"];
  }
  if (publishClientOverlay) {
    return ["client-overlay"];
  }
  return ["chat"];
}

function normalizeChannels(channels: AnnouncementChannel[]) {
  const selected = new Set(channels);
  return announcementChannelOrder.filter((channel) => selected.has(channel));
}

function isAnnouncementChannel(value: string): value is AnnouncementChannel {
  return value === "chat" || value === "top-banner" || value === "client-overlay";
}

function getChatChannelHint(available: boolean, restConnected: boolean, sharedReady: boolean) {
  if (available) {
    return "官方 REST · 可用";
  }
  if (!sharedReady) {
    return "安全发布链路未就绪";
  }
  return restConnected ? "聊天发布能力未就绪" : "官方 REST 未连接";
}

function getOverlayChannelHint(available: boolean, bridgeConnected: boolean, sharedReady: boolean) {
  if (available) {
    return "Native Bridge · 可用";
  }
  if (!sharedReady) {
    return "安全发布链路未就绪";
  }
  return bridgeConnected ? "浮层能力探针未就绪" : "Native Bridge 未连接";
}

function getTopBannerChannelHint(available: boolean, bridgeConnected: boolean, sharedReady: boolean) {
  if (available) {
    return "Native Bridge · 可用";
  }
  if (!sharedReady) {
    return "安全发布链路未就绪";
  }
  return bridgeConnected ? "顶部横幅能力探针未就绪" : "Native Bridge 未连接";
}

function formatChannelList(channels: AnnouncementChannel[]) {
  return channels.map((channel) => announcementChannelLabels[channel]).join("、");
}

function hasDeliveredOrUncertain(deliveries: ChannelDeliveryResult[]) {
  return deliveries.some((delivery) =>
    delivery.state === "succeeded" || delivery.state === "uncertain"
  );
}

function buildChannelAttentionMessage(
  succeeded: ChannelDeliveryResult[],
  failed: ChannelDeliveryResult[],
  uncertain: ChannelDeliveryResult[]
) {
  const segments: string[] = [];
  if (succeeded.length > 0) {
    segments.push(`${formatChannelList(succeeded.map((item) => item.channel))}已完成派发`);
  }
  if (failed.length > 0) {
    segments.push(`${formatChannelList(failed.map((item) => item.channel))}发送失败`);
  }
  if (uncertain.length > 0) {
    segments.push(`${formatChannelList(uncertain.map((item) => item.channel))}结果待确认`);
  }
  const nextStep = succeeded.length > 0 || uncertain.length > 0
    ? "系统不会自动重发已送达或结果待确认的渠道。"
    : "请确认渠道能力恢复后重新提交。";
  return `${segments.join("；")}。${nextStep}`;
}

function deliveryStateLabel(state: string) {
  switch (state) {
    case "accepted":
    case "pending":
      return "队列中";
    case "dispatched":
      return "发送中";
    case "succeeded":
      return "已派发";
    case "failed":
      return "失败";
    case "uncertain":
      return "待确认";
    case "cancelled":
      return "已取消";
    default:
      return state;
  }
}

function deliveryStateClass(state: string) {
  return state === "succeeded" || state === "failed" || state === "uncertain" ||
    state === "dispatched" || state === "cancelled"
    ? state
    : "pending";
}

function formatDeliveryMeta(result: ChannelDeliveryResult) {
  const details: string[] = [];
  if (typeof result.deliveredRecipients === "number" && typeof result.attemptedRecipients === "number") {
    details.push(
      `${result.deliveredRecipients} / ${result.attemptedRecipients} 个客户端`
    );
  } else if (typeof result.attemptedRecipients === "number") {
    details.push(`已向 ${result.attemptedRecipients} 个在线客户端广播 · 无逐客户端 ACK`);
  } else if ((result.channel === "client-overlay" || result.channel === "top-banner") &&
      result.state === "succeeded") {
    details.push(`${announcementChannelLabels[result.channel]}可靠广播已调用 · 无逐客户端 ACK`);
  }
  if (typeof result.httpStatus === "number") {
    details.push(`HTTP ${result.httpStatus}`);
  }
  return details.length > 0 ? details.join(" · ") : deliveryStateLabel(result.state);
}

function createAttemptId() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function isPending(state: CommandStatus["state"]) {
  return state === "accepted" || state === "dispatched";
}

function isPendingPhase(phase: PublishPhase) {
  return phase === "accepted" || phase === "dispatched";
}

function shortId(commandId: string) {
  return commandId.slice(0, 8);
}

function delay(milliseconds: number) {
  return new Promise<void>((resolve) => globalThis.setTimeout(resolve, milliseconds));
}

function formatDateTimeLocal(date: Date) {
  const local = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
  return local.toISOString().slice(0, 16);
}

function loadPublishAttempt(): PublishAttempt | undefined {
  try {
    const raw = globalThis.sessionStorage?.getItem(publishAttemptStorageKey);
    if (!raw) {
      return undefined;
    }
    const attempt = JSON.parse(raw) as Partial<PublishAttempt>;
    if (!attempt.fingerprint || !attempt.createKey || !attempt.publishKey ||
        !attempt.draft || typeof attempt.draft.title !== "string" ||
        typeof attempt.draft.body !== "string" || typeof attempt.draft.schedule !== "string") {
      globalThis.sessionStorage?.removeItem(publishAttemptStorageKey);
      return undefined;
    }
    const storedChannels = (attempt.draft as Partial<PublishAttempt["draft"]>).channels;
    if (storedChannels !== undefined &&
        (!Array.isArray(storedChannels) || storedChannels.some((channel) => !isAnnouncementChannel(channel)))) {
      globalThis.sessionStorage?.removeItem(publishAttemptStorageKey);
      return undefined;
    }
    attempt.draft = {
      ...attempt.draft,
      channels: storedChannels ? normalizeChannels(storedChannels) : ["chat"]
    };
    return attempt as PublishAttempt;
  } catch {
    return undefined;
  }
}

function savePublishAttempt(attempt: PublishAttempt) {
  try {
    globalThis.sessionStorage?.setItem(publishAttemptStorageKey, JSON.stringify(attempt));
  } catch {
    // The in-memory attempt still protects the active page when storage is unavailable.
  }
}

function clearPublishAttempt() {
  try {
    globalThis.sessionStorage?.removeItem(publishAttemptStorageKey);
  } catch {
    // Storage may be disabled; the caller also clears the in-memory attempt.
  }
}
