import { useEffect, useState } from "react";
import { getSession, logout, PlayerSession } from "./api";
import { Login } from "./Login";
import { Portal } from "./Portal";

const csrfStorageKey = "pal-player-csrf";

export function App() {
  const [session, setSession] = useState<PlayerSession | null>(null);
  const [csrfToken, setCsrfToken] = useState(() => sessionStorage.getItem(csrfStorageKey));
  const [restoring, setRestoring] = useState(true);
  const [sessionNotice, setSessionNotice] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    getSession()
      .then((restored) => {
        if (!active) return;
        if (!restored.authenticated) {
          clearSession();
          return;
        }
        setSession(restored);
        if (restored.csrfToken) {
          sessionStorage.setItem(csrfStorageKey, restored.csrfToken);
          setCsrfToken(restored.csrfToken);
        }
      })
      .catch(() => { if (active) clearSession(); })
      .finally(() => { if (active) setRestoring(false); });
    return () => { active = false; };
  }, []);

  function authenticate(next: PlayerSession) {
    setSession(next);
    setCsrfToken(next.csrfToken);
    setSessionNotice(null);
    if (next.csrfToken) sessionStorage.setItem(csrfStorageKey, next.csrfToken);
  }

  function clearSession(notice: string | null = null) {
    setSession(null);
    setCsrfToken(null);
    setSessionNotice(notice);
    sessionStorage.removeItem(csrfStorageKey);
  }

  async function signOut() {
    try {
      if (csrfToken) await logout(csrfToken);
    } finally {
      clearSession();
    }
  }

  if (restoring) return <div className="boot" role="status"><span className="brand-mark">幻</span><p>正在恢复安全会话…</p></div>;
  if (!session?.authenticated || !csrfToken) return <Login initialMessage={sessionNotice} onAuthenticated={authenticate} />;
  return <Portal
    session={session}
    csrfToken={csrfToken}
    onLogout={signOut}
    onSessionExpired={() => clearSession("安全会话已过期。请重新完成平台身份和本周游戏角色验证后再继续。")}
  />;
}
