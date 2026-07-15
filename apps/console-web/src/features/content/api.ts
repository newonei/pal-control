import { adminFetch, adminHighRiskFetch } from "../../lib/api/adminFetch";

export const PUBLISH_CONFIRMATION = "PUBLISH ECONOMY CONTENT";
export const ROLLBACK_CONFIRMATION = "ROLLBACK ECONOMY CONTENT";

export type ContentDefinition = Record<string, unknown>;

export type ContentValidationIssue = {
  code: string;
  path: string;
  message: string;
};

export type ContentValidationResult = {
  valid: boolean;
  contentHash: string;
  errors: ContentValidationIssue[];
  warnings: ContentValidationIssue[];
  validatedAt: string;
};

export type EconomyContentDraft = {
  draftId: string;
  serverId: string;
  name: string;
  state: string;
  basedOnVersionId: string | null;
  revision: number;
  contentHash: string;
  definition: ContentDefinition;
  lastValidation: ContentValidationResult | null;
  publishedVersionId: string | null;
  createdBy: string;
  updatedBy: string;
  createdAt: string;
  updatedAt: string;
};

export type EconomyContentVersion = {
  versionId: string;
  serverId: string;
  versionNumber: number;
  businessDate: string;
  rulesVersion: string;
  contentHash: string;
  definition: ContentDefinition;
  sourceDraftId: string;
  publishedBy: string;
  publishedAt: string;
};

export type EconomyContentPointer = {
  serverId: string;
  versionId: string;
  versionNumber: number;
  businessDate: string;
  rulesVersion: string;
  contentHash: string;
  updatedAt: string;
};

export type EconomyContentCurrent = {
  pointer: EconomyContentPointer;
  version: EconomyContentVersion;
  rotation: {
    seed?: string;
    algorithmVersion?: number;
    currentContentVersionId?: string;
    [key: string]: unknown;
  };
};

export type ContentDiffEntry = {
  path: string;
  kind: string;
  before: string | null;
  after: string | null;
};

export type ContentDiffResult = {
  draftId: string;
  revision: number;
  items: ContentDiffEntry[];
};

export type ContentPublishResult = {
  version: EconomyContentVersion;
  pointer: EconomyContentPointer;
  versionCreated: boolean;
  pointerChanged: boolean;
  replayed: boolean;
};

export type ContentRollbackResult = {
  pointer: EconomyContentPointer;
  previousVersionId: string;
  pointerChanged: boolean;
  replayed: boolean;
};

export class ContentApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly code: string,
    readonly validation: ContentValidationResult | null = null
  ) {
    super(message);
    this.name = "ContentApiError";
  }
}

export async function getCurrentContent(
  serverId: string,
  signal?: AbortSignal
): Promise<EconomyContentCurrent | null> {
  const response = await adminFetch(contentUrl(serverId, "/current"), {
    signal,
    cache: "no-store"
  });
  if (response.status === 404) {
    const problem = await readProblem(response);
    if (problem.code === "CONTENT_NOT_PUBLISHED") return null;
    throw problem;
  }
  return readJson<EconomyContentCurrent>(response, "当前内容版本读取失败");
}

export async function listContentVersions(
  serverId: string,
  signal?: AbortSignal
): Promise<EconomyContentVersion[]> {
  const response = await adminFetch(contentUrl(serverId, "/versions"), {
    signal,
    cache: "no-store"
  });
  return (await readJson<{ items: EconomyContentVersion[] }>(
    response,
    "内容版本列表读取失败"
  )).items;
}

export async function listContentDrafts(
  serverId: string,
  signal?: AbortSignal
): Promise<EconomyContentDraft[]> {
  const response = await adminFetch(contentUrl(serverId, "/drafts"), {
    signal,
    cache: "no-store"
  });
  return (await readJson<{ items: EconomyContentDraft[] }>(
    response,
    "内容草稿列表读取失败"
  )).items;
}

export async function getContentDraft(
  serverId: string,
  draftId: string,
  signal?: AbortSignal
): Promise<EconomyContentDraft> {
  const response = await adminFetch(contentUrl(serverId, `/drafts/${encodeURIComponent(draftId)}`), {
    signal,
    cache: "no-store"
  });
  return readJson<EconomyContentDraft>(response, "内容草稿读取失败");
}

export async function createContentDraft(
  serverId: string,
  input: {
    name: string;
    basedOnVersionId: string | null;
    definition: ContentDefinition;
  }
): Promise<EconomyContentDraft> {
  const response = await adminFetch(contentUrl(serverId, "/drafts"), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(input)
  });
  return readJson<EconomyContentDraft>(response, "内容草稿创建失败");
}

export async function updateContentDraft(
  serverId: string,
  draftId: string,
  revision: number,
  definition: ContentDefinition
): Promise<EconomyContentDraft> {
  const response = await adminFetch(contentUrl(serverId, `/drafts/${encodeURIComponent(draftId)}`), {
    method: "PUT",
    headers: {
      "Content-Type": "application/json",
      "If-Match": String(revision)
    },
    body: JSON.stringify({ definition })
  });
  return readJson<EconomyContentDraft>(response, "内容草稿保存失败");
}

export async function getContentDraftDiff(
  serverId: string,
  draftId: string,
  signal?: AbortSignal
): Promise<ContentDiffResult> {
  const response = await adminFetch(
    contentUrl(serverId, `/drafts/${encodeURIComponent(draftId)}/diff`),
    { signal, cache: "no-store" }
  );
  return readJson<ContentDiffResult>(response, "内容差异读取失败");
}

