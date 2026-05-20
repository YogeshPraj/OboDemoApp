import { useEffect, useMemo, useState } from 'react';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import { PublicClientApplication } from '@azure/msal-browser';
import { initMsal, DEFAULT_CLIENT_ID } from './auth/msalConfig';
import { MsalAuthProvider, SessionAuthProvider, useAuth } from './auth/AuthContext';
import { PreSignIn, SignInConfig } from './components/PreSignIn';
import { MainApp } from './components/MainApp';
import { logger } from './utils/logger';

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? 'http://localhost:5080';

export default function App() {
  const [config, setConfig] = useState<SignInConfig | null>(null);

  if (!config) {
    return (
      <div className="app-shell">
        <PreSignIn onSubmit={setConfig} />
      </div>
    );
  }

  if (config.mode === 'server-oidc') {
    return <ServerOidcRoot config={config} onReset={() => setConfig(null)} />;
  }
  return <MsalRoot config={config} onReset={() => setConfig(null)} />;
}

// ── MSAL flow (Popup / Redirect) ────────────────────────────────────────────
function MsalRoot({ config, onReset }: { config: SignInConfig; onReset: () => void }) {
  const [pca, setPca] = useState<PublicClientApplication | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const inst = await initMsal(config.clientId || DEFAULT_CLIENT_ID);
        if (!cancelled) setPca(inst);
      } catch (e: any) {
        setError(e?.message ?? String(e));
        logger.error('app', 'initMsal failed', e);
      }
    })();
    return () => { cancelled = true; };
  }, [config.clientId]);

  if (!pca) {
    return (
      <div className="app-shell">
        <div className="app-body">
          <div className="card">Initializing MSAL… {error && <div className="pill err">{error}</div>}</div>
        </div>
      </div>
    );
  }

  return (
    <MsalProvider instance={pca}>
      <MsalSignInGate config={config} onReset={onReset} />
    </MsalProvider>
  );
}

function MsalSignInGate({ config, onReset }: { config: SignInConfig; onReset: () => void }) {
  const { instance } = useMsal();
  const isAuthed = useIsAuthenticated();
  const [signingIn, setSigningIn] = useState(false);

  const scopes = useMemo(() => config.scopes.filter(Boolean), [config.scopes]);
  // The first BFF-shaped scope the user picked (or fall back to api://<clientId>/access_as_user).
  const bffScope = useMemo(() => {
    const fromUser = scopes.find(s => s.startsWith('api://') && !s.endsWith('/.default'));
    if (fromUser) return fromUser;
    return `api://${config.clientId || DEFAULT_CLIENT_ID}/access_as_user`;
  }, [scopes, config.clientId]);

  async function doSignIn() {
    setSigningIn(true);
    try {
      if (config.mode === 'msal-popup') {
        await instance.loginPopup({ scopes });
      } else {
        await instance.loginRedirect({ scopes });
      }
    } catch (e: any) {
      logger.error('app', 'sign-in failed', e);
    } finally {
      setSigningIn(false);
    }
  }

  if (!isAuthed) {
    return (
      <div className="app-shell">
        <header className="app-header">
          <h1>CMSP Demo — Sign in (MSAL {config.mode === 'msal-popup' ? 'Popup' : 'Redirect'})</h1>
          <button className="secondary" onClick={onReset}>Change config</button>
        </header>
        <div className="app-body">
          <div className="card col" style={{ maxWidth: 720 }}>
            <div className="row"><strong>Client ID:</strong> <code>{config.clientId || DEFAULT_CLIENT_ID}</code></div>
            <div className="row"><strong>Scopes:</strong> <code>{scopes.join(' ') || '(none — OIDC only)'}</code></div>
            <div className="row"><strong>Mode:</strong> <code>{config.mode}</code></div>
            <div className="row"><button onClick={doSignIn} disabled={signingIn}>{signingIn ? 'Signing in…' : 'Sign in'}</button></div>
          </div>
        </div>
      </div>
    );
  }

  return (
    <MsalAuthProvider bffScope={bffScope}>
      <MainAppHost onReset={onReset} />
    </MsalAuthProvider>
  );
}

// ── Server-OIDC flow ────────────────────────────────────────────────────────
function ServerOidcRoot({ config, onReset }: { config: SignInConfig; onReset: () => void }) {
  return (
    <SessionAuthProvider>
      <ServerOidcGate onReset={onReset} />
    </SessionAuthProvider>
  );
}

function ServerOidcGate({ onReset }: { onReset: () => void }) {
  const { isAuthenticated } = useAuth();

  function signIn() {
    const url = `${API_BASE}/api/auth/sign-in?returnUrl=${encodeURIComponent(window.location.pathname || '/')}`;
    window.location.href = url;
  }

  if (!isAuthenticated) {
    return (
      <div className="app-shell">
        <header className="app-header">
          <h1>CMSP Demo — Sign in (Server OIDC)</h1>
          <button className="secondary" onClick={onReset}>Change config</button>
        </header>
        <div className="app-body">
          <div className="card col" style={{ maxWidth: 720 }}>
            <p className="muted" style={{ margin: 0 }}>
              The BFF will redeem the auth code with <code>{'{AadAppIdUri}/.default'}</code>. The browser
              will only receive an HttpOnly <code>CMSP_SESSION</code> cookie. Tokens never leave the BFF.
            </p>
            <div className="row">
              <button onClick={signIn}>Sign in with Server OIDC</button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return <MainAppHost onReset={onReset} />;
}

// ── Common host — used by both flows once authenticated ─────────────────────
function MainAppHost({ onReset }: { onReset: () => void }) {
  return <MainApp onReset={onReset} />;
}
