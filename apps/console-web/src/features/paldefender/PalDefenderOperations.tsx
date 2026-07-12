import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import {
  getPalDefenderCommand,
  getPalDefenderItems,
  getPalDefenderPals,
  getPalDefenderPlayer,
  getPalDefenderProgression,
  getPalDefenderTechs,
  submitPalDefenderCommand,
  type GameCatalogEntry,
  type GameResourceCatalog,
  type PalDefenderCommand,
  type PalDefenderItems,
  type PalDefenderPals,
  type PalDefenderPlayer,
  type PalDefenderProgression,
  type PalDefenderTechs,
  type PlayerSummary
} from "../../lib/api/client";
import { ResourcePickerField, type ResourcePickerOption } from "./ResourcePicker";

type OperationTab = "rewards" | "technology" | "message" | "moderation";

type PendingOperation = {
  title: string;
  description: string;
  path: string;
  payload: unknown;
  details: Array<{ label: string; value: string }>;
  targetIdentifier: string;
  targetName: string;
  auditReason: string;
  successRefresh?: boolean;
  danger?: boolean;
};

type Props = {
  players: PlayerSummary[];
  selectedPlayerId?: string;
  onSelectPlayer: (playerId: string) => void;
  connected: boolean;
  catalog?: GameResourceCatalog;
};

const terminalStates = new Set(["succeeded", "failed", "uncertain", "cancelled"]);

