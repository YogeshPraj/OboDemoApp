import { useEffect, useMemo, useState } from 'react';
import { networkStore, NetworkRecord } from '../../utils/networkWatcher';

export function NetworkTab() {
  const [items, setItems] = useState<NetworkRecord[]>(() => networkStore.snapshot());
  const [sel, setSel] = useState<number | null>(null);
  const [capturing, setCapturing] = useState<boolean>(networkStore.isCapturing());
  const [filter, setFilter] = useState('');

  useEffect(() => {
    const unsub = networkStore.subscribe(() => setItems(networkStore.snapshot()));
    return () => { unsub(); };
  }, []);

  const filtered = useMemo(() => {
    const q = filter.toLowerCase();
    return items.filter((r) => !q || r.url.toLowerCase().includes(q) || r.method.toLowerCase().includes(q));
  }, [items, filter]);

  const selected = useMemo(() => items.find((r) => r.id === sel) ?? null, [items, sel]);

  function toggleCapture() {
    networkStore.setCapturing(!capturing);
    setCapturing(!capturing);
  }

  return (
    <div className="col" style={{ gap: '0.75rem' }}>
      <div className="toolbar card" style={{ padding: '0.5rem 0.75rem' }}>
        <button className={capturing ? 'danger' : ''} onClick={toggleCapture}>
          {capturing ? '⏸ Stop capture' : '▶ Start capture'}
        </button>
        <input placeholder="filter URL / method…" value={filter} onChange={(e) => setFilter(e.target.value)} className="grow" />
        <span className="muted">{filtered.length} / {items.length}</span>
        <button className="secondary" onClick={() => { networkStore.clear(); setSel(null); }}>Clear</button>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: selected ? '1.4fr 1fr' : '1fr', gap: '0.75rem', height: 'calc(100vh - 230px)' }}>
        <div className="card" style={{ padding: 0, overflow: 'auto' }}>
          <div className="net-row" style={{ background: '#0b1224', position: 'sticky', top: 0, fontWeight: 700 }}>
            <span>Method</span><span>Status</span><span>URL</span><span>Type</span><span>Time</span>
          </div>
          {filtered.map((r) => (
            <div className="net-row" key={r.id} onClick={() => setSel(r.id)} style={r.id === sel ? { background: '#1b2c4a' } : undefined}>
              <span className="method">{r.method}</span>
              <span className={'status ' + (r.ok ? 'ok' : r.status ? 'err' : '')}>{r.status ?? (r.error ? 'ERR' : '…')}</span>
              <span className="url" title={r.url}>{r.url}</span>
              <span>{r.via}</span>
              <span>{r.durationMs != null ? `${r.durationMs} ms` : '—'}</span>
            </div>
          ))}
        </div>

        {selected && (
          <div className="card col" style={{ overflow: 'auto', gap: '0.25rem' }}>
            <div className="row" style={{ justifyContent: 'space-between' }}>
              <strong style={{ wordBreak: 'break-all' }}>{selected.method} {selected.url}</strong>
              <button className="secondary" style={{ flexShrink: 0 }} onClick={() => setSel(null)}>Close</button>
            </div>

            <h3 style={{ margin: '0.75rem 0 0.25rem' }}>Request headers</h3>
            <pre className="mono net-pre">{stringify(selected.requestHeaders)}</pre>

            {selected.requestBody && (<>
              <h3 style={{ margin: '0.75rem 0 0.25rem' }}>Request body</h3>
              <pre className="mono net-pre">{prettyBody(selected.requestBody)}</pre>
            </>)}

            <h3 style={{ margin: '0.75rem 0 0.25rem' }}>Response ({selected.status} {selected.statusText})</h3>
            <pre className="mono net-pre">{stringify(selected.responseHeaders)}</pre>

            <div className="row" style={{ margin: '0.75rem 0 0.25rem', alignItems: 'baseline' }}>
              <h3 style={{ margin: 0 }}>Response body</h3>
              {selected.responseBody && (
                <button className="secondary" style={{ fontSize: 11, padding: '2px 8px' }}
                  onClick={() => navigator.clipboard.writeText(selected.responseBody ?? '')}>
                  Copy
                </button>
              )}
            </div>
            <pre className="mono net-pre">{prettyBody(selected.responseBody)}</pre>

            {selected.error && (<>
              <h3 style={{ margin: '0.75rem 0 0.25rem', color: 'var(--err)' }}>Error</h3>
              <pre className="mono net-pre" style={{ color: 'var(--err)' }}>{selected.error}</pre>
            </>)}
          </div>
        )}
      </div>
    </div>
  );
}

function stringify(v: unknown): string {
  try { return typeof v === 'string' ? v : JSON.stringify(v, null, 2); }
  catch { return String(v); }
}

/** Pretty-print a response/request body string.
 *  1. If it parses as JSON → re-indent with 2 spaces.
 *  2. If it looks URL-encoded → split into key = value lines.
 *  3. Otherwise return as-is.
 */
function prettyBody(s: string | null | undefined): string {
  if (!s) return '(none)';

  // 1. Try JSON
  try { return JSON.stringify(JSON.parse(s), null, 2); }
  catch { /* not JSON */ }

  // 2. Try URL-encoded (e.g. MSAL token requests)
  if (s.includes('=') && !s.includes('\n')) {
    try {
      const pairs = [...new URLSearchParams(s).entries()];
      if (pairs.length > 1) {
        const pad = Math.max(...pairs.map(([k]) => k.length));
        return pairs.map(([k, v]) => `${k.padEnd(pad)} = ${v}`).join('\n');
      }
    } catch { /* not URL-encoded */ }
  }

  return s;
}
