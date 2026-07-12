import { useCallback, useEffect, useMemo, useState } from "react";
import { InventoryPanel } from "../inventory/InventoryPanel";
import { PalPanel } from "../pals/PalPanel";
import {
  getPalDefenderItems,
  getPalDefenderPals,
  getPalDefenderPlayer,
  getPalDefenderProgression,
  getPalDefenderTechs,
  type GameCatalogEntry,
  type GameResourceCatalog,
  type NativeInventoryProbe,
  type NativePalProbe,
  type PalDefenderInventoryContainer,
  type PalDefenderItems,
  type PalDefenderPalRecord,
  type PalDefenderPals,
  type PalDefenderPlayer,
  type PalDefenderProgression,
  type PalDefenderTechs,
  type PalSkillCatalog,
  type PlayerSummary,
  type ServerCapabilities
} from "../../lib/api/client";
import { PlayerProgressionPanel } from "./PlayerProgressionPanel";

type PlayerCenterTab = "overview" | "inventory" | "pals" | "progression" | "advanced";
type AdvancedTool = "progression" | "inventory" | "pals";
type ReadSection = "player" | "progression" | "inventory" | "pals" | "techs";
type InventoryContainerKey = keyof PalDefenderItems["Inventory"];

const inventoryContainers: Array<{ key: InventoryContainerKey; label: string }> = [
  { key: "Items", label: "普通背包" },
  { key: "KeyItems", label: "关键物品" },
  { key: "Weapons", label: "武器栏" },
  { key: "Armor", label: "护甲栏" },
  { key: "Food", label: "食物栏" },
  { key: "DropSlot", label: "丢弃栏" }
];

export type PlayerCenterProps = {
  players: PlayerSummary[];
  selectedPlayerId?: string;
  onSelectPlayer: (playerId: string) => void;
  onNavigate: (key: string) => void;
  connected: boolean;
  catalog?: GameResourceCatalog;
  capabilities?: ServerCapabilities;
  nativeInventoryProbe: NativeInventoryProbe | null | undefined;
  nativePalProbe: NativePalProbe | null | undefined;
  palSkillCatalog: PalSkillCatalog | null | undefined;
  inventoryLoading: boolean;
  palLoading: boolean;
  onRefreshInventory: () => void | Promise<void>;
  onRefreshPals: () => Promise<void>;
};

