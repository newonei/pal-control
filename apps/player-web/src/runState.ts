import type { ExtractionRun } from "./api";

const labels: Record<ExtractionRun["state"], string> = {
  preparing: "待确认",
  deployed: "结算中",
  extracted: "已兑换",
  settled: "已兑换",
  failed: "兑换失败",
  uncertain: "待人工核对",
  cancelled: "已取消/过期"
};

export function resourceExchangeStateLabel(state: ExtractionRun["state"]): string {
  return labels[state] ?? state;
}
