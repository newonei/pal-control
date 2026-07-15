import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent
} from "react";
import {
  ContentApiError,
  PUBLISH_CONFIRMATION,
  ROLLBACK_CONFIRMATION,
  contentErrorMessage,
  createContentDraft,
  getContentDraft,
  getContentDraftDiff,
  getCurrentContent,
  listContentDrafts,
  listContentVersions,
  publishContentDraft,
  rollbackContentVersion,
  updateContentDraft,
  validateContentDraft,
  type ContentDefinition,
  type ContentDiffResult,
  type ContentValidationResult,
  type EconomyContentCurrent,
  type EconomyContentDraft,
  type EconomyContentVersion
} from "./api";
import "./content.css";

type ContentManagementProps = {
  serverId: string;
};

type BusyOperation = "loading" | "draft" | "save" | "validate" | "diff" | "publish" | "rollback" | null;

export function ContentManagement({ serverId }: ContentManagementProps) {
  const [current, setCurrent] = useState<EconomyContentCurrent | null>(null);
  const [versions, setVersions] = useState<EconomyContentVersion[]>([]);
  const [drafts, setDrafts] = useState<EconomyContentDraft[]>([]);
  const [selectedDraftId, setSelectedDraftId] = useState<string | null>(null);
  const [draft, setDraft] = useState<EconomyContentDraft | null>(null);
  const [editorText, setEditorText] = useState("");
  const [savedEditorText, setSavedEditorText] = useState("");
  const [validation, setValidation] = useState<ContentValidationResult | null>(null);
  const [diff, setDiff] = useState<ContentDiffResult | null>(null);
  const [busy, setBusy] = useState<BusyOperation>("loading");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [revisionConflict, setRevisionConflict] = useState(false);

  const [createName, setCreateName] = useState("");
  const [createBaseVersionId, setCreateBaseVersionId] = useState("");

  const [publishDate, setPublishDate] = useState("");
  const [publishReason, setPublishReason] = useState("");
  const [publishConfirmation, setPublishConfirmation] = useState("");
  const [publishTotp, setPublishTotp] = useState("");

  const [rollbackTarget, setRollbackTarget] = useState("");
  const [rollbackReason, setRollbackReason] = useState("");
  const [rollbackConfirmation, setRollbackConfirmation] = useState("");
  const [rollbackTotp, setRollbackTotp] = useState("");
  const operationKeys = useRef(new Map<string, string>());

  const dirty = Boolean(draft) && editorText !== savedEditorText;
  const editable = draft ? draft.state.toLocaleLowerCase() === "draft" : false;
  const parsedEditor = useMemo(() => parseDefinition(editorText), [editorText]);
  const currentVersionId = current?.pointer.versionId ?? null;
  const rollbackVersions = versions.filter((version) => version.versionId !== currentVersionId);

  const refreshOverview = useCallback(async (signal?: AbortSignal) => {
    setBusy((value) => value ?? "loading");
    const results = await Promise.allSettled([
      getCurrentContent(serverId, signal),
      listContentVersions(serverId, signal),
      listContentDrafts(serverId, signal)
    ]);
    if (signal?.aborted) return;

    const errors: string[] = [];
    const [currentResult, versionsResult, draftsResult] = results;
    if (currentResult.status === "fulfilled") {
      setCurrent(currentResult.value);
    } else {
      errors.push(contentErrorMessage(currentResult.reason, "当前版本读取失败"));
    }
    if (versionsResult.status === "fulfilled") {
      setVersions(sortVersions(versionsResult.value));
    } else {
      errors.push(contentErrorMessage(versionsResult.reason, "版本历史读取失败"));
    }
    if (draftsResult.status === "fulfilled") {
      const nextDrafts = sortDrafts(draftsResult.value);
      setDrafts(nextDrafts);
      setSelectedDraftId((selected) => selected && nextDrafts.some((item) => item.draftId === selected)
        ? selected
        : nextDrafts[0]?.draftId ?? null);
    } else {
      errors.push(contentErrorMessage(draftsResult.reason, "草稿列表读取失败"));
    }
    setError(errors.length > 0 ? errors.join("；") : null);
    setBusy((value) => value === "loading" ? null : value);
  }, [serverId]);

  useEffect(() => {
    const controller = new AbortController();
    void refreshOverview(controller.signal);
    return () => controller.abort();
  }, [refreshOverview]);

  useEffect(() => {
    if (!current || createBaseVersionId) return;
    setCreateBaseVersionId(current.pointer.versionId);
  }, [createBaseVersionId, current]);

  useEffect(() => {
    if (!selectedDraftId) {
      setDraft(null);
      setEditorText("");
      setSavedEditorText("");
      setValidation(null);
      setDiff(null);
      return;
    }
    const controller = new AbortController();
    setDraft(null);
    setEditorText("");
    setSavedEditorText("");
    setValidation(null);
    setDiff(null);
    setBusy("loading");
    setError(null);
    void getContentDraft(serverId, selectedDraftId, controller.signal)
      .then((nextDraft) => {
        if (controller.signal.aborted) return;
        applyRemoteDraft(nextDraft, true);
      })
      .catch((nextError: unknown) => {
        if (!controller.signal.aborted) {
          setError(contentErrorMessage(nextError, "草稿读取失败"));
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setBusy(null);
      });
    return () => controller.abort();
  }, [selectedDraftId, serverId]);

  function applyRemoteDraft(nextDraft: EconomyContentDraft, allowRecovery: boolean) {
    const remoteText = formatDefinition(nextDraft.definition);
    const recovery = allowRecovery ? readEditorRecovery(serverId, nextDraft.draftId) : null;
    setDraft(nextDraft);
    setSavedEditorText(remoteText);
    setValidation(nextDraft.lastValidation);
    setDiff(null);
    setRevisionConflict(false);
    if (recovery && recovery.text !== remoteText) {
      setEditorText(recovery.text);
      setNotice(recovery.revision === nextDraft.revision
        ? "已恢复本标签页中尚未保存的 JSON。"
        : `已恢复基于 revision ${recovery.revision} 的本地 JSON；服务端当前为 revision ${nextDraft.revision}，请合并后再保存。`);
    } else {
      setEditorText(remoteText);
    }
    if (!publishDate) {
      setPublishDate(businessDateForDefinition(nextDraft.definition));
    }
  }

  function changeEditor(value: string) {
    setEditorText(value);
    setError(null);
    setNotice(null);
    setValidation(null);
    setDiff(null);
    if (draft) writeEditorRecovery(serverId, draft.draftId, draft.revision, value);
  }

  async function reloadSelectedDraft() {
    if (!draft || busy) return;
    if (dirty && !globalThis.confirm("服务端草稿会覆盖编辑区。未保存 JSON 已保留在当前标签页，确定重新加载吗？")) {
      return;
    }
    setBusy("loading");
    setError(null);
    try {
      const nextDraft = await getContentDraft(serverId, draft.draftId);
      clearEditorRecovery(serverId, draft.draftId);
      applyRemoteDraft(nextDraft, false);
      setNotice(`已加载服务端 revision ${nextDraft.revision}。`);
    } catch (nextError) {
      setError(contentErrorMessage(nextError, "草稿重新加载失败"));
    } finally {
      setBusy(null);
    }
  }

  async function submitCreateDraft(event: FormEvent) {
    event.preventDefault();
    if (busy) return;
    const name = createName.trim();
    const base = versions.find((version) => version.versionId === createBaseVersionId)
      ?? (current?.version.versionId === createBaseVersionId ? current.version : null);
    if (name.length < 2) {
      setError("草稿名称至少需要 2 个字符。");
      return;
    }
    if (!base) {
      setError("需要选择一个已发布版本作为草稿基础。");
      return;
    }
    setBusy("draft");
    setError(null);
    setNotice(null);
    try {
      const created = await createContentDraft(serverId, {
        name,
        basedOnVersionId: base.versionId,
        definition: base.definition
      });
      setDrafts((items) => sortDrafts([created, ...items.filter((item) => item.draftId !== created.draftId)]));
      setCreateName("");
      setSelectedDraftId(created.draftId);
      setNotice(`草稿“${created.name}”已创建。`);
    } catch (nextError) {
      setError(contentErrorMessage(nextError, "草稿创建失败"));
    } finally {
      setBusy(null);
    }
  }

  async function saveDraft(): Promise<EconomyContentDraft | null> {
    if (!draft || !editable) return draft;
    if (!parsedEditor.definition) {
      setError(parsedEditor.error ?? "JSON 格式无效。");
      return null;
    }
    if (!dirty) return draft;
    setBusy("save");
    setError(null);
    setNotice(null);
    try {
      const saved = await updateContentDraft(
        serverId,
        draft.draftId,
        draft.revision,
        parsedEditor.definition
      );
      const text = formatDefinition(saved.definition);
      setDraft(saved);
      setDrafts((items) => sortDrafts(items.map((item) => item.draftId === saved.draftId ? saved : item)));
      setEditorText(text);
      setSavedEditorText(text);
      setValidation(saved.lastValidation);
      setRevisionConflict(false);
      clearEditorRecovery(serverId, saved.draftId);
      setNotice(`已保存 revision ${saved.revision}，hash ${shortHash(saved.contentHash)}。`);
      return saved;
    } catch (nextError) {
      if (nextError instanceof ContentApiError && nextError.code === "CONTENT_DRAFT_REVISION_CONFLICT") {
        setRevisionConflict(true);
      }
      setError(contentErrorMessage(nextError, "草稿保存失败"));
      return null;
    } finally {
      setBusy(null);
    }
  }

  async function runValidation() {
    if (!draft || busy) return;
    const target = await saveDraft();
    if (!target) return;
    setBusy("validate");
    setError(null);
    try {
      const result = await validateContentDraft(serverId, target.draftId, target.revision);
      setValidation(result);
      setDraft((value) => value ? { ...value, lastValidation: result } : value);
      setNotice(result.valid
        ? `校验通过，内容 hash 为 ${shortHash(result.contentHash)}。`
        : `校验未通过：${result.errors.length} 个错误、${result.warnings.length} 个警告。`);
    } catch (nextError) {
      if (nextError instanceof ContentApiError && nextError.validation) {
        setValidation(nextError.validation);
      }
      setError(contentErrorMessage(nextError, "内容校验失败"));
    } finally {
      setBusy(null);
    }
  }

  async function loadDiff() {
    if (!draft || busy) return;
    const target = await saveDraft();
    if (!target) return;
    setBusy("diff");
    setError(null);
    try {
      const result = await getContentDraftDiff(serverId, target.draftId);
      setDiff(result);
      setNotice(result.items.length === 0 ? "草稿与基础版本没有差异。" : `已生成 ${result.items.length} 条差异。`);
    } catch (nextError) {
      setError(contentErrorMessage(nextError, "差异读取失败"));
    } finally {
      setBusy(null);
    }
  }

  async function submitPublish(event: FormEvent) {
    event.preventDefault();
    if (!draft || busy) return;
    if (publishReason.trim().length < 8) {
      setError("发布原因至少需要 8 个字符，并会写入审计记录。");
      return;
    }
    if (publishConfirmation !== PUBLISH_CONFIRMATION) {
      setError(`请完整输入确认短语：${PUBLISH_CONFIRMATION}`);
      return;
    }
    setError(null);
    const target = await saveDraft();
    if (!target) return;
    setBusy("publish");
    try {
      const checked = await validateContentDraft(serverId, target.draftId, target.revision);
      setValidation(checked);
      if (!checked.valid) {
        setError(`发布已停止：服务端校验发现 ${checked.errors.length} 个错误。`);
        return;
      }
      const signature = ["publish", target.draftId, target.revision, publishDate, publishReason.trim(), publishConfirmation].join("|");
      const result = await publishContentDraft(serverId, {
        draftId: target.draftId,
        revision: target.revision,
        businessDate: publishDate,
        reason: publishReason.trim(),
        confirmation: publishConfirmation,
        totp: publishTotp,
        idempotencyKey: idempotencyKey(operationKeys.current, signature)
      });
      clearEditorRecovery(serverId, target.draftId);
      setPublishTotp("");
      setPublishConfirmation("");
      setPublishReason("");
      setNotice(`版本 v${result.version.versionNumber} 已激活，hash ${shortHash(result.pointer.contentHash)}${result.replayed ? "（幂等重放）" : ""}。`);
      await refreshOverview();
      const refreshedDraft = await getContentDraft(serverId, target.draftId).catch(() => null);
      if (refreshedDraft) applyRemoteDraft(refreshedDraft, false);
    } catch (nextError) {
      setPublishTotp("");
      if (nextError instanceof ContentApiError && nextError.validation) {
        setValidation(nextError.validation);
      }
      setError(contentErrorMessage(nextError, "内容发布失败"));
    } finally {
      setBusy(null);
    }
  }

  async function submitRollback(event: FormEvent) {
    event.preventDefault();
    if (!current || busy) return;
    if (rollbackReason.trim().length < 8) {
      setError("回滚原因至少需要 8 个字符，并会写入审计记录。");
      return;
    }
    if (rollbackConfirmation !== ROLLBACK_CONFIRMATION) {
      setError(`请完整输入确认短语：${ROLLBACK_CONFIRMATION}`);
      return;
    }
    setBusy("rollback");
    setError(null);
    setNotice(null);
    const targetVersionId = rollbackTarget || null;
    const signature = ["rollback", current.pointer.versionId, targetVersionId ?? "previous", rollbackReason.trim(), rollbackConfirmation].join("|");
    try {
      const result = await rollbackContentVersion(serverId, {
        targetVersionId,
        expectedCurrentVersionId: current.pointer.versionId,
        reason: rollbackReason.trim(),
        confirmation: rollbackConfirmation,
        totp: rollbackTotp,
        idempotencyKey: idempotencyKey(operationKeys.current, signature)
      });
      setRollbackTotp("");
      setRollbackConfirmation("");
      setRollbackReason("");
      setRollbackTarget("");
      setNotice(`已切换到版本 v${result.pointer.versionNumber}，hash ${shortHash(result.pointer.contentHash)}${result.replayed ? "（幂等重放）" : ""}。`);
      await refreshOverview();
    } catch (nextError) {
      setRollbackTotp("");
      setError(contentErrorMessage(nextError, "内容回滚失败"));
    } finally {
      setBusy(null);
    }
  }

  const counts = definitionCounts(current?.version.definition);

  return (
    <section className="content-management-page">
      <header className="content-heading">
        <div>
          <p className="eyebrow">ECONOMY CONTENT / VERSIONED CONTROL</p>
          <h2>版本化内容管理</h2>
          <p>编辑白名单资源、兑换区、商品与任务；发布后以不可变版本和内容 hash 固化。</p>
        </div>
        <div className="content-heading-actions">
          <span className={current ? "content-state ready" : "content-state warning"}>
            <i />{current ? `v${current.pointer.versionNumber} 已激活` : "尚未发布"}
          </span>
          <button className="ghost-button" disabled={Boolean(busy)} onClick={() => void refreshOverview()} type="button">
            {busy === "loading" ? "刷新中…" : "刷新总览"}
          </button>
        </div>
      </header>

      {error ? <div className="content-alert error" role="alert"><strong>操作未完成</strong><span>{error}</span><button onClick={() => setError(null)} type="button">关闭</button></div> : null}
      {notice ? <div className="content-alert success" role="status"><strong>已更新</strong><span>{notice}</span><button onClick={() => setNotice(null)} type="button">关闭</button></div> : null}

      <div className="content-current-grid" aria-label="当前内容版本">
        <ContentMetric label="当前版本" value={current ? `v${current.pointer.versionNumber}` : "--"} detail={current?.pointer.businessDate ?? "无营业日"} />
        <ContentMetric label="内容 hash" value={current ? shortHash(current.pointer.contentHash) : "--"} detail={current?.pointer.contentHash ?? "尚未发布"} mono />
        <ContentMetric label="规则版本" value={current?.pointer.rulesVersion ?? "--"} detail={current ? `更新于 ${formatDate(current.pointer.updatedAt)}` : "等待首个版本"} />
        <ContentMetric label="商品 / 资源" value={`${counts.products} / ${counts.resources}`} detail="版本内定义数量" />
        <ContentMetric label="兑换区 / 任务" value={`${counts.zones} / ${counts.tasks}`} detail="方案 A 运行内容" />
      </div>

      <div className="content-workbench">
        <aside className="content-sidebar-card">
          <section>
            <div className="content-card-title"><div><h3>草稿</h3><small>{drafts.length} 个可编辑草稿</small></div></div>
            <div className="content-list">
              {drafts.map((item) => (
                <button
                  className={item.draftId === selectedDraftId ? "active" : ""}
                  key={item.draftId}
                  onClick={() => setSelectedDraftId(item.draftId)}
                  type="button"
                >
                  <span><strong>{item.name}</strong><em>r{item.revision}</em></span>
                  <small>{shortHash(item.contentHash)} · {formatRelative(item.updatedAt)}</small>
                </button>
              ))}
              {drafts.length === 0 ? <p className="content-empty">暂无草稿，请从已发布版本创建。</p> : null}
            </div>
          </section>

          <form className="content-create-form" onSubmit={submitCreateDraft}>
            <h3>创建草稿</h3>
            <label><span>草稿名称</span><input maxLength={128} onChange={(event) => setCreateName(event.target.value)} placeholder="例如：第 3 周资源调整" value={createName} /></label>
            <label><span>基于版本</span>
              <select onChange={(event) => setCreateBaseVersionId(event.target.value)} value={createBaseVersionId}>
                {versions.map((version) => <option key={version.versionId} value={version.versionId}>v{version.versionNumber} · {version.businessDate}</option>)}
                {versions.length === 0 && current ? <option value={current.version.versionId}>v{current.version.versionNumber} · {current.version.businessDate}</option> : null}
              </select>
            </label>
            <button className="primary-button" disabled={Boolean(busy) || !createBaseVersionId} type="submit">{busy === "draft" ? "创建中…" : "复制为新草稿"}</button>
          </form>

          <section className="content-version-history">
            <div className="content-card-title"><div><h3>版本历史</h3><small>{versions.length} 个不可变版本</small></div></div>
            <ol>
              {versions.map((version) => (
                <li className={version.versionId === currentVersionId ? "current" : ""} key={version.versionId}>
                  <button onClick={() => version.versionId !== currentVersionId && setRollbackTarget(version.versionId)} type="button">
                    <span><strong>v{version.versionNumber}</strong>{version.versionId === currentVersionId ? <em>当前</em> : null}</span>
                    <small>{version.businessDate} · {shortHash(version.contentHash)}</small>
                  </button>
                </li>
              ))}
            </ol>
          </section>
        </aside>

        <main className="content-editor-card">
          {draft ? <>
            <header className="content-editor-heading">
              <div><p>CONTENT DEFINITION / JSON</p><h3>{draft.name}</h3><small>revision {draft.revision} · {draft.updatedBy} · {formatDate(draft.updatedAt)}</small></div>
              <div>
                <span className={dirty ? "content-dirty dirty" : "content-dirty"}>{dirty ? "未保存" : "已同步"}</span>
                <button disabled={Boolean(busy)} onClick={() => void reloadSelectedDraft()} type="button">重新加载</button>
              </div>
            </header>

            {revisionConflict ? <div className="content-conflict"><strong>检测到并发修改</strong><p>编辑区中的本地 JSON 未丢失。复制需要保留的内容，重新加载服务端 revision 后再合并保存。</p><button onClick={() => void reloadSelectedDraft()} type="button">重新加载服务端版本</button></div> : null}

            <label className="content-json-editor">
              <span className="sr-only">经济内容 JSON</span>
              <textarea
                aria-invalid={Boolean(parsedEditor.error)}
                onChange={(event) => changeEditor(event.target.value)}
                readOnly={!editable}
                spellCheck={false}
                value={editorText}
              />
            </label>
            <div className="content-editor-status">
              <span className={parsedEditor.error ? "invalid" : "valid"}>{parsedEditor.error ?? "JSON 结构可解析"}</span>
              <code title={draft.contentHash}>{shortHash(draft.contentHash)}</code>
            </div>
            <footer className="content-editor-actions">
              <button disabled={Boolean(busy) || !editable || !dirty || Boolean(parsedEditor.error)} onClick={() => void saveDraft()} type="button">{busy === "save" ? "保存中…" : "保存草稿"}</button>
              <button disabled={Boolean(busy) || !editable || Boolean(parsedEditor.error)} onClick={() => void loadDiff()} type="button">{busy === "diff" ? "生成中…" : "保存并查看 Diff"}</button>
              <button className="primary-button" disabled={Boolean(busy) || !editable || Boolean(parsedEditor.error)} onClick={() => void runValidation()} type="button">{busy === "validate" ? "校验中…" : "保存并校验"}</button>
            </footer>
          </> : <div className="content-editor-empty"><span>{busy === "loading" ? "…" : "{}"}</span><strong>{busy === "loading" ? "正在加载草稿" : "请选择或创建一个草稿"}</strong><p>编辑器只保存完整 JSON 定义，服务端负责结构、依赖和白名单校验。</p></div>}
        </main>

        <aside className="content-review-column">
          <ValidationPanel validation={validation} />
          <DiffPanel diff={diff} />

          <form className="content-risk-card publish" onSubmit={submitPublish}>
            <header><div><p>HIGH RISK</p><h3>校验并发布</h3></div><span>维护模式</span></header>
            <p>服务端会再次校验。发布要求经济维护模式已开启，且所有报价与结算已排空。</p>
            <label><span>营业日</span><input onChange={(event) => setPublishDate(event.target.value)} required type="date" value={publishDate} /></label>
            <label><span>审计原因（至少 8 字符）</span><textarea maxLength={512} onChange={(event) => setPublishReason(event.target.value)} placeholder="说明本次价格、资源或任务调整" value={publishReason} /></label>
            <label><span>确认短语</span><input autoComplete="off" onChange={(event) => setPublishConfirmation(event.target.value)} placeholder={PUBLISH_CONFIRMATION} value={publishConfirmation} /></label>
            <label><span>6 位 TOTP</span><input autoComplete="one-time-code" inputMode="numeric" maxLength={6} onChange={(event) => setPublishTotp(event.target.value.replace(/\D/g, ""))} value={publishTotp} /></label>
            <button disabled={Boolean(busy) || !editable || publishConfirmation !== PUBLISH_CONFIRMATION || publishReason.trim().length < 8 || !/^\d{6}$/.test(publishTotp)} type="submit">{busy === "publish" ? "发布中…" : "发布并原子切换"}</button>
          </form>

          <form className="content-risk-card rollback" onSubmit={submitRollback}>
            <header><div><p>HIGH RISK</p><h3>回滚版本指针</h3></div><span>可审计</span></header>
            <p>回滚只切换到已有不可变版本；同样要求维护模式和结算排空。</p>
            <label><span>目标版本</span>
              <select onChange={(event) => setRollbackTarget(event.target.value)} value={rollbackTarget}>
                <option value="">自动选择上一版本</option>
                {rollbackVersions.map((version) => <option key={version.versionId} value={version.versionId}>v{version.versionNumber} · {version.businessDate} · {shortHash(version.contentHash)}</option>)}
              </select>
            </label>
            <label><span>审计原因（至少 8 字符）</span><textarea maxLength={512} onChange={(event) => setRollbackReason(event.target.value)} placeholder="说明回滚原因和影响范围" value={rollbackReason} /></label>
            <label><span>确认短语</span><input autoComplete="off" onChange={(event) => setRollbackConfirmation(event.target.value)} placeholder={ROLLBACK_CONFIRMATION} value={rollbackConfirmation} /></label>
            <label><span>6 位 TOTP</span><input autoComplete="one-time-code" inputMode="numeric" maxLength={6} onChange={(event) => setRollbackTotp(event.target.value.replace(/\D/g, ""))} value={rollbackTotp} /></label>
            <button disabled={Boolean(busy) || !current || rollbackConfirmation !== ROLLBACK_CONFIRMATION || rollbackReason.trim().length < 8 || !/^\d{6}$/.test(rollbackTotp)} type="submit">{busy === "rollback" ? "回滚中…" : "确认回滚"}</button>
          </form>
        </aside>
      </div>
    </section>
  );
}

function ContentMetric({ label, value, detail, mono = false }: { label: string; value: string; detail: string; mono?: boolean }) {
  return <article className="content-metric"><span>{label}</span><strong className={mono ? "mono" : ""} title={detail}>{value}</strong><small>{detail}</small></article>;
}

function ValidationPanel({ validation }: { validation: ContentValidationResult | null }) {
  return <section className="content-review-card">
    <header><div><p>SERVER VALIDATION</p><h3>校验结果</h3></div>{validation ? <span className={validation.valid ? "passed" : "failed"}>{validation.valid ? "通过" : "未通过"}</span> : <span>未校验</span>}</header>
    {!validation ? <p className="content-review-empty">保存草稿后运行校验，检查资源目录、依赖版本、商品、兑换区和轮换规则。</p> : <>
      <div className="content-validation-summary"><strong>{validation.errors.length}</strong><span>错误</span><strong>{validation.warnings.length}</strong><span>警告</span></div>
      <code title={validation.contentHash}>{shortHash(validation.contentHash)}</code>
      <div className="content-issue-list">
        {[...validation.errors, ...validation.warnings].slice(0, 12).map((issue, index) => <article className={index < validation.errors.length ? "error" : "warning"} key={`${issue.code}-${issue.path}-${index}`}><strong>{issue.code}</strong><code>{issue.path || "$"}</code><p>{issue.message}</p></article>)}
      </div>
    </>}
  </section>;
}

function DiffPanel({ diff }: { diff: ContentDiffResult | null }) {
  return <section className="content-review-card">
    <header><div><p>BASE COMPARISON</p><h3>版本差异</h3></div><span>{diff ? `${diff.items.length} 项` : "未生成"}</span></header>
    {!diff ? <p className="content-review-empty">Diff 以草稿的 basedOnVersion 为基线，由服务端生成。</p> : diff.items.length === 0 ? <p className="content-review-empty">草稿与基础版本一致。</p> : <div className="content-diff-list">
      {diff.items.slice(0, 20).map((item, index) => <details key={`${item.path}-${index}`}><summary><span className={item.kind.toLocaleLowerCase()}>{diffKindLabel(item.kind)}</span><code>{item.path}</code></summary><dl><div><dt>之前</dt><dd>{item.before ?? "∅"}</dd></div><div><dt>之后</dt><dd>{item.after ?? "∅"}</dd></div></dl></details>)}
    </div>}
  </section>;
}

export function parseDefinition(text: string): { definition: ContentDefinition | null; error: string | null } {
  try {
    const value = JSON.parse(text) as unknown;
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      return { definition: null, error: "顶层 JSON 必须是对象。" };
    }
    return { definition: value as ContentDefinition, error: null };
  } catch (error) {
    return { definition: null, error: error instanceof Error ? `JSON 无法解析：${error.message}` : "JSON 无法解析。" };
  }
}

