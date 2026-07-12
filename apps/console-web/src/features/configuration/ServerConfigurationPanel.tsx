import { useEffect, useMemo, useRef, useState } from "react";
import {
  getServerConfiguration,
  updateServerConfiguration,
  type ServerConfiguration,
  type ServerConfigurationOption
} from "../../lib/api/client";
import {
  SERVER_CONFIGURATION_CATEGORY_ORDER,
  SERVER_CONFIGURATION_DEFINITION_BY_KEY,
  SERVER_CONFIGURATION_DEFINITIONS
} from "./serverConfigurationDefinitions";

const ALL_CATEGORIES = "全部设置";
const FALLBACK_CATEGORY = "高级与兼容";

type Feedback = { kind: "success" | "error"; text: string };
type ResolvedDefinition = {
  key: string;
  label: string;
  help: string;
  category: string;
  unit?: string;
  dangerous?: boolean;
  min?: number;
  max?: number;
  step?: number;
};

export function ServerConfigurationPanel() {
  const [document, setDocument] = useState<ServerConfiguration>();
  const [changes, setChanges] = useState<Record<string, unknown>>({});
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState<string>(ALL_CATEGORIES);
  const [customizedOnly, setCustomizedOnly] = useState(false);
  const [reviewOpen, setReviewOpen] = useState(false);
  const [feedback, setFeedback] = useState<Feedback>();

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, []);

  const optionByKey = useMemo(
    () => new Map(document?.options.map((option) => [option.key, option]) ?? []),
    [document]
  );
  const definitionIndex = useMemo(
    () => new Map<string, number>(SERVER_CONFIGURATION_DEFINITIONS.map((definition, index) => [definition.key, index])),
    []
  );
  const orderedOptions = useMemo(() => [...(document?.options ?? [])].sort((left, right) => {
    const leftDefinition = resolveDefinition(left);
    const rightDefinition = resolveDefinition(right);
    const leftCategory = categoryIndex(leftDefinition.category);
    const rightCategory = categoryIndex(rightDefinition.category);
    if (leftCategory !== rightCategory) return leftCategory - rightCategory;
    return (definitionIndex.get(left.key) ?? Number.MAX_SAFE_INTEGER)
      - (definitionIndex.get(right.key) ?? Number.MAX_SAFE_INTEGER);
  }), [definitionIndex, document]);

  const dirtyKeys = useMemo(() => Object.keys(changes), [changes]);
  const validationErrors = useMemo(() => {
    const errors = new Map<string, string>();
    for (const [key, value] of Object.entries(changes)) {
      const option = optionByKey.get(key);
      if (!option) continue;
      const error = validateValue(option, value, resolveDefinition(option));
      if (error) errors.set(key, error);
    }
    return errors;
  }, [changes, optionByKey]);

  const categories = useMemo(() => {
    const present = new Set(orderedOptions.map((option) => resolveDefinition(option).category));
    const known = SERVER_CONFIGURATION_CATEGORY_ORDER.filter((item) => present.has(item));
    const unknown = [...present].filter((item) => !known.includes(item as never)).sort((a, b) => a.localeCompare(b, "zh-CN"));
    return [...known, ...unknown];
  }, [orderedOptions]);

  const filteredOptions = useMemo(() => {
    const needle = normalize(query);
    return orderedOptions.filter((option) => {
      const definition = resolveDefinition(option);
      if (category !== ALL_CATEGORIES && definition.category !== category) return false;
      if (customizedOnly && !isCustomized(option, changes)) return false;
      if (!needle) return true;
      return normalize([
        definition.label,
        definition.help,
        definition.category,
        definition.unit ?? "",
        definition.key
      ].join(" ")).includes(needle);
    });
  }, [category, changes, customizedOnly, orderedOptions, query]);

  const groupedOptions = useMemo(() => categories.map((item) => ({
    category: item,
    options: filteredOptions.filter((option) => resolveDefinition(option).category === item)
  })).filter((group) => group.options.length > 0), [categories, filteredOptions]);

  const customizedCount = orderedOptions.filter((option) => isCustomized(option, changes)).length;
  const dangerousKeys = dirtyKeys.filter((key) => {
    const option = optionByKey.get(key);
    return option ? Boolean(resolveDefinition(option).dangerous) : false;
  });

  useEffect(() => {
    if (dirtyKeys.length === 0) return;
    const guard = (event: BeforeUnloadEvent) => event.preventDefault();
    window.addEventListener("beforeunload", guard);
    return () => window.removeEventListener("beforeunload", guard);
  }, [dirtyKeys.length]);

  async function load(signal?: AbortSignal) {
    setLoading(true);
    setFeedback(undefined);
    try {
      const next = await getServerConfiguration(signal);
      setDocument(next);
      setChanges({});
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") return;
      setFeedback({ kind: "error", text: error instanceof Error ? error.message : "读取服务器配置失败" });
    } finally {
      setLoading(false);
    }
  }

  async function reload() {
    if (dirtyKeys.length > 0 && !window.confirm(`将放弃 ${dirtyKeys.length} 项未保存修改并重新读取，是否继续？`)) return;
    await load();
  }

  function changeValue(option: ServerConfigurationOption, value: unknown) {
    setChanges((current) => {
      const next = { ...current };
      if (!option.sensitive && valuesEqual(value, option.value)) delete next[option.key];
      else next[option.key] = value;
      return next;
    });
    setFeedback(undefined);
  }

  function undoChange(key: string) {
    setChanges((current) => {
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  function restoreDefault(option: ServerConfigurationOption) {
    if (option.sensitive) return;
    changeValue(option, option.defaultValue);
  }

  async function save() {
    if (!document || dirtyKeys.length === 0 || validationErrors.size > 0) return;
    setSaving(true);
    setFeedback(undefined);
    try {
      const updated = await updateServerConfiguration({ revision: document.revision, changes });
      setDocument(updated);
      setChanges({});
      setReviewOpen(false);
      setFeedback({ kind: "success", text: "配置已保存并重新读取。系统已创建备份；相关设置将在 PalServer 重启后生效。" });
    } catch (error) {
      setReviewOpen(false);
      setFeedback({ kind: "error", text: error instanceof Error ? error.message : "保存服务器配置失败" });
    } finally {
      setSaving(false);
    }
  }

  if (loading && !document) {
    return <section className="pd-page config-page"><div className="config-empty">正在读取 PalWorldSettings.ini…</div></section>;
  }
  if (!document) {
    return <section className="pd-page config-page">
      {feedback ? <div className="config-alert error" role="alert">{feedback.text}</div> : null}
      <button className="primary-button" onClick={() => void load()} type="button">重新读取</button>
    </section>;
  }

  return (
    <section className="pd-page config-page">
      <header className="pd-page-header config-heading config-heading--dynamic">
        <div>
          <p className="eyebrow">PALWORLD SETTINGS</p>
          <h2>服务器配置</h2>
          <p>完整读取当前版本的 PalWorldSettings.ini；可以按中文名称或内部参数名搜索。</p>
        </div>
        <div className="config-heading-actions">
          <button className="ghost-button" disabled={saving || loading} onClick={() => void reload()} type="button">
            {loading ? "读取中…" : "重新读取"}
          </button>
          <button
            className="primary-button"
            disabled={saving || dirtyKeys.length === 0 || validationErrors.size > 0}
            onClick={() => setReviewOpen(true)}
            type="button"
          >
            {saving ? "保存中…" : `检查并保存${dirtyKeys.length ? `（${dirtyKeys.length}）` : ""}`}
          </button>
        </div>
      </header>

      {feedback ? <div className={`config-alert ${feedback.kind}`} role="status">{feedback.text}</div> : null}
      <div className="config-warning">保存只会安全写入配置文件并创建备份，不会中断当前服务；大多数参数需要重启 PalServer 后生效。</div>

      <div className="config-summary" aria-label="配置概况">
        <article><span>可配置参数</span><strong>{document.options.length}</strong><small>来自当前服务器文件</small></article>
        <article><span>非默认设置</span><strong>{customizedCount}</strong><small>含本次草稿</small></article>
        <article className={dirtyKeys.length ? "changed" : ""}><span>未保存修改</span><strong>{dirtyKeys.length}</strong><small>{validationErrors.size ? `${validationErrors.size} 项需要修正` : "草稿不会自动提交"}</small></article>
        <article className={dangerousKeys.length ? "danger" : ""}><span>危险修改</span><strong>{dangerousKeys.length}</strong><small>保存前必须再次确认</small></article>
      </div>

      <section className="config-toolbar-card" aria-label="查找和筛选配置">
        <label className="config-search">
          <span>搜索设置</span>
          <input
            onChange={(event) => {
              setQuery(event.target.value);
              if (event.target.value) setCategory(ALL_CATEGORIES);
            }}
            placeholder="搜索中文名称、说明或内部参数名"
            type="search"
            value={query}
          />
          {query ? <button aria-label="清除搜索" onClick={() => setQuery("")} type="button">×</button> : null}
        </label>
        <label className="config-filter-toggle">
          <input checked={customizedOnly} onChange={(event) => setCustomizedOnly(event.target.checked)} type="checkbox" />
          <span><strong>仅看非默认</strong><small>包括当前草稿修改</small></span>
        </label>
        <div className="config-result-count"><strong>{filteredOptions.length}</strong><span>项匹配</span></div>
      </section>

      <div className="config-workspace">
        <nav aria-label="配置分类" className="config-category-nav">
          <button
            aria-current={category === ALL_CATEGORIES ? "page" : undefined}
            className={category === ALL_CATEGORIES ? "active" : ""}
            onClick={() => setCategory(ALL_CATEGORIES)}
            type="button"
          >
            <span>{ALL_CATEGORIES}</span><em>{orderedOptions.length}</em>
          </button>
          {categories.map((item) => {
            const options = orderedOptions.filter((option) => resolveDefinition(option).category === item);
            const modified = options.filter((option) => Object.prototype.hasOwnProperty.call(changes, option.key)).length;
            return <button
              aria-current={category === item ? "page" : undefined}
              className={category === item ? "active" : ""}
              key={item}
              onClick={() => setCategory(item)}
              type="button"
            >
              <span>{item}</span><em>{options.length}</em>{modified ? <i>{modified}</i> : null}
            </button>;
          })}
        </nav>

        <div className="config-sections">
          {groupedOptions.map((group) => {
            const modified = group.options.filter((option) => Object.prototype.hasOwnProperty.call(changes, option.key)).length;
            return <section className="config-section" key={group.category}>
              <header>
                <div><h3>{group.category}</h3><p>{group.options.length} 项当前可见</p></div>
                {modified ? <span className="config-section-dirty">已修改 {modified}</span> : null}
              </header>
              <div className="config-option-grid">
                {group.options.map((option) => {
                  const definition = resolveDefinition(option);
                  const changed = Object.prototype.hasOwnProperty.call(changes, option.key);
                  const value = changed ? changes[option.key] : option.value;
                  const error = validationErrors.get(option.key);
                  const customized = isCustomized(option, changes);
                  return <article
                    className="config-option"
                    data-dangerous={definition.dangerous || undefined}
                    data-dirty={changed || undefined}
                    data-sensitive={option.sensitive || undefined}
                    key={option.key}
                  >
                    <div className="config-option-heading">
                      <div>
                        <strong>{definition.label}</strong>
                        <code>{definition.key}</code>
                      </div>
                      <span className="config-option-badges">
                        {changed ? <em className="changed">已修改</em> : null}
                        {definition.dangerous ? <em className="danger">谨慎</em> : null}
                        {option.sensitive ? <em className="sensitive">敏感</em> : null}
                        {!changed && customized ? <em>非默认</em> : null}
                      </span>
                    </div>
                    <p className="config-option-help" id={`config-help-${safeId(option.key)}`}>{definition.help}</p>
                    <ConfigurationControl
                      changed={changed}
                      definition={definition}
                      onChange={(next) => changeValue(option, next)}
                      onUndo={() => undoChange(option.key)}
                      option={option}
                      value={value}
                    />
                    {error ? <small className="config-field-error" role="alert">{error}</small> : null}
                    <footer className="config-option-footer">
                      <span>{option.sensitive ? `当前：${option.hasValue ? "已设置（不回显）" : "未设置"}` : `默认：${formatValue(option.defaultValue, definition.unit)}`}</span>
                      <div>
                        {changed ? <button onClick={() => undoChange(option.key)} type="button">撤销修改</button> : null}
                        {!option.sensitive && !valuesEqual(value, option.defaultValue) ? <button onClick={() => restoreDefault(option)} type="button">恢复默认</button> : null}
                      </div>
                    </footer>
                  </article>;
                })}
              </div>
            </section>;
          })}
          {groupedOptions.length === 0 ? <div className="config-no-results">
            <strong>没有匹配的设置</strong>
            <span>尝试清除搜索词、切换分类或关闭“仅看非默认”。</span>
            <button onClick={() => { setQuery(""); setCategory(ALL_CATEGORIES); setCustomizedOnly(false); }} type="button">清除筛选</button>
          </div> : null}
        </div>
      </div>

      {dirtyKeys.length > 0 ? <aside className="config-save-bar" aria-label="未保存修改">
        <div><strong>{dirtyKeys.length} 项修改尚未保存</strong><span>{validationErrors.size ? `请先修正 ${validationErrors.size} 项输入` : "保存前可查看完整差异"}</span></div>
        <div>
          <button className="ghost-button" disabled={saving} onClick={() => setChanges({})} type="button">撤销全部</button>
          <button className="primary-button" disabled={saving || validationErrors.size > 0} onClick={() => setReviewOpen(true)} type="button">检查并保存</button>
        </div>
      </aside> : null}

      <footer className="config-meta">
        <span>配置文件</span><code title={document.filePath}>{document.filePath}</code>
        <span>默认模板</span><code title={document.defaultFilePath}>{document.defaultFilePath}</code>
        <span>最后修改</span><code>{new Date(document.lastModifiedAt).toLocaleString()}</code>
        <span>版本</span><code>{document.schemaVersion}</code>
      </footer>

      {reviewOpen ? <ConfigurationReviewDialog
        changes={changes}
        configDocument={document}
        onCancel={() => setReviewOpen(false)}
        onConfirm={() => void save()}
        saving={saving}
      /> : null}
    </section>
  );
}

function ConfigurationControl({
  changed,
  definition,
  onChange,
  onUndo,
  option,
  value
}: {
  changed: boolean;
  definition: ResolvedDefinition;
  onChange: (value: unknown) => void;
  onUndo: () => void;
  option: ServerConfigurationOption;
  value: unknown;
}) {
  const describedBy = `config-help-${safeId(option.key)}`;
  if (option.sensitive) {
    const willClear = changed && value === null;
    return <div className="config-secret-control">
      <label>
        <span>{willClear ? "将清空此密码" : changed ? "已填写新密码" : "输入新密码"}</span>
        <input
          aria-describedby={describedBy}
          autoComplete="new-password"
          disabled={willClear}
          onChange={(event) => event.target.value ? onChange(event.target.value) : onUndo()}
          placeholder={option.hasValue ? "原密码不会返回；输入后将替换" : "输入要设置的密码"}
          type="password"
          value={changed && typeof value === "string" ? value : ""}
        />
      </label>
      <button className={willClear ? "active" : ""} onClick={() => onChange(null)} type="button">
        {willClear ? "已选择清空" : "清空密码"}
      </button>
    </div>;
  }

  if (option.kind === "boolean") {
    const checked = Boolean(value);
    return <label className="config-switch">
      <input aria-describedby={describedBy} checked={checked} onChange={(event) => onChange(event.target.checked)} type="checkbox" />
      <span aria-hidden="true" />
      <strong>{checked ? "已启用" : "已停用"}</strong>
    </label>;
  }

  if (option.kind === "enum") {
    return <label className="config-value-control">
      <span>当前选择</span>
      <select aria-describedby={describedBy} onChange={(event) => onChange(event.target.value)} value={String(value ?? "")}>
        {(option.allowedValues ?? []).map((allowed) => <option key={allowed} value={allowed}>{formatEnum(option.key, allowed)}</option>)}
      </select>
    </label>;
  }

  if (option.kind === "string-list") {
    const selected = Array.isArray(value) ? value.map(String) : [];
    if ((option.allowedValues?.length ?? 0) > 0) {
      return <fieldset className="config-list-options">
        <legend>选择允许的项目</legend>
        {option.allowedValues!.map((allowed) => <label key={allowed}>
          <input
            checked={selected.includes(allowed)}
            onChange={(event) => onChange(event.target.checked
              ? option.allowedValues!.filter((item) => item === allowed || selected.includes(item))
              : selected.filter((item) => item !== allowed))}
            type="checkbox"
          />
          <span>{formatEnum(option.key, allowed)}</span>
        </label>)}
      </fieldset>;
    }
    return <label className="config-value-control">
      <span>每行填写一项，也可使用逗号分隔</span>
      <textarea
        aria-describedby={describedBy}
        onChange={(event) => onChange(event.target.value.split(/[\n,]+/).map((item) => item.trim()).filter(Boolean))}
        placeholder="留空表示不限制"
        value={selected.join("\n")}
      />
    </label>;
  }

  if (option.kind === "integer" || option.kind === "number") {
    const minimum = option.minimum ?? definition.min;
    const maximum = option.maximum ?? definition.max;
    const step = option.step ?? definition.step ?? (option.kind === "integer" ? 1 : 0.1);
    return <label className="config-value-control">
      <span>数值{definition.unit ? `（${definition.unit}）` : ""}</span>
      <div className="config-number-input">
        <input
          aria-describedby={describedBy}
          inputMode={option.kind === "integer" ? "numeric" : "decimal"}
          max={maximum}
          min={minimum}
          onChange={(event) => {
            const raw = event.target.value;
            if (!raw) onChange("");
            else onChange(Number(raw));
          }}
          step={step}
          type="number"
          value={value === null || value === undefined ? "" : String(value)}
        />
        {definition.unit ? <em>{definition.unit}</em> : null}
      </div>
      {minimum !== undefined || maximum !== undefined ? <small>范围：{minimum ?? "不限"} – {maximum ?? "不限"}</small> : null}
    </label>;
  }

  const multiline = option.key === "ServerDescription";
  return <label className="config-value-control">
    <span>文本内容</span>
    {multiline ? <textarea aria-describedby={describedBy} onChange={(event) => onChange(event.target.value)} value={String(value ?? "")} />
      : <input aria-describedby={describedBy} onChange={(event) => onChange(event.target.value)} type="text" value={String(value ?? "")} />}
  </label>;
}

function ConfigurationReviewDialog({
  changes,
  configDocument,
  onCancel,
  onConfirm,
  saving
}: {
  changes: Record<string, unknown>;
  configDocument: ServerConfiguration;
  onCancel: () => void;
  onConfirm: () => void;
  saving: boolean;
}) {
  const cancelRef = useRef<HTMLButtonElement>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const optionByKey = new Map(configDocument.options.map((option) => [option.key, option]));
  const entries = Object.entries(changes).map(([key, value]) => {
    const option = optionByKey.get(key);
    return option ? { option, definition: resolveDefinition(option), value } : null;
  }).filter((entry): entry is NonNullable<typeof entry> => Boolean(entry));
  const dangerous = entries.filter((entry) => entry.definition.dangerous);

  useEffect(() => {
    const previousOverflow = document.body.style.overflow;
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    document.body.style.overflow = "hidden";
    const timer = globalThis.setTimeout(() => cancelRef.current?.focus(), 0);
    const handleKeyDown = (event: KeyboardEvent) => { if (event.key === "Escape" && !saving) onCancel(); };
    window.addEventListener("keydown", handleKeyDown);
    return () => {
      globalThis.clearTimeout(timer);
      window.removeEventListener("keydown", handleKeyDown);
      document.body.style.overflow = previousOverflow;
      previousFocus?.focus();
    };
  }, [onCancel, saving]);

  return <div className="pd-modal-backdrop config-review-backdrop" onMouseDown={(event) => {
    if (event.target === event.currentTarget && !saving) onCancel();
  }}>
    <section aria-labelledby="config-review-title" aria-modal="true" className="pd-dialog config-review-dialog" role="alertdialog">
      <header className="pd-dialog__header">
        <div><p className="eyebrow">REVIEW CHANGES</p><h3 id="config-review-title">确认保存 {entries.length} 项配置修改</h3><span>只提交下列变化；服务器中的其他参数保持原样。</span></div>
        <button aria-label="关闭确认窗口" disabled={saving} onClick={onCancel} type="button">×</button>
      </header>
      <div className="pd-dialog__body">
        {dangerous.length > 0 ? <div className="config-review-warning" role="alert">
          <strong>其中 {dangerous.length} 项可能改变安全、PvP、存档或高负载行为</strong>
          <span>请逐项核对。保存后仍需重启 PalServer 才会应用。</span>
        </div> : null}
        <div className="config-change-list">
          {entries.map(({ option, definition, value }) => <article data-dangerous={definition.dangerous || undefined} key={option.key}>
            <div><strong>{definition.label}</strong><code>{option.key}</code></div>
            <span>{describeOldValue(option)}</span><b aria-hidden="true">→</b><em>{describeNewValue(option, value, definition.unit)}</em>
          </article>)}
        </div>
        {dangerous.length > 0 ? <label className="config-danger-ack">
          <input checked={acknowledged} onChange={(event) => setAcknowledged(event.target.checked)} type="checkbox" />
          <span><strong>我已核对上述危险修改</strong><small>我了解这些设置可能影响玩家、存档、安全性或服务器性能。</small></span>
        </label> : null}
      </div>
      <footer className="pd-dialog__footer">
        <span>保存时会创建原配置备份</span>
        <div>
          <button className="ghost-button" disabled={saving} onClick={onCancel} ref={cancelRef} type="button">返回修改</button>
          <button className="primary-button" disabled={saving || (dangerous.length > 0 && !acknowledged)} onClick={onConfirm} type="button">{saving ? "保存中…" : "确认保存"}</button>
        </div>
      </footer>
    </section>
  </div>;
}

function resolveDefinition(option: ServerConfigurationOption): ResolvedDefinition {
  return SERVER_CONFIGURATION_DEFINITION_BY_KEY.get(option.key) ?? {
    key: option.key,
    label: option.key,
    help: "当前服务器版本包含的兼容参数；修改前请确认其游戏内含义。",
    category: FALLBACK_CATEGORY,
    dangerous: true
  };
}

function categoryIndex(category: string) {
  const index = SERVER_CONFIGURATION_CATEGORY_ORDER.indexOf(category as never);
  return index >= 0 ? index : SERVER_CONFIGURATION_CATEGORY_ORDER.length;
}

function validateValue(option: ServerConfigurationOption, value: unknown, definition: ResolvedDefinition) {
  if (option.sensitive) {
    if (value === null) return undefined;
    return typeof value === "string" && value.length > 0 ? undefined : "请输入新密码，或使用“清空密码”。";
  }
  if (option.kind === "integer" || option.kind === "number") {
    if (typeof value !== "number" || !Number.isFinite(value)) return "请输入有效数值。";
    if (option.kind === "integer" && !Number.isInteger(value)) return "此项必须是整数。";
    const minimum = option.minimum ?? definition.min;
    const maximum = option.maximum ?? definition.max;
    if (minimum !== undefined && value < minimum) return `不能小于 ${minimum}${definition.unit ?? ""}。`;
    if (maximum !== undefined && value > maximum) return `不能大于 ${maximum}${definition.unit ?? ""}。`;
  }
  if (option.kind === "enum" && !(option.allowedValues ?? []).includes(String(value))) return "请选择目录中的有效值。";
  if (option.kind === "string-list" && !Array.isArray(value)) return "列表格式无效。";
  if (option.key === "CrossplayPlatforms" && Array.isArray(value) && value.length === 0) {
    return "至少要保留一个可连接平台。";
  }
  return undefined;
}

function isCustomized(option: ServerConfigurationOption, changes: Record<string, unknown>) {
  if (!Object.prototype.hasOwnProperty.call(changes, option.key)) return option.customized;
  if (option.sensitive) return changes[option.key] !== null;
  return !valuesEqual(changes[option.key], option.defaultValue);
}

function valuesEqual(left: unknown, right: unknown) {
  if (Array.isArray(left) && Array.isArray(right)) {
    return left.length === right.length && left.every((value, index) => value === right[index]);
  }
  return left === right;
}

function formatValue(value: unknown, unit?: string) {
  if (Array.isArray(value)) return value.length ? value.join("、") : "空列表";
  if (typeof value === "boolean") return value ? "启用" : "停用";
  if (value === null || value === undefined || value === "") return "空";
  return `${String(value)}${unit ?? ""}`;
}

function formatEnum(key: string, value: string) {
  const common: Record<string, string> = { None: "无 / 默认", True: "启用", False: "停用", Text: "文本", Json: "JSON" };
  const deathPenalty: Record<string, string> = { None: "不掉落", Item: "仅物品", ItemAndEquipment: "物品与装备", All: "全部" };
  if (key === "DeathPenalty" && deathPenalty[value]) return `${deathPenalty[value]}（${value}）`;
  return common[value] ? `${common[value]}（${value}）` : value;
}

function describeOldValue(option: ServerConfigurationOption) {
  if (option.sensitive) return option.hasValue ? "当前已设置" : "当前未设置";
  return `当前：${formatValue(option.value)}`;
}

function describeNewValue(option: ServerConfigurationOption, value: unknown, unit?: string) {
  if (option.sensitive) return value === null ? "清空密码" : "替换为新密码（内容隐藏）";
  return `改为：${formatValue(value, unit)}`;
}

function normalize(value: string) {
  return value.trim().toLocaleLowerCase().replace(/[\s_-]+/g, "");
}

function safeId(value: string) {
  return value.replace(/[^a-zA-Z0-9_-]/g, "-");
}
