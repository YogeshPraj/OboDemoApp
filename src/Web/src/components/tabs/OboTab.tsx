import { useState } from 'react';
import { useAuth } from '../../auth/AuthContext';
import { logger } from '../../utils/logger';

type Mode = 'rest' | 'mcp';
type RequestAuthMode = 'user' | 's2s' | 'none';

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? 'http://localhost:5080';

// All Web traffic goes to the BFF (src/API, port 5080).
// The BFF then forwards to PartnerAPI (src/PartnerAPI, port 5081) with
// the correct token (OBO for user flows, S2S for app flows).
const PRESETS: { label: string; mode: Mode; method: string; url: string; auth: RequestAuthMode }[] = [
  { label: 'BFF · Who am I?',                     mode: 'rest', method: 'GET',  url: `${API_BASE}/api/me`,                      auth: 'user' },
  { label: 'BFF · Auth me (session-aware)',       mode: 'rest', method: 'GET',  url: `${API_BASE}/api/auth/me`,                 auth: 'user' },
  { label: 'BFF → PartnerAPI · OBO claims',       mode: 'rest', method: 'GET',  url: `${API_BASE}/api/proxy/obo/claims`,        auth: 'user' },
  { label: 'BFF → PartnerAPI · OBO → Graph /me',  mode: 'rest', method: 'GET',  url: `${API_BASE}/api/proxy/obo/graph-me`,      auth: 'user' },
  { label: 'BFF → PartnerAPI · S2S claims',       mode: 'rest', method: 'GET',  url: `${API_BASE}/api/proxy/s2s/claims`,        auth: 'user' },
  { label: 'BFF → PartnerAPI · MCP OBO',          mode: 'mcp',  method: 'POST', url: `${API_BASE}/api/proxy/mcp-obo`,           auth: 'user' },
  { label: 'BFF → PartnerAPI · MCP S2S',          mode: 'mcp',  method: 'POST', url: `${API_BASE}/api/proxy/mcp-s2s`,           auth: 'user' },
];