export async function validateContentDraft(
  serverId: string,
  draftId: string,
  revision: number
): Promise<ContentValidationResult> {
  const response = await adminFetch(
    contentUrl(serverId, `/drafts/${encodeURIComponent(draftId)}/validate`),
    {
      method: "POST",
      headers: { "If-Match": String(revision) }
    }
  );
  return readJson<ContentValidationResult>(response, "内容草稿校验失败");
}

export async function publishContentDraft(
  serverId: string,
  input: {
    draftId: string;
    revision: number;
    businessDate: string;
    reason: string;
    confirmation: string;
    totp: string;
    idempotencyKey: string;
  }
): Promise<ContentPublishResult> {
  const response = await adminHighRiskFetch(
    contentUrl(serverId, `/drafts/${encodeURIComponent(input.draftId)}/publish`),
    {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "If-Match": String(input.revision),
        "Idempotency-Key": input.idempotencyKey
      },
      body: JSON.stringify({
        businessDate: input.businessDate,
        reason: input.reason,
        confirmation: input.confirmation
      })
    },
    { totp: input.totp, reason: input.reason }
  );
  return readJson<ContentPublishResult>(response, "内容发布失败");
}

export async function rollbackContentVersion(
  serverId: string,
  input: {
    targetVersionId: string | null;
    expectedCurrentVersionId: string;
    reason: string;
    confirmation: string;
    totp: string;
    idempotencyKey: string;
  }
): Promise<ContentRollbackResult> {
  const response = await adminHighRiskFetch(contentUrl(serverId, "/rollback"), {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "Idempotency-Key": input.idempotencyKey
    },
    body: JSON.stringify({
      targetVersionId: input.targetVersionId,
      expectedCurrentVersionId: input.expectedCurrentVersionId,
      reason: input.reason,
      confirmation: input.confirmation
    })
  }, { totp: input.totp, reason: input.reason });
  return readJson<ContentRollbackResult>(response, "内容回滚失败");
}

export function contentErrorMessage(error: unknown, fallback: string): string {
  if (!(error instanceof ContentApiError)) {
    return error instanceof Error && error.name !== "AbortError" && error.message
      ? error.message
      : fallback;
  }
  const guidance: Record<string, string> = {
    CONTENT_DRAFT_REVISION_CONFLICT: "草稿已被其他管理员更新。保留当前本地 JSON，然后重新加载服务端 revision 再合并。",
    CONTENT_MAINTENANCE_REQUIRED: "发布或回滚前，必须先进入经济维护模式并等待玩家操作全部排空。",
    CONTENT_SETTLEMENT_DRAIN_REQUIRED: "仍有未终结的报价或结算，请等待其完成后重试。",
    CONTENT_VALIDATION_FAILED: "草稿未通过服务端校验，请按错误路径修正后重新校验。",
    CONTENT_BUSINESS_DATE_MISMATCH: "发布日期必须等于服务端当前营业日，请修正日期后重试。",
    CONTENT_CONFIRMATION_REQUIRED: "确认短语不匹配，请完整输入页面给出的英文短语。",
    CONTENT_REASON_REQUIRED: "审计原因需要 8 到 512 个字符。",
    IDEMPOTENCY_CONFLICT: "该幂等键已用于不同请求。请修改操作参数后重新提交。",
    HTTP_403: "权限不足、TOTP 已过期或审计原因未通过高风险策略，请确认账号角色并输入新的 6 位验证码。"
  };
  return guidance[error.code] ?? `${error.message}（${error.code}）`;
}

async function readJson<T>(response: Response, fallback: string): Promise<T> {
  if (!response.ok) throw await readProblem(response, fallback);
  try {
    return await response.json() as T;
  } catch {
    throw new ContentApiError(`${fallback}：响应不是有效 JSON。`, response.status, "CONTENT_RESPONSE_INVALID");
  }
}

async function readProblem(
  response: Response,
  fallback = `内容请求失败（HTTP ${response.status}）`
): Promise<ContentApiError> {
  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    return new ContentApiError(fallback, response.status, `HTTP_${response.status}`);
  }
  const root = asRecord(payload);
  const nested = asRecord(root?.error);
  const problem = nested ?? root;
  const code = stringValue(problem?.code) || `HTTP_${response.status}`;
  const message = stringValue(problem?.message) || stringValue(problem?.detail) || fallback;
  const validation = parseValidation(root?.validation);
  return new ContentApiError(message, response.status, code, validation);
}

function parseValidation(value: unknown): ContentValidationResult | null {
  const record = asRecord(value);
  if (!record || typeof record.valid !== "boolean") return null;
  return {
    valid: record.valid,
    contentHash: stringValue(record.contentHash),
    errors: parseIssues(record.errors),
    warnings: parseIssues(record.warnings),
    validatedAt: stringValue(record.validatedAt)
  };
}

function parseIssues(value: unknown): ContentValidationIssue[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((item) => {
    const issue = asRecord(item);
    if (!issue) return [];
    return [{
      code: stringValue(issue.code),
      path: stringValue(issue.path),
      message: stringValue(issue.message)
    }];
  });
}

function contentUrl(serverId: string, suffix: string): string {
  return `/api/v1/servers/${encodeURIComponent(serverId)}/economy-content${suffix}`;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? value as Record<string, unknown>
    : null;
}

function stringValue(value: unknown): string {
  return typeof value === "string" ? value : "";
}