export function PlayerCenter({
  players,
  selectedPlayerId,
  onSelectPlayer,
  onNavigate,
  connected,
  catalog,
  capabilities,
  nativeInventoryProbe,
  nativePalProbe,
  palSkillCatalog,
  inventoryLoading,
  palLoading,
  onRefreshInventory,
  onRefreshPals
}: PlayerCenterProps) {
  const [activeTab, setActiveTab] = useState<PlayerCenterTab>("overview");
  const [advancedTool, setAdvancedTool] = useState<AdvancedTool>("progression");
  const [query, setQuery] = useState("");
  const [palQuery, setPalQuery] = useState("");
  const [activeInventoryContainer, setActiveInventoryContainer] =
    useState<InventoryContainerKey>("Items");
  const [loading, setLoading] = useState(false);
  const [errors, setErrors] = useState<Partial<Record<ReadSection, string>>>({});
  const [player, setPlayer] = useState<PalDefenderPlayer>();
  const [progression, setProgression] = useState<PalDefenderProgression>();
  const [items, setItems] = useState<PalDefenderItems>();
  const [pals, setPals] = useState<PalDefenderPals>();
  const [techs, setTechs] = useState<PalDefenderTechs>();

  const selectedSummary = useMemo(
    () => players.find((entry) => entry.playerId === selectedPlayerId),
    [players, selectedPlayerId]
  );
  const playerIdentifier = selectedSummary?.uid ?? selectedSummary?.playerId ?? selectedPlayerId;

  const filteredPlayers = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase("zh-Hans");
    if (!needle) return players;
    return players.filter((entry) =>
      `${entry.name} ${entry.uid ?? ""} ${entry.playerId}`
        .toLocaleLowerCase("zh-Hans")
        .includes(needle)
    );
  }, [players, query]);

  const itemCatalog = useMemo(() => catalogMap(catalog?.items), [catalog]);
  const palCatalog = useMemo(() => catalogMap(catalog?.pals), [catalog]);
  const technologyCatalog = useMemo(() => catalogMap(catalog?.technologies), [catalog]);

  const refreshPlayerData = useCallback(async (signal?: AbortSignal) => {
    setPlayer(undefined);
    setProgression(undefined);
    setItems(undefined);
    setPals(undefined);
    setTechs(undefined);
    setErrors({});

    if (!connected || !playerIdentifier) {
      setLoading(false);
      return;
    }

    setLoading(true);
    const results = await Promise.allSettled([
      getPalDefenderPlayer(playerIdentifier, signal),
      getPalDefenderProgression(playerIdentifier, signal),
      getPalDefenderItems(playerIdentifier, signal),
      getPalDefenderPals(playerIdentifier, signal),
      getPalDefenderTechs(playerIdentifier, signal)
    ] as const);

    if (signal?.aborted) return;

    const nextErrors: Partial<Record<ReadSection, string>> = {};
    const [playerResult, progressionResult, itemsResult, palsResult, techsResult] = results;

    if (playerResult.status === "fulfilled") setPlayer(playerResult.value);
    else nextErrors.player = errorMessage(playerResult.reason, "玩家资料读取失败");

    if (progressionResult.status === "fulfilled") setProgression(progressionResult.value);
    else nextErrors.progression = errorMessage(progressionResult.reason, "成长进度读取失败");

    if (itemsResult.status === "fulfilled") setItems(itemsResult.value);
    else nextErrors.inventory = errorMessage(itemsResult.reason, "背包资料读取失败");

    if (palsResult.status === "fulfilled") setPals(palsResult.value);
    else nextErrors.pals = errorMessage(palsResult.reason, "帕鲁资料读取失败");

    if (techsResult.status === "fulfilled") setTechs(techsResult.value);
    else nextErrors.techs = errorMessage(techsResult.reason, "科技资料读取失败");

    setErrors(nextErrors);
    setLoading(false);
  }, [connected, playerIdentifier]);

  useEffect(() => {
    const controller = new AbortController();
    setPalQuery("");
    void refreshPlayerData(controller.signal);
    return () => controller.abort();
  }, [refreshPlayerData]);

  const progressionData = progression?.Progression;
  const allInventoryContainers = items?.Inventory
    ? inventoryContainers.map(({ key }) => items.Inventory[key])
    : [];
  const usedInventorySlots = allInventoryContainers.reduce(
    (total, container) => total + (container?.UsedSlots ?? 0),
    0
  );
  const totalItemCount = allInventoryContainers.reduce(
    (total, container) => total + Object.values(container?.Slots ?? {})
      .reduce((containerTotal, slot) => containerTotal + slot.Count, 0),
    0
  );
  const palCount = (pals?.Meta?.TeamCount ?? 0)
    + (pals?.Meta?.PalboxCount ?? 0)
    + basePalCount(pals);
  const unlockedTechnologyCount = techs?.Meta?.UnlockedCount
    ?? techs?.Techs?.Unlocked?.length
    ?? 0;
  const nativeInventoryMatched = Boolean(selectedPlayerId && nativeInventoryProbe?.inventories.some(
    (entry) => normalizeId(entry.ownerPlayerUId ?? "") === normalizeId(selectedPlayerId)
  ));
  const nativePlayerPals = selectedPlayerId
    ? (nativePalProbe?.pals ?? []).filter(
      (entry) => normalizeId(entry.ownerPlayerUId) === normalizeId(selectedPlayerId)
    ).length
    : 0;

  return (
    <div className="player-layout">
      <section className="player-list-panel">
        <div className="panel-heading">
          <div><p className="eyebrow">PLAYER DIRECTORY</p><h2>玩家目录</h2></div>
          <span className="count-pill">{players.length}</span>
        </div>
        <label className="search-box">
          <span>搜索</span>
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="玩家名称、Steam ID 或玩家 UID"
          />
        </label>
        <div className="player-list">
          {filteredPlayers.map((entry) => (
            <button
              className={selectedPlayerId === entry.playerId ? "player-row selected" : "player-row"}
              key={entry.playerId}
              onClick={() => onSelectPlayer(entry.playerId)}
              type="button"
            >
              <span className="avatar">{entry.name.slice(0, 1) || "游"}</span>
              <span><strong>{entry.name}</strong><small>{entry.online ? "在线" : "离线"}</small></span>
              <em>Lv.{entry.level ?? "--"}</em>
            </button>
          ))}
          {filteredPlayers.length === 0 ? (
            <div className="player-list-empty">
              <strong>没有匹配的玩家</strong>
              <small>可以尝试玩家名称、平台账号或玩家 UID</small>
            </div>
          ) : null}
        </div>
      </section>

      <section className="detail-panel">
        <header className="player-detail-heading">
          <div className="player-detail-title">
            <span className="hero-icon">{selectedSummary?.name.slice(0, 1) || "游"}</span>
            <div>
              <p className="eyebrow">PLAYER PROFILE / PALDEFENDER</p>
              <h2>{player?.Player?.Name ?? selectedSummary?.name ?? "请选择玩家"}</h2>
              <p>{player?.Player?.GuildName || "暂无公会资料"} · {player?.Player?.Status ?? (selectedSummary?.online ? "Online" : "Unknown")}</p>
            </div>
          </div>
          <div className="pd-heading-actions">
            <button
              className="ghost-button"
              disabled={loading || !playerIdentifier || !connected}
              onClick={() => void refreshPlayerData()}
              type="button"
            >{loading ? "同步中…" : "刷新资料"}</button>
            <button
              className="primary-button"
              disabled={!selectedPlayerId}
              onClick={() => onNavigate("operations")}
              type="button"
            >发放奖励 / 管理</button>
          </div>
        </header>

        {!connected ? (
          <div className="pd-banner warning" role="status">
            <strong>PalDefender 未连接</strong>
            <span>玩家主资料暂不可读；Native 高级工具会按自身连接状态独立启用。</span>
          </div>
        ) : null}

        <div className="metric-grid">
          <Metric label="当前等级" value={progressionData?.Player?.level ? `Lv.${progressionData.Player.level}` : selectedSummary?.level ? `Lv.${selectedSummary.level}` : "--"} />
          <Metric label="背包物品" value={items ? `${totalItemCount} 件` : "--"} note={items ? `${usedInventorySlots} 个槽位` : undefined} />
          <Metric label="持有帕鲁" value={pals ? String(palCount) : "--"} note={pals ? `队伍 ${pals.Meta.TeamCount} · 帕鲁盒 ${pals.Meta.PalboxCount}` : undefined} />
          <Metric label="已解锁科技" value={techs ? String(unlockedTechnologyCount) : "--"} note={techs ? `共 ${techs.Meta.TotalCount} 项` : undefined} />
        </div>

        <div className="tabs" role="tablist" aria-label="玩家资料分类">
          <TabButton active={activeTab === "overview"} onClick={() => setActiveTab("overview")}>总览</TabButton>
          <TabButton active={activeTab === "inventory"} onClick={() => setActiveTab("inventory")}>背包</TabButton>
          <TabButton active={activeTab === "pals"} onClick={() => setActiveTab("pals")}>帕鲁</TabButton>
          <TabButton active={activeTab === "progression"} onClick={() => setActiveTab("progression")}>成长与科技</TabButton>
          <TabButton active={activeTab === "advanced"} onClick={() => setActiveTab("advanced")}>高级精细工具</TabButton>
        </div>

        {activeTab === "overview" ? (
          <OverviewTab
            errors={errors}
            loading={loading}
            onNavigate={onNavigate}
            player={player}
            progression={progression}
            selectedSummary={selectedSummary}
          />
        ) : null}

        {activeTab === "inventory" ? (
          <InventoryTab
            activeContainer={activeInventoryContainer}
            error={errors.inventory}
            itemCatalog={itemCatalog}
            items={items}
            loading={loading}
            onContainer={setActiveInventoryContainer}
            onNavigate={onNavigate}
          />
        ) : null}

        {activeTab === "pals" ? (
          <PalsTab
            error={errors.pals}
            loading={loading}
            onNavigate={onNavigate}
            palCatalog={palCatalog}
            pals={pals}
            query={palQuery}
            setQuery={setPalQuery}
          />
        ) : null}

        {activeTab === "progression" ? (
          <ProgressionTab
            errors={errors}
            loading={loading}
            onNavigate={onNavigate}
            progression={progression}
            technologyCatalog={technologyCatalog}
            techs={techs}
          />
        ) : null}

        {activeTab === "advanced" ? (
          <section className="pd-section-grid player-advanced-tools">
            <div className="capability-grid">
              <Capability label="成长精细写入" enabled={capabilities?.writePlayerProgression} detail={capabilities?.writePlayerProgression ? "在线玩家可用" : "当前未启用"} />
              <Capability label="背包精确匹配" enabled={nativeInventoryMatched} detail={nativeInventoryMatched ? "已匹配当前玩家" : "没有匹配对象"} />
              <Capability label="帕鲁精确匹配" enabled={nativePlayerPals > 0} detail={nativePlayerPals > 0 ? `${nativePlayerPals} 只已加载` : "没有匹配对象"} />
            </div>
            <div className="tabs" role="tablist" aria-label="Native 高级工具">
              <TabButton active={advancedTool === "progression"} onClick={() => setAdvancedTool("progression")}>属性点与成长</TabButton>
              <TabButton active={advancedTool === "inventory"} onClick={() => setAdvancedTool("inventory")}>现有槽位数量</TabButton>
              <TabButton active={advancedTool === "pals"} onClick={() => setAdvancedTool("pals")}>帕鲁昵称与技能</TabButton>
            </div>
            {advancedTool === "progression" ? (
              <PlayerProgressionPanel
                playerId={selectedPlayerId}
                canWrite={capabilities?.writePlayerProgression ?? false}
              />
            ) : null}
            {advancedTool === "inventory" ? (
              <InventoryPanel
                probe={nativeInventoryProbe}
                loading={inventoryLoading}
                canWrite={capabilities?.writeInventory ?? false}
                selectedPlayerId={selectedPlayerId}
                onRefresh={onRefreshInventory}
              />
            ) : null}
            {advancedTool === "pals" ? (
              <PalPanel
                probe={nativePalProbe}
                palCatalog={palCatalog}
                skillCatalog={palSkillCatalog}
                loading={palLoading}
                canWrite={capabilities?.writePals ?? false}
                selectedPlayerId={selectedPlayerId}
                onRefresh={onRefreshPals}
              />
            ) : null}
          </section>
        ) : null}
      </section>
    </div>
  );
}

