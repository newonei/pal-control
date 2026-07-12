import { useEffect, useMemo, useState, type FormEvent } from "react";
import {
  mutatePal,
  type GameCatalogEntry,
  type NativePal,
  type NativePalProbe,
  type PalSkillCatalog
} from "../../lib/api/client";
import { PassiveSkillPicker } from "./PassiveSkillPicker";

type PalPanelProps = {
  probe: NativePalProbe | null | undefined;
  loading: boolean;
  canWrite: boolean;
  palCatalog: Map<string, GameCatalogEntry>;
  skillCatalog: PalSkillCatalog | null | undefined;
  selectedPlayerId?: string;
  onRefresh: () => Promise<void>;
};

export function PalPanel({
  probe,
  loading,
  canWrite,
  palCatalog,
  skillCatalog,
  selectedPlayerId,
  onRefresh
}: PalPanelProps) {
  const [query, setQuery] = useState("");
  const [selectedInstanceId, setSelectedInstanceId] = useState<string>();
  const [nickname, setNickname] = useState("");
  const [favorite, setFavorite] = useState(false);
  const [passiveDrafts, setPassiveDrafts] = useState<string[]>([]);
  const [passivePickerSlot, setPassivePickerSlot] = useState<number>();
  const [activeDrafts, setActiveDrafts] = useState<string[]>(["", "", ""]);
  const [reason, setReason] = useState("网页控制台修改帕鲁");
  const [dryRun, setDryRun] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [feedback, setFeedback] = useState<{ kind: "success" | "error"; text: string }>();

  const ownerFilter = normalizeId(selectedPlayerId ?? "");
  const passiveSkillById = useMemo(
    () => new Map((skillCatalog?.passiveSkills ?? []).map((skill) => [skill.id, skill])),
    [skillCatalog]
  );
  const activeSkillById = useMemo(
    () => new Map((skillCatalog?.activeSkills ?? []).map((skill) => [skill.id, skill])),
    [skillCatalog]
  );
  const playerPals = useMemo(() => {
    const pals = probe?.pals ?? [];
    if (!ownerFilter) {
      return pals;
    }
    return pals.filter((pal) => {
      const owner = normalizeId(pal.ownerPlayerUId);
      return owner === ownerFilter;
    });
  }, [ownerFilter, probe]);

  const filteredPals = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    if (!needle) {
      return playerPals;
    }
    return playerPals.filter((pal) => {
      const catalogEntry = findPalCatalogEntry(palCatalog, pal.characterId);
      return `${catalogEntry?.name ?? ""} ${catalogEntry?.englishName ?? ""} ${catalogEntry?.dex ?? ""} ${pal.characterId} ${pal.nickname} ${pal.instanceId} ${pal.passiveSkills.map((skill) => `${skill} ${passiveSkillById.get(skill)?.name ?? ""}`).join(" ")}`
        .toLocaleLowerCase()
        .includes(needle);
    });
  }, [palCatalog, passiveSkillById, playerPals, query]);

  const selectedPal = playerPals.find((pal) => pal.instanceId === selectedInstanceId)
    ?? filteredPals[0];
  const equipableActiveSkills = selectedPal
    ? [...new Set([
        ...selectedPal.activeSkills.equipped.map((skill) => skill.id),
        ...selectedPal.activeSkills.mastered.map((skill) => skill.id)
      ])]
    : [];

  useEffect(() => {
    if (filteredPals.length === 0) {
      setSelectedInstanceId(undefined);
      return;
    }
    if (!filteredPals.some((pal) => pal.instanceId === selectedInstanceId)) {
      setSelectedInstanceId(filteredPals[0].instanceId);
    }
  }, [filteredPals, selectedInstanceId]);

  useEffect(() => {
    if (!selectedPal) {
      return;
    }
    setNickname(selectedPal.nickname);
    setFavorite(selectedPal.favorite);
    setPassiveDrafts([...selectedPal.passiveSkills]);
    setPassivePickerSlot(undefined);
    setActiveDrafts([
      ...selectedPal.activeSkills.equipped.map((skill) => skill.id),
      "",
      ""
    ].slice(0, 3));
    setFeedback(undefined);
  }, [selectedPal?.instanceId, selectedPal?.revision]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!selectedPal) {
      return;
    }
    const nextNickname = nickname !== selectedPal.nickname ? nickname : null;
    const nextFavorite = favorite !== selectedPal.favorite ? favorite : null;
    const changedPassiveIndexes = passiveDrafts
      .map((skill, index) => skill !== selectedPal.passiveSkills[index] ? index : -1)
      .filter((index) => index >= 0);
    if (changedPassiveIndexes.length > 1) {
      setFeedback({ kind: "error", text: "每次只替换一条被动技能，完成后再修改下一条。" });
      return;
    }
    const passiveIndex = changedPassiveIndexes[0];
    const nextPassive = passiveIndex === undefined ? null : {
      index: passiveIndex,
      expectedSkillId: selectedPal.passiveSkills[passiveIndex],
      skillId: passiveDrafts[passiveIndex].trim()
    };
    const nextActiveSkills = activeDrafts.filter(Boolean);
    const currentActiveSkills = selectedPal.activeSkills.equipped.map((skill) => skill.id);
    const activeChanged = JSON.stringify(nextActiveSkills) !== JSON.stringify(currentActiveSkills);
    if (nextPassive && !skillCatalog?.passiveSkills.some((skill) => skill.id === nextPassive.skillId)) {
      setFeedback({ kind: "error", text: "请选择技能目录中存在的被动技能。" });
      return;
    }
    if (activeChanged && nextActiveSkills.length === 0) {
      setFeedback({ kind: "error", text: "至少保留一个已装备主动技能。" });
      return;
    }
    if (new Set(nextActiveSkills).size !== nextActiveSkills.length) {
      setFeedback({ kind: "error", text: "主动技能槽位不能重复。" });
      return;
    }
    if (nextNickname === null && nextFavorite === null && !nextPassive && !activeChanged) {
      setFeedback({ kind: "error", text: "至少修改昵称、收藏、主动技能或被动技能中的一项。" });
      return;
    }
    if (reason.trim().length < 3) {
      setFeedback({ kind: "error", text: "修改原因至少需要 3 个字符。" });
      return;
    }

    setSubmitting(true);
    setFeedback(undefined);
    try {
      const result = await mutatePal(selectedPal, {
        nickname: nextNickname,
        favorite: nextFavorite,
        passiveSkill: nextPassive,
        equippedActiveSkills: activeChanged ? nextActiveSkills : null,
        reason: reason.trim(),
        dryRun
      });
      if (result.dryRun) {
        setFeedback({
          kind: "success",
          text: `预演通过：将调用 ${result.settlement.function}，未写入任何数据。`
        });
      } else {
        setFeedback({
          kind: "success",
          text: result.settlement.mirrorSynchronized && result.settlement.readBackVerified
            ? `修改成功：${result.settlement.function} 已完成原生镜像结算和回读校验。`
            : "修改已写入，但原生结算状态未完整返回。"
        });
        await onRefresh();
      }
    } catch (error) {
      setFeedback({
        kind: "error",
        text: error instanceof Error ? error.message : "帕鲁修改失败。"
      });
    } finally {
      setSubmitting(false);
    }
  }

  if (loading) {
    return <div className="pal-empty">正在从游戏线程读取 PalBox 与队伍快照…</div>;
  }
  if (!probe) {
    return (
      <div className="pal-empty">
        <strong>帕鲁探针暂时不可用</strong>
        <span>请确认 Native Bridge 已连接后重新刷新。</span>
        <button className="ghost-button" onClick={() => void onRefresh()}>重新读取</button>
      </div>
    );
  }
  if (!probe.mappingReady) {
    return (
      <div className="pal-empty">
        <strong>当前游戏版本的帕鲁字段映射未就绪</strong>
        <span>写入能力保持关闭，避免访问未经验证的运行期字段。</span>
      </div>
    );
  }

  return (
    <section className="pal-panel" aria-label="帕鲁列表">
      <div className="pal-heading">
        <div>
          <p className="eyebrow">LIVE PAL SNAPSHOT</p>
          <h3>帕鲁列表</h3>
          <p>仅显示当前服务端进程中已加载、且能稳定映射到玩家 UID 的帕鲁。</p>
        </div>
        <div className="pal-summary">
          <span><strong>{playerPals.length}</strong> 当前列表</span>
          <span><strong>{probe.parameterObjectCount}</strong> 运行期对象</span>
          <button className="ghost-button" onClick={() => void onRefresh()}>刷新快照</button>
        </div>
      </div>

      <label className="pal-search">
        <span>⌕</span>
        <input
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="搜索帕鲁名称、种类、实例 ID 或被动词条"
        />
      </label>

      {filteredPals.length === 0 ? (
        <div className="pal-empty compact">
          <strong>{playerPals.length === 0 ? "当前玩家没有已加载的帕鲁" : "没有匹配结果"}</strong>
          <span>玩家下线或帕鲁未加载时，原生运行期列表可能为空。</span>
        </div>
      ) : (
        <div className="pal-workspace">
          <div className="pal-list">
            {filteredPals.map((pal) => {
              const catalogEntry = findPalCatalogEntry(palCatalog, pal.characterId);
              return (
                <button
                  className={pal.instanceId === selectedPal?.instanceId ? "pal-row selected" : "pal-row"}
                  key={pal.instanceId}
                  onClick={() => setSelectedInstanceId(pal.instanceId)}
                >
                  <span className="pal-avatar">{pal.rare ? "✦" : "◇"}</span>
                  <span className="pal-row-main">
                    <span className="pal-row-title">
                      <strong>{catalogEntry?.name ?? pal.characterId}</strong>
                      {pal.favorite ? <em>收藏</em> : null}
                    </span>
                    <small>{palIdentityDetails(pal, catalogEntry)} · Lv.{pal.level} · Rank {pal.rank}</small>
                    <span className="pal-passives">
                      {pal.passiveSkills.map((skill) => (
                        <i key={skill} title={skill}>{passiveSkillById.get(skill)?.name ?? skill}</i>
                      ))}
                    </span>
                  </span>
                  <span className="pal-slot">#{pal.location.slotIndex}</span>
                </button>
              );
            })}
          </div>

          {selectedPal ? (
            <form className="pal-editor" onSubmit={submit}>
              <div className="pal-editor-heading">
                <div>
                  <p className="eyebrow">GUARDED MUTATION</p>
                  <h3>{palName(selectedPal, palCatalog)}</h3>
                  <small>{palIdentityDetails(selectedPal, findPalCatalogEntry(palCatalog, selectedPal.characterId))}</small>
                </div>
                <code title={selectedPal.instanceId}>{shortId(selectedPal.instanceId)}</code>
              </div>

              <div className="pal-stats">
                <span><small>等级</small><strong>{selectedPal.level}</strong></span>
                <span><small>浓缩 Rank</small><strong>{selectedPal.rank}</strong></span>
                <span><small>经验</small><strong>{selectedPal.exp.toLocaleString()}</strong></span>
              </div>

              <div className="pal-talents">
                <span>生命 IV <strong>{selectedPal.talents.hp}</strong></span>
                <span>近战 IV <strong>{selectedPal.talents.melee}</strong></span>
                <span>攻击 IV <strong>{selectedPal.talents.shot}</strong></span>
                <span>防御 IV <strong>{selectedPal.talents.defense}</strong></span>
              </div>

              <section className="pal-skill-editor">
                <div className="skill-section-heading">
                  <div>
                    <strong>主动技能</strong>
                    <small>仅允许装备当前已装备或已掌握技能，最多 3 个</small>
                  </div>
                  <em>{selectedPal.activeSkills.mastered.length} 已掌握</em>
                </div>
                <div className="active-skill-slots">
                  {[0, 1, 2].map((index) => (
                    <label key={index}>
                      槽位 {index + 1}
                      <select
                        value={activeDrafts[index] ?? ""}
                        onChange={(event) => setActiveDrafts((current) => {
                          const next = [...current];
                          next[index] = event.target.value;
                          return next;
                        })}
                        disabled={!canWrite || submitting || !skillCatalog}
                      >
                        <option value="">不装备</option>
                        {equipableActiveSkills.map((skillId) => (
                          <option value={skillId} key={skillId}>
                            {activeSkillById.get(skillId)?.name ?? skillId}
                          </option>
                        ))}
                      </select>
                    </label>
                  ))}
                </div>
                <div className="mastered-skill-list">
                  {selectedPal.activeSkills.mastered.length > 0
                    ? selectedPal.activeSkills.mastered.map((skill) => (
                        <i key={skill.id} title={skill.id}>{activeSkillById.get(skill.id)?.name ?? skill.id}</i>
                      ))
                    : <span>当前没有额外的已掌握主动技能</span>}
                </div>

                <div className="skill-section-heading passive-heading">
                  <div>
                    <strong>被动技能</strong>
                    <small>点击技能打开中文目录；每次只替换一条</small>
                  </div>
                  <em>{skillCatalog?.localizedPassiveSkillCount ?? 0} 中文 / {skillCatalog?.passiveSkillCount ?? 0} 全部</em>
                </div>
                <div className="passive-skill-slots">
                  {selectedPal.passiveSkills.map((skill, index) => (
                    <label key={`${index}-${skill}`}>
                      词条 {index + 1}
                      <button
                        type="button"
                        className={(passiveDrafts[index] ?? skill) !== skill
                          ? "passive-skill-trigger changed"
                          : "passive-skill-trigger"}
                        onClick={() => setPassivePickerSlot(index)}
                        disabled={!canWrite || submitting || !skillCatalog}
                      >
                        <span>{passiveSkillById.get(passiveDrafts[index] ?? skill)?.name ?? passiveDrafts[index] ?? skill}</span>
                        <small>{passiveDrafts[index] ?? skill}</small>
                        <em>{(passiveDrafts[index] ?? skill) !== skill ? "待预演" : "选择"}</em>
                      </button>
                    </label>
                  ))}
                </div>
              </section>

              <label className="field-label">
                昵称
                <input
                  value={nickname}
                  maxLength={24}
                  onChange={(event) => setNickname(event.target.value)}
                  placeholder={palName(selectedPal, palCatalog)}
                  disabled={!canWrite || submitting}
                />
              </label>

              <label className="pal-check">
                <input
                  type="checkbox"
                  checked={favorite}
                  onChange={(event) => setFavorite(event.target.checked)}
                  disabled={!canWrite || submitting}
                />
                <span><strong>收藏帕鲁</strong><small>写入后由 OnRep_SaveParameter 原生结算</small></span>
              </label>

              <label className="field-label">
                修改原因
                <input
                  value={reason}
                  maxLength={120}
                  onChange={(event) => setReason(event.target.value)}
                  disabled={!canWrite || submitting}
                />
              </label>

              <label className="pal-check dry-run-check">
                <input
                  type="checkbox"
                  checked={dryRun}
                  onChange={(event) => setDryRun(event.target.checked)}
                  disabled={!canWrite || submitting}
                />
                <span>
                  <strong>仅预演，不写入</strong>
                  <small>取消勾选后才会执行真实修改</small>
                </span>
              </label>

              {feedback ? <div className={`pal-feedback ${feedback.kind}`}>{feedback.text}</div> : null}

              <div className="pal-editor-actions">
                <span>{canWrite ? "昵称、收藏与技能可写" : "写入能力未启用"}</span>
                <button className={dryRun ? "ghost-button" : "primary-button"} disabled={!canWrite || submitting}>
                  {submitting ? "处理中…" : dryRun ? "预演修改" : "确认写入"}
                </button>
              </div>

              <p className="pal-write-note">
                等级、Rank 与个体值保持只读；被动替换调用 AddPassiveSkill，主动装备调用 ClearEquipWaza / AddEquipWaza，随后统一执行镜像结算和回读验证。
              </p>
            </form>
          ) : null}
        </div>
      )}

      {passivePickerSlot !== undefined && selectedPal && skillCatalog ? (
        <PassiveSkillPicker
          skills={skillCatalog.passiveSkills}
          slotIndex={passivePickerSlot}
          currentSkillId={passiveDrafts[passivePickerSlot] ?? selectedPal.passiveSkills[passivePickerSlot]}
          usedSkillIds={passiveDrafts}
          onClose={() => setPassivePickerSlot(undefined)}
          onSelect={(skillId) => {
            setPassiveDrafts((current) => {
              const next = [...current];
              next[passivePickerSlot] = skillId;
              return next;
            });
            setPassivePickerSlot(undefined);
          }}
        />
      ) : null}
    </section>
  );
}

function normalizeId(value: string) {
  return value.replace(/[^a-zA-Z0-9]/g, "").toLocaleLowerCase();
}

function shortId(value: string) {
  return `${value.slice(0, 8)}…${value.slice(-4)}`;
}

function palName(pal: NativePal, palCatalog: Map<string, GameCatalogEntry>) {
  return findPalCatalogEntry(palCatalog, pal.characterId)?.name ?? pal.characterId;
}

function findPalCatalogEntry(palCatalog: Map<string, GameCatalogEntry>, palId: string) {
  return palCatalog.get(palId) ?? palCatalog.get(palId.trim().toLocaleLowerCase());
}

function palIdentityDetails(pal: NativePal, entry?: GameCatalogEntry) {
  return [
    pal.nickname.trim() ? `昵称 ${pal.nickname.trim()}` : "",
    entry?.englishName ?? "",
    `PalID ${pal.characterId}`,
    entry?.dex ? `图鉴 #${entry.dex}` : ""
  ].filter(Boolean).join(" · ");
}
