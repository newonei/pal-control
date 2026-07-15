import type { ExtractionRun, Order } from "./api";

export const ECONOMY_POLL_INTERVAL_MS = 3_000;

const orderStatesAwaitingUpdate = new Set<Order["state"]>([
  "accepted",
  "pending",
  "delivering",
  // A definite delivery failure is followed by the durable refund worker.
  "failed"
]);

const runStatesAwaitingUpdate = new Set<ExtractionRun["state"]>([
  "preparing",
  "deployed"
]);

export function hasPendingEconomyActivity(
  orders: readonly Order[],
  runs: readonly ExtractionRun[]
): boolean {
  return orders.some((order) => orderStatesAwaitingUpdate.has(order.state)) ||
    runs.some((run) => runStatesAwaitingUpdate.has(run.state));
}

export function quoteSecondsRemaining(expiresAt: string, nowMs = Date.now()): number {
  const expiresAtMs = Date.parse(expiresAt);
  if (!Number.isFinite(expiresAtMs) || !Number.isFinite(nowMs)) return 0;
  return Math.max(0, Math.ceil((expiresAtMs - nowMs) / 1_000));
}

export function formatQuoteCountdown(seconds: number): string {
  const safeSeconds = Number.isFinite(seconds) ? Math.max(0, Math.ceil(seconds)) : 0;
  if (safeSeconds === 0) return "已过期";
  const minutes = Math.floor(safeSeconds / 60);
  const remainder = safeSeconds % 60;
  return minutes > 0
    ? `${minutes} 分 ${String(remainder).padStart(2, "0")} 秒`
    : `${remainder} 秒`;
}

export type PlayerErrorPresentation = {
  message: string;
  nextStep: string;
};

type ErrorShape = {
  message?: unknown;
  status?: unknown;
  code?: unknown;
};

export function describePlayerError(reason: unknown): PlayerErrorPresentation {
  const error = reason && typeof reason === "object" ? reason as ErrorShape : null;
  const message = typeof error?.message === "string" && error.message.trim()
    ? error.message
    : "数据加载失败";
  const status = typeof error?.status === "number" ? error.status : 0;
  const code = typeof error?.code === "string" ? error.code.toUpperCase() : "";

  if (code === "PLAYER_NOT_ONLINE") {
    return { message, nextStep: "请先进入本周游戏世界并等待角色加载完成，再刷新页面后重试。" };
  }
  if (code.includes("BINDING") || code.includes("WORLD_MISMATCH")) {
    return { message, nextStep: "当前周角色绑定可能已经失效。请安全退出玩家门户，再重新登录并绑定本周角色。" };
  }
  if (code.includes("MAINTENANCE") || code.includes("CIRCUIT_OPEN") ||
      code.includes("SEASON_WORLD_UNBOUND") || status === 423) {
    return { message, nextStep: "当前只读资料仍可查看；请勿重复提交交易，等待管理员恢复后点击“刷新数据”。" };
  }
  if (code.includes("QUEUE") || code.includes("BACKLOG") ||
      code === "PLAYER_SETTLEMENT_IN_PROGRESS" || status === 429) {
    return { message, nextStep: "服务器正在处理其他经济请求。请保留当前页面，稍后刷新状态，不要连续点击提交。" };
  }
  if (code.includes("VERSION") || code.includes("CAPABILITY") ||
      code.includes("ADAPTER_NOT_CONNECTED") || code.includes("DEPENDENCY_PROBE")) {
    return { message, nextStep: "服务器组件未通过安全检查。请勿继续交易，等待管理员修复版本或连接状态。" };
  }
  if (code === "QUOTE_EXPIRED" || code === "QUOTE_REPLACED" ||
      code === "QUOTE_CONTENT_CHANGED" ||
      code === "EXTRACTION_QUOTE_NOT_SETTLEABLE") {
    return { message, nextStep: "旧报价不会扣除物品。关闭窗口后重新扫描背包即可取得新报价。" };
  }
  if (code === "OFFER_NOT_AVAILABLE" || code === "OFFER_EVIDENCE_REQUIRED") {
    return { message, nextStep: "商城内容已经轮换。请刷新商城并重新确认当前价格、库存和限购后再购买。" };
  }
  if (code === "GLOBAL_STOCK_EXCEEDED") {
    return { message, nextStep: "该商品的服务器库存已经售罄。请刷新商城选择其他仍有库存的商品。" };
  }
  if (code === "NO_SELLABLE_EXTRACTION_LOOT") {
    return { message, nextStep: "请把白名单资源放在允许扫描的背包容器中，再重新扫描。" };
  }
  if (code === "EXTRACTION_ZONE_CLOSED") {
    return { message, nextStep: "当前兑换点尚未开放。请在地图中查看下次开放时间，前往其他开放兑换点或稍后再来。" };
  }
  if (code.includes("OUTSIDE_EXTRACTION_ZONE") || code.includes("ZONE_NOT_STABLE")) {
    return { message, nextStep: "请停留在开放兑换点范围内，等待位置稳定后再扫描。" };
  }
  if (status >= 500 || code.includes("STORE_NOT_") || code.includes("STORE_UNAVAILABLE")) {
    return { message, nextStep: "本次请求没有确认成功。请先查看订单或兑换记录，确认没有处理中记录后再重试。" };
  }

  return { message, nextStep: "请先刷新当前状态；若问题持续出现，请把错误信息和发生时间提供给管理员。" };
}

export function orderStateGuidance(state: Order["state"]): string | null {
  switch (state) {
    case "failed":
      return "发货已明确失败，系统正在处理退款；请等待状态更新。";
    case "uncertain":
      return "发货结果待管理员核对，请勿重复购买；系统不会自动重发或退款。";
    case "partial":
      return "已有部分物品确认到账，请勿重复购买；管理员将按回执核对差额。";
    case "cancelled":
      return "订单已取消；请在资金流水中核对是否发生过扣款或退款。";
    case "refunded":
      return "款项已退回，请在资金流水中核对到账记录。";
    default:
      return null;
  }
}

export function runStateGuidance(state: ExtractionRun["state"]): string | null {
  switch (state) {
    case "uncertain":
      return "扣物结果待管理员核对，请勿再次提交同一批资源。";
    case "failed":
      return "此次兑换没有入账；若背包数量异常，请联系管理员核对。";
    case "cancelled":
      return "报价已取消或过期，本次兑换不会继续执行。";
    default:
      return null;
  }
}
