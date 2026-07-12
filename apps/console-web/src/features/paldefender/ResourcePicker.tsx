import { useEffect, useId, useMemo, useRef, useState } from "react";

export type ResourcePickerOption = {
  id: string; label: string; category?: string; description?: string; keywords?: string[];
  badges?: string[]; disabled?: boolean; tone?: "standard" | "elevated" | "high" | "invalid";
};

type Props = {
  label: string; title: string; value: string; options: ResourcePickerOption[];
  onChange: (id: string) => void; placeholder: string; sourceNote: string;
  disabled?: boolean; allowCustom?: boolean; customHint?: string;
};

const pageSize = 80;

export function ResourcePickerField({ label, title, value, options, onChange, placeholder, sourceNote, disabled = false, allowCustom = false, customHint }: Props) {
  const [open, setOpen] = useState(false);
  const selected = options.find((option) => option.id === value);
  return <div className="resource-picker-field">
    <span>{label}</span>
    <button className={selected ? "resource-picker-trigger selected" : "resource-picker-trigger"} disabled={disabled} onClick={() => setOpen(true)} type="button">
      <span><strong>{selected?.label ?? (value || placeholder)}</strong><small>{selected?.category ? localizeCategory(selected.category) : (value ? "自定义内部标识" : "点击打开搜索目录")}</small></span>
      {selected || value ? <code>{selected?.id ?? value}</code> : null}<em>{selected || value ? "更换" : "选择"}</em>
    </button>
    <small className="resource-picker-source">{sourceNote}</small>
    <ResourcePickerDialog allowCustom={allowCustom} customHint={customHint} onClose={() => setOpen(false)} onSelect={(id) => { onChange(id); setOpen(false); }} open={open} options={options} title={title} value={value} />
  </div>;
}

