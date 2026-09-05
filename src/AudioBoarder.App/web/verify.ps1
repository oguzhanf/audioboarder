[CmdletBinding()]
param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot "..\Assets\web"),
    [string]$ResultPath
)

$ErrorActionPreference = "Stop"
$edge = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (!$edge) { throw "Microsoft Edge is required to verify the offline canvas." }

$profile = Join-Path ([IO.Path]::GetTempPath()) ("audioboarder-canvas-" + [Guid]::NewGuid().ToString("N"))
$encoding = [Text.UTF8Encoding]::new($false)
$assets = @{}
foreach ($name in @("index.html", "main-canvas.js", "canvas.js", "canvas.css")) {
    $assets["/$name"] = [IO.File]::ReadAllBytes((Join-Path $AssetDirectory $name))
}
$bootstrap = @'
<script>
window.__messages = []; window.__errors = [];
window.chrome = window.chrome || {};
window.chrome.webview = {
  postMessage: m => window.__messages.push(m),
  addEventListener: (type, handler) => { window.__hostMessage = handler; }
};
window.addEventListener("error", e => window.__errors.push(String(e.message)));
</script>
'@
$html = $encoding.GetString($assets["/index.html"]).Replace("<head>", "<head>`n$bootstrap")
$html = $html.Replace("</body>", '<pre id="verification-result"></pre><script type="module" src="/verify-browser.js"></script></body>')
$assets["/verify.html"] = $encoding.GetBytes($html)
$assets["/verify-browser.js"] = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot "verify-browser.js"))
$server = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$process = $null
try {
    $server.Start()
    $port = ([Net.IPEndPoint]$server.LocalEndpoint).Port
    $start = [Diagnostics.ProcessStartInfo]::new($edge)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @("--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--disable-background-networking", "--disable-extensions", "--disable-sync", "--remote-debugging-port=0",
        "--window-size=1200,800", "--user-data-dir=$profile",
        "http://127.0.0.1:$port/verify.html")) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $result = $null
    while ($null -eq $result -and [DateTime]::UtcNow -lt $deadline) {
        if (!$server.Pending()) { Start-Sleep -Milliseconds 20; continue }
        $client = $server.AcceptTcpClient()
        try {
            $stream = $client.GetStream()
            $stream.ReadTimeout = 2000
            $reader = [IO.StreamReader]::new($stream, $encoding, $false, 1024, $true)
            try {
                $request = $reader.ReadLine()
                if (!$request) { continue }
                $length = 0
                do {
                    $header = $reader.ReadLine()
                    if ($header -match '^Content-Length:\s*(\d+)$') { $length = [int]$Matches[1] }
                } while ($header)
                $path = ($request -split ' ')[1].Split('?')[0]
                if ($path -eq "/verification-result" -and $request.StartsWith("POST ") -and $length -gt 0 -and $length -lt 65536) {
                    $buffer = [char[]]::new($length)
                    if ($reader.ReadBlock($buffer, 0, $length) -ne $length) { throw "Incomplete browser result." }
                    $result = [Uri]::UnescapeDataString((-join $buffer)) | ConvertFrom-Json
                    $body = $encoding.GetBytes("OK")
                } else {
                    $body = $assets[$path]
                }
                $status = if ($null -ne $body) { "200 OK" } else { "404 Not Found" }
                if ($null -eq $body) { $body = $encoding.GetBytes("Not found") }
                $mime = switch ([IO.Path]::GetExtension($path)) {
                    ".js" { "text/javascript" }
                    ".css" { "text/css" }
                    default { "text/html" }
                }
                $headers = $encoding.GetBytes("HTTP/1.1 $status`r`nContent-Type: $mime; charset=utf-8`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n")
                $stream.Write($headers, 0, $headers.Length)
                $stream.Write($body, 0, $body.Length)
                $stream.Flush()
            }
            finally { $reader.Dispose() }
        }
        finally { $client.Dispose() }
    }
    if ($null -eq $result) { throw "Headless Edge canvas verification timed out without a result." }
    if (!$result.passed) {
        throw "Canvas verification failed: $($result.error)"
    }
    if ($ResultPath) {
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ResultPath -Encoding utf8NoBOM
    }
    Write-Host "Offline SVG canvas verified in headless Edge ($($result.checks.Count) assertions)." -ForegroundColor Green
}
finally {
    $server.Stop()
    if ($process) {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    $owned = Get-CimInstance Win32_Process -Filter "Name = 'msedge.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains([IO.Path]::GetFileName($profile)) }
    foreach ($child in $owned) {
        Stop-Process -Id $child.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $child.ProcessId -Timeout 5 -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $profile) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $profile -Recurse -Force
                break
            }
            catch [IO.IOException] {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}
