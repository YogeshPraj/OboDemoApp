/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_DEFAULT_CLIENT_ID?: string;
  readonly VITE_AUTHORITY?: string;
  readonly VITE_API_BASE?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