function OverviewTab({
  errors,
  loading,
  onNavigate,
  player,
  progression,
  selectedSummary
}: {
  errors: Partial<Record<ReadSection, string>>;
  loading: boolean;
  onNavigate: (key: string) => void;
  player?: PalDefenderPlayer;
  progression?: PalDefenderProgression;
  selectedSummary?: PlayerSummary;
}) {
  if (!selectedSummary && !player) return <EmptyState title="请选择玩家" text="从左侧玩家目录选择目标后查看完整资料。" />;
  const detail = player?.Player;
  const data = progression?.Progression;
  return (
    <section className="pd-section-grid">
      {errors.player ? <ReadError text={errors.player} /> : null}
      {errors.progression ? <ReadError text={errors.progression} /> : null}
      {loading && !player && !progression ? <EmptyState title="正在同步玩家资料" text="正在分别读取身份、成长、背包、帕鲁与科技。" /> : null}
      <div className="pd-player-identity">
        <span className="pd-player-avatar">{(detail?.Name ?? selectedSummary?.name ?? "游").slice(0, 1)}</span>
        <div>
          <strong>{detail?.Name ?? selectedSummary?.name ?? "未知玩家"}</strong>
          <small>{detail?.UserId ?? selectedSummary?.uid ?? "未取得平台账号"}</small>
        </div>
        <span className={detail?.Status === "Online" || selectedSummary?.online ? "pd-state success" : "pd-state"}>
          {detail?.Status === "Online" || selectedSummary?.online ? "在线" : "离线 / 未知"}
        </span>
        <dl>
          <div><dt>玩家 UID</dt><dd>{detail?.PlayerUID ?? selectedSummary?.playerId ?? "--"}</dd></div>
          <div><dt>所属公会</dt><dd>{detail?.GuildName || "无公会"}</dd></div>
        </dl>
      </div>
      <div className="player-overview-grid">
        <article className="notice-card">
          <div>
            <p className="eyebrow">PLAYER PROGRESSION</p>
            <h3>Lv.{data?.Player?.level ?? selectedSummary?.level ?? "--"} · 经验 {formatNumber(data?.Player?.exp)}</h3>
            <p>科技点 {formatNumber(data?.Currencies?.technologyPoints)} · 远古科技点 {formatNumber(data?.Currencies?.ancientTechnologyPoints)} · 未分配属性点 {formatNumber(data?.Player?.unusedStatusPoints)}</p>
          </div>
          <button className="primary-button" onClick={() => onNavigate("operations")} type="button">发放奖励</button>
        </article>
        <article className="notice-card">
          <div>
            <p className="eyebrow">WORLD POSITION</p>
            <h3>{detail?.GuildName || "无公会"}</h3>
            <p>{detail?.MapLocation ? `地图坐标 X ${detail.MapLocation.x.toFixed(1)} · Y ${detail.MapLocation.y.toFixed(1)}` : "当前没有可显示的地图位置"}</p>
          </div>
          <button className="ghost-button" onClick={() => onNavigate("map")} type="button">查看实时地图</button>
        </article>
      </div>
      <div className="pd-metric-grid compact">
        <SmallMetric label="累计 Boss 击败" value={data?.Bosses?.totalBossDefeatCount ?? "--"} />
        <SmallMetric label="捕获种类" value={data ? Object.keys(data.Captures?.palCaptureCounts ?? {}).length : "--"} />
        <SmallMetric label="地牢完成" value={data ? (data.Activities.normalDungeonClearCount + data.Activities.fixedDungeonClearCount) : "--"} />
        <SmallMetric label="发现宝箱" value={data?.Activities?.foundTreasureCount ?? "--"} />
      </div>
    </section>
  );
}

