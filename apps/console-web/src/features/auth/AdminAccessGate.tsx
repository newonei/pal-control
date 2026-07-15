import { type FormEvent, type ReactNode, useEffect, useState } from "react";
import {
  authenticateAdmin,
  getAdminAuthState,
  probeAdminSession,
  signOutAdmin,
  subscribeAdminAuth,
  type AdminAuthState
} from "../../lib/api/adminFetch";

export function AdminAccessGate({ children }: { children: ReactNode }) {
  const [auth, setAuth] = useState<AdminAuthState>(getAdminAuthState);
  const [apiKey, setApiKey] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const unsubscribe = subscribeAdminAuth(setAuth);
    void probeAdminSession(controller.signal);
    return () => {
      controller.abort();
      unsubscribe();
    };
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFormError(null);
    try {
      const result = await authenticateAdmin(apiKey);
      if (result.status === "authenticated") {
        setApiKey("");
      }
    } catch (error) {
      setFormError(error instanceof Error ? error.message : "无法保存管理员凭据。");
    }
  }

  if (auth.status === "checking") {
    return <main className="admin-auth-page">
      <section className="admin-auth-card" aria-live="polite">
        <div className="admin-auth-mark">PC</div>
        <p className="eyebrow">PAL CONTROL / ADMIN</p>
        <h1>正在验证管理员会话</h1>
        <p>正在向本机控制 API 探测当前标签页的访问权限……</p>
        <span className="admin-auth-progress" aria-hidden="true" />
      </section>
    </main>;
  }

  if (auth.status !== "authenticated") {
    return <main className="admin-auth-page">
      <section className="admin-auth-card">
        <div className="admin-auth-mark">PC</div>
        <p className="eyebrow">PAL CONTROL / ADMIN</p>
        <h1>管理员登录</h1>
        <p>输入部署时生成的 API Key。凭据只保存在当前浏览器标签页的会话存储中，关闭标签页后自动消失。</p>
        <div className={auth.status === "unavailable" ? "admin-auth-alert warning" : "admin-auth-alert"} role="status">
          {auth.message}
        </div>
        <form className="admin-auth-form" onSubmit={submit}>
          <label htmlFor="admin-api-key">管理员 API Key</label>
          <input
            autoComplete="off"
            autoFocus
            id="admin-api-key"
            maxLength={512}
            minLength={24}
            onChange={(event) => setApiKey(event.target.value)}
            placeholder="粘贴 X-Pal-Admin-Key 对应的原始密钥"
            required
            spellCheck={false}
            type="password"
            value={apiKey}
          />
          {formError ? <p className="admin-auth-form-error" role="alert">{formError}</p> : null}
          <button className="primary-button" disabled={apiKey.trim().length < 24} type="submit">
            验证并进入控制台
          </button>
        </form>
        <small>Console 不会把此密钥写入 localStorage、Cookie 或 URL。</small>
      </section>
    </main>;
  }

  return <div className="admin-session-shell">
    <div className="admin-session-bar" aria-label="当前管理员会话">
      <div>
        <span>已认证管理员</span>
        <strong>{auth.session.subject}</strong>
      </div>
      <div className="admin-session-roles" aria-label="管理员角色">
        {auth.session.roles.map((role) => <code key={role}>{role}</code>)}
      </div>
      <button className="ghost-button" onClick={() => signOutAdmin()} type="button">注销</button>
    </div>
    {children}
  </div>;
}
