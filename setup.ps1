<#
.SYNOPSIS
    One-shot setup for CMSPDemo POC.
    Creates all Entra ID app registrations, Key Vault + S2S cert,
    patches local config files, and optionally deploys to Azure.

.DESCRIPTION
    Creates three Entra app registrations:
      • CMSPDemo-BFF               — React SPA + Backend for Frontend (shared registration; MSA + AAD)
      • CMSPDemo-OBOPartnerAPIApp  — Protected downstream API for OBO (delegated user) calls
      • CMSPDemo-S2SPartnerAPIApp  — Identity used for S2S (client-credentials) calls to PartnerAPI

    Also creates:
      • Azure Resource Group
      • Azure Key Vault with a self-signed certificate for CMSPDemo-S2SPartnerAPIApp

    Patches local config files:
      • src/PartnerAPI/appsettings.json   TenantId, ClientId, Audience
      • src/API/appsettings.json          TenantId, ClientId, Audience, OBO/S2S scopes
      • src/Web/.env                      VITE_DEFAULT_CLIENT_ID, VITE_AUTHORITY, VITE_API_BASE
      • dotnet user-secrets               ClientSecret (BFF OBO, OBOPartnerAPIApp OBO→Graph)

    Optional:
      -DeployToAzure   builds and deploys BFF API → App Service, Web → Azure Storage static website

.PARAMETER TenantId
    Entra tenant ID. Default: YogeshInc tenant.

.PARAMETER UserId
    UPN of the signed-in admin user (granted KV access). Default: yogeshcprajapati@outlook.com.

.PARAMETER Location
    Azure region. Default: eastus.

.PARAMETER ResourceGroup
    Resource group name. Default: rg-cmspdemo.

.PARAMETER DeployToAzure
    When set, builds and deploys both projects to Azure.

.PARAMETER SkipLogin
    Skip az login if you are already signed in.

.EXAMPLE
    # Set up Entra + configure local dev (no Azure hosting):
    .\setup.ps1

.EXAMPLE
    # Set up Entra + deploy to Azure:
    .\setup.ps1 -DeployToAzure