function InventoryTab({
  activeContainer,
  error,
  itemCatalog,
  items,
  loading,
  onContainer,
  onNavigate
}: {
  activeContainer: InventoryContainerKey;
  error?: string;
  itemCatalog: Map<string, GameCatalogEntry>;
  items?: PalDefenderItems;
  loading: boolean;
  onContainer: (container: InventoryContainerKey) => void;
  onNavigate: (key: string) => void;
}) {
  const container = items?.Inventory?.[activeContainer];
  const slots = sortedSlots(container);
  return (
    <section className="inventory-panel">
      <div className="inventory-heading">
        <div><p className="eyebrow">PALDEFENDER INVENTORY</p><h3>玩家背包</h3><p>PalDefender 提供六类完整容器快照；内部 ID 仅作为辅助信息显示。</p></div>
        <div className="inventory-summary">
          <span><strong>{container?.UsedSlots ?? 0}</strong> 已占用</span>
          <span><strong>{container?.MaxSlots ?? 0}</strong> 总槽位</span>
          <button className="primary-button" onClick={() => onNavigate("operations")} type="button">选择物品发放</button>
        </div>
      </div>
      {error ? <ReadError text={error} /> : null}
      <div className="container-filter" aria-label="背包容器">
        {inventoryContainers.map(({ key, label }) => {
          const value = items?.Inventory?.[key];
          return (
            <button className={activeContainer === key ? "active" : ""} key={key} onClick={() => onContainer(key)} type="button">
              {label}<em>{value?.UsedSlots ?? 0}/{value?.MaxSlots ?? 0}</em>
            </button>
          );
        })}
      </div>
      {!items && loading ? <EmptyState title="正在读取背包" text="各资料分区独立加载，其他分区失败不会影响背包结果。" /> : null}
      {items && container && !container.Available ? <EmptyState title="该容器当前不可用" text="PalDefender 没有返回可读取的容器对象。" /> : null}
      {container?.Available ? (
        <div className="inventory-table-wrap">
          <table className="inventory-table">
            <thead><tr><th>槽位</th><th>物品</th><th>内部 ID</th><th>数量</th><th>状态</th></tr></thead>
            <tbody>
              {slots.map(([slotIndex, slot]) => (
                <tr key={`${activeContainer}-${slotIndex}`}>
                  <td><code>#{slotIndex}</code></td>
                  <td><strong>{catalogName(itemCatalog, slot.ItemID)}</strong></td>
                  <td><code>{slot.ItemID}</code></td>
                  <td>{formatNumber(slot.Count)}</td>
                  <td><span className="slot-state occupied">已占用</span></td>
                </tr>
              ))}
            </tbody>
          </table>
          {slots.length === 0 ? <div className="inventory-table-empty">当前容器没有物品</div> : null}
        </div>
      ) : null}
    </section>
  );
}