export function OboTab() {
  const auth = useAuth();
  const [mode, setMode] = useState<Mode>('rest');
  const [method, setMethod] = useState('GET');
  const [url, setUrl] = useState(`${API_BASE}/api/proxy/obo/claims`);
  const [headers, setHeaders] = useState('{\n  "Accept": "application/json"\n}');
  const [body, setBody] = useState('');

  // MCP-specific
  const [mcpMethod, setMcpMethod] = useState('tools/list');
  const [mcpParams, setMcpParams] = useState('{}');
  const [mcpSession, setMcpSession] = useState<string | null>(null);

  // Request auth (separate from app-level sign-in mode)
  const [reqAuth, setReqAuth] = useState<RequestAuthMode>('user');
  const [tenantId, setTenantId]   = useState('');
  const [s2sClientId, setS2sClientId] = useState('');
  const [kvUrl, setKvUrl]         = useState('');
  const [certName, setCertName]   = useState('');
  const [s2sScope, setS2sScope]   = useState('');

  const [busy, setBusy] = useState(false);
  const [response, setResponse] = useState<{ status?: number; headers?: Record<string, string>; body?: string; error?: string; durationMs?: number } | null>(null);

  function applyPreset(label: string) {
    const p = PRESETS.find((x) => x.label === label);
    if (!p) return;
    setMode(p.mode); setMethod(p.method); setUrl(p.url); setReqAuth(p.auth);
    if (p.mode === 'mcp') { setMcpMethod('tools/list'); setMcpParams('{}'); }
    logger.info('obo', `applied preset: ${label}`);
  }

  async function acquireS2STokenViaHelper(): Promise<string | null> {
    // Hits the BFF helper using whichever auth mode is active.
    const resp = await auth.authFetch(`${API_BASE}/api/helpers/acquire-s2s`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tenantId: tenantId.trim(),
        clientId: s2sClientId.trim(),
        keyVaultUrl: kvUrl.trim(),
        certificateName: certName.trim(),
        scope: s2sScope.trim(),
      }),
    });
    if (!resp.ok) {
      const t = await resp.text();
      throw new Error(`S2S helper failed: ${resp.status} ${t}`);
    }
    const data = await resp.json();
    return data.accessToken;
  }

  async function send() {
    setBusy(true);
    setResponse(null);
    const t0 = performance.now();
    try {
      let h: Record<string, string> = {};
      try { h = headers.trim() ? JSON.parse(headers) : {}; } catch { /* free-form */ }

      let reqBody: string | undefined;
      let actualUrl = url;
      let actualMethod = method;
      if (mode === 'mcp') {
        actualMethod = 'POST';
        h['Content-Type'] = h['Content-Type'] ?? 'application/json';
        h['Accept'] = 'application/json, text/event-stream';
        if (mcpSession) h['Mcp-Session-Id'] = mcpSession;
        reqBody = JSON.stringify({
          jsonrpc: '2.0',
          id: crypto.randomUUID(),
          method: mcpMethod,
          params: safeParse(mcpParams),
        });
      } else if (body.trim()) {
        reqBody = body;
        if (!h['Content-Type']) h['Content-Type'] = 'application/json';
      }

      logger.info('obo', `→ ${actualMethod} ${actualUrl}`, { reqAuth, mode, authMode: auth.mode });

      let resp: Response;
      if (reqAuth === 'user') {
        // Use whichever mode is currently active (MSAL Bearer or session cookie).
        resp = await auth.authFetch(actualUrl, { method: actualMethod, headers: h, body: reqBody });
      } else if (reqAuth === 's2s') {
        const s2sToken = await acquireS2STokenViaHelper();
        if (s2sToken) h['Authorization'] = `Bearer ${s2sToken}`;
        resp = await fetch(actualUrl, { method: actualMethod, headers: h, body: reqBody });
      } else {
        resp = await fetch(actualUrl, { method: actualMethod, headers: h, body: reqBody });
      }

      const respHeaders: Record<string, string> = {};
      resp.headers.forEach((v, k) => { respHeaders[k] = v; });
      const sid = resp.headers.get('Mcp-Session-Id');
      if (sid) setMcpSession(sid);

      const text = await resp.text();
      setResponse({ status: resp.status, headers: respHeaders, body: text, durationMs: Math.round(performance.now() - t0) });
      logger.info('obo', `← ${resp.status} in ${Math.round(performance.now() - t0)}ms`);
    } catch (e: any) {
      setResponse({ error: e?.message ?? String(e), durationMs: Math.round(performance.now() - t0) });
      logger.error('obo', 'request failed', e);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="col" style={{ gap: '0.75rem' }}>
      <div className="card col">
        <div className="row">
          <strong>Mode:</strong>
          <label><input type="radio" checked={mode === 'rest'} onChange={() => setMode('rest')} /> Plain REST</label>
          <label><input type="radio" checked={mode === 'mcp'}  onChange={() => setMode('mcp')}  /> MCP Streamable</label>
          <div className="spacer" />
          <select onChange={(e) => applyPreset(e.target.value)} defaultValue="">
            <option value="" disabled>— presets —</option>
            {PRESETS.map((p) => <option key={p.label} value={p.label}>{p.label}</option>)}
          </select>
        </div>

        <div className="row">
          {mode === 'rest' && (
            <select value={method} onChange={(e) => setMethod(e.target.value)}>
              {['GET','POST','PUT','PATCH','DELETE'].map((m) => <option key={m}>{m}</option>)}
            </select>
          )}
          <input className="grow mono" value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://…" />
          <button onClick={send} disabled={busy}>{busy ? 'Sending…' : 'Send'}</button>
        </div>

        {mode === 'mcp' && (
          <div className="row">
            <label className="field grow">
              <span>JSON-RPC method</span>
              <select value={mcpMethod} onChange={(e) => setMcpMethod(e.target.value)}>
                <option>initialize</option>
                <option>tools/list</option>
                <option>tools/call</option>
                <option>prompts/list</option>
                <option>resources/list</option>
              </select>
            </label>
            <label className="field grow">
              <span>params (JSON) — e.g. <code>{`{ "name": "GetCallerClaims", "arguments": {} }`}</code></span>
              <input className="mono" value={mcpParams} onChange={(e) => setMcpParams(e.target.value)} />
            </label>
            <label className="field">
              <span>Session</span>
              <input className="mono" value={mcpSession ?? ''} placeholder="auto" onChange={(e) => setMcpSession(e.target.value || null)} style={{ width: 220 }} />
            </label>
          </div>
        )}

        {mode === 'rest' && (
          <>
            <label className="field"><span>Headers (JSON)</span>
              <textarea rows={4} value={headers} onChange={(e) => setHeaders(e.target.value)} />
            </label>
            <label className="field"><span>Body</span>
              <textarea rows={5} value={body} onChange={(e) => setBody(e.target.value)} />
            </label>
          </>
        )}
      </div>

      <div className="card col">
        <strong>Authentication for this request</strong>
        <div className="row">
          <label><input type="radio" checked={reqAuth === 'user'} onChange={() => setReqAuth('user')} /> User ({auth.mode === 'msal' ? 'Bearer via MSAL' : 'CMSP_SESSION cookie'})</label>
          <label><input type="radio" checked={reqAuth === 's2s'}  onChange={() => setReqAuth('s2s')}  /> Service-to-Service (client cert via Key Vault)</label>
          <label><input type="radio" checked={reqAuth === 'none'} onChange={() => setReqAuth('none')} /> None</label>
        </div>

        {reqAuth === 'user' && (
          <span className="muted">
            {auth.mode === 'msal'
              ? <>MSAL acquires a bearer token for the BFF and sends it as <code>Authorization: Bearer …</code>. The BFF OBO-exchanges it for PartnerAPI.</>
              : <>The browser sends only the <code>CMSP_SESSION</code> cookie. The BFF uses the access token stored in the session to perform OBO.</>}
          </span>
        )}

        {reqAuth === 's2s' && (
          <div className="col">
            <span className="muted">
              The browser asks the BFF's S2S helper, which reads a KV certificate using managed identity
              and acquires a client-credentials token. The private key never leaves Azure.
            </span>
            <div className="row">
              <label className="field grow"><span>Tenant ID</span>
                <input className="mono" value={tenantId} onChange={(e) => setTenantId(e.target.value)} placeholder="contoso.onmicrosoft.com or GUID" />
              </label>
              <label className="field grow"><span>Client ID</span>
                <input className="mono" value={s2sClientId} onChange={(e) => setS2sClientId(e.target.value)} placeholder="App ID of caller" />
              </label>
            </div>
            <div className="row">
              <label className="field grow"><span>Key Vault URL</span>
                <input className="mono" value={kvUrl} onChange={(e) => setKvUrl(e.target.value)} placeholder="https://my-kv.vault.azure.net/" />
              </label>
              <label className="field grow"><span>Certificate name</span>
                <input className="mono" value={certName} onChange={(e) => setCertName(e.target.value)} placeholder="my-cert" />
              </label>
            </div>
            <label className="field"><span>Target scope (.default)</span>
              <input className="mono" value={s2sScope} onChange={(e) => setS2sScope(e.target.value)} placeholder="api://<target-app-id>/.default" />
            </label>
          </div>
        )}
      </div>

      <div className="card">
        <strong>Response</strong>
        {!response && <div className="muted">No response yet.</div>}
        {response && (
          <>
            <div className="row">
              <span className={'pill ' + (response.status && response.status < 400 ? 'ok' : 'err')}>{response.status ?? 'ERR'}</span>
              <span className="muted">{response.durationMs} ms</span>
            </div>
            {response.headers && <>
              <h4>Headers</h4>
              <pre className="mono">{JSON.stringify(response.headers, null, 2)}</pre>
            </>}
            {response.body && <>
              <h4>Body</h4>
              <pre className="mono" style={{ maxHeight: 400, overflow: 'auto' }}>{prettyJson(response.body)}</pre>
            </>}
            {response.error && <pre className="mono" style={{ color: 'var(--err)' }}>{response.error}</pre>}
          </>
        )}
      </div>
    </div>
  );
}

function safeParse(s: string): unknown {
  try { return JSON.parse(s); } catch { return {}; }
}
function prettyJson(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2); } catch { return s; }
}
