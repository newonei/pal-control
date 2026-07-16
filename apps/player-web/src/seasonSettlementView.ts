import type { SeasonSettlement, SeasonSettlementBoard } from "./api";

export type SettlementTone = "success" | "warning" | "muted" | "danger";

export type SettlementStatePresentation = {
  label: string;
  detail: string;
  tone: SettlementTone;
};

export function participationReasonLabel(reasonCode: string) {
  return ({
    "frozen-contribution-recorded": "已计入冻结周榜",
    "no-frozen-contribution": "截止前没有可计入周榜的贡献",
    "identity-banned-at-freeze": "冻结时账号处于封禁状态，成绩保留但不参与排名",
    "manual-exclusion-at-freeze": "冻结前经审核排除，成绩保留但不参与排名"
  } as Record<string, string>)[reasonCode] ?? "周榜参与状态以服务器冻结记录为准";
}

export function boardReasonLabel(board: SeasonSettlementBoard) {
  return ({
    eligible: "达到最低贡献，已进入排名",
    "no-frozen-contribution": "截止前没有该榜单的有效贡献",
    "below-minimum-settled-exchanges": "有效兑换次数未达到最低要求",
    "below-minimum-resource-value": "资源兑换价值未达到最低要求",
    "below-minimum-task-points": "任务积分未达到最低要求",
    "identity-banned-at-freeze": "冻结时账号封禁，未进入排名",
    "manual-exclusion-at-freeze": "冻结前经审核排除，未进入排名"
  } as Record<string, string>)[board.reasonCode] ?? "未进入排名";
}

export function voucherExpiryPresentation(
  expiry: SeasonSettlement["voucherExpiry"]
): SettlementStatePresentation {
  if (expiry.itemState === "expired" && expiry.ledgerRecorded) {
    return {
      label: "周券已过期",
      detail: `账本已确认清除 ${expiry.expiredAmount.toLocaleString("zh-CN")} 张周战备券。`,
      tone: "success"
    };
  }
  if (expiry.itemState === "pending") {
    return {
      label: expiry.jobState === "running" ? "周券过期处理中" : "周券待过期",
      detail: `已冻结待清除数量 ${expiry.scheduledAmount.toLocaleString("zh-CN")}，尚未写入账本。`,
      tone: "warning"
    };
  }
  if (expiry.itemState === "not-applicable") {
    return {
      label: "没有待过期周券",
      detail: "结算任务已检查，本周没有需要清除的周战备券。",
      tone: "muted"
    };
  }
  return {
    label: "周券过期任务未准备",
    detail: "排行榜已经冻结，运营结算任务尚未生成。",
    tone: "warning"
  };
}

export function permanentRewardPresentation(
  reward: SeasonSettlement["permanentRewards"][number]
): SettlementStatePresentation {
  if (reward.decisionState === "cancelled" || reward.deliveryState === "cancelled") {
    return {
      label: "奖励已取消",
      detail: rewardCancellationReason(reward.reasonCode),
      tone: "danger"
    };
  }
  if (reward.deliveryState === "paid" && reward.ledgerRecorded) {
    return {
      label: "永久币已发放",
      detail: "权威钱包账本已记录，本奖励跨周永久保留。",
      tone: "success"
    };
  }
  return {
    label: "永久币待发放",
    detail: "奖励决定已经固化，正在等待结算任务写入钱包账本。",
    tone: "warning"
  };
}

export function rewardBoardLabel(board: string, source: string) {
  if (source === "supplement" || board === "manual") return "人工补发";
  if (board === "resource-value") return "资源价值榜";
  if (board === "task-points") return "任务积分榜";
  return "周榜奖励";
}

function rewardCancellationReason(reasonCode: string | null) {
  return ({
    "identity-banned-before-reward": "发奖前账号被封禁，冻结名次不变，本次奖励取消。",
    "manual-reward-cancellation": "发奖前经审核取消，冻结名次不变。"
  } as Record<string, string>)[reasonCode ?? ""] ?? "奖励决定已取消，冻结名次不变。";
}