function idempotencyKey() {
  return globalThis.crypto?.randomUUID?.() ?? `pd-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function wait(milliseconds: number) {
  return new Promise((resolve) => globalThis.setTimeout(resolve, milliseconds));
}

export function PalDefenderOperations({
  players,
  selectedPlayerId,
  onSelectPlayer,
  connected,
  catalog
}: Props) {
  const [activeTab, setActiveTab] = useState<OperationTab>("rewards");
  const [reason, setReason] = useState("玩家奖励发放");
  const [loading, setLoading] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string>();
  const [command, setCommand] = useState<PalDefenderCommand>();
  const [player, setPlayer] = useState<PalDefenderPlayer>();
  const [progression, setProgression] = useState<PalDefenderProgression>();
  const [items, setItems] = useState<PalDefenderItems>();
  const [pals, setPals] = useState<PalDefenderPals>();
  const [techs, setTechs] = useState<PalDefenderTechs>();
  const [pendingOperation, setPendingOperation] = useState<PendingOperation>();

  const [experience, setExperience] = useState("1000");
  const [technologyPoints, setTechnologyPoints] = useState("0");
  const [ancientTechnologyPoints, setAncientTechnologyPoints] = useState("0");
  const [itemId, setItemId] = useState("");
  const [itemCount, setItemCount] = useState("100");
  const [palId, setPalId] = useState("");
  const [palLevel, setPalLevel] = useState("1");
  const [eggId, setEggId] = useState("");
  const [eggPalId, setEggPalId] = useState("");
  const [eggLevel, setEggLevel] = useState("1");
  const [templateName, setTemplateName] = useState("");
  const [learnTechnology, setLearnTechnology] = useState("");
  const [forgetTechnology, setForgetTechnology] = useState("");
  const [message, setMessage] = useState("");
  const [messageType, setMessageType] = useState("PlayerLogImportant");
  const [moderationReason, setModerationReason] = useState("");
  const commandInFlight = useRef(false);

  const selectedSummary = useMemo(
    () => players.find((entry) => entry.playerId === selectedPlayerId),
    [players, selectedPlayerId]
  );
  const playerIdentifier = selectedSummary?.uid ?? selectedSummary?.playerId ?? selectedPlayerId;

  async function refreshPlayerData(signal?: AbortSignal) {
    if (!playerIdentifier || !connected) {
      setPlayer(undefined);
      setProgression(undefined);
      setItems(undefined);
      setPals(undefined);
      setTechs(undefined);
      return;
    }
    setLoading(true);
    setError(undefined);
    setPlayer(undefined);
    setProgression(undefined);
    setItems(undefined);
    setPals(undefined);
    setTechs(undefined);
    try {
      const [nextPlayer, nextProgression, nextItems, nextPals, nextTechs] = await Promise.allSettled([
        getPalDefenderPlayer(playerIdentifier, signal),
        getPalDefenderProgression(playerIdentifier, signal),
        getPalDefenderItems(playerIdentifier, signal),
        getPalDefenderPals(playerIdentifier, signal),
        getPalDefenderTechs(playerIdentifier, signal)
      ]);
      if (nextPlayer.status === "fulfilled") setPlayer(nextPlayer.value);
      if (nextProgression.status === "fulfilled") setProgression(nextProgression.value);
      if (nextItems.status === "fulfilled") setItems(nextItems.value);
      if (nextPals.status === "fulfilled") setPals(nextPals.value);
      if (nextTechs.status === "fulfilled") setTechs(nextTechs.value);
      const failures = [nextPlayer, nextProgression, nextItems, nextPals, nextTechs]
        .filter((result): result is PromiseRejectedResult => result.status === "rejected")
        .map((result) => result.reason instanceof Error ? result.reason.message : "部分玩家数据读取失败");
      if (failures.length > 0 && !signal?.aborted) {
        setError(`部分资料暂不可用：${Array.from(new Set(failures)).join("；")}`);
      }
    } catch (nextError) {
      if (!signal?.aborted) {
        setError(nextError instanceof Error ? nextError.message : "玩家资料读取失败");
      }
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }

  useEffect(() => {
    const controller = new AbortController();
    void refreshPlayerData(controller.signal);
    return () => controller.abort();
  }, [playerIdentifier, connected]);

  async function runCommand(
    path: string,
    payload: unknown,
    successRefresh = true,
    targetIdentifier = playerIdentifier,
    auditReason = reason.trim()
  ) {
    if (commandInFlight.current) return;
    if (!targetIdentifier && path.includes("{player}")) {
      setError("请先选择玩家。");
      return;
    }
    if (auditReason.length < 3) {
      setError("请填写至少 3 个字的操作原因。");
      return;
    }
    commandInFlight.current = true;
    setSending(true);
    setError(undefined);
    setCommand(undefined);
    const resolvedPath = path.replace("{player}", encodeURIComponent(targetIdentifier ?? ""));
    try {
      let next = await submitPalDefenderCommand({
        path: resolvedPath,
        payload,
        reason: auditReason,
        idempotencyKey: idempotencyKey()
      });
      setCommand(next);
      for (let attempt = 0; attempt < 60 && !terminalStates.has(next.state); attempt += 1) {
        await wait(750);
        next = await getPalDefenderCommand(next.statusUrl);
        setCommand(next);
      }
      if (!terminalStates.has(next.state)) {
        setError("命令仍在后台执行，请到“接口与审计”查看最终结果，勿重复提交。");
      } else if (next.state === "succeeded" && successRefresh) {
        await refreshPlayerData();
      } else if (next.state === "uncertain") {
        setError("上游结果不确定：请先刷新玩家数据人工核验，系统不会自动重试。");
      } else if (next.state === "failed") {
        setError(next.error?.message ?? "PalDefender 拒绝了本次操作。");
      }
    } catch (nextError) {
      setError(
        `${nextError instanceof Error ? nextError.message : "命令提交失败"}。若请求已离开浏览器，请先核对玩家数据，勿立即重复提交。`
      );
    } finally {
      commandInFlight.current = false;
      setSending(false);
    }
  }

  function queueOperation(operation: Omit<PendingOperation, "targetIdentifier" | "targetName" | "auditReason">) {
    const auditReason = reason.trim();
    if (auditReason.length < 3) {
      setError("请填写至少 3 个字的操作原因。");
      return;
    }
    if (!playerIdentifier) {
      setError("请先选择一名玩家。");
      return;
    }
    setError(undefined);
    setPendingOperation({
      ...operation,
      targetIdentifier: playerIdentifier,
      targetName: player?.Player?.Name ?? selectedSummary?.name ?? "当前玩家",
      auditReason
    });
  }

  function requirePlayer() {
    if (!playerIdentifier) {
      setError("请先选择一名玩家。");
      return false;
    }
    if (!connected) {
      setError("PalDefender 当前未连接，不能执行该操作。");
      return false;
    }
    return true;
  }

  function chooseTab(tab: OperationTab) {
    setActiveTab(tab);
    setError(undefined);
    setReason({
      rewards: "玩家奖励发放",
      technology: "玩家科技管理",
      message: "玩家定向通知",
      moderation: "玩家违规处置"
    }[tab]);
  }

  function submitProgression(event: FormEvent) {
    event.preventDefault();
    if (!requirePlayer()) return;
    const payload: Record<string, number> = {};
    const exp = Number(experience);
    const tech = Number(technologyPoints);
    const ancient = Number(ancientTechnologyPoints);
    if (Number.isInteger(exp) && exp > 0) payload.EXP = exp;
    if (Number.isInteger(tech) && tech > 0) payload.TechnologyPoints = tech;
    if (Number.isInteger(ancient) && ancient > 0) payload.AncientTechnologyPoints = ancient;
    if (Object.keys(payload).length === 0) {
      setError("经验或点数至少填写一项正整数。");
      return;
    }
    queueOperation({
      title: "确认发放经验与点数",
      description: "数值会在玩家现有进度上增加，不会覆盖原值。",
      path: "give/progression/{player}",
      payload,
      details: [
        ...(payload.EXP ? [{ label: "经验 EXP", value: String(payload.EXP) }] : []),
        ...(payload.TechnologyPoints ? [{ label: "科技点", value: String(payload.TechnologyPoints) }] : []),
        ...(payload.AncientTechnologyPoints ? [{ label: "远古科技点", value: String(payload.AncientTechnologyPoints) }] : [])
      ]
    });
  }

  const progressionData = progression?.Progression;
  const inventoryContainers = items?.Inventory ? Object.values(items.Inventory) : [];
  const usedSlots = inventoryContainers.reduce((sum, container) => sum + (container?.UsedSlots ?? 0), 0);
  const palCount = (pals?.Meta?.TeamCount ?? 0) + (pals?.Meta?.PalboxCount ?? 0);
  const unlockedCount = techs?.Meta?.UnlockedCount ?? techs?.Techs?.Unlocked?.length ?? 0;
  const palCatalogById = useMemo(
    () => new Map((catalog?.pals ?? []).map((entry) => [normalizeResourceId(entry.id), entry])),
    [catalog]
  );
  const itemOptions = useMemo(() => mergeOptions(
    (catalog?.items ?? []).map((entry) => ({
      id: entry.id,
      label: entry.name,
      category: entry.category,
      description: `内部标识 ${entry.id}`
    })),
    inventoryContainers.flatMap((container) => Object.values(container?.Slots ?? {}).map((slot) => ({
      id: slot.ItemID,
      label: slot.ItemID,
      category: "玩家已有",
      badges: ["玩家已有"]
    })))
  ), [catalog, items]);
  const palOptions = useMemo(() => mergeOptions(
    (catalog?.pals ?? []).map((entry) => ({
      id: entry.id,
      label: entry.name,
      category: "Pal",
      description: palCatalogDescription(entry, entry.id),
      keywords: [entry.englishName ?? "", entry.id, entry.dex ?? ""],
      badges: entry.dex ? [`#${entry.dex}`] : undefined
    })),
    [
      ...Object.values(pals?.Pals?.Team ?? {}),
      ...Object.values(pals?.Pals?.Palbox ?? {})
    ].map((entry) => {
      const catalogEntry = palCatalogById.get(normalizeResourceId(entry.PalID));
      return {
        id: catalogEntry?.id ?? entry.PalID,
        label: catalogEntry?.name ?? entry.PalID,
        category: "玩家已有",
        description: catalogEntry
          ? palCatalogDescription(catalogEntry, entry.PalID, entry.Nickname)
          : [entry.Nickname ? `昵称 ${entry.Nickname}` : "", `PalID ${entry.PalID}`].filter(Boolean).join(" · "),
        keywords: [catalogEntry?.englishName ?? "", entry.PalID, catalogEntry?.dex ?? "", entry.Nickname],
        badges: ["玩家已有", ...(catalogEntry?.dex ? [`#${catalogEntry.dex}`] : [])]
      };
    })
  ), [catalog, palCatalogById, pals]);
  const eggOptions = useMemo<ResourcePickerOption[]>(() =>
    (catalog?.eggs ?? []).map((entry) => ({
      id: entry.id,
      label: localizeEggName(entry.id, entry.name),
      category: entry.category,
      description: entry.id
    })), [catalog]);
  const unlockedSet = useMemo(() => new Set(techs?.Techs?.Unlocked ?? []), [techs]);
  const technologyOptions = useMemo<ResourcePickerOption[]>(() => mergeOptions(
    (catalog?.technologies ?? []).map((entry) => ({
      id: entry.id,
      label: entry.name,
      category: "Technology",
      description: unlockedSet.has(entry.id) ? "当前玩家已解锁" : "当前玩家尚未解锁",
      badges: unlockedSet.has(entry.id) ? ["已解锁"] : ["未解锁"]
    })),
    (techs?.Techs?.Unlocked ?? []).map((id) => ({
      id,
      label: id,
      category: "玩家已解锁",
      badges: ["已解锁"]
    }))
  ), [catalog, techs, unlockedSet]);
  const templateOptions = useMemo<ResourcePickerOption[]>(() =>
    (catalog?.templates ?? []).map((entry) => {
      const catalogEntry = palCatalogById.get(normalizeResourceId(entry.palId ?? ""));
      return {
        id: entry.fileName,
        label: catalogEntry?.name ?? entry.palId ?? entry.fileName,
        category: "Templates",
        description: [
          entry.nickname ? `模板昵称 ${entry.nickname}` : "",
          catalogEntry?.englishName ?? "",
          entry.palId ? `PalID ${entry.palId}` : "未知帕鲁",
          catalogEntry?.dex ? `图鉴 #${catalogEntry.dex}` : "",
          `Lv.${entry.level ?? "--"}`,
          entry.summary
        ].filter(Boolean).join(" · "),
        keywords: [entry.nickname ?? "", catalogEntry?.englishName ?? "", entry.palId ?? "", catalogEntry?.dex ?? ""],
        badges: [riskLabel(entry.riskLevel), `被动 ${entry.passiveCount}`],
        disabled: !entry.selectable,
        tone: entry.riskLevel
      };
    }), [catalog, palCatalogById]);
  const playerOptions = useMemo<ResourcePickerOption[]>(() => players.map((entry) => ({
    id: entry.playerId,
    label: entry.name,
    category: entry.online ? "在线玩家" : "离线玩家",
    description: entry.uid ?? entry.playerId,
    badges: [entry.online ? "在线" : "离线", `Lv.${entry.level ?? "--"}`]
  })), [players]);

  return (
    <section className="pd-page" aria-label="PalDefender 玩家运营中心">
      <header className="pd-page-heading">
        <div>
          <p className="eyebrow">PLAYER OPERATIONS / PALDEFENDER</p>
          <h2>玩家运营中心</h2>
          <p>向玩家发放经验、物品、帕鲁和科技，并通过持久命令队列记录每次操作。</p>
        </div>
        <div className="pd-heading-actions">
          <ResourcePickerField
            disabled={players.length === 0}
            label="当前玩家"
            onChange={onSelectPlayer}
            options={playerOptions}
            placeholder="选择在线玩家"
            sourceNote="来自当前服务器在线玩家目录"
            title="选择目标玩家"
            value={selectedPlayerId ?? ""}
          />
          <button className="ghost-button" disabled={loading || !playerIdentifier} onClick={() => void refreshPlayerData()} type="button">
            {loading ? "读取中…" : "刷新资料"}
          </button>
        </div>
      </header>

      {!connected ? (
        <div className="pd-banner warning" role="status">
          <strong>PalDefender 未连接</strong>
          <span>页面保留全部操作，但在插件 REST API 恢复前不会发出命令。</span>
        </div>
      ) : null}

      <div className="pd-player-identity">
        <span className="pd-player-avatar">{selectedSummary?.name?.slice(0, 1) || "游"}</span>
        <div>
          <strong>{player?.Player?.Name ?? selectedSummary?.name ?? "请选择玩家"}</strong>
          <small>{player?.Player?.UserId ?? selectedSummary?.uid ?? "未取得平台账号"}</small>
        </div>
        <span className={player?.Player?.Status === "Online" || selectedSummary?.online ? "pd-state success" : "pd-state"}>
          {player?.Player?.Status === "Online" || selectedSummary?.online ? "在线" : "离线 / 未知"}
        </span>
        <dl>
          <div><dt>玩家 UID</dt><dd>{player?.Player?.PlayerUID ?? selectedSummary?.playerId ?? "--"}</dd></div>
          <div><dt>所属公会</dt><dd>{player?.Player?.GuildName || "无公会"}</dd></div>
        </dl>
      </div>

      <div className="pd-metric-grid">
        <Metric label="当前等级" value={progressionData?.Player?.level ?? selectedSummary?.level ?? "--"} note={`经验 ${progressionData?.Player?.exp ?? "--"}`} />
        <Metric label="科技点" value={progressionData?.Currencies?.technologyPoints ?? "--"} note={`远古 ${progressionData?.Currencies?.ancientTechnologyPoints ?? "--"}`} />
        <Metric label="背包槽位" value={usedSlots} note={`${inventoryContainers.length} 个容器`} />
        <Metric label="持有帕鲁" value={palCount} note={`已解锁科技 ${unlockedCount}`} />
      </div>

      <details className="pd-profile-details">
        <summary>查看 PalDefender 完整进度摘要</summary>
        <div>
          <article><span>未分配属性点</span><strong>{progressionData?.Player?.unusedStatusPoints ?? "--"}</strong></article>
          <article><span>累计 Boss 击败</span><strong>{progressionData?.Bosses?.totalBossDefeatCount ?? "--"}</strong></article>
          <article><span>捕获种类</span><strong>{Object.keys(progressionData?.Captures?.palCaptureCounts ?? {}).length}</strong></article>
          <article><span>遗物进度</span><code>{formatRecord(progressionData?.Currencies?.relics)}</code></article>
        </div>
      </details>

      <div className="pd-workbench">
        <div className="pd-tabs" role="tablist" aria-label="玩家操作分类">
          <button className={activeTab === "rewards" ? "active" : ""} onClick={() => chooseTab("rewards")} type="button">奖励发放</button>
          <button className={activeTab === "technology" ? "active" : ""} onClick={() => chooseTab("technology")} type="button">科技管理</button>
          <button className={activeTab === "message" ? "active" : ""} onClick={() => chooseTab("message")} type="button">定向消息</button>
          <button className={activeTab === "moderation" ? "active" : ""} onClick={() => chooseTab("moderation")} type="button">玩家管理</button>
        </div>

        <label className="pd-reason-field">
          <span>统一操作原因</span>
          <input maxLength={160} value={reason} onChange={(event) => setReason(event.target.value)} placeholder="会写入审计记录，至少 3 个字" />
          <small>每次提交都会创建独立幂等键；页面不会自动重试写操作。</small>
        </label>

        {activeTab === "rewards" ? (
          <div className="pd-form-grid rewards">
            <form className="pd-action-card featured" onSubmit={submitProgression}>
              <CardHeading title="经验与点数" description="增加经验、科技点和远古科技点，不会覆盖当前数值。" badge="常用" />
              <div className="pd-field-row three">
                <NumberField label="经验 EXP" min={0} max={100000000} value={experience} onChange={setExperience} />
                <NumberField label="科技点" min={0} max={1000000} value={technologyPoints} onChange={setTechnologyPoints} />
                <NumberField label="远古科技点" min={0} max={1000000} value={ancientTechnologyPoints} onChange={setAncientTechnologyPoints} />
              </div>
              <SubmitButton disabled={sending || !connected || !playerIdentifier}>发放经验与点数</SubmitButton>
            </form>

            <form className="pd-action-card" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              const count = Number(itemCount);
              if (!itemId.trim() || !Number.isInteger(count) || count < 1) return setError("请填写有效物品 ID 和正整数数量。");
              queueOperation({
                title: "确认发放物品",
                description: "物品会直接进入目标玩家背包，请核对显示名称、内部标识和数量。",
                path: "give/items/{player}",
                payload: { Items: [{ ItemID: itemId.trim(), Count: count }] },
                details: [
                  { label: "物品", value: optionLabel(itemOptions, itemId.trim()) },
                  { label: "数量", value: String(count) }
                ]
              });
            }}>
              <CardHeading title="发放物品" description="向玩家背包新增物品；物品 ID 使用游戏内部名称。" />
              <div className="pd-field-row">
                <ResourcePickerField
                  allowCustom
                  customHint="仅用于已经安装并验证过的模组物品；系统无法校验目录外物品。"
                  label="物品"
                  onChange={setItemId}
                  options={itemOptions}
                  placeholder="从完整物品目录选择"
                  sourceNote={catalog ? `${catalog.items.length} 个参考物品 · 支持名称/分类/ID 搜索` : "资源目录加载中，可使用高级自定义入口"}
                  title="选择要发放的物品"
                  value={itemId}
                />
                <NumberField label="数量" min={1} max={999999} value={itemCount} onChange={setItemCount} />
              </div>
              <SubmitButton disabled={sending || !connected || !playerIdentifier || !itemId}>发放物品</SubmitButton>
            </form>

            <form className="pd-action-card" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              const level = Number(palLevel);
              if (!palId.trim() || !Number.isInteger(level) || level < 1) return setError("请填写有效帕鲁 ID 和等级。");
              queueOperation({
                title: "确认发放帕鲁",
                description: "帕鲁会直接发放给目标玩家，请核对种类和等级。",
                path: "give/pals/{player}",
                payload: { Pals: [{ PalID: palId.trim(), Level: level }] },
                details: [
                  { label: "帕鲁", value: optionLabel(palOptions, palId.trim()) },
                  { label: "等级", value: `Lv.${level}` }
                ]
              });
            }}>
              <CardHeading title="发放帕鲁" description="直接向玩家发放指定等级的帕鲁。" />
              <div className="pd-field-row">
                <ResourcePickerField
                  allowCustom
                  customHint="仅用于已确认存在的自定义或模组帕鲁种类。"
                  label="帕鲁种类"
                  onChange={setPalId}
                  options={palOptions}
                  placeholder="搜索并选择帕鲁"
                  sourceNote={catalog ? `${catalog.pals.length} 个帕鲁参考条目` : "帕鲁目录加载中"}
                  title="选择要发放的帕鲁"
                  value={palId}
                />
                <NumberField label="等级" min={1} max={65} value={palLevel} onChange={setPalLevel} />
              </div>
              <SubmitButton disabled={sending || !connected || !playerIdentifier || !palId}>发放帕鲁</SubmitButton>
            </form>

            <form className="pd-action-card" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              const level = Number(eggLevel);
              if (!eggId.trim() || !eggPalId.trim() || !Number.isInteger(level) || level < 1) return setError("请填写蛋 ID、帕鲁 ID 和有效等级。");
              queueOperation({
                title: "确认发放帕鲁蛋",
                description: "蛋类型、孵化帕鲁和等级将一并写入，请逐项核对。",
                path: "give/paleggs/{player}",
                payload: { PalEggs: [{ EggID: eggId.trim(), PalID: eggPalId.trim(), Level: level }] },
                details: [
                  { label: "蛋类型", value: optionLabel(eggOptions, eggId.trim()) },
                  { label: "蛋内帕鲁", value: optionLabel(palOptions, eggPalId.trim()) },
                  { label: "孵化等级", value: `Lv.${level}` }
                ]
              });
            }}>
              <CardHeading title="发放帕鲁蛋" description="按蛋类型与孵化帕鲁发放，可指定孵化等级。" />
              <div className="pd-field-row three">
                <ResourcePickerField
                  label="帕鲁蛋类型"
                  onChange={setEggId}
                  options={eggOptions}
                  placeholder="选择属性与尺寸"
                  sourceNote="45 种 PalDefender 文档支持的蛋类型"
                  title="选择帕鲁蛋类型"
                  value={eggId}
                />
                <ResourcePickerField
                  allowCustom
                  customHint="仅用于已验证的模组帕鲁。"
                  label="蛋内帕鲁"
                  onChange={setEggPalId}
                  options={palOptions}
                  placeholder="选择孵化出的帕鲁"
                  sourceNote={catalog ? `${catalog.pals.length} 个帕鲁参考条目` : "帕鲁目录加载中"}
                  title="选择蛋内帕鲁"
                  value={eggPalId}
                />
                <NumberField label="等级" min={1} max={65} value={eggLevel} onChange={setEggLevel} />
              </div>
              <SubmitButton disabled={sending || !connected || !playerIdentifier || !eggId || !eggPalId}>发放帕鲁蛋</SubmitButton>
            </form>

            <form className="pd-action-card" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              if (!/^[a-zA-Z0-9._-]+\.json$/.test(templateName.trim())) return setError("模板必须是安全的 .json 文件名，不能包含目录路径。");
              const selectedTemplate = catalog?.templates.find((entry) => entry.fileName === templateName.trim());
              if (!selectedTemplate?.selectable) return setError("该模板未通过安全摘要检查，不能从网页直接发放。");
              queueOperation({
                title: "确认按模板发放帕鲁",
                description: "模板会按服务端文件中的完整属性生成帕鲁，请再次核对风险摘要。",
                path: "give/paltemplate/{player}",
                payload: { PalTemplates: [templateName.trim()] },
                details: [
                  { label: "模板", value: optionLabel(templateOptions, templateName.trim()) },
                  { label: "风险级别", value: riskLabel(selectedTemplate.riskLevel) },
                  { label: "模板摘要", value: selectedTemplate.summary }
                ],
                danger: selectedTemplate.riskLevel !== "standard"
              });
            }}>
              <CardHeading title="模板发放" description="使用服务端预先审核的 PalDefender 帕鲁模板。" badge="高级" />
              <ResourcePickerField
                label="服务器模板"
                onChange={setTemplateName}
                options={templateOptions}
                placeholder="选择已验证的模板"
                sourceNote={`${catalog?.templates.length ?? 0} 个服务器模板 · 高风险模板自动锁定`}
                title="选择 PalDefender 模板"
                value={templateName}
              />
              <SubmitButton disabled={sending || !connected || !playerIdentifier || !templateName}>确认模板发放</SubmitButton>
            </form>
          </div>
        ) : null}

        {activeTab === "technology" ? (
          <div className="pd-section-grid">
            <form className="pd-action-card" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              if (!learnTechnology.trim()) return setError("请选择要学习的科技。");
              if (unlockedSet.has(learnTechnology.trim())) return setError("该科技已解锁，请选择尚未解锁的科技。");
              queueOperation({
                title: "确认解锁科技",
                description: "该科技会加入目标玩家的已解锁列表。",
                path: "learntech/{player}",
                payload: { Technology: learnTechnology.trim() },
                details: [{ label: "科技", value: optionLabel(technologyOptions, learnTechnology.trim()) }]
              });
            }}>
              <CardHeading title="学习科技" description="解锁一个指定科技；已解锁的项目会被安全跳过。" />
              <ResourcePickerField
                allowCustom
                customHint="仅用于目录尚未覆盖的新版本或模组科技；请先确认 TechID。"
                label="要学习的科技"
                onChange={setLearnTechnology}
                options={technologyOptions.filter((option) => !unlockedSet.has(option.id))}
                placeholder="搜索尚未解锁的科技"
                sourceNote={catalog ? `${catalog.technologies.length} 项科技参考目录` : "科技目录加载中"}
                title="选择要学习的科技"
                value={learnTechnology}
              />
              <SubmitButton disabled={sending || !connected || !playerIdentifier || !learnTechnology}>学习科技</SubmitButton>
            </form>
            <form className="pd-action-card danger-zone" onSubmit={(event) => {
              event.preventDefault();
              if (!requirePlayer()) return;
              if (!forgetTechnology.trim() || forgetTechnology.trim().toLowerCase() === "all") return setError("为安全起见，网页不允许遗忘 All，请选择单个科技。");
              queueOperation({
                title: "确认遗忘单项科技",
                description: "这是高风险操作；只会移除下方这一项科技，不允许批量 All。",
                path: "forgettech/{player}",
                payload: { Technology: forgetTechnology.trim() },
                details: [{ label: "要遗忘的科技", value: optionLabel(technologyOptions, forgetTechnology.trim()) }],
                danger: true
              });
            }}>
              <CardHeading title="遗忘单项科技" description="高风险操作；网页明确禁止使用 All 批量遗忘。" badge="需确认" />
              <ResourcePickerField
                label="已解锁科技"
                onChange={setForgetTechnology}
                options={technologyOptions.filter((option) => unlockedSet.has(option.id))}
                placeholder="从当前玩家已解锁科技中选择"
                sourceNote={`${unlockedSet.size} 项当前玩家已解锁科技`}
                title="选择要遗忘的科技"
                value={forgetTechnology}
              />
              <button className="danger-button" disabled={sending || !connected || !playerIdentifier || !forgetTechnology} type="submit">遗忘该科技</button>
            </form>
            <section className="pd-read-card">
              <CardHeading title="已解锁科技" description={`${unlockedCount} / ${techs?.Meta?.TotalCount ?? "--"} 项`} />
              <div className="pd-chip-list">
                {(techs?.Techs?.Unlocked ?? []).slice(0, 80).map((entry) => <button key={entry} onClick={() => setForgetTechnology(entry)} title="选择此科技用于遗忘操作" type="button"><code>{entry}</code></button>)}
                {(techs?.Techs?.Unlocked?.length ?? 0) === 0 ? <span>暂无可显示数据</span> : null}
              </div>
            </section>
          </div>
        ) : null}

        {activeTab === "message" ? (
          <form className="pd-action-card pd-single-form" onSubmit={(event) => {
            event.preventDefault();
            if (!requirePlayer()) return;
            if (!message.trim()) return setError("请输入要发送的消息。");
            queueOperation({
              title: "确认发送定向消息",
              description: "消息只会发送给当前选择的玩家。",
              path: "SendPlayerMessage",
              payload: {
                SendType: messageType,
                UserID: playerIdentifier,
                Message: message.trim()
              },
              details: [
                { label: "显示方式", value: messageTypeLabel(messageType) },
                { label: "消息内容", value: message.trim() }
              ],
              successRefresh: false
            });
          }}>
            <CardHeading title="向玩家发送消息" description="通过 PalDefender 发送定向聊天或日志提示，不会重复走公告渠道。" />
            <div className="pd-field-row">
              <label className="pd-field"><span>显示方式</span><select value={messageType} onChange={(event) => setMessageType(event.target.value)}>
                <option value="PlayerLogImportant">重要提示</option>
                <option value="PlayerLogVeryImportant">非常重要提示</option>
                <option value="PlayerLogNormal">普通提示</option>
                <option value="PlayerChat">玩家聊天</option>
                <option value="PlayerGlobalChat">全局聊天样式</option>
                <option value="PlayerGuildChat">公会聊天样式</option>
              </select></label>
              <label className="pd-field wide"><span>消息内容</span><textarea maxLength={500} rows={4} value={message} onChange={(event) => setMessage(event.target.value)} placeholder="输入发送给当前玩家的消息" /></label>
            </div>
            <SubmitButton disabled={sending || !connected || !playerIdentifier}>发送定向消息</SubmitButton>
          </form>
        ) : null}

        {activeTab === "moderation" ? (
          <div className="pd-section-grid moderation">
            <section className="pd-action-card">
              <CardHeading title="管理操作说明" description="踢出和封禁只针对当前选择的玩家，操作原因会同时传给游戏与审计系统。" />
              <TextField label="玩家可见原因" value={moderationReason} onChange={setModerationReason} placeholder="例如：违反服务器规则" />
              <div className="pd-inline-actions">
                <button className="ghost-button" disabled={sending || !connected || !playerIdentifier} onClick={() => {
                  if (moderationReason.trim().length < 3) return setError("请填写至少 3 个字的管理原因。");
                  queueOperation({
                    title: "确认踢出玩家",
                    description: "玩家会立即与服务器断开，但不会被加入封禁列表。",
                    path: "kick/{player}",
                    payload: { Reason: moderationReason.trim() },
                    details: [{ label: "玩家可见原因", value: moderationReason.trim() }],
                    successRefresh: false,
                    danger: true
                  });
                }} type="button">踢出玩家</button>
                <button className="danger-button" disabled={sending || !connected || !playerIdentifier} onClick={() => {
                  if (moderationReason.trim().length < 3) return setError("请填写至少 3 个字的管理原因。");
                  queueOperation({
                    title: "确认封禁玩家账号",
                    description: "封禁会立即生效；网页不会联动封禁 IP，避免共享网络误伤。",
                    path: "ban/{player}",
                    payload: { Reason: moderationReason.trim(), IP: false },
                    details: [
                      { label: "封禁原因", value: moderationReason.trim() },
                      { label: "IP 联动", value: "否" }
                    ],
                    successRefresh: false,
                    danger: true
                  });
                }} type="button">封禁账号</button>
              </div>
            </section>
            <aside className="pd-safety-card">
              <strong>安全边界</strong>
              <p>网页默认不联动封禁 IP，避免共享网络误伤。需要解除封禁时，请前往“公会与封禁”。</p>
              <p>删除基地接口没有授予当前令牌权限，因此控制台不会展示可点击的删除入口。</p>
            </aside>
          </div>
        ) : null}
      </div>

      {pendingOperation ? (
        <OperationConfirmDialog
          operation={pendingOperation}
          onCancel={() => setPendingOperation(undefined)}
          onConfirm={() => {
            const operation = pendingOperation;
            setPendingOperation(undefined);
            void runCommand(
              operation.path,
              operation.payload,
              operation.successRefresh ?? true,
              operation.targetIdentifier,
              operation.auditReason
            );
          }}
        />
      ) : null}

      {error ? <div className="pd-feedback error" role="alert">{error}</div> : null}
      {command ? <CommandResult command={command} sending={sending} /> : null}
    </section>
  );
}

