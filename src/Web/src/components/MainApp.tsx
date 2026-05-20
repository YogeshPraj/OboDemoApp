import { useState } from 'react';
import { useAuth } from '../auth/AuthContext';
import { WidgetTab } from './tabs/WidgetTab';
import { OboTab } from './tabs/OboTab';
import { LogsTab } from './tabs/LogsTab';
import { NetworkTab } from './tabs/NetworkTab';
import { logger } from '../utils/logger';

type TabId = 'widget' | 'obo' | 'logs' | 'network';

export function MainApp({ onReset }: { onReset: () => void }) {
  const { user, mode, refresh, signOut } = useAuth();
  const [tab, setTab] = useState<TabId>('widget');

  async function handleSignOut() {
    logger.info('app', `signing out (${mode}) ${user?.upn ?? ''}`);
    await signOut();
    onReset();
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <h1>CMSP Demo</h1>
        <div className="row">
          {/* User identity block */}
          <div className="col" style={{ gap: '0.15rem', alignItems: 'flex-end' }}>
            <span className="pill ok">
              {mode === 'session' ? 'Session: ' : 'Signed in: '}{user?.upn ?? '?'}
            </span>
            {(user?.oid || user?.puid) && (
              <div className="row" style={{ gap: '0.75rem', justifyContent: 'flex-end' }}>
                {user.oid  && <span className="user-claim" title={`Object ID (OID): ${user.oid}`}>OID&nbsp;<span className="mono">{user.oid}</span></span>}
                {user.puid && <span className="user-claim" title={`Passport Unique ID (PUID): ${user.puid}`}>PUID&nbsp;<span className="mono">{user.puid}</span></span>}
                <span className="user-claim" title="Auth mode (browser holds token vs. server-side session)">
                  AUTH&nbsp;<span className="mono">{mode === 'msal' ? 'MSAL' : 'SESSION'}</span>
                </span>
              </div>
            )}
          </div>
          <button className="secondary" onClick={() => refresh()}>Refresh</button>
          <button className="secondary" onClick={onReset}>Change config</button>
          <button className="danger"    onClick={handleSignOut}>Sign out</button>
        </div>
      </header>

      <div className="tabs">
        <button className={'tab' + (tab === 'widget'  ? ' active' : '')} onClick={() => setTab('widget')}>1. Widget</button>
        <button className={'tab' + (tab === 'obo'     ? ' active' : '')} onClick={() => setTab('obo')}>2. OBO / API</button>
        <button className={'tab' + (tab === 'logs'    ? ' active' : '')} onClick={() => setTab('logs')}>3. Logs</button>
        <button className={'tab' + (tab === 'network' ? ' active' : '')} onClick={() => setTab('network')}>4. Network</button>
      </div>

      <div className="app-body">
        {tab === 'widget'  && <WidgetTab />}
        {tab === 'obo'     && <OboTab />}
        {tab === 'logs'    && <LogsTab />}
        {tab === 'network' && <NetworkTab />}
      </div>
    </div>
  );
}
