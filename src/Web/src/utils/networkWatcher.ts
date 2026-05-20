import { logger } from './logger';

export interface NetworkRecord {
  id: number;
  startedAt: number;
  endedAt?: number;
  durationMs?: number;
  method: string;
  url: string;
  status?: number;
  statusText?: string;
  ok?: boolean;
  via: 'fetch' | 'xhr';
  requestHeaders?: Record<string, string>;
  requestBody?: string;
  responseHeaders?: Record<string, string>;
  responseBody?: string;
  error?: string;
}

type Listener = (rec: NetworkRecord) => void;

class NetworkStore {
  private items: NetworkRecord[] = [];
  private listeners = new Set<Listener>();
  private nextId = 1;
  private max = 1000;
  private capturing = true;

  isCapturing() { return this.capturing; }
  setCapturing(on: boolean) { this.capturing = on; logger.info('network', on ? 'capture started' : 'capture paused'); }

  push(partial: Omit<NetworkRecord, 'id'>): NetworkRecord {
    const rec: NetworkRecord = { id: this.nextId++, ...partial };
    this.items.push(rec);
    if (this.items.length > this.max) this.items.splice(0, this.items.length - this.max);
    for (const fn of this.listeners) fn(rec);
    return rec;
  }
  update(id: number, patch: Partial<NetworkRecord>) {
    const i = this.items.findIndex((x) => x.id === id);
    if (i < 0) return;
    this.items[i] = { ...this.items[i], ...patch };
    for (const fn of this.listeners) fn(this.items[i]);
  }

  snapshot() { return [...this.items]; }
  clear() {
    this.items = [];
    const sentinel: NetworkRecord = { id: -1, startedAt: Date.now(), method: '-', url: '— cleared —', via: 'fetch' };
    for (const fn of this.listeners) fn(sentinel);
  }
  subscribe(fn: Listener) { this.listeners.add(fn); return () => this.listeners.delete(fn); }
}

export const networkStore = new NetworkStore();

let installed = false;

export function installNetworkWatcher() {
  if (installed) return;
  installed = true;
  patchFetch();
  patchXhr();
  logger.info('network', 'Network watcher installed (fetch + XHR)');
}

function headersToObject(h: HeadersInit | Headers | undefined | null): Record<string, string> {
  const out: Record<string, string> = {};
  if (!h) return out;
  if (h instanceof Headers) {
    h.forEach((v, k) => { out[k] = v; });
    return out;
  }
  if (Array.isArray(h)) {
    for (const [k, v] of h) out[k] = String(v);
    return out;
  }
  for (const [k, v] of Object.entries(h)) out[k] = String(v);
  return out;
}

function safeText(body: BodyInit | null | undefined): string | undefined {
  if (body == null) return undefined;
  if (typeof body === 'string') return truncate(body);
  if (body instanceof URLSearchParams) return truncate(body.toString());
  if (body instanceof FormData) {
    const parts: string[] = [];
    body.forEach((v, k) => parts.push(`${k}=${typeof v === 'string' ? v : '[Blob]'}`));
    return truncate(parts.join('&'));
  }
  return '[binary]';
}

function truncate(s: string, max = 8000) {
  return s.length > max ? `${s.slice(0, max)}\n... (truncated ${s.length - max} chars)` : s;
}

function patchFetch() {
  const orig = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit) => {
    if (!networkStore.isCapturing()) return orig(input, init);
    const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
    const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
    const headers = headersToObject(init?.headers ?? (input instanceof Request ? input.headers : undefined));
    const reqBody = init?.body !== undefined ? safeText(init.body as BodyInit) : undefined;

    const rec = networkStore.push({
      startedAt: Date.now(),
      method, url, via: 'fetch',
      requestHeaders: headers,
      requestBody: reqBody,
    });
    logger.debug('network', `→ ${method} ${url}`);

    try {
      const resp = await orig(input, init);
      const respHeaders: Record<string, string> = {};
      resp.headers.forEach((v, k) => { respHeaders[k] = v; });

      // Tee the body so the caller still gets it.
      let bodyText: string | undefined;
      try {
        const cloned = resp.clone();
        const t = await cloned.text();
        bodyText = truncate(t);
      } catch { /* opaque/streamed body */ }

      networkStore.update(rec.id, {
        endedAt: Date.now(),
        durationMs: Date.now() - rec.startedAt,
        status: resp.status,
        statusText: resp.statusText,
        ok: resp.ok,
        responseHeaders: respHeaders,
        responseBody: bodyText,
      });
      logger.debug('network', `← ${resp.status} ${method} ${url}`);
      return resp;
    } catch (e: any) {
      networkStore.update(rec.id, {
        endedAt: Date.now(),
        durationMs: Date.now() - rec.startedAt,
        ok: false,
        error: e?.message ?? String(e),
      });
      logger.warn('network', `✗ ${method} ${url}`, e);
      throw e;
    }
  };
}

function patchXhr() {
  const OrigXHR = window.XMLHttpRequest;
  function PatchedXHR(this: any) {
    const xhr = new OrigXHR();
    let method = 'GET', url = '';
    let reqHeaders: Record<string, string> = {};
    let reqBody: string | undefined;
    let recId: number | null = null;
    let startedAt = 0;

    const origOpen = xhr.open.bind(xhr) as (...a: any[]) => void;
    xhr.open = (m: string, u: string | URL, ...rest: any[]) => {
      method = m.toUpperCase();
      url = typeof u === 'string' ? u : u.toString();
      return origOpen(m, u, ...rest);
    };

    const origSetHeader = xhr.setRequestHeader.bind(xhr);
    xhr.setRequestHeader = (k: string, v: string) => {
      reqHeaders[k] = v;
      return origSetHeader(k, v);
    };

    const origSend = xhr.send.bind(xhr);
    xhr.send = (body?: any) => {
      if (!networkStore.isCapturing()) return origSend(body);
      reqBody = body !== undefined ? safeText(body) : undefined;
      startedAt = Date.now();
      const rec = networkStore.push({
        startedAt, method, url, via: 'xhr',
        requestHeaders: reqHeaders, requestBody: reqBody,
      });
      recId = rec.id;
      logger.debug('network', `→ ${method} ${url} (xhr)`);

      xhr.addEventListener('loadend', () => {
        if (recId == null) return;
        const respHeaders = parseHeaderBlob(xhr.getAllResponseHeaders());
        let bodyText: string | undefined;
        try { bodyText = typeof xhr.responseText === 'string' ? truncate(xhr.responseText) : undefined; } catch { /* not text */ }
        networkStore.update(recId, {
          endedAt: Date.now(),
          durationMs: Date.now() - startedAt,
          status: xhr.status,
          statusText: xhr.statusText,
          ok: xhr.status >= 200 && xhr.status < 300,
          responseHeaders: respHeaders,
          responseBody: bodyText,
        });
        logger.debug('network', `← ${xhr.status} ${method} ${url} (xhr)`);
      });
      xhr.addEventListener('error', () => {
        if (recId == null) return;
        networkStore.update(recId, { endedAt: Date.now(), ok: false, error: 'XHR network error' });
      });
      return origSend(body);
    };

    return xhr;
  }
  PatchedXHR.prototype = OrigXHR.prototype;
  // @ts-expect-error patching the global
  window.XMLHttpRequest = PatchedXHR;
}

function parseHeaderBlob(blob: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const line of blob.split(/\r?\n/)) {
    const i = line.indexOf(':');
    if (i < 0) continue;
    out[line.slice(0, i).trim().toLowerCase()] = line.slice(i + 1).trim();
  }
  return out;
}