export function businessDateForDefinition(definition: ContentDefinition, now = new Date()): string {
  const timeZone = typeof definition.timeZoneId === "string" ? definition.timeZoneId : "Asia/Shanghai";
  try {
    const parts = new Intl.DateTimeFormat("en-CA", {
      timeZone,
      year: "numeric",
      month: "2-digit",
      day: "2-digit"
    }).formatToParts(now);
    const value = Object.fromEntries(parts.map((part) => [part.type, part.value]));
    return `${value.year}-${value.month}-${value.day}`;
  } catch {
    return now.toISOString().slice(0, 10);
  }
}

function definitionCounts(definition?: ContentDefinition) {
  return {
    products: Array.isArray(definition?.products) ? definition.products.length : 0,
    resources: Array.isArray(definition?.resources) ? definition.resources.length : 0,
    zones: Array.isArray(definition?.exchangeZones) ? definition.exchangeZones.length : 0,
    tasks: Array.isArray(definition?.tasks) ? definition.tasks.length : 0
  };
}

function formatDefinition(definition: ContentDefinition): string {
  return JSON.stringify(definition, null, 2);
}

function sortDrafts(items: EconomyContentDraft[]) {
  return [...items].sort((left, right) => Date.parse(right.updatedAt) - Date.parse(left.updatedAt));
}

