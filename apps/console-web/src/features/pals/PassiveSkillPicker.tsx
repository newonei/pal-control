import { useEffect, useMemo, useState } from "react";
import type { PassiveSkillCatalogEntry } from "../../lib/api/client";

type PassiveSkillPickerProps = {
  skills: PassiveSkillCatalogEntry[];
  slotIndex: number;
  currentSkillId: string;
  usedSkillIds: string[];
  onSelect: (skillId: string) => void;
  onClose: () => void;
};

type PolarityFilter = "all" | "positive" | "negative" | "neutral";

const rowHeight = 112;
const viewportHeight = 470;
const overscan = 4;

export function PassiveSkillPicker({
  skills,
  slotIndex,
  currentSkillId,
  usedSkillIds,
  onSelect,
  onClose
}: PassiveSkillPickerProps) {
  const [query, setQuery] = useState("");
  const [polarity, setPolarity] = useState<PolarityFilter>("all");
  const [showInternal, setShowInternal] = useState(false);
  const [scrollTop, setScrollTop] = useState(0);

  useEffect(() => {
    function closeOnEscape(event: KeyboardEvent) {
      if (event.key === "Escape") {
        onClose();
      }
    }
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [onClose]);

  const filteredSkills = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase("zh-Hans");
    return skills
      .filter((skill) => showInternal || !skill.internal || skill.id === currentSkillId)
      .filter((skill) => polarity === "all" || skill.polarity === polarity)
      .filter((skill) => {
        if (!needle) {
          return true;
        }
        const searchable = [
          skill.name,
          skill.id,
          cleanDescription(skill),
          ...skill.effects.flatMap((effect) => [effect.type, effectLabel(effect.type)])
        ].join(" ").toLocaleLowerCase("zh-Hans");
        return searchable.includes(needle);
      })
      .sort((left, right) => {
        if (left.id === currentSkillId) return -1;
        if (right.id === currentSkillId) return 1;
        if (left.rank !== right.rank) return right.rank - left.rank;
        return left.name.localeCompare(right.name, "zh-Hans");
      });
  }, [currentSkillId, polarity, query, showInternal, skills]);

  const startIndex = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const visibleCount = Math.ceil(viewportHeight / rowHeight) + overscan * 2;
  const endIndex = Math.min(filteredSkills.length, startIndex + visibleCount);
  const visibleSkills = filteredSkills.slice(startIndex, endIndex);

  function changePolarity(next: PolarityFilter) {
    setPolarity(next);
    setScrollTop(0);
  }

  return (
    <div className="skill-picker-backdrop" role="presentation" onMouseDown={(event) => {
      if (event.target === event.currentTarget) onClose();
    }}>
      <section className="skill-picker-dialog" role="dialog" aria-modal="true" aria-label={`选择词条 ${slotIndex + 1}`}>
        <header className="skill-picker-header">
          <div>
            <p className="eyebrow">PASSIVE SKILL CATALOG</p>
            <h3>选择被动技能</h3>
            <span>词条 {slotIndex + 1} · 中文名称来自游戏原生简体中文数据表</span>
          </div>
          <button type="button" className="skill-picker-close" onClick={onClose} aria-label="关闭技能选择">×</button>
        </header>

        <div className="skill-picker-toolbar">
          <label className="skill-picker-search">
            <span>⌕</span>
            <input
              autoFocus
              value={query}
              onChange={(event) => {
                setQuery(event.target.value);
                setScrollTop(0);
              }}
              placeholder="搜索中文名、技能 ID、描述或效果，例如：攻击、冷却、工作速度"
            />
            {query ? <button type="button" onClick={() => setQuery("")}>清空</button> : null}
          </label>

          <div className="skill-picker-filters" aria-label="技能筛选">
            {([
              ["all", "全部"],
              ["positive", "正面"],
              ["negative", "负面"],
              ["neutral", "中性"]
            ] as const).map(([value, label]) => (
              <button
                type="button"
                className={polarity === value ? "active" : ""}
                key={value}
                onClick={() => changePolarity(value)}
              >
                {label}
              </button>
            ))}
            <label className="skill-internal-toggle">
              <input
                type="checkbox"
                checked={showInternal}
                onChange={(event) => {
                  setShowInternal(event.target.checked);
                  setScrollTop(0);
                }}
              />
              显示无中文/内部技能
            </label>
          </div>
        </div>

        <div className="skill-picker-count">
          找到 <strong>{filteredSkills.length}</strong> 项
          {!showInternal ? <span>已隐藏 {skills.length - skills.filter((skill) => !skill.internal).length} 项内部数据</span> : null}
        </div>

        <div
          className="skill-picker-results"
          style={{ height: viewportHeight }}
          onScroll={(event) => setScrollTop(event.currentTarget.scrollTop)}
        >
          {filteredSkills.length === 0 ? (
            <div className="skill-picker-empty">
              <strong>没有匹配的技能</strong>
              <span>可以尝试技能中文名、内部 ID 或打开内部技能开关。</span>
            </div>
          ) : (
            <div className="skill-picker-virtual" style={{ height: filteredSkills.length * rowHeight }}>
              {visibleSkills.map((skill, visibleIndex) => {
                const absoluteIndex = startIndex + visibleIndex;
                const isCurrent = skill.id === currentSkillId;
                const usedByOtherSlot = usedSkillIds.includes(skill.id) && !isCurrent;
                return (
                  <article
                    className={`skill-option ${isCurrent ? "current" : ""} ${skill.polarity}`}
                    key={skill.id}
                    style={{ transform: `translateY(${absoluteIndex * rowHeight}px)` }}
                  >
                    <span className="skill-rank" title={`Rank ${skill.rank}`}>{rankLabel(skill.rank)}</span>
                    <div className="skill-option-main">
                      <div className="skill-option-title">
                        <strong>{skill.name}</strong>
                        {isCurrent ? <em>当前技能</em> : null}
                        {!skill.localized ? <em className="internal">内部</em> : null}
                      </div>
                      <code>{skill.id}</code>
                      <p>{skillDescription(skill)}</p>
                    </div>
                    <div className="skill-option-actions">
                      <span>{polarityLabel(skill.polarity)}</span>
                      <button
                        type="button"
                        disabled={usedByOtherSlot}
                        onClick={() => onSelect(skill.id)}
                        aria-label={`选择 ${skill.name}`}
                      >
                        {usedByOtherSlot ? "其他词条已使用" : isCurrent ? "保持不变" : "选择"}
                      </button>
                    </div>
                  </article>
                );
              })}
            </div>
          )}
        </div>

        <footer className="skill-picker-footer">
          <span>选择只会更新网页草稿，点击“预演修改”后才会进入原生校验。</span>
          <button type="button" className="ghost-button" onClick={onClose}>取消</button>
        </footer>
      </section>
    </div>
  );
}

