<#
.SYNOPSIS
  Rebuilds the vendored, offline SVG canvas bundle into ../Assets/web.

.DESCRIPTION
  Packages plain JavaScript modules and CSS without Node.js or a bundler.
  The output in ../Assets/web ships with the app through the WebView2 virtual host.

.EXAMPLE
  pwsh -File .\build-bundle.ps1
#>
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path (Split-Path -Parent $here) "Assets\web"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
foreach ($name in @("main-canvas.js", "canvas.js", "canvas.css")) {
    $text = [IO.File]::ReadAllText((Join-Path $here "src\$name")).Replace("`r`n", "`n")
    [IO.File]::WriteAllText((Join-Path $output $name), $text, $encoding)
}
$html = [IO.File]::ReadAllText((Join-Path $here "index.html")).Replace("`r`n", "`n")
$html = $html.Replace("/src/main-canvas.js", "./main-canvas.js").Replace("/src/canvas.css", "./canvas.css")
[IO.File]::WriteAllText((Join-Path $output "index.html"), $html, $encoding)
Write-Host "Packaged offline canvas modules in $output" -ForegroundColor Green