function Metric({ label, value, note }: { label: string; value: string | number; note: string }) {
  return <article className="pd-metric"><span>{label}</span><strong>{value}</strong><small>{note}</small></article>;
}

function CardHeading({ title, description, badge }: { title: string; description: string; badge?: string }) {
  return <header className="pd-card-heading"><div><h3>{title}</h3><p>{description}</p></div>{badge ? <span>{badge}</span> : null}</header>;
}

function TextField({ label, value, onChange, placeholder }: { label: string; value: string; onChange: (value: string) => void; placeholder?: string }) {
  return <label className="pd-field"><span>{label}</span><input value={value} onChange={(event) => onChange(event.target.value)} placeholder={placeholder} /></label>;
}

function NumberField({ label, min, max, value, onChange }: { label: string; min: number; max: number; value: string; onChange: (value: string) => void }) {
  return <label className="pd-field"><span>{label}</span><input inputMode="numeric" min={min} max={max} step={1} type="number" value={value} onChange={(event) => onChange(event.target.value)} /></label>;
}

function SubmitButton({ children, disabled }: { children: string; disabled: boolean }) {
  return <button className="primary-button pd-submit" disabled={disabled} type="submit">{children}</button>;
}

function OperationConfirmDialog({
  operation,
  onCancel,
  onConfirm
}: {
  operation: PendingOperation;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const cancelRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.style.overflow = "hidden";
    const timer = globalThis.setTimeout(() => cancelRef.current?.focus(), 0);
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") onCancel();
    };
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      globalThis.clearTimeout(timer);
      document.removeEventListener("keydown", handleKeyDown);
      document.body.style.overflow = previousOverflow;
      previousFocus?.focus();
    };
  }, []);

  return (
    <div className="resource-picker-backdrop" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onCancel();
    }}>
      <section aria-labelledby="operation-confirm-title" aria-modal="true" className={`operation-confirm-dialog${operation.danger ? " danger" : ""}`} role="alertdialog">
        <header>
          <div>
            <p className="eyebrow">REVIEW BEFORE EXECUTION</p>
            <h3 id="operation-confirm-title">{operation.title}</h3>
            <p>{operation.description}</p>
          </div>
          <button aria-label="关闭确认窗口" onClick={onCancel} type="button">×</button>
        </header>

        <div className="operation-confirm-target">
          <span>目标玩家</span>
          <strong>{operation.targetName}</strong>
          <code>{operation.targetIdentifier}</code>
        </div>

        <dl className="operation-confirm-details">
          {operation.details.map((detail) => (
            <div key={`${detail.label}-${detail.value}`}>
              <dt>{detail.label}</dt>
              <dd>{detail.value}</dd>
            </div>
          ))}
          <div>
            <dt>审计原因</dt>
            <dd>{operation.auditReason}</dd>
          </div>
        </dl>

        <div className="operation-confirm-note">
          <strong>提交后不会自动重试</strong>
          <span>若返回结果不确定，请先刷新玩家资料核验，避免重复发放。</span>
        </div>

        <footer>
          <button className="ghost-button" onClick={onCancel} ref={cancelRef} type="button">返回修改</button>
          <button className={operation.danger ? "danger-button" : "primary-button"} onClick={onConfirm} type="button">
            {operation.danger ? "确认执行高风险操作" : "确认并提交"}
          </button>
        </footer>
      </section>
    </div>
  );
}

