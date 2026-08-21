<#
.SYNOPSIS
  Rebuilds the vendored, offline Excalidraw whiteboard bundle into ../Assets/web.

.DESCRIPTION
  Run this whenever you bump the @excalidraw/excalidraw version (or change main.jsx).
  It installs dependencies, copies Excalidraw's production fonts for offline use,
  and runs the Vite build. The output in ../Assets/web is what ships with the app
  (served by ExcalidrawCanvas via a WebView2 virtual-host mapping).

.EXAMPLE
  pwsh -File .\build-bundle.ps1
#>
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here

Write-Host "1/3 Installing npm dependencies..." -ForegroundColor Cyan
npm install

Write-Host "2/3 Copying Excalidraw production fonts (offline self-hosting)..." -ForegroundColor Cyan
$fontSrc = Join-Path $here "node_modules\@excalidraw\excalidraw\dist\prod\fonts"
$fontDst = Join-Path $here "public\fonts"
if (Test-Path $fontDst) { Remove-Item -Recurse -Force $fontDst }
New-Item -ItemType Directory -Force -Path $fontDst | Out-Null
Copy-Item -Recurse -Force "$fontSrc\*" $fontDst
$count = (Get-ChildItem -Recurse $fontDst -File).Count
Write-Host "   copied $count font files" -ForegroundColor DarkGray

Write-Host "3/3 Building the bundle into ../Assets/web ..." -ForegroundColor Cyan
npm run build

Write-Host "Done. Vendored bundle is in ..\Assets\web" -ForegroundColor Green
