# CMSP Demo — Copilot Studio · OBO API · MCP Streamable · Omnichannel widget

POC that exercises three Entra-protected API flavors, MCP streamable HTTP, and the
Dynamics Omnichannel chat widget with Token Response / Token Exchange.

## Architecture (BFF pattern)

```
┌─────────────────────────────────────────────────────────────┐
│  Web — Vite + React + TypeScript + MSAL  (port 5173)        │
│  • Pre-sign-in: Client ID, scopes, popup/redirect           │
│  • Tab 1 – Widget  (Omnichannel script + auth callback)     │
│  • Tab 2 – OBO/API (Postman-like, targets BFF only)         │
│  • Tab 3 – Logs    (full app + MSAL log stream)             │
│  • Tab 4 – Network (fetch + XHR interceptor, start/stop)    │
└──────────────────────┬──────────────────────────────────────┘
                       │  Bearer token scoped to BFF
                       │  (api://<bff-app-id>/access_as_user)
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  src/API  — BFF API  (port 5080)                            │
│  • Validates incoming user tokens from Web                  │
│  • /api/me                  — caller identity at BFF        │
│  • /api/proxy/obo/claims    ─┐                              │
│  • /api/proxy/obo/graph-me  ─┤ OBO-exchange → PartnerAPI   │
│  • /api/proxy/mcp-obo       ─┘                              │
│  • /api/proxy/s2s/claims    ─┐                              │
│  • /api/proxy/mcp-s2s       ─┘ BFF credentials → PartnerAPI│
│  • /api/helpers/acquire-s2s — KV cert → client_credentials │
└──────────────────────┬──────────────────────────────────────┘
                       │  OBO token (user) or app token (S2S)
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  src/PartnerAPI  — Protected downstream API  (port 5081)    │
│  Flavor 1  GET /api/s2s/claims        S2S only              │
│  Flavor 2  GET /api/obo/claims        OBO only              │
│  Flavor 3  GET /api/obo/graph-me      OBO → Graph /me       │
│  Flavor 4  POST /mcp/s2s              MCP streamable, S2S   │
│  Flavor 5  POST /mcp/obo              MCP streamable, OBO   │
└─────────────────────────────────────────────────────────────┘
```

### Token flow

```
MSAL (browser)       → acquires token for BFF scope
Web → BFF            → Bearer <bff-user-token>
BFF (OBO paths)      → exchanges bff-user-token for partner-api-user-token via OBO
BFF (S2S paths)      → uses own client credentials to get partner-api-app-token
BFF → PartnerAPI     → Bearer <partner-api-token>
PartnerAPI → Graph   → Bearer <graph-token>  (for /obo/graph-me — full 3-hop chain)
```

### Entra app registrations (created by `setup.ps1`)

> App role `CMSPDemo.S2S` is defined on **CMSPDemo-OBOPartnerAPIApp** and granted to **CMSPDemo-S2SPartnerAPIApp**.
> The Web SPA and BFF share a single app registration (`CMSPDemo-BFF`) — 3 registrations total.

| App | Audience | Credential | Notes |
|-----|----------|-----------|-------|
| CMSPDemo-BFF (SPA + BFF) | AzureADandPersonalMicrosoftAccount | client secret | SPA redirect: localhost:5173; exposes `access_as_user`; OBO exchange |
| CMSPDemo-OBOPartnerAPIApp | AzureADMyOrg | client secret | exposes `access_as_user` to BFF; app role `CMSPDemo.S2S` |
| CMSPDemo-S2SPartnerAPIApp | AzureADMyOrg | KV certificate | granted `CMSPDemo.S2S` app role; S2S caller identity |

## Prerequisites

- .NET 10 SDK
- Node.js ≥ 20 + npm
- Azure CLI (`az --version`)
- An Entra tenant where you are at least Application Administrator

## Quick start

### 1. Run the setup script

```powershell
cd D:\CMSPDemo
.\setup.ps1
```

This will:
- Sign you in to Azure CLI (`az login --tenant <tenantId>`)
- Create the resource group + Key Vault (self-signed daemon cert inside)
- Create the 3 Entra app registrations with correct scopes, app roles, and pre-authorizations
- Patch `appsettings.json` in both API projects + write `src/Web/.env`
- Store client secrets in `dotnet user-secrets` (never in tracked files)

To also deploy to Azure App Service + Azure Storage static website:

```powershell
.\setup.ps1 -DeployToAzure
```

### 2. Run locally (3 terminals)

```powershell
# Terminal 1 — PartnerAPI (port 5081)
cd D:\CMSPDemo\src\PartnerAPI
dotnet run

# Terminal 2 — BFF API (port 5080)
cd D:\CMSPDemo\src\API
dotnet run

# Terminal 3 — Web (port 5173)
cd D:\CMSPDemo\src\Web
npm install
npm run dev
```

Open http://localhost:5173.

### 3. Manual config (if not using setup.ps1)

**`src/PartnerAPI/appsettings.json`**
```jsonc
"AzureAd": {
  "TenantId": "<your-tenant-id>",
  "ClientId": "<partner-api-client-id>",
  "Audience": "api://<partner-api-client-id>"
}
// + dotnet user-secrets set "AzureAd:ClientSecret" "<secret>"
```

**`src/API/appsettings.json`**
```jsonc
"AzureAd": {
  "TenantId": "<your-tenant-id>",
  "ClientId": "<bff-client-id>",
  "Audience": "api://<bff-client-id>"
},
"DownstreamApis": {
  "PartnerApi": {
    "BaseUrl":   "http://localhost:5081",
    "Scopes":    ["api://<partner-api-client-id>/access_as_user"],
    "AppScopes": ["api://<partner-api-client-id>/.default"]
  }
}
// + dotnet user-secrets set "AzureAd:ClientSecret" "<secret>"
```

**`src/Web/.env`**
```
VITE_DEFAULT_CLIENT_ID=<bff-client-id>
VITE_AUTHORITY=https://login.microsoftonline.com/<tenant-id>
VITE_API_BASE=http://localhost:5080
```

## Project layout

```
D:\CMSPDemo\
├── setup.ps1                   ← one-shot Entra + Azure setup
├── .setup-state.json           ← idempotency state (auto-created, gitignored)
├── src/
│   ├── PartnerAPI/             ← protected downstream API (Flavors 1-4 + MCP)
│   │   ├── Auth/AuthPolicies.cs
│   │   ├── Claims/ClaimsResponse.cs
│   │   ├── Mcp/ClaimsTools.cs
│   │   └── Program.cs
│   │
│   ├── API/                    ← BFF (the only thing Web talks to)
│   │   ├── Auth/BffAuthPolicies.cs
│   │   ├── Services/PartnerApiService.cs   ← OBO + S2S forwarding
│   │   ├── Endpoints/ProxyEndpoints.cs     ← /api/proxy/*
│   │   ├── Helpers/S2SHelperEndpoint.cs    ← /api/helpers/acquire-s2s
│   │   └── Program.cs
│   │
│   └── Web/                    ← Vite + React + TypeScript + MSAL
│       └── src/
│           ├── auth/msalConfig.ts
│           ├── utils/logger.ts
│           ├── utils/networkWatcher.ts
│           └── components/
│               ├── PreSignIn.tsx
│               ├── MainApp.tsx
│               └── tabs/
│                   ├── WidgetTab.tsx
│                   ├── OboTab.tsx
│                   └── LogsTab.tsx / NetworkTab.tsx
```

## MCP Streamable — how to test

All MCP calls go through the BFF (`/api/proxy/mcp-obo` or `/api/proxy/mcp-s2s`).

1. In the **OBO/API tab**, pick a **MCP** preset.
2. Set the JSON-RPC method (e.g. `tools/list`) and params.
3. Click **Send**. The BFF acquires the correct token, calls PartnerAPI `/mcp/*`, and streams the SSE response back.

Available MCP tools in PartnerAPI:

| Tool | Description |
|------|-------------|
| `GetCallerClaims` | Full claim set of the caller (BFF acting as user or app) |
| `WhoAmI` | Compact summary: name, tid, scopes, roles |
| `Echo` | Echoes text — verifies transport |

## Notes

- `setup.ps1` is idempotent: if you run it twice it reuses existing apps/resources.
- State is saved in `.setup-state.json` (add to `.gitignore`).
- Client secrets go only into `dotnet user-secrets` (never into tracked `appsettings.json`).
- The S2S helper at `/api/helpers/acquire-s2s` lives in the **BFF**, not PartnerAPI.
  The private key is fetched from Key Vault using `DefaultAzureCredential` (managed identity in Azure, `az login` locally).
- For production: replace the client secret with a certificate and use Key Vault references for all secrets.