function CommandResult({ command, sending }: { command: PalDefenderCommand; sending: boolean }) {
  const tone = command.state === "succeeded" ? "success" : command.state === "failed" ? "error" : command.state === "uncertain" ? "warning" : "pending";
  const labels: Record<string, string> = {
    accepted: "已持久化接收",
    dispatched: "已发送至 PalDefender",
    succeeded: "执行成功",
    failed: "执行失败",
    uncertain: "结果待人工核验",
    cancelled: "已取消"
  };
  return (
    <section className={`pd-command-result ${tone}`} aria-live="polite">
      <div>
        <span className={`pd-state ${tone}`}>{sending && !terminalStates.has(command.state) ? "执行中" : labels[command.state] ?? command.state}</span>
        <strong>命令 {command.commandId}</strong>
        <small>{command.result?.upstreamPath ?? "等待上游回执"}</small>
      </div>
      <dl>
        <div><dt>HTTP</dt><dd>{command.result?.httpStatus ?? "--"}</dd></div>
        <div><dt>创建时间</dt><dd>{new Date(command.createdAt).toLocaleString("zh-CN")}</dd></div>
      </dl>
      {command.error ? <p>{command.error.code} · {command.error.message}</p> : null}
    </section>
  );
}

function mergeOptions(...groups: ResourcePickerOption[][]): ResourcePickerOption[] {
  const byId = new Map<string, ResourcePickerOption>();
  for (const option of groups.flat()) {
    if (!option.id) continue;
    const normalizedId = normalizeResourceId(option.id);
    const current = byId.get(normalizedId);
    if (!current) {
      byId.set(normalizedId, option);
      continue;
    }
    byId.set(normalizedId, {
      ...current,
      label: normalizeResourceId(current.label) === normalizeResourceId(current.id) && option.label
        ? option.label
        : current.label,
      description: current.description ?? option.description,
      keywords: Array.from(new Set([...(current.keywords ?? []), ...(option.keywords ?? []), current.id, option.id])),
      badges: Array.from(new Set([...(current.badges ?? []), ...(option.badges ?? [])]))
    });
  }
  return Array.from(byId.values());
}