function sortVersions(items: EconomyContentVersion[]) {
  return [...items].sort((left, right) => right.versionNumber - left.versionNumber);
}

function shortHash(value?: string | null): string {
  if (!value) return "--";
  return value.length > 15 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value;
}

function formatDate(value?: string | null): string {
  if (!value) return "--";
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return "--";
  return date.toLocaleString("zh-CN", { hour12: false });
}

function formatRelative(value: string): string {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return "未知时间";
  const seconds = Math.max(0, Math.floor((Date.now() - timestamp) / 1_000));
  if (seconds < 60) return "刚刚";
  if (seconds < 3_600) return `${Math.floor(seconds / 60)} 分钟前`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3_600)} 小时前`;
  return `${Math.floor(seconds / 86_400)} 天前`;
}

function diffKindLabel(kind: string): string {
  const value = kind.toLocaleLowerCase();
  if (value === "added") return "新增";
  if (value === "removed") return "移除";
  return "修改";
}

function idempotencyKey(keys: Map<string, string>, signature: string): string {
  const existing = keys.get(signature);
  if (existing) return existing;
  const key = globalThis.crypto?.randomUUID?.() ?? `content-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  keys.set(signature, key);
  return key;
}

type EditorRecovery = { revision: number; text: string };

function recoveryKey(serverId: string, draftId: string) {
  return `pal-control.economy-content.editor.${serverId}.${draftId}`;
}

function readEditorRecovery(serverId: string, draftId: string): EditorRecovery | null {
  try {
    const raw = globalThis.sessionStorage?.getItem(recoveryKey(serverId, draftId));
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<EditorRecovery>;
    return Number.isInteger(value.revision) && typeof value.text === "string"
      ? { revision: value.revision!, text: value.text }
      : null;
  } catch {
    return null;
  }
}

function writeEditorRecovery(serverId: string, draftId: string, revision: number, text: string) {
  try {
    globalThis.sessionStorage?.setItem(recoveryKey(serverId, draftId), JSON.stringify({ revision, text }));
  } catch {
    // The editor remains usable when browser storage is unavailable.
  }
}

function clearEditorRecovery(serverId: string, draftId: string) {
  try {
    globalThis.sessionStorage?.removeItem(recoveryKey(serverId, draftId));
  } catch {
    // Nothing else is required; server state remains authoritative.
  }
}
