import { FormEvent, useMemo, useState } from "react";
import { CodeChallenge, PlayerSession, requestCode, verifyCode } from "./api";

type Props = { onAuthenticated: (session: PlayerSession) => void };

export function Login({ onAuthenticated }: Props) {
  const [userId, setUserId] = useState("");
  const [challenge, setChallenge] = useState<CodeChallenge | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(null);

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
    if (!validUserId) return setMessage("请输入完整的平台 UserId，Steam 玩家也可以直接填写 17 位 SteamID64。");
    setBusy(true);
    setMessage(null);
    try {
      const next = await requestCode(normalizedUserId);
      setChallenge(next);
      setCode("");
      setMessage("验证码已发送到游戏内，请保持角色在线并查看通知。");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "验证码请求失败");
    } finally {
      setBusy(false);
    }
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
        <span className="eyebrow">PALWORLD EXTRACTION</span>
        <h1 id="brand-title">幻兽商域</h1>
        <p>每一趟出发都有代价，每一次撤离都有收获。</p>
        <div className="login-features" aria-label="玩法特点">
          <span>每周新档</span><span>永久商域币</span><span>在线即时发货</span>
        </div>
      </section>

      <section className="login-card" aria-labelledby="login-title">
        <div className="mark" aria-hidden="true">幻</div>
        <span className="eyebrow">PLAYER ACCESS</span>
        <h2 id="login-title">玩家身份验证</h2>
        <p className="muted">网页不会要求游戏密码。验证码只会发送给当前在线角色。</p>

        {!challenge ? (
          <form onSubmit={begin}>
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
            <small id="user-id-help">Steam 玩家可填写 steam_ 加 SteamID64，或直接粘贴 17 位 SteamID64；不是角色昵称。</small>
            <button className="primary full" disabled={busy || !validUserId} type="submit">
              {busy ? "正在发送…" : "获取游戏内验证码"}
            </button>
          </form>
        ) : (
          <form onSubmit={verify}>
            <div className="challenge-user">
              <span>正在验证</span><strong>{normalizedUserId}</strong>
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