function PalsTab({
  error,
  loading,
  onNavigate,
  palCatalog,
  pals,
  query,
  setQuery
}: {
  error?: string;
  loading: boolean;
  onNavigate: (key: string) => void;
  palCatalog: Map<string, GameCatalogEntry>;
  pals?: PalDefenderPals;
  query: string;
  setQuery: (value: string) => void;
}) {
  const needle = query.trim().toLocaleLowerCase("zh-Hans");
  const groups = palGroups(pals).map((group) => ({
    ...group,
    entries: group.entries.filter(({ instanceId, pal, location }) => {
      const entry = findCatalogEntry(palCatalog, pal.PalID);
      return `${entry?.name ?? ""} ${entry?.englishName ?? ""} ${entry?.dex ?? ""} ${pal.PalID} ${pal.Nickname} ${instanceId} ${location}`
        .toLocaleLowerCase("zh-Hans")
        .includes(needle);
    })
  }));
  return (
    <section className="pal-panel">
      <div className="pal-heading">
        <div><p className="eyebrow">PALDEFENDER PAL ROSTER</p><h3>队伍、帕鲁盒与基地帕鲁</h3><p>完整资料以 PalDefender 为主；Native 编辑只在高级工具中精确匹配已加载实例。</p></div>
        <div className="pal-summary">
          <span><strong>{pals?.Meta?.TeamCount ?? 0}</strong> 队伍</span>
          <span><strong>{pals?.Meta?.PalboxCount ?? 0}</strong> 帕鲁盒</span>
          <button className="primary-button" onClick={() => onNavigate("operations")} type="button">选择帕鲁发放</button>
        </div>
      </div>
      {error ? <ReadError text={error} /> : null}
      <label className="pal-search"><span>⌕</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索中文名、种类、昵称或实例 ID" /></label>
      {!pals && loading ? <EmptyState title="正在读取帕鲁资料" text="正在分别读取队伍、帕鲁盒与基地信息。" /> : null}
      <div className="pd-section-grid">
        {groups.map((group) => (
          <section className="pd-read-card" key={group.key}>
            <div className="pd-section-title"><div><span>{group.label}</span><strong>{group.entries.length} 只</strong></div></div>
            <div className="pd-code-list">
              {group.entries.map(({ instanceId, location, pal }) => {
                const entry = findCatalogEntry(palCatalog, pal.PalID);
                return (
                  <article className="pd-member-row" key={`${group.key}-${instanceId}`}>
                    <span>
                      <strong>{entry?.name ?? pal.PalID}</strong>
                      <small>{[pal.Nickname ? `昵称 ${pal.Nickname}` : "", location, `Lv.${pal.Level}`, genderLabel(pal.Gender), pal.Shiny ? "闪光" : ""].filter(Boolean).join(" · ")}</small>
                    </span>
                    <span>
                      <code>{pal.PalID}</code>
                      <small>{[entry?.englishName ?? "", entry?.dex ? `图鉴 #${entry.dex}` : "", `HP ${formatNumber(pal.HP)}`, `SAN ${formatNumber(pal.SAN)}`, `被动 ${pal.Passives?.length ?? 0}`].filter(Boolean).join(" · ")}</small>
                    </span>
                  </article>
                );
              })}
              {group.entries.length === 0 ? <div className="pd-empty">没有匹配的帕鲁</div> : null}
            </div>
          </section>
        ))}
      </div>
    </section>
  );
}