function optionLabel(options: ResourcePickerOption[], id: string) {
  const normalizedId = normalizeResourceId(id);
  const option = options.find((entry) => normalizeResourceId(entry.id) === normalizedId);
  return option && option.label !== option.id ? `${option.label} · ${option.id}` : id;
}

function normalizeResourceId(value: string) {
  return value.trim().toLocaleLowerCase();
}

function palCatalogDescription(entry: GameCatalogEntry, observedId: string, nickname?: string) {
  return [
    nickname ? `昵称 ${nickname}` : "",
    entry.englishName ?? "",
    `PalID ${observedId}`,
    entry.dex ? `图鉴 #${entry.dex}` : ""
  ].filter(Boolean).join(" · ");
}

function messageTypeLabel(value: string) {
  const labels: Record<string, string> = {
    PlayerLogImportant: "重要提示",
    PlayerLogVeryImportant: "非常重要提示",
    PlayerLogNormal: "普通提示",
    PlayerChat: "玩家聊天",
    PlayerGlobalChat: "全局聊天样式",
    PlayerGuildChat: "公会聊天样式"
  };
  return labels[value] ?? value;
}

function localizeEggName(id: string, fallback: string) {
  const match = /^PalEgg_([A-Za-z]+)_(\d{2})$/.exec(id);
  if (!match) return fallback;
  const elements: Record<string, string> = {
    Dark: "暗属性",
    Dragon: "龙属性",
    Earth: "地属性",
    Electricity: "雷属性",
    Fire: "火属性",
    Ice: "冰属性",
    Leaf: "草属性",
    Normal: "无属性",
    Water: "水属性"
  };
  return `${elements[match[1]] ?? match[1]}帕鲁蛋 · 尺寸 ${match[2]}`;
}

function riskLabel(risk: "standard" | "elevated" | "high" | "invalid") {
  return {
    standard: "常规模板",
    elevated: "需重点核对",
    high: "高风险已锁定",
    invalid: "模板无效"
  }[risk];
}

function formatRecord(value: Record<string, number> | undefined) {
  if (!value || Object.keys(value).length === 0) return "暂无";
  return Object.entries(value).map(([key, amount]) => `${key}: ${amount}`).join(" · ");
}
