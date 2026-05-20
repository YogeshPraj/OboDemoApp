import { useEffect, useRef, useState } from 'react';
import { AuthenticationResult } from '@azure/msal-browser';
import { logger } from '../../utils/logger';

type AuthFlow = 'token-response' | 'token-exchange' | 'none';

export function WidgetTab({
  authResult, onAcquireToken,
}: {
  authResult: AuthenticationResult | null;
  onAcquireToken: () => Promise<AuthenticationResult | null>;
}) {
  const [script, setScript] = useState<string>(samplePlaceholder());
  const [flow, setFlow] = useState<AuthFlow>('token-response');
  const [exchangeUrl, setExchangeUrl] = useState<string>('');
  const [hostUrl, setHostUrl] = useState<string>('');
  const [tokenScope, setTokenScope] = useState<string>('User.Read');
  const [running, setRunning] = useState(false);
  const hostRef = useRef<HTMLDivElement | null>(null);

  // Register / re-register the Omnichannel auth callback whenever the flow changes.
  useEffect(() => {
    (window as any).CMSP_AUTH_CALLBACK = async (callback?: (token: string) => void) => {
      try {
        logger.info('widget', `auth callback invoked (flow=${flow})`);
        let token: string | undefined;
        if (flow === 'token-response') {
          const r = authResult ?? (await onAcquireToken());
          token = r?.accessToken;
        } else if (flow === 'token-exchange') {
          if (!exchangeUrl) throw new Error('Token Exchange URL is required for token-exchange flow.');
          const r = authResult ?? (await onAcquireToken());
          if (!r?.accessToken) throw new Error('No user token to exchange.');
          const exchanged = await fetch(exchangeUrl, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${r.accessToken}` },
            body: JSON.stringify({ scope: tokenScope }),
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
  }, [flow, exchangeUrl, tokenScope, authResult, onAcquireToken]);

  function inject() {
    if (!hostRef.current) return;
    hostRef.current.innerHTML = '';

    // Patch the widget script to wire up our auth callback. Microsoft's
    // Omnichannel snippet exposes a global like `Microsoft.Omnichannel.LiveChatWidget`
    // and accepts an `authenticatedUserToken` provider. We rewrite the data
    // attributes / inline JS to point at our CMSP_AUTH_CALLBACK.
    const patched = script
      .replace(/data-authenticated-user-token-provider="[^"]*"/g,
               'data-authenticated-user-token-provider="CMSP_AUTH_CALLBACK"')
      .replace(/getAuthToken\s*:\s*function[^,}]+/g,
               'getAuthToken: window.CMSP_AUTH_CALLBACK');

    const wrapper = document.createElement('div');
    wrapper.innerHTML = patched;

    // Re-create <script> elements so the browser actually executes them.
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
        <div className="row">
          <label><input type="radio" checked={flow === 'token-response'} onChange={() => setFlow('token-response')} /> Token Response (return MSAL access token directly)</label>
          <label><input type="radio" checked={flow === 'token-exchange'} onChange={() => setFlow('token-exchange')} /> Token Exchange (POST user token to your exchanger)</label>
          <label><input type="radio" checked={flow === 'none'} onChange={() => setFlow('none')} /> None (unauthenticated)</label>
        </div>
        {flow === 'token-exchange' && (
          <div className="row">
            <label className="field grow">
              <span>Token Exchange URL (your backend that swaps the user token for the chat token)</span>
              <input className="mono" value={exchangeUrl} onChange={(e) => setExchangeUrl(e.target.value)} placeholder="https://your-host/api/exchange" />
            </label>
            <label className="field">
              <span>Scope</span>
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
          <span className="muted">The auth callback is registered on <code>window.CMSP_AUTH_CALLBACK</code>.</span>
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
