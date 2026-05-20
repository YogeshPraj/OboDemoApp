export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface LogEntry {
  id: number;
  ts: number;          // epoch ms
  level: LogLevel;
  source: string;      // 'msal', 'network', 'app', 'widget', 'obo', ...
  message: string;
  detail?: unknown;
}

type Listener = (entry: LogEntry) => void;

class Logger {
  private entries: LogEntry[] = [];
  private listeners = new Set<Listener>();
  private nextId = 1;
  private max = 5000;

  log(level: LogLevel, source: string, message: string, detail?: unknown) {
    const entry: LogEntry = { id: this.nextId++, ts: Date.now(), level, source, message, detail };
    this.entries.push(entry);
    if (this.entries.length > this.max) this.entries.splice(0, this.entries.length - this.max);
    for (const fn of this.listeners) fn(entry);
    // Mirror to dev console for convenience.
    const fn = level === 'error' ? console.error : level === 'warn' ? console.warn : level === 'debug' ? console.debug : console.log;
    fn(`[${source}] ${message}`, detail ?? '');
  }
  debug(s: string, m: string, d?: unknown) { this.log('debug', s, m, d); }
  info(s: string, m: string, d?: unknown)  { this.log('info',  s, m, d); }
  warn(s: string, m: string, d?: unknown)  { this.log('warn',  s, m, d); }
  error(s: string, m: string, d?: unknown) { this.log('error', s, m, d); }

  snapshot(): LogEntry[] { return [...this.entries]; }
  clear() { this.entries = []; this.emitReset(); }

  subscribe(fn: Listener) { this.listeners.add(fn); return () => this.listeners.delete(fn); }

  private emitReset() {
    const reset: LogEntry = { id: -1, ts: Date.now(), level: 'info', source: 'logger', message: '— cleared —' };
    for (const fn of this.listeners) fn(reset);
  }
}

export const logger = new Logger();
