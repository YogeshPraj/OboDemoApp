import { createContext, useContext, useState, useCallback, useEffect, ReactNode } from 'react';
import { useMsal } from '@azure/msal-react';
import { AuthenticationResult, AccountInfo } from '@azure/msal-browser';
import { logger } from '../utils/logger';

const API_BASE = (import.meta.env.VITE_API_BASE as string | undefined) ?? 'http://localhost:5080';
const MCS_APP_ID = import.meta.env.VITE_MCS_APP_ID as string | undefined;

export type AuthMode = 'msal' | 'session';

export interface UserInfo {
  oid?: string;
  upn?: string;
  tid?: string;
  name?: string;
  puid?: string;
}

export interface AuthContextValue {
  mode: AuthMode;
  user: UserInfo | null;
  isAuthenticated: boolean;
  /** Re-read user identity (silent token refresh for MSAL, /api/auth/me for session). */
  refresh: () => Promise<UserInfo | null>;
  /** Sign out (handles both modes). */
  signOut: () => Promise<void>;
  /** Make an authenticated fetch against the BFF. Adds Bearer (MSAL) or credentials (session). */
  authFetch: (input: string, init?: RequestInit) => Promise<Response>;
  /** Acquire a token for a specific scope — for handing to the Omnichannel widget. */
  acquireTokenForScope: (scope: string) => Promise<string>;
}

const AuthContext = createContext<AuthContextValue | null>(null);
export const useAuth = (): AuthContextValue => {
  const v = useContext(AuthContext);
  if (!v) throw new Error('useAuth() called outside <AuthProvider>');
  return v;
};

// ── MSAL provider ──────────────────────────────────────────────────────────
export function MsalAuthProvider({
  bffScope, children,
}: {
  /** The scope MSAL acquires for the BFF (api://<bff-app-id>/access_as_user). */
  bffScope: string;
  children: ReactNode;
}) {
  const { instance, accounts } = useMsal();
  const account: AccountInfo | undefined = accounts[0];
  const [result, setResult] = useState<AuthenticationResult | null>(null);

  const user: UserInfo | null = account ? {
    oid:  ((account.idTokenClaims as Record<string, string>)?.oid)  ?? account.localAccountId,
    puid: ((account.idTokenClaims as Record<string, string>)?.puid) ?? undefined,
    tid:  account.tenantId,
    upn:  account.username,
    name: account.name,
  } : null;

  const refresh = useCallback(async (): Promise<UserInfo | null> => {
    if (!account) return null;
    try {
      const r = await instance.acquireTokenSilent({ scopes: [bffScope], account });
      setResult(r);
      return user;
    } catch (e) {
      logger.warn('auth', 'silent token failed, falling back to popup', e);
      const r = await instance.acquireTokenPopup({ scopes: [bffScope], account });
      setResult(r);
      return user;
    }
  }, [account, bffScope, instance, user]);

  const acquireBffBearer = useCallback(async (): Promise<string> => {
    if (!account) throw new Error('Not signed in');
    if (result?.accessToken) {
      // Best-effort: re-use last token (msal handles its own expiry).
      // If it's stale, the next acquireTokenSilent will replace it.
    }
    const r = await instance.acquireTokenSilent({ scopes: [bffScope], account });
    setResult(r);
    return r.accessToken;
  }, [account, bffScope, instance, result]);

  const authFetch = useCallback(async (input: string, init: RequestInit = {}): Promise<Response> => {
    const token = await acquireBffBearer();
    const headers = new Headers(init.headers);
    headers.set('Authorization', `Bearer ${token}`);
    return fetch(input, { ...init, headers });
  }, [acquireBffBearer]);

  const acquireTokenForScope = useCallback(async (scope: string): Promise<string> => {
    if (!account) throw new Error('Not signed in');
    const r = await instance.acquireTokenSilent({ scopes: [scope], account });
    return r.accessToken;
  }, [account, instance]);

  const signOut = useCallback(async (): Promise<void> => {
    if (!account) return;
    await instance.logoutPopup({ account });
  }, [account, instance]);

  const value: AuthContextValue = {
    mode: 'msal',
    user,
    isAuthenticated: !!account,
    refresh,
    signOut,
    authFetch,
    acquireTokenForScope,
  };
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// ── Session (server-OIDC) provider ─────────────────────────────────────────
export function SessionAuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<UserInfo | null>(null);
  const [loaded, setLoaded] = useState(false);

  const refresh = useCallback(async (): Promise<UserInfo | null> => {
    try {
      const r = await fetch(`${API_BASE}/api/auth/me`, { credentials: 'include' });
      if (!r.ok) { setUser(null); return null; }
      const data = await r.json();
      const u: UserInfo = {
        oid: data.oid, upn: data.upn, tid: data.tid, name: data.name, puid: data.puid,
      };
      setUser(u);
      return u;
    } catch (e) {
      logger.warn('auth', '/api/auth/me failed', e);
      setUser(null);
      return null;
    } finally {
      setLoaded(true);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const signOut = useCallback(async () => {
    await fetch(`${API_BASE}/api/auth/sign-out`, { method: 'POST', credentials: 'include' });
    setUser(null);
  }, []);

  const authFetch = useCallback(async (input: string, init: RequestInit = {}): Promise<Response> => {
    return fetch(input, { ...init, credentials: 'include' });
  }, []);

  const acquireTokenForScope = useCallback(async (scope: string): Promise<string> => {
    // For session mode we only support the MCSApp scope (BFF returns it via OBO).
    if (MCS_APP_ID && (scope.includes(MCS_APP_ID) || scope.startsWith('api://' + MCS_APP_ID))) {
      const r = await fetch(`${API_BASE}/api/widget/mcs-token`, { credentials: 'include' });
      if (!r.ok) throw new Error(`MCS token fetch failed: ${r.status}`);
      const data = await r.json();
      return data.accessToken as string;
    }
    throw new Error(`acquireTokenForScope("${scope}") is not supported in session mode. ` +
                    `Only the MCSApp scope is brokered via /api/widget/mcs-token.`);
  }, []);

  const value: AuthContextValue = {
    mode: 'session',
    user,
    isAuthenticated: !!user,
    refresh,
    signOut,
    authFetch,
    acquireTokenForScope,
  };

  if (!loaded) {
    return (
      <div className="app-shell">
        <div className="app-body"><div className="card">Checking session…</div></div>
      </div>
    );
  }
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
