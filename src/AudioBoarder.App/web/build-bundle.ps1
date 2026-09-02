<#
.SYNOPSIS
  Rebuilds the vendored, offline SVG canvas bundle into ../Assets/web.

.DESCRIPTION
  Installs exact locked dependencies and runs the Vite build. The output in ../Assets/web is what
  ships with the app (served by ExcalidrawCanvas via a WebView2 virtual-host mapping).

.EXAMPLE
  pwsh -File .\build-bundle.ps1
#>
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

Write-Host "1/2 Installing npm dependencies..." -ForegroundColor Cyan
npm ci

Write-Host "2/2 Building the bundle into ../Assets/web ..." -ForegroundColor Cyan
npm run build

Write-Host "Done. Vendored bundle is in ..\Assets\web" -ForegroundColor Green
