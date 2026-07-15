import { FormEvent, useEffect, useMemo, useState } from "react";
import {
  CodeChallenge,
  getAuthenticationMode,
  PlayerAuthenticationMode,
  PlayerSession,
  requestCode,
  steamLoginStartUrl,
  verifyCode
} from "./api";

type Props = { onAuthenticated: (session: PlayerSession) => void };

export function Login({ onAuthenticated }: Props) {
  const [userId, setUserId] = useState("");
  const [challenge, setChallenge] = useState<CodeChallenge | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [authentication, setAuthentication] = useState<PlayerAuthenticationMode | null>(null);

  useEffect(() => {
    let active = true;
    getAuthenticationMode()
      .then((mode) => { if (active) setAuthentication(mode); })
      .catch((error) => {
        if (active) setMessage(error instanceof Error ? error.message : "无法读取登录模式");
      });
    return () => { active = false; };
  }, []);

  const rawIdentifier = userId.trim().toLowerCase();
  const normalizedUserId = /^\d{17}$/.test(rawIdentifier)
    ? `steam_${rawIdentifier}`
    : rawIdentifier;
  const validUserId = useMemo(
    () => /^(steam|gdk|xbox|xuid|epic)_[a-z0-9]{3,64}$/i.test(normalizedUserId),
    [normalizedUserId]
  );

  async function begin(event: FormEvent) {
    event.preventDefault();
    const openIdPending = authentication?.steamOpenIdRequired === true &&
      authentication.pendingPlatformIdentity;
    if (!openIdPending && !validUserId) {
      return setMessage("请输入完整的平台 UserId，Steam 玩家也可以直接填写 17 位 SteamID64。");
    }
    setBusy(true);
    setMessage(null);
    try {
      const next = await requestCode(openIdPending ? null : normalizedUserId);
      setChallenge(next);
      setCode("");
      setMessage("验证码已发送到游戏内，请保持角色在线并查看通知。");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "验证码请求失败");
    } finally {
      setBusy(false);
    }
  }

  function beginSteamLogin() {
    window.location.assign(steamLoginStartUrl());
  }

  async function verify(event: FormEvent) {
    event.preventDefault();
    if (!challenge || code.length !== 8) return;
    setBusy(true);
    setMessage(null);
    try {
      const session = await verifyCode(challenge.challengeId, code);
      if (!session.authenticated || !session.csrfToken) throw new Error("服务器没有建立有效会话，请重新验证");
      onAuthenticated(session);
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "验证码验证失败");
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="login-shell">
      <section className="login-brand" aria-labelledby="brand-title">
        <span className="eyebrow">PALWORLD RESOURCE ECONOMY</span>
        <h1 id="brand-title">幻兽商域</h1>
        <p>经营一周世界，把资源转化为下一轮战备。</p>
        <div className="login-features" aria-label="玩法特点">
          <span>每周新档</span><span>永久商域币</span><span>在线即时发货</span>
        </div>
      </section>

      <section className="login-card" aria-labelledby="login-title">
        <div className="mark" aria-hidden="true">幻</div>
        <span className="eyebrow">PLAYER ACCESS</span>
        <h2 id="login-title">玩家身份验证</h2>
        <p className="muted">网页不会接收 Steam 或游戏密码。公网服先在 Steam 官方页面验证，再用游戏内验证码绑定本周角色。</p>

        {!challenge ? (
          authentication?.steamOpenIdRequired && !authentication.pendingPlatformIdentity ? (
            <div className="login-step">
              <p>先跳转到 <strong>steamcommunity.com</strong> 验证 Steam 身份；返回后还要输入发送到当前在线角色的验证码。</p>
              <button className="primary full" type="button" onClick={beginSteamLogin}>
                使用 Steam 安全登录
              </button>
              <small>只会跳转到 Steam 官方 OpenID 页面，本网站不会看到或保存你的 Steam 密码。</small>
            </div>
          ) : (
            <form onSubmit={begin}>
              {authentication?.steamOpenIdRequired ? (
                <div className="challenge-user">
                  <span>平台身份</span><strong>Steam 已验证，等待绑定本周角色</strong>
                </div>
              ) : (
                <>
                  <label htmlFor="platform-user-id">平台 UserId / SteamID64</label>
                  <input
                    id="platform-user-id"
                    value={userId}
                    onChange={(event) => setUserId(event.target.value)}
                    placeholder="steam_7656119… 或 17 位 SteamID64"
                    autoComplete="username"
                    spellCheck={false}
                    maxLength={69}
                    required
                    aria-describedby="user-id-help"
                  />
                  <small id="user-id-help">可信好友服 fallback：验证码证明你控制当前在线角色，但不等同于 Steam 网站身份验证。</small>
                </>
              )}
              <button
                className="primary full"
                disabled={busy || (!authentication?.steamOpenIdRequired && !validUserId)}
                type="submit"
              >
                {busy ? "正在发送…" : "获取游戏内验证码"}
              </button>
            </form>
          )
        ) : (
          <form onSubmit={verify}>
            <div className="challenge-user">
              <span>正在验证</span>
              <strong>{authentication?.steamOpenIdRequired ? "Steam 身份 + 当前周角色" : normalizedUserId}</strong>
              <button type="button" className="text-button" onClick={() => setChallenge(null)}>更换账号</button>
            </div>
            <label htmlFor="verify-code">8 位游戏内验证码</label>
            <input
              id="verify-code"
              className="code-input"
              value={code}
              onChange={(event) => setCode(event.target.value.replace(/\D/g, "").slice(0, 8))}
              placeholder="00000000"
              autoComplete="one-time-code"
              inputMode="numeric"
              pattern="[0-9]{8}"
              maxLength={8}
              autoFocus
              required
            />
            <small>有效期至 {new Date(challenge.expiresAt).toLocaleTimeString("zh-CN")}</small>
            <button className="primary full" disabled={busy || code.length !== 8} type="submit">
              {busy ? "正在验证…" : "进入玩家商城"}
            </button>
          </form>
        )}

        <div className="status-line" role="status" aria-live="polite">{message}</div>
        <p className="security-note">安全提示：不要把游戏内验证码发送给其他人。</p>
      </section>
    </main>
  );
}
