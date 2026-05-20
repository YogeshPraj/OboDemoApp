import { useState } from 'react';
import { AuthenticationResult } from '@azure/msal-browser';
import { useMsal } from '@azure/msal-react';
import { SignInConfig } from './PreSignIn';
import { WidgetTab } from './tabs/WidgetTab';
import { OboTab } from './tabs/OboTab';
import { LogsTab } from './tabs/LogsTab';
import { NetworkTab } from './tabs/NetworkTab';
import { logger } from '../utils/logger';

type TabId = 'widget' | 'obo' | 'logs' | 'network';

export function MainApp({
  config, onReset, authResult, onAcquireToken,
}: {
  config: SignInConfig;
  onReset: () => void;
  authResult: AuthenticationResult | null;
  onAcquireToken: () => Promise<AuthenticationResult | null>;
}) {
  const { instance, accounts } = useMsal();
  const [tab, setTab] = useState<TabId>('widget');
  const account = accounts[0];

  // Pull OID and PUID from the cached ID-token claims.
  // OID  — present on both AAD and MSA accounts.
  // PUID — Passport Unique ID, present on MSA (personal) accounts only.
  const claims = account?.idTokenClaims as Record<string, string> | undefined;
  const oid  = claims?.oid  ?? account?.localAccountId;
  const puid = claims?.puid ?? undefined;

  async function signOut() {
    logger.info('app', `signing out ${account?.username}`);
    await instance.logoutPopup({ account });
    onReset();
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <h1>CMSP Demo</h1>
        <div className="row">
          {/* User identity block */}
          <div className="col" style={{ gap: '0.15rem', alignItems: 'flex-end' }}>
            <span className="pill ok">Signed in: {account?.username}</span>
            {(oid || puid) && (
              <div className="row" style={{ gap: '0.75rem', justifyContent: 'flex-end' }}>
                {oid  && <span className="user-claim" title={`Object ID (OID): ${oid}`}>OID&nbsp;<span className="mono">{oid}</span></span>}
                {puid && <span className="user-claim" title={`Passport Unique ID (PUID): ${puid}`}>PUID&nbsp;<span className="mono">{puid}</span></span>}
              </div>
            )}
          </div>
          <button className="secondary" onClick={onAcquireToken}>Refresh token</button>
          <button className="secondary" onClick={onReset}>Change config</button>
          <button className="danger"   onClick={signOut}>Sign out</button>
        </div>
      </header>

      <div className="tabs">
        <button className={'tab' + (tab === 'widget'  ? ' active' : '')} onClick={() => setTab('widget')}>1. Widget</button>
        <button className={'tab' + (tab === 'obo'     ? ' active' : '')} onClick={() => setTab('obo')}>2. OBO / API</button>
        <button className={'tab' + (tab === 'logs'    ? ' active' : '')} onClick={() => setTab('logs')}>3. Logs</button>
        <button className={'tab' + (tab === 'network' ? ' active' : '')} onClick={() => setTab('network')}>4. Network</button>
      </div>

      <div className="app-body">
        {tab === 'widget'  && <WidgetTab  authResult={authResult} onAcquireToken={onAcquireToken} />}
        {tab === 'obo'     && <OboTab     config={config} authResult={authResult} onAcquireToken={onAcquireToken} />}
        {tab === 'logs'    && <LogsTab />}
        {tab === 'network' && <NetworkTab />}
      </div>
    </div>
  );
}