export function skillDescription(skill: PassiveSkillCatalogEntry) {
  const clean = cleanDescription(skill);
  if (clean) return clean;
  const effects = skill.effects
    .filter((effect) => effect.type && effect.type.toLocaleLowerCase() !== "no")
    .map((effect) => `${effectLabel(effect.type)} ${formatSignedValue(effect.value)}`);
  return effects.length > 0 ? effects.join(" · ") : "该技能没有可展示的效果说明";
}

function cleanDescription(skill: PassiveSkillCatalogEntry) {
  let description = skill.description ?? "";
  skill.effects.forEach((effect, index) => {
    description = description.replaceAll(`{EffectValue${index + 1}}`, formatNumber(effect.value));
  });
  return description
    .replace(/<\/?[^>]+>/g, "")
    .replace(/\r?\n/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function effectLabel(type: string) {
  const labels: Record<string, string> = {
    ShotAttack: "攻击力",
    MeleeAttack: "近战攻击",
    Defense: "防御力",
    MaxHP: "生命上限",
    CraftSpeed: "工作速度",
    MoveSpeed: "移动速度",
    ActiveSkillCoolTime_Decrease: "主动技能冷却时间",
    Sanity_Decrease: "SAN值下降速度",
    FullStomach_Decrease: "饱食度下降速度",
    Stamina: "耐力",
    WorkSpeed: "工作速度"
  };
  return labels[type] ?? type.replaceAll("_", " ");
}

function formatSignedValue(value: number) {
  return `${value > 0 ? "+" : ""}${formatNumber(value)}%`;
}

function formatNumber(value: number) {
  return Number.isInteger(value) ? value.toString() : value.toFixed(1).replace(/\.0$/, "");
}

function rankLabel(rank: number) {
  if (rank > 0) return "★".repeat(Math.min(rank, 4));
  if (rank < 0) return `−${Math.abs(rank)}`;
  return "◇";
}

function polarityLabel(polarity: PassiveSkillCatalogEntry["polarity"]) {
  if (polarity === "positive") return "正面";
  if (polarity === "negative") return "负面";
  return "中性";
}
