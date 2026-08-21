# One-time Azure sign-in for AudioBoarder.
#
# After this runs once, AudioBoarder picks up the cached token automatically
# (DefaultAzureCredential -> Azure CLI / Az PowerShell cache), so you never need
# to put credentials in a config file.
#
# Usage:
#   pwsh -ExecutionPolicy Bypass -File .\scripts\setup-azure.ps1
#   pwsh -ExecutionPolicy Bypass -File .\scripts\setup-azure.ps1 -Tenant <guid> -Subscription <guid>
#
# With no arguments it signs in to your default tenant/subscription and lists
# every Foundry deployment it can see, so you know what the app will discover.

param(
  [string]$Tenant = "",
  [string]$Subscription = ""
)

$ErrorActionPreference = "Stop"

Write-Host "=== AudioBoarder Azure setup ==="
if ($Tenant)       { Write-Host "Tenant:       $Tenant" }
if ($Subscription) { Write-Host "Subscription: $Subscription" }
Write-Host ""

if (-not (Get-Module -ListAvailable Az.Accounts)) {
  Write-Host "Installing Az.Accounts module..."
  Install-Module -Name Az.Accounts -Scope CurrentUser -Force -AllowClobber
}
Import-Module Az.Accounts -ErrorAction Stop

$ctx = Get-AzContext -ErrorAction SilentlyContinue
$needsLogin = -not $ctx -or ($Tenant -and $ctx.Tenant.Id -ne $Tenant)
if ($needsLogin) {
  Write-Host "Signing in (a browser window will open)..."
  $connect = @{}
  if ($Tenant)       { $connect.Tenant = $Tenant }
  if ($Subscription) { $connect.Subscription = $Subscription }
  Connect-AzAccount @connect | Out-Null
} else {
  Write-Host "Already signed in: $($ctx.Account.Id)"
}

$ctx = Get-AzContext
Write-Host "OK signed in:"
Write-Host "  Account:      $($ctx.Account.Id)"
Write-Host "  Tenant:       $($ctx.Tenant.Id)"
Write-Host "  Subscription: $($ctx.Subscription.Name) ($($ctx.Subscription.Id))"
Write-Host ""

Write-Host "Probing Cognitive Services / Foundry resources..."
try {
  if (-not (Get-Module -ListAvailable Az.CognitiveServices)) {
    Install-Module -Name Az.CognitiveServices -Scope CurrentUser -Force -AllowClobber
  }
  Import-Module Az.CognitiveServices -ErrorAction Stop
  $accts = Get-AzCognitiveServicesAccount
  if (-not $accts) {
    Write-Warning "No Cognitive Services accounts found. Create one in Azure AI Foundry first."
    exit 2
  }
  $accts | Where-Object { $_.Kind -in @("OpenAI","AIServices") } | ForEach-Object {
    Write-Host ""
    Write-Host "  Account: $($_.AccountName)  Kind=$($_.Kind)  Region=$($_.Location)"
    Write-Host "  Endpoint: $($_.Endpoint)"
    $depls = Get-AzCognitiveServicesAccountDeployment -ResourceGroupName $_.ResourceGroupName -AccountName $_.AccountName
    foreach ($d in $depls) {
      Write-Host "    - Deployment: $($d.Name)  Model=$($d.Properties.Model.Name)  Sku=$($d.Sku.Name) cap=$($d.Sku.Capacity)"
    }
  }
} catch {
  Write-Warning "Could not enumerate Cognitive Services: $($_.Exception.Message)"
  exit 3
}

Write-Host ""
Write-Host "Setup complete. Run 'AudioBoarder.exe healthcheck' to confirm, then launch the app."
Write-Host "To pin a specific deployment, copy appsettings.Local.json.example to"
Write-Host "appsettings.Local.json and set AzureOpenAI.DeploymentName."