function ProgressionTab({
  errors,
  loading,
  onNavigate,
  progression,
  technologyCatalog,
  techs
}: {
  errors: Partial<Record<ReadSection, string>>;
  loading: boolean;
  onNavigate: (key: string) => void;
  progression?: PalDefenderProgression;
  technologyCatalog: Map<string, GameCatalogEntry>;
  techs?: PalDefenderTechs;
}) {
  const data = progression?.Progression;
  return (
    <section className="progression-panel">
      <div className="progression-toolbar">
        <div><p className="eyebrow">PALDEFENDER PROGRESSION</p><h3>成长与科技</h3><small>常规发放与科技管理统一通过 PalDefender 持久命令队列执行</small></div>
        <button className="primary-button" onClick={() => onNavigate("operations")} type="button">发放 / 管理科技</button>
      </div>
      {errors.progression ? <ReadError text={errors.progression} /> : null}
      {errors.techs ? <ReadError text={errors.techs} /> : null}
      {!progression && !techs && loading ? <EmptyState title="正在读取成长与科技" text="两个资料分区独立读取，单项失败不会遮蔽另一项。" /> : null}
      <div className="progression-metrics">
        <ProgressMetric label="等级" value={data ? `Lv.${data.Player.level}` : "--"} />
        <ProgressMetric label="总经验" value={formatNumber(data?.Player.exp)} />
        <ProgressMetric label="未分配属性点" value={formatNumber(data?.Player.unusedStatusPoints)} />
        <ProgressMetric label="科技点" value={formatNumber(data?.Currencies.technologyPoints)} />
        <ProgressMetric label="远古科技点" value={formatNumber(data?.Currencies.ancientTechnologyPoints)} />
        <ProgressMetric label="遗物种类" value={data ? String(Object.keys(data.Currencies.relics ?? {}).length) : "--"} />
      </div>
      <div className="pd-detail-columns">
        <section className="pd-read-card">
          <div className="pd-section-title"><div><span>已解锁科技</span><strong>{techs?.Meta?.UnlockedCount ?? 0} / {techs?.Meta?.TotalCount ?? "--"}</strong></div></div>
          <div className="pd-code-list">
            {(techs?.Techs?.Unlocked ?? []).map((technologyId) => (
              <article className="pd-member-row" key={technologyId}>
                <span><strong>{catalogName(technologyCatalog, technologyId)}</strong><small>已解锁</small></span>
                <code>{technologyId}</code>
              </article>
            ))}
            {(techs?.Techs?.Unlocked?.length ?? 0) === 0 ? <div className="pd-empty">暂无已解锁科技</div> : null}
          </div>
        </section>
        <section className="pd-read-card">
          <div className="pd-section-title"><div><span>成长里程碑</span><strong>实时快照</strong></div></div>
          <div className="pd-code-list">
            <Milestone label="Boss 击败" value={data?.Bosses.totalBossDefeatCount} />
            <Milestone label="捕获总数" value={sumRecord(data?.Captures.palCaptureCounts)} />
            <Milestone label="地下城完成" value={data ? data.Activities.normalDungeonClearCount + data.Activities.fixedDungeonClearCount : undefined} />
            <Milestone label="据点征服" value={data?.Activities.campConqueredCount} />
            <Milestone label="钓鱼次数" value={sumRecord(data?.Activities.fishingCounts)} />
          </div>
        </section>
      </div>
    </section>
  );
}