#>
[CmdletBinding()]
param(
    [string] $TenantId      = "4c5a89db-d84f-4a3b-978b-f0585ebcb401",
    [string] $UserId        = "yogeshcprajapati@outlook.com",
    [string] $Location      = "eastus",
    [string] $ResourceGroup = "rg-cmspdemo",
    [string] $Prefix        = "cmspdemo",
    [switch] $DeployToAzure,
    [switch] $SkipLogin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ROOT        = $PSScriptRoot
$API_DIR     = Join-Path $ROOT "src" "API"
$PARTNER_DIR = Join-Path $ROOT "src" "PartnerAPI"
$WEB_DIR     = Join-Path $ROOT "src" "Web"
$STATE_FILE  = Join-Path $ROOT ".setup-state.json"

# ─── Colour helpers ───────────────────────────────────────────────────────────
function Write-Step { param([int]$n,[string]$msg) Write-Host "`n╔══ [$n] $msg" -ForegroundColor Cyan }
function Write-Ok   { param([string]$msg) Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Info { param([string]$msg) Write-Host "  ℹ $msg" -ForegroundColor Gray }
function Write-Warn { param([string]$msg) Write-Host "  ⚠ $msg" -ForegroundColor Yellow }
function Write-Fail { param([string]$msg) Write-Host "  ✗ $msg" -ForegroundColor Red }

# ─── State persistence (idempotency) ─────────────────────────────────────────
# All state is saved to .setup-state.json so re-runs skip already-created resources.
$state = if (Test-Path $STATE_FILE) {
    Write-Info "Loading previous state from $STATE_FILE"
    Get-Content $STATE_FILE | ConvertFrom-Json
} else {
    [PSCustomObject]@{
        suffix           = -join ((97..122) | Get-Random -Count 6 | ForEach-Object {[char]$_})
        resourceGroup    = $ResourceGroup
        kvName           = ""
        # CMSPDemo-OBOPartnerAPIApp — the downstream protected API (OBO + S2S server-side)
        oboAppId         = ""  ;  oboObjId      = ""
        oboSpId          = ""
        oboScopeId       = ""  # scope id for access_as_user on OBOPartnerAPIApp
        # CMSPDemo-BFF — shared SPA + Backend for Frontend registration (MSA + AAD)
        bffAppId         = ""  ;  bffObjId      = ""
        bffSpId          = ""
        bffScopeId       = ""  # scope id for access_as_user on BFF
        # CMSPDemo-S2SPartnerAPIApp — S2S caller identity (cert in Key Vault)
        s2sAppId         = ""  ;  s2sObjId      = ""
        s2sSpId          = ""
        # Shared app role on OBOPartnerAPIApp for S2S callers
        s2sAppRoleId     = ""
        # Key Vault cert for CMSPDemo-S2SPartnerAPIApp
        certName         = "cmspdemo-s2s-cert"
        storageAccount   = ""
        appServiceName   = ""
    }
}
function Save-State { $state | ConvertTo-Json -Depth 5 | Set-Content $STATE_FILE -Encoding utf8 }

$KvName = if ($state.kvName) { $state.kvName } else { "kv-$Prefix-$($state.suffix)" }
$state.kvName = $KvName

# ─── Step 0: Prerequisites ────────────────────────────────────────────────────
Write-Step 0 "Checking prerequisites"
foreach ($cmd in "az","dotnet","node","npm") {
    if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) {
        throw "[$cmd] not found. Install it and re-run."
    }
    Write-Ok "$cmd available"
}

# ─── Step 1: Azure login ──────────────────────────────────────────────────────
Write-Step 1 "Azure login"
if (-not $SkipLogin) {
    az login --tenant $TenantId --only-show-errors | Out-Null
    Write-Ok "Signed in"
} else {
    Write-Info "Skipping login (-SkipLogin)"
}
az account set --subscription (az account list --query "[?tenantId=='$TenantId'].id" -o tsv | Select-Object -First 1) 2>$null
Write-Ok "Active subscription set"

# ─── Step 2: Resource group ───────────────────────────────────────────────────
Write-Step 2 "Resource Group: $ResourceGroup"
az group create --name $ResourceGroup --location $Location --only-show-errors | Out-Null
Write-Ok "Resource group ready"

# ─── Step 3: Key Vault ────────────────────────────────────────────────────────
Write-Step 3 "Key Vault: $KvName"
$kvExists = az keyvault list --resource-group $ResourceGroup --query "[?name=='$KvName'].name" -o tsv
if (-not $kvExists) {
    az keyvault create --name $KvName --resource-group $ResourceGroup --location $Location `
        --enable-rbac-authorization false --only-show-errors | Out-Null
    Write-Ok "Key Vault created"
} else {
    Write-Info "Key Vault already exists"
}
az keyvault set-policy --name $KvName --upn $UserId `
    --certificate-permissions get list create import delete `
    --secret-permissions     get list set delete `
    --key-permissions        get list create delete `
    --only-show-errors | Out-Null
Write-Ok "KV access policy set for $UserId"
Save-State

# ─── Helpers ──────────────────────────────────────────────────────────────────
function Get-OrCreate-App {
    param([string]$DisplayName, [string]$SignInAudience = "AzureADMyOrg")
    $existing = az ad app list --display-name $DisplayName --only-show-errors | ConvertFrom-Json
    if ($existing.Count -gt 0) {
        Write-Info "$DisplayName already exists (appId=$($existing[0].appId))"
        return $existing[0]
    }
    $app = az ad app create --display-name $DisplayName --sign-in-audience $SignInAudience `
               --only-show-errors | ConvertFrom-Json
    Write-Ok "$DisplayName created (appId=$($app.appId))"
    return $app
}

function Get-OrCreate-SP {
    param([string]$AppId)
    $existing = az ad sp list --filter "appId eq '$AppId'" --only-show-errors | ConvertFrom-Json
    if ($existing.Count -gt 0) { return $existing[0] }
    return (az ad sp create --id $AppId --only-show-errors | ConvertFrom-Json)
}

function Patch-AppManifest {
    param([string]$ObjId, [hashtable]$Body)
    $json = $Body | ConvertTo-Json -Depth 10 -Compress
    az rest --method PATCH `
        --uri "https://graph.microsoft.com/v1.0/applications/$ObjId" `
        --body $json `
        --headers "Content-Type=application/json" | Out-Null
}

# ─── Step 4: CMSPDemo-OBOPartnerAPIApp Registration ──────────────────────────
# This is the protected downstream API. It accepts:
#   • OBO (delegated user) tokens  → /api/obo/claims, /mcp/obo
#   • S2S (app-only)     tokens  → /api/s2s/claims, /mcp/s2s
# The BFF is its primary caller; CMSPDemo-S2SPartnerAPIApp calls it directly for S2S demos.
Write-Step 4 "CMSPDemo-OBOPartnerAPIApp — Protected downstream API"

$oboApp = if ($state.oboObjId) {
    az ad app show --id $state.oboObjId --only-show-errors | ConvertFrom-Json
} else {
    Get-OrCreate-App "CMSPDemo-OBOPartnerAPIApp"
}
$state.oboAppId = $oboApp.appId
$state.oboObjId = $oboApp.id
Save-State

az ad app update --id $state.oboAppId `
    --identifier-uris "api://$($state.oboAppId)" --only-show-errors 2>$null
Write-Ok "App ID URI: api://$($state.oboAppId)"

$oboScopeId  = if ($state.oboScopeId)   { $state.oboScopeId }   else { [guid]::NewGuid().ToString() }
$s2sAppRoleId = if ($state.s2sAppRoleId) { $state.s2sAppRoleId } else { [guid]::NewGuid().ToString() }
$state.oboScopeId  = $oboScopeId
$state.s2sAppRoleId = $s2sAppRoleId
Save-State

Patch-AppManifest -ObjId $state.oboObjId -Body @{
    api = @{
        oauth2PermissionScopes = @(@{
            id                      = $oboScopeId
            value                   = "access_as_user"
            type                    = "User"
            isEnabled               = $true
            adminConsentDisplayName = "Access OBOPartnerAPI as user (via BFF OBO)"
            adminConsentDescription = "Allows the BFF to call OBOPartnerAPIApp on behalf of the signed-in user."
            userConsentDisplayName  = "Access OBOPartnerAPI as you"
            userConsentDescription  = "Allows the BFF to access OBOPartnerAPIApp on your behalf."
        })
    }
    appRoles = @(@{
        id                  = $s2sAppRoleId
        displayName         = "S2S Caller"
        description         = "Grants app-level (S2S / client-credentials) access to OBOPartnerAPIApp."
        value               = "CMSPDemo.S2S"
        allowedMemberTypes  = @("Application")
        isEnabled           = $true
    })
}
Write-Ok "OBOPartnerAPIApp: scope (access_as_user) + app role (CMSPDemo.S2S) configured"

# OBOPartnerAPIApp needs a client secret to call Graph /me via OBO chain
Write-Info "Adding client secret to OBOPartnerAPIApp (for OBO → Graph /me)…"
$oboSecret = az ad app credential reset --id $state.oboAppId --append `
    --display-name "obo-partner-api-secret" --years 2 --only-show-errors | ConvertFrom-Json
Write-Ok "OBOPartnerAPIApp client secret created"

$oboSp = Get-OrCreate-SP -AppId $state.oboAppId
$state.oboSpId = $oboSp.id
Save-State
Write-Ok "OBOPartnerAPIApp SP: $($state.oboSpId)"

# ─── Step 5: CMSPDemo-BFF Registration ───────────────────────────────────────
Write-Step 5 "CMSPDemo-BFF — Backend for Frontend"

$bffApp = if ($state.bffObjId) {
    az ad app show --id $state.bffObjId --only-show-errors | ConvertFrom-Json
} else {
    Get-OrCreate-App "CMSPDemo-BFF" -SignInAudience "AzureADandPersonalMicrosoftAccount"
}
$state.bffAppId = $bffApp.appId
$state.bffObjId = $bffApp.id
Save-State

az ad app update --id $state.bffAppId `
    --identifier-uris "api://$($state.bffAppId)" --only-show-errors 2>$null
Write-Ok "BFF App ID URI: api://$($state.bffAppId)"

$bffScopeId = if ($state.bffScopeId) { $state.bffScopeId } else { [guid]::NewGuid().ToString() }
$state.bffScopeId = $bffScopeId
Save-State

Patch-AppManifest -ObjId $state.bffObjId -Body @{
    api = @{
        oauth2PermissionScopes = @(@{
            id                      = $bffScopeId
            value                   = "access_as_user"
            type                    = "User"
            isEnabled               = $true
            adminConsentDisplayName = "Access BFF as user"
            adminConsentDescription = "Allows the React Web client to call the BFF on behalf of the signed-in user."
            userConsentDisplayName  = "Access BFF as you"
            userConsentDescription  = "Allows the React Web client to access the BFF on your behalf."
        })
    }
}
Write-Ok "BFF scope (access_as_user) configured"

Patch-AppManifest -ObjId $state.bffObjId -Body @{
    spa = @{ redirectUris = @("http://localhost:5173","https://localhost:5173") }
}
Write-Ok "BFF SPA redirect URIs set (Web and BFF share this app registration)"

Write-Info "Adding client secret to BFF (for OBO exchange + S2S to OBOPartnerAPIApp)…"
$bffSecret = az ad app credential reset --id $state.bffAppId --append `
    --display-name "bff-client-secret" --years 2 --only-show-errors | ConvertFrom-Json
Write-Ok "BFF client secret created"

# BFF → OBOPartnerAPIApp: delegated (OBO) + app-role (S2S)
az ad app permission add --id $state.bffAppId `
    --api $state.oboAppId `
    --api-permissions "$($state.oboScopeId)=Scope" --only-show-errors 2>$null

az ad app permission add --id $state.bffAppId `
    --api $state.oboAppId `
    --api-permissions "$($state.s2sAppRoleId)=Role" --only-show-errors 2>$null
Write-Ok "BFF → OBOPartnerAPIApp permissions: Scope (OBO) + Role (S2S)"

# Pre-authorize BFF so users see no consent prompt for OBO
Patch-AppManifest -ObjId $state.oboObjId -Body @{
    api = @{
        preAuthorizedApplications = @(@{
            appId                   = $state.bffAppId
            delegatedPermissionIds  = @($state.oboScopeId)
        })
    }
}
Write-Ok "BFF pre-authorized in OBOPartnerAPIApp"

$bffSp = Get-OrCreate-SP -AppId $state.bffAppId
$state.bffSpId = $bffSp.id
Save-State
Write-Ok "BFF SP: $($state.bffSpId)"

Write-Info "Granting admin consent for BFF → OBOPartnerAPIApp…"
try {
    az ad app permission admin-consent --id $state.bffAppId --only-show-errors 2>$null
    Write-Ok "Admin consent granted"
} catch {
    Write-Warn "Admin consent failed — grant manually: https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/CallAnAPI/appId/$($state.bffAppId)"
}

# ─── Step 6: CMSPDemo-S2SPartnerAPIApp Registration ──────────────────────────
# This identity is used by background services / daemons that call OBOPartnerAPIApp
# using client-credentials (no user context). Its certificate lives in Key Vault.
Write-Step 6 "CMSPDemo-S2SPartnerAPIApp — S2S caller identity (cert in Key Vault)"

$s2sApp = if ($state.s2sObjId) {
    az ad app show --id $state.s2sObjId --only-show-errors | ConvertFrom-Json
} else {
    Get-OrCreate-App "CMSPDemo-S2SPartnerAPIApp"
}
$state.s2sAppId = $s2sApp.appId
$state.s2sObjId = $s2sApp.id
Save-State

# Grant S2SPartnerAPIApp the CMSPDemo.S2S app role on OBOPartnerAPIApp
az ad app permission add --id $state.s2sAppId `
    --api $state.oboAppId `
    --api-permissions "$($state.s2sAppRoleId)=Role" --only-show-errors 2>$null

$s2sSp = Get-OrCreate-SP -AppId $state.s2sAppId
$state.s2sSpId = $s2sSp.id
Save-State

# Assign the app role to the SP (permission add alone is not enough)
$existingAssign = az rest --method GET `
    --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($state.s2sSpId)/appRoleAssignments" `
    --only-show-errors | ConvertFrom-Json
$alreadyAssigned = $existingAssign.value | Where-Object { $_.appRoleId -eq $state.s2sAppRoleId }
if (-not $alreadyAssigned) {
    az rest --method POST `
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$($state.s2sSpId)/appRoleAssignments" `
        --body "{`"principalId`":`"$($state.s2sSpId)`",`"resourceId`":`"$($state.oboSpId)`",`"appRoleId`":`"$($state.s2sAppRoleId)`"}" `
        --headers "Content-Type=application/json" --only-show-errors | Out-Null
    Write-Ok "CMSPDemo.S2S app role assigned to S2SPartnerAPIApp SP"
} else {
    Write-Info "App role already assigned"
}

try {
    az ad app permission admin-consent --id $state.s2sAppId --only-show-errors 2>$null
    Write-Ok "S2SPartnerAPIApp admin consent granted"
} catch {
    Write-Warn "S2SPartnerAPIApp admin consent failed — grant manually."
}

# ─── Step 7: Key Vault Certificate for CMSPDemo-S2SPartnerAPIApp ─────────────
Write-Step 7 "S2S certificate in Key Vault (cert: $($state.certName))"

$certExists = az keyvault certificate list --vault-name $KvName `
    --query "[?name=='$($state.certName)'].name" -o tsv --only-show-errors
if (-not $certExists) {
    Write-Info "Creating self-signed certificate…"
    $policy = @{
        issuerParameters          = @{ name = "Self" }
        keyProperties             = @{ exportable = $true; keySize = 2048; keyType = "RSA"; reuseKey = $false }
        secretProperties          = @{ contentType = "application/x-pkcs12" }
        x509CertificateProperties = @{ subject = "CN=CMSPDemo-S2SPartnerAPIApp"; validityInMonths = 12 }
    } | ConvertTo-Json -Depth 6
    $policyFile = Join-Path $env:TEMP "cmspdemo-cert-policy.json"
    $policy | Set-Content $policyFile -Encoding utf8
    az keyvault certificate create `
        --vault-name $KvName --name $state.certName `
        --policy "@$policyFile" --only-show-errors | Out-Null
    Remove-Item $policyFile -Force
    Write-Ok "Certificate created: $($state.certName)"
} else {
    Write-Info "Certificate already exists: $($state.certName)"
}

# Upload the public key to the S2SPartnerAPIApp registration
$certInfo   = az keyvault certificate show --vault-name $KvName --name $state.certName --only-show-errors | ConvertFrom-Json
$certBase64 = $certInfo.cer    # Base64 DER — public portion only

Patch-AppManifest -ObjId $state.s2sObjId -Body @{
    keyCredentials = @(@{
        type        = "AsymmetricX509Cert"
        usage       = "Verify"
        key         = $certBase64
        displayName = "CMSPDemo-S2S-Cert"
    })
}
Write-Ok "S2SPartnerAPIApp cert public key uploaded to app registration"
Save-State

# ─── Step 8: Patch local config files ────────────────────────────────────────
Write-Step 8 "Patching local configuration files"

# ── src/PartnerAPI/appsettings.json (OBOPartnerAPIApp is the server identity) ──
$partnerSettings = Get-Content "$PARTNER_DIR\appsettings.json" | ConvertFrom-Json
$partnerSettings.AzureAd.TenantId = $TenantId
$partnerSettings.AzureAd.ClientId = $state.oboAppId
$partnerSettings.AzureAd.Audience = "api://$($state.oboAppId)"
$partnerSettings | ConvertTo-Json -Depth 10 | Set-Content "$PARTNER_DIR\appsettings.json" -Encoding utf8
Write-Ok "PartnerAPI appsettings.json → OBOPartnerAPIApp identity"

dotnet user-secrets --project $PARTNER_DIR set "AzureAd:ClientSecret" $oboSecret.password | Out-Null
dotnet user-secrets --project $PARTNER_DIR set "AzureAd:TenantId"     $TenantId          | Out-Null
dotnet user-secrets --project $PARTNER_DIR set "AzureAd:ClientId"     $state.oboAppId    | Out-Null
Write-Ok "PartnerAPI user-secrets set"

# ── src/API/appsettings.json (BFF identity + scopes to reach OBOPartnerAPIApp) ──
$bffSettings = Get-Content "$API_DIR\appsettings.json" | ConvertFrom-Json
$bffSettings.AzureAd.TenantId = $TenantId
$bffSettings.AzureAd.ClientId = $state.bffAppId
$bffSettings.AzureAd.Audience = "api://$($state.bffAppId)"
$bffSettings.DownstreamApis.PartnerApi.Scopes    = @("api://$($state.oboAppId)/access_as_user")
$bffSettings.DownstreamApis.PartnerApi.AppScopes = @("api://$($state.oboAppId)/.default")
$bffSettings | ConvertTo-Json -Depth 10 | Set-Content "$API_DIR\appsettings.json" -Encoding utf8
Write-Ok "BFF appsettings.json → BFF identity + OBOPartnerAPIApp scopes"

dotnet user-secrets --project $API_DIR set "AzureAd:ClientSecret" $bffSecret.password | Out-Null
dotnet user-secrets --project $API_DIR set "AzureAd:TenantId"     $TenantId           | Out-Null
dotnet user-secrets --project $API_DIR set "AzureAd:ClientId"     $state.bffAppId     | Out-Null
Write-Ok "BFF user-secrets set"

# ── src/Web/.env ──
# CMSPDemo-BFF is registered with AzureADandPersonalMicrosoftAccount, so the
# SPA authority must be "common" (not tenant-specific) to allow MSA sign-in.
@"
# Auto-generated by setup.ps1 — do not edit manually; re-run setup.ps1 to regenerate.
VITE_DEFAULT_CLIENT_ID=$($state.bffAppId)
VITE_AUTHORITY=https://login.microsoftonline.com/common
VITE_API_BASE=http://localhost:5080
"@ | Set-Content "$WEB_DIR\.env" -Encoding utf8
Write-Ok "Web .env written (VITE_DEFAULT_CLIENT_ID=$($state.bffAppId), authority=common)"

# ─── Step 9: (Optional) Deploy to Azure ──────────────────────────────────────
if ($DeployToAzure) {
    Write-Step 9 "Deploying to Azure"

    $planName = "asp-$Prefix-$($state.suffix)"
    $appName  = "app-$Prefix-bff-$($state.suffix)"
    $state.appServiceName = $appName
    Save-State

    Write-Info "Creating App Service Plan: $planName"
    az appservice plan create --name $planName --resource-group $ResourceGroup `
        --location $Location --sku B1 --is-linux --only-show-errors | Out-Null

    Write-Info "Creating Web App: $appName"
    az webapp create --name $appName --resource-group $ResourceGroup `
        --plan $planName --runtime "DOTNETCORE:10.0" --only-show-errors | Out-Null

    az webapp config appsettings set --name $appName --resource-group $ResourceGroup `
        --settings `
            "AzureAd__TenantId=$TenantId" `
            "AzureAd__ClientId=$($state.bffAppId)" `
            "AzureAd__Audience=api://$($state.bffAppId)" `
            "AzureAd__ClientSecret=$($bffSecret.password)" `
            "DownstreamApis__PartnerApi__BaseUrl=http://localhost:5081" `
            "DownstreamApis__PartnerApi__Scopes__0=api://$($state.oboAppId)/access_as_user" `
            "DownstreamApis__PartnerApi__AppScopes__0=api://$($state.oboAppId)/.default" `
        --only-show-errors | Out-Null
    Write-Ok "BFF App Service settings configured"

    Write-Info "Building BFF API…"
    dotnet publish "$API_DIR\CMSPDemo.API.csproj" -c Release -o "$API_DIR\publish" | Out-Null
    Compress-Archive -Path "$API_DIR\publish\*" -DestinationPath "$API_DIR\publish.zip" -Force
    az webapp deploy --name $appName --resource-group $ResourceGroup `
        --src-path "$API_DIR\publish.zip" --type zip --only-show-errors | Out-Null
    Write-Ok "BFF API deployed to https://$appName.azurewebsites.net"

    $storageName = "st${Prefix}$($state.suffix)"
    $state.storageAccount = $storageName
    Save-State

    Write-Info "Creating storage account: $storageName"
    az storage account create --name $storageName --resource-group $ResourceGroup `
        --location $Location --sku Standard_LRS --kind StorageV2 --only-show-errors | Out-Null
    az storage blob service-properties update --account-name $storageName `
        --static-website --index-document "index.html" --404-document "index.html" `
        --only-show-errors | Out-Null

    $webUrl = az storage account show --name $storageName `
        --query "primaryEndpoints.web" -o tsv --only-show-errors
    Write-Ok "Static website URL: $webUrl"

    Patch-AppManifest -ObjId $state.bffObjId -Body @{
        spa = @{ redirectUris = @("http://localhost:5173","https://localhost:5173",($webUrl.TrimEnd('/'))) }
    }
    Write-Ok "SPA redirect URIs updated with $webUrl"

    @"
VITE_DEFAULT_CLIENT_ID=$($state.bffAppId)
VITE_AUTHORITY=https://login.microsoftonline.com/common
VITE_API_BASE=https://$appName.azurewebsites.net
"@ | Set-Content "$WEB_DIR\.env.production" -Encoding utf8

    Write-Info "Building Web…"
    Push-Location $WEB_DIR
    npm install --silent
    npm run build -- --mode production
    Pop-Location

    Write-Info "Uploading Web to Azure Storage…"
    az storage blob upload-batch `
        --destination '$web' --account-name $storageName `
        --source "$WEB_DIR\dist" --overwrite --only-show-errors | Out-Null
    Write-Ok "Web deployed to $webUrl"

    az webapp cors add --name $appName --resource-group $ResourceGroup `
        --allowed-origins ($webUrl.TrimEnd('/')) --only-show-errors | Out-Null
    Write-Ok "CORS updated on BFF App Service"
}

# ─── Summary ─────────────────────────────────────────────────────────────────
Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║                    Setup Complete 🎉                        ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════════════════╝`n" -ForegroundColor Cyan

Write-Host "Entra App Registrations:" -ForegroundColor White
Write-Host "  CMSPDemo-BFF (SPA + BFF)  appId : $($state.bffAppId)"
Write-Host "  CMSPDemo-OBOPartnerAPIApp appId : $($state.oboAppId)"
Write-Host "  CMSPDemo-S2SPartnerAPIApp appId : $($state.s2sAppId)"
Write-Host ""
Write-Host "Azure Resources:" -ForegroundColor White
Write-Host "  Resource Group : $ResourceGroup"
Write-Host "  Key Vault      : $KvName  (cert: $($state.certName))"
if ($DeployToAzure) {
    Write-Host "  BFF App Service: https://$($state.appServiceName).azurewebsites.net"
    Write-Host "  Web Static Site: $(az storage account show --name $state.storageAccount --query primaryEndpoints.web -o tsv --only-show-errors 2>$null)"
}
Write-Host ""
Write-Host "Local dev — run in three separate terminals:" -ForegroundColor White
Write-Host "  cd $PARTNER_DIR ; dotnet run   # OBOPartnerAPIApp server  :5081"
Write-Host "  cd $API_DIR     ; dotnet run   # BFF                      :5080"
Write-Host "  cd $WEB_DIR     ; npm run dev  # Web SPA                  :5173"
Write-Host ""
Write-Host "State saved to: $STATE_FILE (re-run is idempotent)" -ForegroundColor Gray
