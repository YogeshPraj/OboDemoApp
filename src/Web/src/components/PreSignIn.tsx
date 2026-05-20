import { useMemo, useState } from 'react';
import { InteractionType } from '@azure/msal-browser';
import { COMMON_SCOPES, DEFAULT_CLIENT_ID } from '../auth/msalConfig';

export interface SignInConfig {
  clientId: string;
  scopes: string[];
  interaction: InteractionType;
}

// ── Expander ────────────────────────────────────────────────────────────────
function Expander({
  num, title, open, onToggle, children,
}: {
  num: number;
  title: string;
  open: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  return (
    <div className="expander">
      <button className="expander-header" onClick={onToggle}>
        <span className="expander-num">{num})</span>
        <span className="expander-title">{title}</span>
        <svg
          className={`expander-chevron${open ? ' open' : ''}`}
          width="18" height="18" viewBox="0 0 24 24"
          fill="none" stroke="currentColor" strokeWidth="2.5"
          strokeLinecap="round" strokeLinejoin="round"
        >
          <polyline points="6 9 12 15 18 9" />
        </svg>
      </button>
      {open && <div className="expander-body">{children}</div>}
    </div>
  );
}

// ── PreSignIn ────────────────────────────────────────────────────────────────
export function PreSignIn({ onSubmit }: { onSubmit: (cfg: SignInConfig) => void }) {
  const [clientId, setClientId]     = useState('');
  const [picked, setPicked]         = useState<Set<string>>(new Set(['openid', 'profile', 'offline_access', 'User.Read']));
  const [custom, setCustom]         = useState('');
  const [interaction, setInteraction] = useState<InteractionType>(InteractionType.Popup);

  // 1 and 3 open by default, 2 collapsed
  const [open1, setOpen1] = useState(true);
  const [open2, setOpen2] = useState(false);
  const [open3, setOpen3] = useState(true);

  const groups = useMemo(() => {
    const m = new Map<string, typeof COMMON_SCOPES>();
    for (const s of COMMON_SCOPES) {
      if (!m.has(s.group)) m.set(s.group, [] as any);
      (m.get(s.group) as any).push(s);
    }
    return [...m.entries()];
  }, []);

  function toggle(v: string) {
    const next = new Set(picked);
    next.has(v) ? next.delete(v) : next.add(v);
    setPicked(next);
  }

  function submit() {
    const customScopes = custom.split(/[\s,]+/).map((s) => s.trim()).filter(Boolean);
    onSubmit({ clientId: clientId.trim(), scopes: [...picked, ...customScopes], interaction });
  }

  return (
    <>
      <header className="app-header">
        <h1>CMSP Demo — Copilot / OBO / MCP Tester</h1>
        <span className="pill">Pre-sign-in</span>
      </header>

      <div className="app-body">
        <div className="col" style={{ maxWidth: 880, gap: '0.5rem' }}>

          {/* ── 1) Azure Entra App ── */}
          <Expander num={1} title="Azure Entra App" open={open1} onToggle={() => setOpen1(!open1)}>
            <label className="field">
              <span>Client ID (App Registration in Entra ID)</span>
              <input
                type="text"
                placeholder={DEFAULT_CLIENT_ID || '00000000-0000-0000-0000-000000000000'}
                value={clientId}
                onChange={(e) => setClientId(e.target.value)}
              />
            </label>
          </Expander>

          {/* ── 2) Scopes ── */}
          <Expander num={2} title="Scopes" open={open2} onToggle={() => setOpen2(!open2)}>
            {groups.map(([group, items]) => (
              <div className="col" key={group}>
                <strong className="muted" style={{ fontSize: 11, letterSpacing: 1, textTransform: 'uppercase' }}>
                  {group}
                </strong>
                <div className="scope-grid">
                  {items.map((s) => (
                    <label key={s.value} title={s.value}>
                      <input type="checkbox" checked={picked.has(s.value)} onChange={() => toggle(s.value)} />
                      {s.label}
                    </label>
                  ))}
                </div>
              </div>
            ))}
            <label className="field">
              <span>
                Custom scopes (space- or comma-separated). Example:{' '}
                <code>api://&lt;api-app-id&gt;/.default</code>
              </span>
              <input
                type="text"
                placeholder="api://<api-app-id>/access_as_user"
                value={custom}
                onChange={(e) => setCustom(e.target.value)}
              />
            </label>
          </Expander>

          {/* ── 3) Sign-in style ── */}
          <Expander num={3} title="Sign-in style" open={open3} onToggle={() => setOpen3(!open3)}>
            <div className="row">
              <label>
                <input type="radio" checked={interaction === InteractionType.Popup}
                  onChange={() => setInteraction(InteractionType.Popup)} />
                {' '}Popup
              </label>
              <label>
                <input type="radio" checked={interaction === InteractionType.Redirect}
                  onChange={() => setInteraction(InteractionType.Redirect)} />
                {' '}Redirect
              </label>
            </div>
          </Expander>

          {/* ── Continue ── */}
          <div className="row" style={{ marginTop: '0.5rem' }}>
            <button onClick={submit}>Continue</button>
            <span className="muted">
              Tip: leave the Client ID blank to use <code>VITE_DEFAULT_CLIENT_ID</code>.
            </span>
          </div>

        </div>
      </div>
    </>
  );
}