function Metric({ label, value, note }: { label: string; value: string; note?: string }) {
  return <div className="metric-card"><span>{label}</span><strong>{value}</strong>{note ? <small>{note}</small> : null}</div>;
}

function SmallMetric({ label, value }: { label: string; value: string | number }) {
  return <article className="pd-metric"><span>{label}</span><strong>{value}</strong></article>;
}

function ProgressMetric({ label, value }: { label: string; value: string }) {
  return <div><span>{label}</span><strong>{value}</strong></div>;
}

function Milestone({ label, value }: { label: string; value?: number }) {
  return <article className="pd-member-row"><span><strong>{label}</strong><small>PalDefender 进度</small></span><strong>{formatNumber(value)}</strong></article>;
}

function Capability({ label, enabled, detail }: { label: string; enabled?: boolean; detail: string }) {
  return <div className="capability-card"><span className={enabled ? "capability-icon enabled" : "capability-icon"}>{enabled ? "✓" : "×"}</span><div><strong>{label}</strong><small>{detail}</small></div></div>;
}

function TabButton({ active, children, onClick }: { active: boolean; children: string; onClick: () => void }) {
  return <button className={active ? "active" : ""} onClick={onClick} role="tab" type="button">{children}</button>;
}

function ReadError({ text }: { text: string }) {
  return <div className="pd-feedback error" role="alert">{text}</div>;
}

