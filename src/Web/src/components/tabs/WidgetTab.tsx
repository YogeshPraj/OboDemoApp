import { useEffect, useRef, useState } from 'react';
import { useAuth } from '../../auth/AuthContext';
import { logger } from '../../utils/logger';

type AuthFlow = 'mcs-broker' | 'token-response' | 'token-exchange' | 'none';

const API_BASE   = (import.meta.env.VITE_API_BASE as string | undefined)   ?? 'http://localhost:5080';
const MCS_APP_ID = (import.meta.env.VITE_MCS_APP_ID as string | undefined) ?? '';

export function WidgetTab() {
  const auth = useAuth();
  const [script, setScript] = useState<string>(samplePlaceholder());
  const [flow, setFlow] = useState<AuthFlow>('mcs-broker');
  const [exchangeUrl, setExchangeUrl] = useState<string>(`${API_BASE}/api/widget/mcs-token`);
  const [hostUrl, setHostUrl] = useState<string>('');
  const [tokenScope, setTokenScope] = useState<string>(
    MCS_APP_ID ? `api://${MCS_APP_ID}/access_as_user` : 'User.Read');
  const [running, setRunning] = useState(false);
  const hostRef = useRef<HTMLDivElement | null>(null);

  // Register / re-register the Omnichannel auth callback whenever the flow changes.
  useEffect(() => {
    (window as any).CMSP_AUTH_CALLBACK = async (callback?: (token: string) => void) => {
      try {
        logger.info('widget', `auth callback invoked (flow=${flow}, authMode=${auth.mode})`);
        let token: string | undefined;

        if (flow === 'mcs-broker') {
          // Preferred path. Works in BOTH auth modes:
          //   • MSAL    → AuthContext.acquireTokenForScope() runs MSAL silent against MCSApp scope.
          //   • Session → AuthContext.acquireTokenForScope() hits BFF /api/widget/mcs-token,
          //               which OBO-exchanges the stored access token to T_mcs.
          if (!MCS_APP_ID) throw new Error('VITE_MCS_APP_ID is not set in .env.');
          token = await auth.acquireTokenForScope(`api://${MCS_APP_ID}/access_as_user`);
        }
        else if (flow === 'token-response') {
          // Legacy path: SPA acquires the scope directly via MSAL.
          // Only useful in MSAL mode.
          if (auth.mode !== 'msal')
            throw new Error('Token Response flow requires MSAL mode. Use "MCS broker" in session mode.');
          token = await auth.acquireTokenForScope(tokenScope);
        }
        else if (flow === 'token-exchange') {
          // Generic exchanger — POST the user token to an arbitrary BFF endpoint.
          const exchanged = await auth.authFetch(exchangeUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ targetScope: tokenScope }),
          });
          if (!exchanged.ok) throw new Error(`Token Exchange failed: ${exchanged.status}`);
          const data = await exchanged.json();
          token = data.token ?? data.access_token ?? data.accessToken;
        }

        if (!token) throw new Error('Auth callback produced no token.');
        logger.info('widget', `delivering token (len=${token.length}) to widget`);
        if (typeof callback === 'function') callback(token);
        return token;
      } catch (e: any) {
        logger.error('widget', 'auth callback failed', e);
        if (typeof callback === 'function') callback('');
        return '';
      }
    };
    logger.debug('widget', 'CMSP_AUTH_CALLBACK registered on window');
  }, [flow, exchangeUrl, tokenScope, auth]);

  function inject() {
    if (!hostRef.current) return;
    hostRef.current.innerHTML = '';

    const patched = script
      .replace(/data-authenticated-user-token-provider="[^"]*"/g,
               'data-authenticated-user-token-provider="CMSP_AUTH_CALLBACK"')
      .replace(/getAuthToken\s*:\s*function[^,}]+/g,
               'getAuthToken: window.CMSP_AUTH_CALLBACK');

    const wrapper = document.createElement('div');
    wrapper.innerHTML = patched;

    wrapper.querySelectorAll('script').forEach((s) => {
      const ns = document.createElement('script');
      for (const a of Array.from(s.attributes)) ns.setAttribute(a.name, a.value);
      ns.text = s.textContent ?? '';
      s.replaceWith(ns);
    });

    hostRef.current.appendChild(wrapper);
    setRunning(true);
    logger.info('widget', 'widget script injected');
  }

  function reset() {
    if (hostRef.current) hostRef.current.innerHTML = '';
    setRunning(false);
    logger.info('widget', 'widget host reset');
  }

  return (
    <div className="col" style={{ gap: '0.75rem' }}>
      <div className="card col">
        <strong>1. Paste the widget &lt;script&gt; snippet from your Dynamics environment</strong>
        <textarea rows={10} value={script} onChange={(e) => setScript(e.target.value)} />
      </div>

      <div className="card col">
        <strong>2. Authentication flow</strong>
        <div className="col" style={{ gap: '0.4rem' }}>
          <label>
            <input type="radio" checked={flow === 'mcs-broker'} onChange={() => setFlow('mcs-broker')} />
            {' '}<strong>MCS broker</strong> — auth-mode-aware. MSAL → silent token; Session → BFF
            <code>/api/widget/mcs-token</code> (OBO inside the BFF). <em>Recommended.</em>
          </label>
          <label>
            <input type="radio" checked={flow === 'token-response'} onChange={() => setFlow('token-response')} />
            {' '}<strong>Token Response</strong> — SPA acquires the scope directly (MSAL only).
          </label>
          <label>
            <input type="radio" checked={flow === 'token-exchange'} onChange={() => setFlow('token-exchange')} />
            {' '}<strong>Token Exchange</strong> — POST user token to a custom exchanger URL.
          </label>
          <label>
            <input type="radio" checked={flow === 'none'} onChange={() => setFlow('none')} />
            {' '}None (unauthenticated widget)
          </label>
        </div>
        {(flow === 'token-exchange' || flow === 'token-response') && (
          <div className="row">
            {flow === 'token-exchange' && (
              <label className="field grow">
                <span>Token Exchange URL</span>
                <input className="mono" value={exchangeUrl} onChange={(e) => setExchangeUrl(e.target.value)} />
              </label>
            )}
            <label className="field grow">
              <span>Target scope</span>
              <input className="mono" value={tokenScope} onChange={(e) => setTokenScope(e.target.value)} />
            </label>
          </div>
        )}
        <label className="field">
          <span>Host page URL (optional — recorded in logs so you can verify origin checks on your bot)</span>
          <input className="mono" value={hostUrl} onChange={(e) => setHostUrl(e.target.value)} placeholder="https://yourapp.example.com" />
        </label>
        <div className="row">
          <button onClick={inject}>Start widget</button>
          {running && <button className="secondary" onClick={reset}>Reset</button>}
          <span className="muted">Callback is registered on <code>window.CMSP_AUTH_CALLBACK</code> (auth mode: <code>{auth.mode}</code>).</span>
        </div>
      </div>

      <div className="card">
        <strong>Widget host</strong>
        <div ref={hostRef} style={{ minHeight: 180, marginTop: 8, padding: 12, border: '1px dashed var(--border)', borderRadius: 6 }} />
      </div>
    </div>
  );
}

function samplePlaceholder() {
  return `<!-- Paste the Omnichannel for Customer Service widget script here. Example shape: -->
<script
  id="Microsoft_Omnichannel_LCWidget"
  src="https://oc-cdn-public-eur.azureedge.net/livechatwidget/scripts/LiveChatBootstrapper.js"
  data-app-id="00000000-0000-0000-0000-000000000000"
  data-lcw-version="prod"
  data-org-id="00000000-0000-0000-0000-000000000000"
  data-org-url="https://m-xxxxx.xx.omnichannelengagementhub.com"
  data-authenticated-user-token-provider="ORIGINAL_PROVIDER_NAME">
</script>`;
}
