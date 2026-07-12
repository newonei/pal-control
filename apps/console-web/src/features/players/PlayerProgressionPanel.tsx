import { useCallback, useEffect, useMemo, useState } from "react";
import {
  getPlayerProgression,
  mutatePlayerProgression,
  type PlayerProgression,
  type PlayerProgressionMutationResult,
  type PlayerProgressionPatch
} from "../../lib/api/client";

type Operation =
  | "addExperience"
  | "grantStatusPoints"
  | "grantTechnologyPoints"
  | "grantAncientTechnologyPoints"
  | "allocateStatusPoints";

const statusOptions = [
  ["StatusName_AddMaxHP", "生命值"],
  ["StatusName_AddMaxSP", "耐力"],
  ["StatusName_AddPower", "攻击力"],
  ["StatusName_AddMaxInventoryWeight", "负重"],
  ["StatusName_AddWorkSpeed", "工作速度"],
  ["StatusName_AddCaptureLevel", "捕获等级"]
] as const;

const operationLabels: Record<Operation, string> = {
  addExperience: "增加经验",
  grantStatusPoints: "发放未分配属性点",
  grantTechnologyPoints: "发放科技点",
  grantAncientTechnologyPoints: "发放古代科技点",
  allocateStatusPoints: "分配属性点"
};