function EmptyState({ title, text }: { title: string; text: string }) {
  return <div className="pd-empty"><strong>{title}</strong><span>{text}</span></div>;
}

function sortedSlots(container: PalDefenderInventoryContainer | undefined) {
  return Object.entries(container?.Slots ?? {}).sort(([left], [right]) => Number(left) - Number(right));
}

function catalogMap(entries: GameCatalogEntry[] | undefined) {
  return new Map((entries ?? []).flatMap((entry) => [
    [entry.id, entry] as const,
    [normalizeResourceId(entry.id), entry] as const
  ]));
}

function catalogName(entries: Map<string, GameCatalogEntry>, id: string) {
  return findCatalogEntry(entries, id)?.name ?? id;
}

function findCatalogEntry(entries: Map<string, GameCatalogEntry>, id: string) {
  return entries.get(id) ?? entries.get(normalizeResourceId(id));
}

function normalizeResourceId(value: string) {
  return value.trim().toLocaleLowerCase();
}

function palGroups(pals: PalDefenderPals | undefined) {
  const team = Object.entries(pals?.Pals?.Team ?? {}).map(([instanceId, pal]) => ({ instanceId, pal, location: `队伍槽位 ${(pal.team_slot_index ?? 0) + 1}` }));
  const palbox = Object.entries(pals?.Pals?.Palbox ?? {}).map(([instanceId, pal]) => ({ instanceId, pal, location: `帕鲁盒第 ${(pal.page ?? 0) + 1} 页 · 槽位 ${(pal.slot ?? 0) + 1}` }));
  const bases = (pals?.Pals?.BaseCamps ?? []).flatMap((camp, campIndex) =>
    Object.entries(camp.pals ?? {}).map(([instanceId, pal]) => ({
      instanceId,
      pal,
      location: `基地 ${campIndex + 1} · 槽位 ${(pal.base_camp_slot_index ?? 0) + 1}`
    }))
  );
  return [
    { key: "team", label: "当前队伍", entries: team },
    { key: "palbox", label: "帕鲁盒", entries: palbox },
    { key: "bases", label: "基地工作帕鲁", entries: bases }
  ] satisfies Array<{
    key: string;
    label: string;
    entries: Array<{ instanceId: string; pal: PalDefenderPalRecord; location: string }>;
  }>;
}

function basePalCount(pals: PalDefenderPals | undefined) {
  return (pals?.Pals?.BaseCamps ?? []).reduce((total, camp) => total + Object.keys(camp.pals ?? {}).length, 0);
}

function normalizeId(value: string) {
  return value.replace(/[^a-zA-Z0-9]/g, "").toLocaleLowerCase();
}

function formatNumber(value: number | undefined | null) {
  return typeof value === "number" && Number.isFinite(value)
    ? new Intl.NumberFormat("zh-CN").format(value)
    : "--";
}

function sumRecord(value: Record<string, number> | undefined) {
  if (!value) return undefined;
  return Object.values(value).reduce((total, amount) => total + amount, 0);
}

function genderLabel(value: string) {
  if (value.toLocaleLowerCase() === "male") return "雄性";
  if (value.toLocaleLowerCase() === "female") return "雌性";
  return "无性别";
}

function errorMessage(reason: unknown, fallback: string) {
  return reason instanceof Error ? reason.message : fallback;
}
