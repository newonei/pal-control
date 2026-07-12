import { useEffect, useMemo, useState } from "react";
import {
  setInventoryQuantity,
  type InventoryMutationResult,
  type NativeInventoryProbe,
  type NativeInventorySlot
} from "../../lib/api/client";

const containerLabels: Record<string, string> = {
  all: "全部容器",
  common: "普通背包",
  dropSlot: "丢弃栏",
  essential: "关键物品",
  weaponLoadout: "武器栏",
  armor: "护甲栏",
  food: "食物栏"
};

const pageSize = 20;

type InventoryRow = NativeInventorySlot & {
  containerKind: string;
  containerId: string | null;
};

type InventoryPanelProps = {
  probe: NativeInventoryProbe | null | undefined;
  loading: boolean;
  canWrite: boolean;
  selectedPlayerId?: string;
  onRefresh: () => void | Promise<void>;
};

export function InventoryPanel({ probe, loading, canWrite, selectedPlayerId, onRefresh }: InventoryPanelProps) {
  const inventories = probe?.inventories ?? [];
  const [selectedInventoryKey, setSelectedInventoryKey] = useState("");
  const [selectedContainer, setSelectedContainer] = useState("all");
  const [query, setQuery] = useState("");
  const [showEmpty, setShowEmpty] = useState(false);
  const [page, setPage] = useState(1);
  const [draftQuantities, setDraftQuantities] = useState<Record<string, string>>({});
  const [reason, setReason] = useState("网页控制台修改物品数量");
  const [dryRun, setDryRun] = useState(true);
  const [submittingKey, setSubmittingKey] = useState<string>();
  const [feedback, setFeedback] = useState<{
    kind: "success" | "error";
    text: string;
  }>();

  useEffect(() => {
    if (inventories.length === 0) {
      setSelectedInventoryKey("");
      return;
    }
    const stillExists = inventories.some(
      (inventory) => inventoryKey(inventory) === selectedInventoryKey
    );
    if (!stillExists) {
      setSelectedInventoryKey(inventoryKey(inventories[0]));
    }
  }, [inventories, selectedInventoryKey]);

  useEffect(() => {
    if (!selectedPlayerId) return;
    const selected = inventories.find((inventory) =>
      normalizeId(inventory.ownerPlayerUId ?? "") === normalizeId(selectedPlayerId)
    );
    if (selected) setSelectedInventoryKey(inventoryKey(selected));
  }, [inventories, selectedPlayerId]);

  useEffect(() => {
    setPage(1);
  }, [query, selectedContainer, selectedInventoryKey, showEmpty]);

  useEffect(() => {
    setDraftQuantities({});
  }, [probe?.observedAt]);

  const selectedInventory = selectedPlayerId
    ? inventories.find((inventory) =>
        normalizeId(inventory.ownerPlayerUId ?? "") === normalizeId(selectedPlayerId))
    : inventories.find((inventory) => inventoryKey(inventory) === selectedInventoryKey)
      ?? inventories[0];

  const containerStats = useMemo(() => {
    const stats = new Map<string, { total: number; occupied: number }>();
    for (const container of selectedInventory?.containers ?? []) {
      const validSlots = container.slots.filter(
        (slot): slot is NativeInventorySlot => slot !== null
      );
      stats.set(container.kind, {
        total: container.slotCount,
        occupied: validSlots.filter(isOccupied).length
      });
    }
    return stats;
  }, [selectedInventory]);

  const rows = useMemo(() => {
    const result: InventoryRow[] = [];
    const normalizedQuery = query.trim().toLocaleLowerCase();
    for (const container of selectedInventory?.containers ?? []) {
      if (selectedContainer !== "all" && container.kind !== selectedContainer) {
        continue;
      }
      for (const slot of container.slots) {
        if (!slot || (!showEmpty && !isOccupied(slot))) {
          continue;
        }
        const searchable = `${slot.staticItemId} ${container.kind} ${slot.slotIndex}`
          .toLocaleLowerCase();
        if (normalizedQuery && !searchable.includes(normalizedQuery)) {
          continue;
        }
        result.push({
          ...slot,
          containerKind: container.kind,
          containerId: container.containerId
        });
      }
    }
    return result.sort((left, right) =>
      left.containerKind.localeCompare(right.containerKind) ||
      left.slotIndex - right.slotIndex
    );
  }, [query, selectedContainer, selectedInventory, showEmpty]);

  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  const safePage = Math.min(page, totalPages);
  const visibleRows = rows.slice((safePage - 1) * pageSize, safePage * pageSize);
  const occupiedCount = [...containerStats.values()].reduce(
    (total, stat) => total + stat.occupied,
    0
  );
  const totalSlotCount = [...containerStats.values()].reduce(
    (total, stat) => total + stat.total,
    0
  );

  async function updateQuantity(row: InventoryRow) {
    const key = rowKey(row);
    const quantity = Number(draftQuantities[key] ?? row.stackCount);
    const ownerPlayerId = selectedInventory?.ownerPlayerUId;
    if (!ownerPlayerId || !row.containerId) {
      setFeedback({ kind: "error", text: "玩家或容器 ID 尚未解析，不能写入。" });
      return;
    }
    if (!Number.isInteger(quantity) || quantity < 1 || quantity > 999999) {
      setFeedback({ kind: "error", text: "物品数量必须是 1 到 999999 之间的整数。" });
      return;
    }
    if (quantity === row.stackCount) {
      setFeedback({ kind: "error", text: "请输入与当前数量不同的新数量。" });
      return;
    }
    if (reason.trim().length < 3) {
      setFeedback({ kind: "error", text: "修改原因至少需要 3 个字符。" });
      return;
    }

    setSubmittingKey(key);
    setFeedback(undefined);
    try {
      const result: InventoryMutationResult = await setInventoryQuantity({
        ownerPlayerId,
        containerId: row.containerId,
        containerKind: row.containerKind,
        slotIndex: row.slotIndex,
        itemId: row.staticItemId,
        expectedQuantity: row.stackCount,
        quantity,
        reason: reason.trim(),
        dryRun
      });
      if (result.dryRun) {
        setFeedback({
          kind: "success",
          text: `预演通过：${row.staticItemId} 将从 ${row.stackCount} 调整为 ${quantity}，未写入游戏。`
        });
      } else {
        setFeedback({
          kind: "success",
          text: result.settlement.aggregateVerified
            ? `修改成功：${row.staticItemId} 已更新为 ${quantity}，原生容器结算验证通过。`
            : "修改已提交，但没有收到完整的原生结算状态。"
        });
        await onRefresh();
      }
    } catch (error) {
      setFeedback({
        kind: "error",
        text: error instanceof Error ? error.message : "物品数量修改失败。"
      });
    } finally {
      setSubmittingKey(undefined);
    }
  }

  if (!probe && loading) {
    return <div className="inventory-empty">正在从游戏线程读取背包快照…</div>;
  }

  if (!probe?.mappingReady) {
    return (
      <div className="inventory-empty">
        <strong>背包映射尚未就绪</strong>
        <span>请确认 Native Bridge 已连接，然后重新刷新。</span>
        <button className="ghost-button" onClick={() => void onRefresh()}>重新检测</button>
      </div>
    );
  }

  if (!selectedInventory) {
    return (
      <div className="inventory-empty">
        <strong>{selectedPlayerId ? "当前玩家没有已加载的 Native 背包" : "没有已加载的玩家背包"}</strong>
        <span>为避免误改其他玩家，系统不会回退显示第一份背包；玩家对象载入后再刷新。</span>
        <button className="ghost-button" onClick={() => void onRefresh()}>刷新背包</button>
      </div>
    );
  }

  return (
    <section className="inventory-panel" aria-label="物品列表">
      <div className="inventory-heading">
        <div>
          <p className="eyebrow">GUARDED INVENTORY</p>
          <h3>物品与容器</h3>
          <p>
            玩家 UID：<code>{selectedInventory.ownerPlayerUId ?? "未知"}</code>
          </p>
        </div>
        <div className="inventory-summary">
          <span><strong>{occupiedCount}</strong> 已占用</span>
          <span><strong>{totalSlotCount}</strong> 总槽位</span>
          <button
            className="ghost-button"
            disabled={loading}
            onClick={() => void onRefresh()}
          >
            {loading ? "刷新中…" : "刷新快照"}
          </button>
        </div>
      </div>

      {inventories.length > 1 ? (
        <label className="inventory-player-select">
          背包对象
          <select
            value={selectedInventoryKey}
            onChange={(event) => setSelectedInventoryKey(event.target.value)}
          >
            {inventories.map((inventory) => (
              <option key={inventoryKey(inventory)} value={inventoryKey(inventory)}>
                {inventory.ownerPlayerUId ?? inventory.objectName}
              </option>
            ))}
          </select>
        </label>
      ) : null}

      <div className="container-filter" aria-label="背包容器">
        <button
          className={selectedContainer === "all" ? "active" : ""}
          onClick={() => setSelectedContainer("all")}
        >
          全部 <em>{occupiedCount}</em>
        </button>
        {(selectedInventory.containers ?? []).map((container) => {
          const stat = containerStats.get(container.kind);
          return (
            <button
              className={selectedContainer === container.kind ? "active" : ""}
              key={container.kind}
              onClick={() => setSelectedContainer(container.kind)}
              title={container.containerId ?? "容器尚未解析"}
            >
              {containerLabels[container.kind] ?? container.kind}
              <em>{stat?.occupied ?? 0}/{stat?.total ?? 0}</em>
            </button>
          );
        })}
      </div>

      <div className="inventory-toolbar">
        <label className="inventory-search">
          <span>⌕</span>
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="搜索物品 ID、容器或槽位"
          />
        </label>
        <label className="empty-toggle">
          <input
            type="checkbox"
            checked={showEmpty}
            onChange={(event) => setShowEmpty(event.target.checked)}
          />
          显示空槽
        </label>
        <span className="result-count">{rows.length} 条结果</span>
      </div>

      <div className="inventory-mutation-bar">
        <label>
          修改原因
          <input
            value={reason}
            maxLength={120}
            onChange={(event) => setReason(event.target.value)}
            disabled={!canWrite || Boolean(submittingKey)}
          />
        </label>
        <label className="empty-toggle">
          <input
            type="checkbox"
            checked={dryRun}
            onChange={(event) => setDryRun(event.target.checked)}
            disabled={!canWrite || Boolean(submittingKey)}
          />
          仅预演，不写入
        </label>
        <span>普通背包、丢弃栏和食物栏可改；装备类容器只读。</span>
      </div>

      {feedback ? (
        <div className={`pal-feedback ${feedback.kind}`}>{feedback.text}</div>
      ) : null}

      <div className="inventory-table-wrap">
        <table className="inventory-table">
          <thead>
            <tr>
              <th>容器</th>
              <th>槽位</th>
              <th>物品 ID</th>
              <th>数量</th>
              <th>状态</th>
              <th>操作</th>
            </tr>
          </thead>
          <tbody>
            {visibleRows.map((row) => (
              <tr key={`${row.containerKind}-${row.slotIndex}`}>
                <td>
                  <span className="container-tag">
                    {containerLabels[row.containerKind] ?? row.containerKind}
                  </span>
                </td>
                <td><code>#{row.slotIndex}</code></td>
                <td>
                  <strong>{isOccupied(row) ? row.staticItemId : "空槽"}</strong>
                </td>
                <td>
                  {isOccupied(row) ? (
                    <input
                      className="quantity-input"
                      type="number"
                      min={1}
                      max={999999}
                      step={1}
                      value={draftQuantities[rowKey(row)] ?? String(row.stackCount)}
                      onChange={(event) => setDraftQuantities((current) => ({
                        ...current,
                        [rowKey(row)]: event.target.value
                      }))}
                      disabled={!canWrite || !isWritableRow(row) || Boolean(submittingKey)}
                      aria-label={`${row.staticItemId} 数量`}
                    />
                  ) : "—"}
                </td>
                <td>
                  <span className={isOccupied(row) ? "slot-state occupied" : "slot-state"}>
                    {isOccupied(row) ? "已占用" : "空闲"}
                  </span>
                </td>
                <td>
                  <button
                    className={dryRun ? "ghost-button compact-button" : "primary-button compact-button"}
                    disabled={
                      !canWrite || !isWritableRow(row) ||
                      Boolean(submittingKey) ||
                      (draftQuantities[rowKey(row)] ?? String(row.stackCount)) === String(row.stackCount)
                    }
                    onClick={() => void updateQuantity(row)}
                  >
                    {submittingKey === rowKey(row)
                      ? "处理中…"
                      : dryRun ? "预演" : "写入"}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {visibleRows.length === 0 ? (
          <div className="inventory-table-empty">
            {showEmpty ? "没有符合条件的槽位" : "当前容器没有已占用物品，可开启“显示空槽”查看结构"}
          </div>
        ) : null}
      </div>

      <footer className="inventory-pagination">
        <span>第 {safePage} / {totalPages} 页</span>
        <div>
          <button
            disabled={safePage <= 1}
            onClick={() => setPage((current) => Math.max(1, current - 1))}
          >上一页</button>
          <button
            disabled={safePage >= totalPages}
            onClick={() => setPage((current) => Math.min(totalPages, current + 1))}
          >下一页</button>
        </div>
      </footer>
    </section>
  );
}

function inventoryKey(inventory: NativeInventoryProbe["inventories"][number]) {
  return inventory.ownerPlayerUId ?? inventory.objectName;
}

function normalizeId(value: string) {
  return value.replace(/[^a-zA-Z0-9]/g, "").toLocaleLowerCase();
}

function isOccupied(slot: NativeInventorySlot) {
  return slot.stackCount > 0 && slot.staticItemId !== "None";
}

function rowKey(row: InventoryRow) {
  return `${row.containerKind}:${row.slotIndex}:${row.staticItemId}`;
}

function isWritableRow(row: InventoryRow) {
  return isOccupied(row) && row.containerId !== null &&
    ["common", "dropSlot", "food"].includes(row.containerKind);
}
