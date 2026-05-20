import {
  PublicClientApplication,
  Configuration,
  LogLevel,
} from '@azure/msal-browser';
import { logger } from '../utils/logger';

export const DEFAULT_CLIENT_ID =
  (import.meta.env.VITE_DEFAULT_CLIENT_ID as string | undefined) ??
  '00000000-0000-0000-0000-000000000000';

// CMSPDemo-BFF is registered with AzureADandPersonalMicrosoftAccount audience
// (shared SPA + BFF registration). VITE_AUTHORITY must use the "common" endpoint so
// both AAD work accounts and personal Microsoft accounts can sign in.
// setup.ps1 writes https://login.microsoftonline.com/common into .env automatically.
export const AUTHORITY =
  (import.meta.env.VITE_AUTHORITY as string | undefined) ??
  'https://login.microsoftonline.com/common';

export const COMMON_SCOPES: { value: string; label: string; group: string }[] = [
  { value: 'openid',         label: 'openid',                                 group: 'OIDC' },
  { value: 'profile',        label: 'profile',                                group: 'OIDC' },
  { value: 'email',          label: 'email',                                  group: 'OIDC' },
  { value: 'offline_access', label: 'offline_access (refresh token)',         group: 'OIDC' },
  { value: 'User.Read',                                  label: 'Graph: User.Read',                                  group: 'Microsoft Graph' },
  { value: 'User.ReadBasic.All',                         label: 'Graph: User.ReadBasic.All',                         group: 'Microsoft Graph' },
  { value: 'Mail.Read',                                  label: 'Graph: Mail.Read',                                  group: 'Microsoft Graph' },
  { value: 'Files.Read',                                 label: 'Graph: Files.Read',                                 group: 'Microsoft Graph' },
  { value: 'https://service.flow.microsoft.com//.default', label: 'Power Platform (Flow) /.default',                  group: 'Power Platform' },
  { value: 'https://dynamics.microsoft.com//.default',   label: 'Dataverse /.default',                               group: 'Power Platform' },
];

let _msal: PublicClientApplication | null = null;
let _currentClientId: string | null = null;

export function getMsal(clientId: string): PublicClientApplication {
  if (_msal && _currentClientId === clientId) return _msal;
  _currentClientId = clientId;

  const config: Configuration = {
    auth: {
      clientId,
      authority: AUTHORITY,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: 'sessionStorage',
      storeAuthStateInCookie: false,
    },
    system: {
      loggerOptions: {
        logLevel: LogLevel.Info,
        piiLoggingEnabled: false,
        loggerCallback: (level, message) => {
          const map = ['error', 'warn', 'info', 'debug', 'debug'] as const;
          logger.log(map[level] ?? 'info', 'msal', message);
        },
      },
    },
  };

  _msal = new PublicClientApplication(config);
  return _msal;
}

export async function initMsal(clientId: string): Promise<PublicClientApplication> {
  const pca = getMsal(clientId);
  await pca.initialize();
  // Handle redirect promise if returning from a redirect-based sign-in.
  await pca.handleRedirectPromise().catch((e) => logger.error('msal', 'handleRedirectPromise failed', e));
  return pca;
}