export function PlayerProgressionPanel({
  playerId,
  canWrite
}: {
  playerId?: string;
  canWrite: boolean;
}) {
  const [progression, setProgression] = useState<PlayerProgression | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [operation, setOperation] = useState<Operation>("addExperience");
  const [value, setValue] = useState(1);
  const [statusId, setStatusId] = useState<string>(statusOptions[0][0]);
  const [reason, setReason] = useState("开发服务器玩家成长调整");
  const [submitting, setSubmitting] = useState(false);
  const [preview, setPreview] = useState<PlayerProgressionMutationResult | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    if (!playerId) {
      setProgression(null);
      return;
    }
    setLoading(true);
    setError("");
    try {
      setProgression(await getPlayerProgression(playerId, signal));
    } catch (nextError) {
      if (!signal?.aborted) {
        setProgression(null);
        setError(nextError instanceof Error ? nextError.message : "玩家成长数据读取失败");
      }
    } finally {
      if (!signal?.aborted) setLoading(false);
    }
  }, [playerId]);

  useEffect(() => {
    const controller = new AbortController();
    setPreview(null);
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  const statusRanks = useMemo(
    () => new Map((progression?.statusPoints ?? []).map((item) => [item.id, item.rank])),
    [progression]
  );

  function buildPatch(): PlayerProgressionPatch {
    switch (operation) {
      case "addExperience": return { addExperience: value };
      case "grantStatusPoints": return { grantStatusPoints: value };
      case "grantTechnologyPoints": return { grantTechnologyPoints: value };
      case "grantAncientTechnologyPoints": return { grantAncientTechnologyPoints: value };
      case "allocateStatusPoints": return {
        allocateStatusId: statusId,
        allocateStatusPoints: value
      };
    }
  }

  async function submit(dryRun: boolean) {
    if (!playerId || !progression) return;
    setSubmitting(true);
    setError("");
    try {
      const result = await mutatePlayerProgression({
        playerId,
        expectedRevision: progression.revision,
        reason: reason.trim(),
        dryRun,
        patch: buildPatch()
      });
      if (dryRun) {
        setPreview(result);
      } else {
        setPreview(null);
        await load();
      }
    } catch (nextError) {
      setPreview(null);
      setError(nextError instanceof Error ? nextError.message : "玩家成长修改失败");
    } finally {
      setSubmitting(false);
    }
  }

  if (!playerId) {
    return <div className="progression-empty">请先选择一个在线玩家。</div>;
  }
  if (loading && !progression) {
    return <div className="progression-empty">正在读取游戏原生成长数据…</div>;
  }
  if (!progression) {
    return (
      <div className="progression-empty">
        <strong>玩家成长对象尚未加载</strong>
        <p>{error || "玩家需要进入服务器并完成角色加载。"}</p>
        <button className="ghost-button" onClick={() => void load()}>重新读取</button>
      </div>
    );
  }

  const writeReady = canWrite && progression.online;
  return (
    <section className="progression-panel" aria-label="玩家成长">
      <div className="progression-toolbar">
        <div>
          <p className="eyebrow">NATIVE PLAYER PROGRESSION</p>
          <h3>等级、属性与科技点</h3>
          <small>{progression.playerUId} · revision {progression.revision}</small>
        </div>
        <button className="ghost-button" disabled={loading} onClick={() => void load()}>
          {loading ? "刷新中…" : "刷新"}
        </button>
      </div>

      <div className="progression-metrics">
        <ProgressMetric label="等级" value={`Lv.${progression.level}`} />
        <ProgressMetric label="总经验" value={formatNumber(progression.totalExperience)} />
        <ProgressMetric
          label="距下一级"
          value={progression.experienceToNextLevel === null
            ? "--"
            : formatNumber(progression.experienceToNextLevel)}
        />
        <ProgressMetric label="未分配属性点" value={String(progression.unusedStatusPoints)} />
        <ProgressMetric label="科技点" value={String(progression.technologyPoints ?? "--")} />
        <ProgressMetric label="古代科技点" value={String(progression.ancientTechnologyPoints ?? "--")} />
      </div>

      <div className="status-rank-grid">
        {statusOptions.map(([id, label]) => (
          <div className="status-rank-card" key={id}>
            <span>{label}</span>
            <strong>{statusRanks.get(id) ?? 0}</strong>
            <code>{id}</code>
          </div>
        ))}
      </div>

      <div className="progression-editor">
        <div className="progression-editor-heading">
          <div>
            <h4>受控原生修改</h4>
            <p>先预演，再确认执行；每次请求只允许一种操作。</p>
          </div>
          <span className={writeReady ? "write-ready" : "write-locked"}>
            {writeReady ? "在线可写" : "需要玩家在线"}
          </span>
        </div>

        <div className="progression-form-grid">
          <label>
            <span>操作</span>
            <select value={operation} onChange={(event) => {
              setOperation(event.target.value as Operation);
              setPreview(null);
              setValue(1);
            }}>
              {(Object.keys(operationLabels) as Operation[]).map((key) => (
                <option key={key} value={key}>{operationLabels[key]}</option>
              ))}
            </select>
          </label>
          {operation === "allocateStatusPoints" ? (
            <label>
              <span>属性</span>
              <select value={statusId} onChange={(event) => setStatusId(event.target.value)}>
                {statusOptions.map(([id, label]) => (
                  <option key={id} value={id}>{label}</option>
                ))}
              </select>
            </label>
          ) : null}
          <label>
            <span>数量</span>
            <input
              min={1}
              max={10_000_000}
              type="number"
              value={value}
              onChange={(event) => {
                setValue(Math.max(1, Number(event.target.value) || 1));
                setPreview(null);
              }}
            />
          </label>
          <label className="progression-reason">
            <span>修改原因</span>
            <input maxLength={160} value={reason} onChange={(event) => setReason(event.target.value)} />
          </label>
        </div>

        {error ? <div className="progression-error">{error}</div> : null}
        {preview ? (
          <div className="progression-preview">
            <div>
              <strong>预演通过：{operationLabels[operation]}</strong>
              <small>{preview.nativeFunction}</small>
            </div>
            <code>{JSON.stringify(preview.preview ?? preview.before ?? {}, null, 2)}</code>
          </div>
        ) : null}

        <div className="progression-actions">
          <button
            className="ghost-button"
            disabled={!writeReady || submitting || !reason.trim()}
            onClick={() => void submit(true)}
          >{submitting ? "处理中…" : "预演修改"}</button>
          <button
            className="primary-button"
            disabled={!writeReady || submitting || !preview || !reason.trim()}
            onClick={() => void submit(false)}
          >确认执行</button>
        </div>
      </div>
    </section>
  );
}

function ProgressMetric({ label, value }: { label: string; value: string }) {
  return <div><span>{label}</span><strong>{value}</strong></div>;
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat("zh-CN").format(value);
}