function ResourcePickerDialog({ open, title, value, options, onSelect, onClose, allowCustom, customHint }: {
  open: boolean; title: string; value: string; options: ResourcePickerOption[]; onSelect: (id: string) => void;
  onClose: () => void; allowCustom: boolean; customHint?: string;
}) {
  const titleId = useId();
  const searchRef = useRef<HTMLInputElement>(null);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState("all");
  const [page, setPage] = useState(0);
  const [customValue, setCustomValue] = useState("");
  const [customError, setCustomError] = useState<string>();
  const categories = useMemo(() => Array.from(new Set(options.map((option) => option.category).filter((item): item is string => Boolean(item)))).sort((a, b) => a.localeCompare(b, "zh-CN")), [options]);
  const filtered = useMemo(() => {
    const needle = normalize(query);
    return options.filter((option) => (category === "all" || option.category === category) && (!needle || normalize([option.label, option.id, option.category ?? "", option.description ?? "", ...(option.keywords ?? [])].join(" ")).includes(needle)));
  }, [category, options, query]);
  const pageCount = Math.max(1, Math.ceil(filtered.length / pageSize));
  const safePage = Math.min(page, pageCount - 1);
  const visible = filtered.slice(safePage * pageSize, (safePage + 1) * pageSize);

  useEffect(() => {
    if (!open) return;
    const overflow = document.body.style.overflow;
    const focus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.style.overflow = "hidden"; setQuery(""); setCategory("all"); setPage(0); setCustomValue(""); setCustomError(undefined);
    const timer = globalThis.setTimeout(() => searchRef.current?.focus(), 0);
    const keydown = (event: KeyboardEvent) => { if (event.key === "Escape") onClose(); };
    document.addEventListener("keydown", keydown);
    return () => { globalThis.clearTimeout(timer); document.removeEventListener("keydown", keydown); document.body.style.overflow = overflow; focus?.focus(); };
  }, [open, onClose]);
  useEffect(() => setPage(0), [category, query]);
  if (!open) return null;

  function selectCustom() {
    const clean = customValue.trim();
    if (!/^[a-zA-Z0-9_.:-]{1,120}$/.test(clean)) return setCustomError("只允许字母、数字、点、下划线、冒号和短横线，不能包含路径。");
    onSelect(clean);
  }

  return <div className="resource-picker-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <section aria-labelledby={titleId} aria-modal="true" className="resource-picker-dialog" role="dialog">
      <header className="resource-picker-header"><div><p className="eyebrow">搜索与选择</p><h3 id={titleId}>{title}</h3><p>按中文名、英文名、分类或内部标识搜索；选择后才会回填到操作表单。</p></div><button aria-label="关闭选择器" onClick={onClose} type="button">×</button></header>
      <div className="resource-picker-toolbar">
        <label className="resource-picker-search"><span>搜索</span><input onChange={(event) => setQuery(event.target.value)} placeholder="输入中文名、英文名、分类或 ID" ref={searchRef} value={query} /></label>
        <label><span>分类</span><select onChange={(event) => setCategory(event.target.value)} value={category}><option value="all">全部分类</option>{categories.map((item) => <option key={item} value={item}>{localizeCategory(item)}</option>)}</select></label>
        <strong>{filtered.length} 条结果</strong>
      </div>
      <div className="resource-picker-results">
        {visible.map((option) => <button aria-pressed={option.id === value} className={`${option.id === value ? "selected" : ""} ${option.tone ? `tone-${option.tone}` : ""}`.trim()} disabled={option.disabled} key={option.id} onClick={() => onSelect(option.id)} type="button">
          <span className="resource-picker-mark">{(option.label || option.id).slice(0, 1).toLocaleUpperCase()}</span>
          <span className="resource-picker-option-copy"><strong>{option.label || option.id}</strong><small>{option.description || localizeCategory(option.category ?? "未分类")}</small><code>{option.id}</code></span>
          <span className="resource-picker-badges">{(option.badges ?? []).map((badge) => <em key={badge}>{badge}</em>)}{option.disabled ? <em>不可选择</em> : null}</span>
        </button>)}
        {!visible.length ? <div className="resource-picker-empty"><strong>没有匹配结果</strong><span>可更换搜索词、分类，或使用下方高级自定义入口。</span></div> : null}
      </div>
      <footer className="resource-picker-footer">
        <div className="resource-picker-pagination"><button disabled={safePage === 0} onClick={() => setPage((n) => Math.max(0, n - 1))} type="button">上一页</button><span>第 {safePage + 1} / {pageCount} 页</span><button disabled={safePage >= pageCount - 1} onClick={() => setPage((n) => Math.min(pageCount - 1, n + 1))} type="button">下一页</button></div>
        {allowCustom ? <details className="resource-picker-custom"><summary>高级：使用目录外的自定义 ID</summary><p>{customHint ?? "仅用于已确认存在的模组或自定义资源；目录外 ID 无法预先验证。"}</p><div><input onChange={(event) => setCustomValue(event.target.value)} placeholder="输入经过确认的内部 ID" value={customValue} /><button disabled={!customValue.trim()} onClick={selectCustom} type="button">使用此 ID</button></div>{customError ? <small role="alert">{customError}</small> : null}</details> : null}
      </footer>
    </section>
  </div>;
}

function normalize(value: string) { return value.trim().toLocaleLowerCase().replace(/[\s_-]+/g, ""); }
function localizeCategory(category: string) {
  const labels: Record<string, string> = { Accessories: "饰品", Ammo: "弹药", Armor: "护甲", "Boss Reward": "Boss 奖励", Consumables: "消耗品", Essential: "关键物品", Food: "食物", Materials: "材料", Other: "其他", "Pal Sphere": "帕鲁球", "Pal Summon": "帕鲁召唤物", Weapons: "武器", Pal: "帕鲁", Technology: "科技", Dark: "暗属性", Dragon: "龙属性", Ground: "地属性", Electric: "雷属性", Fire: "火属性", Ice: "冰属性", Grass: "草属性", Neutral: "无属性", Water: "水属性", Templates: "模板" };
  return labels[category] ?? category;
}
