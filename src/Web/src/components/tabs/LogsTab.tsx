import { useEffect, useMemo, useRef, useState } from 'react';
import { logger, LogEntry, LogLevel } from '../../utils/logger';

const LEVELS: LogLevel[] = ['debug', 'info', 'warn', 'error'];

export function LogsTab() {
  const [items, setItems] = useState<LogEntry[]>(() => logger.snapshot());
  const [filter, setFilter] = useState('');
  const [levels, setLevels] = useState<Set<LogLevel>>(new Set(LEVELS));
  const [autoscroll, setAutoscroll] = useState(true);
  const bottomRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const unsub = logger.subscribe(() => setItems(logger.snapshot()));
    return () => { unsub(); };
  }, []);

  useEffect(() => {
    if (autoscroll) bottomRef.current?.scrollIntoView({ behavior: 'instant' as ScrollBehavior });
  }, [items, autoscroll]);

  const filtered = useMemo(() => {
    const q = filter.toLowerCase();
    return items.filter((i) => levels.has(i.level) && (!q || i.message.toLowerCase().includes(q) || i.source.toLowerCase().includes(q)));
  }, [items, filter, levels]);

  function toggleLevel(l: LogLevel) {
    const n = new Set(levels);
    n.has(l) ? n.delete(l) : n.add(l);
    setLevels(n);
  }

  return (
    <div className="col" style={{ gap: '0.75rem' }}>
      <div className="toolbar card" style={{ padding: '0.5rem 0.75rem' }}>
        <input placeholder="filter…" value={filter} onChange={(e) => setFilter(e.target.value)} />
        {LEVELS.map((l) => (
          <label key={l}><input type="checkbox" checked={levels.has(l)} onChange={() => toggleLevel(l)} /> {l}</label>
        ))}
        <label><input type="checkbox" checked={autoscroll} onChange={(e) => setAutoscroll(e.target.checked)} /> autoscroll</label>
        <div className="spacer" />
        <span className="muted">{filtered.length} / {items.length}</span>
        <button className="secondary" onClick={() => logger.clear()}>Clear</button>
      </div>
      <div className="card" style={{ padding: 0, height: 'calc(100vh - 230px)', overflow: 'auto' }}>
        {filtered.map((e) => (
          <div className={`log-row ${e.level}`} key={e.id}>
            <span className="ts">{new Date(e.ts).toISOString().split('T')[1].replace('Z', '')}</span>
            <span className="src">[{e.source}]</span>
            <strong>{e.level.toUpperCase()}</strong> {e.message}
            {e.detail !== undefined && <div className="muted" style={{ paddingLeft: 24 }}>{stringify(e.detail)}</div>}
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
    </div>
  );
}

function stringify(v: unknown) {
  try { return typeof v === 'string' ? v : JSON.stringify(v, null, 2); } catch { return String(v); }
}
