import { useEffect, useMemo, useState } from 'react';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import {
  AuthenticationResult,
  PublicClientApplication,
  InteractionType,
} from '@azure/msal-browser';
import { initMsal, DEFAULT_CLIENT_ID } from './auth/msalConfig';
import { PreSignIn, SignInConfig } from './components/PreSignIn';
import { MainApp } from './components/MainApp';
import { logger } from './utils/logger';

export default function App() {
  const [config, setConfig] = useState<SignInConfig | null>(null);
  const [pca, setPca] = useState<PublicClientApplication | null>(null);
  const [error, setError] = useState<string | null>(null);

  // Reset MSAL instance whenever the user changes the client ID.
  useEffect(() => {
    if (!config) return;
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
  }, [config?.clientId]);

  if (!config) {
    return (
      <div className="app-shell">
        <PreSignIn onSubmit={setConfig} />
      </div>
    );
  }

  if (!pca) {
    return (
      <div className="app-shell">
        <div className="app-body"><div className="card">Initializing MSAL… {error && <div className="pill err">{error}</div>}</div></div>
      </div>
    );
  }

  return (
    <MsalProvider instance={pca}>
      <Authenticated config={config} onReset={() => setConfig(null)} />
    </MsalProvider>
  );
}

function Authenticated({ config, onReset }: { config: SignInConfig; onReset: () => void }) {
  const { instance, accounts } = useMsal();
  const isAuthed = useIsAuthenticated();
  const [signingIn, setSigningIn] = useState(false);
  const [authResult, setAuthResult] = useState<AuthenticationResult | null>(null);

  const scopes = useMemo(() => config.scopes.filter(Boolean), [config.scopes]);

  async function doSignIn() {
    setSigningIn(true);
    try {
      const req = { scopes };
      const result = config.interaction === InteractionType.Popup
        ? await instance.loginPopup(req)
        : await (async () => { await instance.loginRedirect(req); return null; })();
      if (result) {
        setAuthResult(result);
        logger.info('app', `signed in as ${result.account?.username}`);
      }
    } catch (e: any) {
      logger.error('app', 'sign-in failed', e);
    } finally {
      setSigningIn(false);
    }
  }

  async function silentToken() {
    if (!accounts[0]) return null;
    try {
      const r = await instance.acquireTokenSilent({ scopes, account: accounts[0] });
      setAuthResult(r);
      return r;
    } catch (e) {
      logger.warn('app', 'silent token failed, falling back to interactive', e);
      const r = await instance.acquireTokenPopup({ scopes, account: accounts[0] });
      setAuthResult(r);
      return r;
    }
  }

  if (!isAuthed) {
    return (
      <div className="app-shell">
        <header className="app-header">
          <h1>CMSP Demo — Sign in</h1>
          <button className="secondary" onClick={onReset}>Change config</button>
        </header>
        <div className="app-body">
          <div className="card col" style={{ maxWidth: 720 }}>
            <div className="row">
              <strong>Client ID:</strong> <code>{config.clientId || DEFAULT_CLIENT_ID}</code>
            </div>
            <div className="row">
              <strong>Scopes:</strong> <code>{scopes.join(' ') || '(none — OIDC only)'}</code>
            </div>
            <div className="row">
              <strong>Interaction:</strong> <code>{config.interaction === InteractionType.Popup ? 'Popup' : 'Redirect'}</code>
            </div>
            <div className="row">
              <button onClick={doSignIn} disabled={signingIn}>{signingIn ? 'Signing in…' : 'Sign in'}</button>
            </div>
          </div>
        </div>
      </div>
    );
  }

  return <MainApp config={config} onReset={onReset} authResult={authResult} onAcquireToken={silentToken} />;
}
